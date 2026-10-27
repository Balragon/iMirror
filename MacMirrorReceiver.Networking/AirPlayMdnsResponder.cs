using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MacMirrorReceiver;

namespace MacMirrorReceiver.Networking;

/// <summary>
/// The mDNS side of the receiver: the multicast socket on UDP 5353, the periodic announcement
/// (3 s after start, then every 30 s), answers to queries that name us (at most once per
/// second, RFC 6762 section 6), the goodbye on dispose, and the DNS wire format. The identity
/// (names, device id) and the TXT records come from the probe service; the SRV ports are read
/// when a packet is built because the listeners bind after construction.
/// </summary>
internal sealed class AirPlayMdnsResponder : IDisposable
{
	public const string AirPlayServiceType = "_airplay._tcp.local";
	public const string RaopServiceType = "_raop._tcp.local";
	public const int DefaultPort = 5353;
	private const string DnsSdServiceEnumerationName = "_services._dns-sd._udp.local";
	private const int QueryResponseMinimumIntervalMilliseconds = 1000;

	private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
	private static readonly IPEndPoint MulticastEndpoint = new IPEndPoint(MulticastAddress, DefaultPort);

	private readonly int _port;
	private readonly string _hostName;
	private readonly string _airPlayInstanceName;
	private readonly string _raopInstanceName;
	private readonly string[] _answeredNames;
	private readonly Func<int> _airPlayPort;
	private readonly Func<int> _raopPort;
	private readonly Func<IReadOnlyList<string>> _airPlayTxt;
	private readonly Func<IReadOnlyList<string>> _raopTxt;
	private UdpClient? _client;
	private bool _bound;
	private long _lastQueryResponseTicks = long.MinValue / 2;
	private Task? _receiveTask;
	private Task? _announceTask;

	/// <param name="port">UDP port to bind (5353, or an ephemeral port in tests).</param>
	/// <param name="hostName">Our host name, e.g. DESKTOP-1.local (A and SRV target).</param>
	/// <param name="airPlayInstanceName">"&lt;display name&gt;._airplay._tcp.local".</param>
	/// <param name="raopInstanceName">"&lt;mac&gt;@&lt;display name&gt;._raop._tcp.local".</param>
	/// <param name="airPlayPort">The bound AirPlay TCP port, read per packet.</param>
	/// <param name="raopPort">The bound RAOP TCP port, read per packet.</param>
	/// <param name="airPlayTxt">The _airplay TXT record values.</param>
	/// <param name="raopTxt">The _raop TXT record values.</param>
	public AirPlayMdnsResponder(
		int port,
		string hostName,
		string airPlayInstanceName,
		string raopInstanceName,
		Func<int> airPlayPort,
		Func<int> raopPort,
		Func<IReadOnlyList<string>> airPlayTxt,
		Func<IReadOnlyList<string>> raopTxt)
	{
		_port = port;
		_hostName = hostName;
		_airPlayInstanceName = airPlayInstanceName;
		_raopInstanceName = raopInstanceName;
		_answeredNames = new[] { AirPlayServiceType, RaopServiceType, DnsSdServiceEnumerationName, airPlayInstanceName, raopInstanceName, hostName };
		_airPlayPort = airPlayPort;
		_raopPort = raopPort;
		_airPlayTxt = airPlayTxt;
		_raopTxt = raopTxt;
	}

	/// <summary>True once the socket is bound and joined to the multicast group.</summary>
	public bool IsBound => _bound;

	/// <summary>The receive and announce loops, for the owner's shutdown wait.</summary>
	public IEnumerable<Task> BackgroundTasks
	{
		get
		{
			if (_receiveTask != null)
			{
				yield return _receiveTask;
			}
			if (_announceTask != null)
			{
				yield return _announceTask;
			}
		}
	}

	/// <summary>
	/// Binds UDP <c>port</c> with address reuse and joins 224.0.0.251. Throws on failure (most
	/// commonly 5353 held exclusively by Bonjour/iTunes) and leaves the responder unbound, so the
	/// owner can report the error and retry later.
	/// </summary>
	public void Bind()
	{
		UdpClient? client = null;
		try
		{
			client = new UdpClient(AddressFamily.InterNetwork);
			client.Client.ExclusiveAddressUse = false;
			client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, optionValue: true);
			client.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
			client.JoinMulticastGroup(MulticastAddress);
		}
		catch
		{
			try
			{
				client?.Dispose();
			}
			catch
			{
			}
			throw;
		}

		// Publish the socket only after bind+join succeed, so a failure above leaves IsBound false.
		_client = client;
		_bound = true;
	}

	/// <summary>Starts the receive and announce loops; the token ends them.</summary>
	public void StartLoops(CancellationToken token)
	{
		_receiveTask = Task.Run(() => ReceiveLoopAsync(token));
		_announceTask = Task.Run(() => AnnounceLoopAsync(token));
	}

	/// <summary>Drops the socket after a partial start so a later <see cref="Bind"/> can retry.</summary>
	public void Reset()
	{
		UdpClient? client = _client;
		_client = null;
		_bound = false;
		try
		{
			client?.Dispose();
		}
		catch
		{
		}
	}

	/// <summary>Multicasts our records with TTL 0 so caches drop them now rather than in 120 s.</summary>
	public void SendGoodbye()
	{
		UdpClient? client = _client;
		if (client == null)
		{
			return;
		}
		byte[] goodbye = BuildMdnsResponse(ttlSeconds: 0);
		client.Send(goodbye, goodbye.Length, MulticastEndpoint);
	}

	public void Dispose()
	{
		_client?.Dispose();
	}

	private async Task ReceiveLoopAsync(CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			try
			{
				UdpReceiveResult result = await _client!.ReceiveAsync(token);
				if (!PacketAsksForProbe(result.Buffer, _answeredNames))
				{
					continue;
				}

				// RFC 6762 section 6: a multicast responder must not send a given record more
				// than once per second. Queries from many hosts (or a flood from one) collapse
				// into at most one announcement per second; the periodic announce loop and
				// the goodbye are unaffected.
				long now = Environment.TickCount64;
				if (now - _lastQueryResponseTicks < QueryResponseMinimumIntervalMilliseconds)
				{
					continue;
				}
				_lastQueryResponseTicks = now;
				await SendAnnouncementAsync(token);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			catch (Exception ex)
			{
				AppLog.Write("AirPlay receiver mDNS receive failed: " + ex.Message);
			}
		}
	}

	private async Task AnnounceLoopAsync(CancellationToken token)
	{
		TimeSpan delay = TimeSpan.FromSeconds(3.0);
		while (!token.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(delay, token);
				await SendAnnouncementAsync(token);
				delay = TimeSpan.FromSeconds(30.0);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (Exception ex)
			{
				AppLog.Write("AirPlay receiver mDNS announce failed: " + ex.Message);
			}
		}
	}

	public async Task SendAnnouncementAsync(CancellationToken token)
	{
		if (_client == null)
		{
			return;
		}

		byte[] packet = BuildMdnsResponse(ttlSeconds: 120);
		await _client.SendAsync(packet, MulticastEndpoint, token);
	}

	private byte[] BuildMdnsResponse(uint ttlSeconds)
	{
		List<DnsRecord> records = new List<DnsRecord>
		{
			DnsRecord.Ptr(AirPlayServiceType, _airPlayInstanceName, ttlSeconds),
			DnsRecord.Ptr(RaopServiceType, _raopInstanceName, ttlSeconds),
			DnsRecord.Srv(_airPlayInstanceName, _airPlayPort(), _hostName, ttlSeconds),
			DnsRecord.Txt(_airPlayInstanceName, _airPlayTxt(), ttlSeconds),
			DnsRecord.Srv(_raopInstanceName, _raopPort(), _hostName, ttlSeconds),
			DnsRecord.Txt(_raopInstanceName, _raopTxt(), ttlSeconds)
		};

		foreach (IPAddress address in ResolveLocalIPv4Addresses())
		{
			records.Add(DnsRecord.A(_hostName, address, ttlSeconds));
		}

		using MemoryStream stream = new MemoryStream();
		Span<byte> header = stackalloc byte[12];
		BinaryPrimitives.WriteUInt16BigEndian(header.Slice(2, 2), 0x8400);
		BinaryPrimitives.WriteUInt16BigEndian(header.Slice(6, 2), (ushort)records.Count);
		stream.Write(header);

		foreach (DnsRecord record in records)
		{
			WriteName(stream, record.Name);
			WriteUInt16(stream, record.Type);
			WriteUInt16(stream, record.Class);
			WriteUInt32(stream, record.Ttl);
			WriteUInt16(stream, (ushort)record.Data.Length);
			stream.Write(record.Data);
		}

		return stream.ToArray();
	}

	/// <summary>
	/// True when <paramref name="packet"/> is an mDNS query (QR bit clear) and one of its
	/// questions names something this responder answers for: the two service types, the
	/// DNS-SD service enumeration name, our instance names or our host name. Responses
	/// from other devices and queries for other names (every ".local" name on the LAN,
	/// Chromecasts, printers, other AirPlay receivers) are ignored, so this responder
	/// neither amplifies other hosts' traffic nor answers for them.
	/// </summary>
	internal static bool PacketAsksForProbe(byte[] packet, IReadOnlyCollection<string> answeredNames)
	{
		if (packet.Length < 12)
		{
			return false;
		}

		// Header flags: QR (bit 15) set means this is a response, not a query.
		if ((packet[2] & 0x80) != 0)
		{
			return false;
		}

		try
		{
			ushort questionCount = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
			int offset = 12;
			for (int i = 0; i < questionCount && offset < packet.Length; i++)
			{
				string name = ReadName(packet, ref offset);
				if (offset + 4 > packet.Length)
				{
					break;
				}
				offset += 4;
				if (IsAnsweredName(name, answeredNames))
				{
					return true;
				}
			}
		}
		catch (Exception ex) when (ex is IndexOutOfRangeException || ex is ArgumentException || ex is DecoderFallbackException)
		{
			// Malformed question section: treat as not for us.
		}

		return false;
	}

	internal static bool IsAnsweredName(string name, IReadOnlyCollection<string> answeredNames)
	{
		string trimmed = name.TrimEnd('.');
		foreach (string answered in answeredNames)
		{
			if (string.Equals(trimmed, answered, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}
		}
		return false;
	}

	/// <summary>
	/// Local IPv4 addresses for the A records, cached for 30 s. Enumerating adapters is a
	/// slow Win32 call and this runs on the mDNS receive thread for every answered query.
	/// </summary>
	internal static IReadOnlyList<IPAddress> ResolveLocalIPv4Addresses()
	{
		lock (LocalAddressCacheGate)
		{
			long now = Environment.TickCount64;
			if (_cachedLocalAddresses != null && now - _cachedLocalAddressesTicks < LocalAddressCacheMilliseconds)
			{
				return _cachedLocalAddresses;
			}

			IReadOnlyList<IPAddress> fresh = EnumerateLocalIPv4Addresses();
			_cachedLocalAddresses = fresh;
			_cachedLocalAddressesTicks = now;
			return fresh;
		}
	}

	internal static IReadOnlyList<IPAddress> EnumerateLocalIPv4Addresses()
	{
		List<IPAddress> addresses = new List<IPAddress>();
		foreach (NetworkInterface networkInterface in NetworkInterface.GetAllNetworkInterfaces())
		{
			if (networkInterface.OperationalStatus != OperationalStatus.Up)
			{
				continue;
			}

			foreach (UnicastIPAddressInformation unicast in networkInterface.GetIPProperties().UnicastAddresses)
			{
				IPAddress address = unicast.Address;
				if (address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
				{
					addresses.Add(address);
				}
			}
		}

		return addresses.Count > 0 ? addresses : new[] { IPAddress.Loopback };
	}

	internal static string ReadName(byte[] packet, ref int offset)
	{
		List<string> labels = new List<string>();
		bool jumped = false;
		int returnOffset = 0;
		int guard = 0;
		while (offset < packet.Length && guard++ < 32)
		{
			byte length = packet[offset++];
			if (length == 0)
			{
				break;
			}
			if ((length & 0xC0) == 0xC0)
			{
				if (offset >= packet.Length)
				{
					break;
				}
				int pointer = ((length & 0x3F) << 8) | packet[offset++];
				if (!jumped)
				{
					returnOffset = offset;
				}
				offset = pointer;
				jumped = true;
			}
			else
			{
				if (offset + length > packet.Length)
				{
					break;
				}
				labels.Add(Encoding.UTF8.GetString(packet, offset, length));
				offset += length;
			}
		}
		if (jumped)
		{
			offset = returnOffset;
		}
		return string.Join(".", labels);
	}

	internal static void WriteName(Stream stream, string name)
	{
		foreach (string label in name.TrimEnd('.').Split('.'))
		{
			byte[] bytes = Encoding.UTF8.GetBytes(label);
			stream.WriteByte((byte)bytes.Length);
			stream.Write(bytes);
		}
		stream.WriteByte(0);
	}

	internal static void WriteUInt16(Stream stream, ushort value)
	{
		Span<byte> buffer = stackalloc byte[2];
		BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
		stream.Write(buffer);
	}

	internal static void WriteUInt32(Stream stream, uint value)
	{
		Span<byte> buffer = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
		stream.Write(buffer);
	}

	internal static byte[] BuildNameData(string name)
	{
		using MemoryStream stream = new MemoryStream();
		WriteName(stream, name);
		return stream.ToArray();
	}

	internal static byte[] BuildSrvData(int port, string target)
	{
		using MemoryStream stream = new MemoryStream();
		WriteUInt16(stream, 0);
		WriteUInt16(stream, 0);
		WriteUInt16(stream, (ushort)port);
		WriteName(stream, target);
		return stream.ToArray();
	}

	internal static byte[] BuildTxtData(IEnumerable<string> values)
	{
		using MemoryStream stream = new MemoryStream();
		foreach (string value in values)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(value);
			if (bytes.Length > 255)
			{
				continue;
			}
			stream.WriteByte((byte)bytes.Length);
			stream.Write(bytes);
		}
		return stream.ToArray();
	}

	private static readonly object LocalAddressCacheGate = new object();
	private static IReadOnlyList<IPAddress>? _cachedLocalAddresses;
	private static long _cachedLocalAddressesTicks;
	private const int LocalAddressCacheMilliseconds = 30_000;

	private readonly record struct DnsRecord(string Name, ushort Type, ushort Class, uint Ttl, byte[] Data)
	{
		private const ushort ClassIn = 1;
		private const ushort ClassInCacheFlush = 0x8001;

		public static DnsRecord Ptr(string name, string value, uint ttl) => new DnsRecord(name, 12, ClassIn, ttl, BuildNameData(value));

		public static DnsRecord Srv(string name, int port, string target, uint ttl) => new DnsRecord(name, 33, ClassInCacheFlush, ttl, BuildSrvData(port, target));

		public static DnsRecord Txt(string name, IEnumerable<string> values, uint ttl) => new DnsRecord(name, 16, ClassInCacheFlush, ttl, BuildTxtData(values));

		public static DnsRecord A(string name, IPAddress address, uint ttl) => new DnsRecord(name, 1, ClassInCacheFlush, ttl, address.GetAddressBytes());
	}
}
