using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MacMirrorReceiver.Models;

namespace MacMirrorReceiver.Networking;

public sealed class MdnsBrowser : IDisposable
{
	private const string ServiceType = "_macmirror._tcp.local";

	private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	private UdpClient? _udpClient;

	private Task? _receiveTask;

	private Task? _queryTask;

	public event Action<MirrorDevice>? DeviceFound;

	public Task StartAsync()
	{
		_udpClient = new UdpClient(AddressFamily.InterNetwork);
		_udpClient.Client.ExclusiveAddressUse = false;
		_udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, optionValue: true);
		_udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
		_udpClient.JoinMulticastGroup(MulticastAddress);
		_receiveTask = Task.Run((Func<Task?>)ReceiveLoopAsync);
		_queryTask = Task.Run((Func<Task?>)QueryLoopAsync);
		return Task.CompletedTask;
	}

	private async Task QueryLoopAsync()
	{
		IPEndPoint endpoint = new IPEndPoint(MulticastAddress, 5353);
		while (!_cts.IsCancellationRequested)
		{
			try
			{
				byte[] array = BuildPtrQuery("_macmirror._tcp.local");
				await _udpClient.SendAsync(array, endpoint, _cts.Token);
				await Task.Delay(TimeSpan.FromSeconds(3.0), _cts.Token);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch
			{
				await Task.Delay(TimeSpan.FromSeconds(5.0));
			}
		}
	}

	private async Task ReceiveLoopAsync()
	{
		while (!_cts.IsCancellationRequested)
		{
			try
			{
				foreach (MirrorDevice item in ParseResponse((await _udpClient.ReceiveAsync(_cts.Token)).Buffer))
				{
					this.DeviceFound?.Invoke(item);
				}
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch
			{
			}
		}
	}

	private static byte[] BuildPtrQuery(string name)
	{
		using MemoryStream memoryStream = new MemoryStream();
		Span<byte> span = stackalloc byte[12];
		BinaryPrimitives.WriteUInt16BigEndian(span.Slice(4, 2), 1);
		memoryStream.Write(span);
		WriteName(memoryStream, name);
		WriteUInt16(memoryStream, 12);
		WriteUInt16(memoryStream, 1);
		return memoryStream.ToArray();
	}

	private static IReadOnlyList<MirrorDevice> ParseResponse(byte[] packet)
	{
		if (packet.Length < 12)
		{
			return Array.Empty<MirrorDevice>();
		}
		ushort num = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4, 2));
		ushort num2 = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(6, 2));
		ushort num3 = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8, 2));
		ushort num4 = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(10, 2));
		int offset = 12;
		for (int i = 0; i < num; i++)
		{
			ReadName(packet, ref offset);
			offset += 4;
		}
		HashSet<string> hashSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, (string, int)> dictionary = new Dictionary<string, (string, int)>(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, IPAddress> dictionary2 = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
		int num5 = num2 + num3 + num4;
		ushort num7;
		int num8;
		for (int j = 0; j < num5 && offset < packet.Length; offset = num8 + num7, j++)
		{
			string text = ReadName(packet, ref offset);
			if (offset + 10 > packet.Length)
			{
				break;
			}
			ushort num6 = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
			offset += 2;
			offset += 2;
			offset += 4;
			num7 = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(offset, 2));
			offset += 2;
			num8 = offset;
			if (offset + num7 > packet.Length)
			{
				break;
			}
			switch (num6)
			{
			case 12:
			{
				int offset3 = num8;
				string text3 = ReadName(packet, ref offset3);
				if (text3.EndsWith("_macmirror._tcp.local", StringComparison.OrdinalIgnoreCase))
				{
					hashSet.Add(text3);
				}
				continue;
			}
			case 33:
				if (num7 >= 6)
				{
					ushort item = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(num8 + 4, 2));
					int offset2 = num8 + 6;
					string text2 = ReadName(packet, ref offset2);
					dictionary[text] = (text2.TrimEnd('.'), item);
					continue;
				}
				break;
			}
			if (num6 == 1 && num7 == 4)
			{
				IPAddress value = new IPAddress(packet.AsSpan(num8, 4));
				dictionary2[text.TrimEnd('.')] = value;
			}
		}
		List<MirrorDevice> list = new List<MirrorDevice>();
		foreach (string item2 in hashSet)
		{
			if (dictionary.TryGetValue(item2, out var value2) && dictionary2.TryGetValue(value2.Item1, out var value3))
			{
				list.Add(new MirrorDevice
				{
					Name = item2.Replace("._macmirror._tcp.local", "", StringComparison.OrdinalIgnoreCase),
					Host = value2.Item1,
					Address = value3,
					Port = value2.Item2
				});
			}
		}
		return list;
	}

	private static string ReadName(byte[] packet, ref int offset)
	{
		List<string> list = new List<string>();
		bool flag = false;
		int num = 0;
		int num2 = 0;
		while (offset < packet.Length && num2++ < 16)
		{
			byte b = packet[offset++];
			if (b == 0)
			{
				break;
			}
			if ((b & 0xC0) == 192)
			{
				if (offset >= packet.Length)
				{
					break;
				}
				int num3 = ((b & 0x3F) << 8) | packet[offset++];
				if (!flag)
				{
					num = offset;
				}
				offset = num3;
				flag = true;
			}
			else
			{
				if (offset + b > packet.Length)
				{
					break;
				}
				list.Add(Encoding.UTF8.GetString(packet, offset, b));
				offset += b;
			}
		}
		if (flag)
		{
			offset = num;
		}
		return string.Join(".", list);
	}

	private static void WriteName(Stream stream, string name)
	{
		string[] array = name.TrimEnd('.').Split('.');
		foreach (string s in array)
		{
			byte[] bytes = Encoding.UTF8.GetBytes(s);
			stream.WriteByte((byte)bytes.Length);
			stream.Write(bytes);
		}
		stream.WriteByte(0);
	}

	private static void WriteUInt16(Stream stream, ushort value)
	{
		Span<byte> span = stackalloc byte[2];
		BinaryPrimitives.WriteUInt16BigEndian(span, value);
		stream.Write(span);
	}

	public void Dispose()
	{
		_cts.Cancel();
		_udpClient?.Dispose();
		try
		{
			_receiveTask?.Wait(TimeSpan.FromSeconds(1.0));
			_queryTask?.Wait(TimeSpan.FromSeconds(1.0));
		}
		catch
		{
		}
		_cts.Dispose();
	}
}
