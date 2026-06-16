using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MacMirrorReceiver.Networking;

// AirPlay 2 screen-mirroring audio (RAOP type=96) receiver. The Mac sends AAC-ELD frames over
// UDP RTP to the dataPort advertised in the SETUP response; a sibling controlPort carries
// timing/retransmit. The receiver opens the UDP sockets, parses RTP, FairPlay-unwraps the
// session AES key provided by the caller, and can optionally capture private local audio
// diagnostics when enabled in Settings or IMIRROR_DUMP_AUDIO is set.
internal sealed class AirPlayAudioReceiver : IDisposable
{
	private const int RtpHeaderLength = 12;
	private const long MaxDumpBytes = 256L * 1024L * 1024L;

	private readonly object _gate = new object();

	private CancellationTokenSource? _cts;
	private UdpClient? _dataClient;
	private UdpClient? _controlClient;
	private Task? _dataTask;
	private Task? _controlTask;
	private AudioDumpWriter? _dump;

	private byte[]? _aesKey;
	private byte[]? _eiv;
	private AirPlayAudioDecryptor? _decryptor;
	private AirPlayAudioStreamInfo _info = AirPlayAudioStreamInfo.Empty;
	private long _decryptedFrameCount;

	private long _packetCount;
	private long _statusWindowStartTick;
	private long _statusWindowBytes;
	private long _statusWindowPackets;
	private readonly Dictionary<int, long> _payloadTypeCounts = new Dictionary<int, long>();

	public int DataPort { get; private set; }

	public int ControlPort { get; private set; }

	// Raised for each decrypted AAC-ELD frame: (frame bytes, RTP timestamp, RTP sequence).
	// Redundant/duplicate packets are passed through as-is; de-duplication and jitter buffering
	// belong to the consumer.
	public event Action<byte[], uint, ushort>? AudioFrameReceived;

	public event Action<AirPlayAudioStreamInfo>? StreamStarted;

	public bool IsRunning
	{
		get
		{
			lock (_gate)
			{
				return _dataClient != null;
			}
		}
	}

	// Bind UDP sockets and start receiving. Restarts cleanly if a prior session was running.
	// Returns true once the data socket is bound (so the SETUP response can advertise the port).
	public bool Start(IPAddress? remoteAddress, int macControlPort, AirPlayAudioStreamInfo info, AirPlayAudioCrypto? crypto, bool dumpAudio = false)
	{
		StopInternal();

		byte[]? aesKey = crypto?.AesKey;
		byte[]? eiv = crypto?.Eiv;

		lock (_gate)
		{
			try
			{
				_cts = new CancellationTokenSource();
				_dataClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
				_controlClient = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
				DataPort = ((IPEndPoint)_dataClient.Client.LocalEndPoint!).Port;
				ControlPort = ((IPEndPoint)_controlClient.Client.LocalEndPoint!).Port;
				_info = info;
				_aesKey = aesKey == null ? null : CopyOf(aesKey);
				_eiv = eiv == null ? null : CopyOf(eiv);
				_decryptor = AirPlayAudioDecryptor.TryCreate(crypto?.AesKey, crypto?.SharedSecret, crypto?.Eiv);
				_packetCount = 0;
				_decryptedFrameCount = 0;
				_statusWindowStartTick = 0;
				_statusWindowBytes = 0;
				_statusWindowPackets = 0;
				_payloadTypeCounts.Clear();

				_dump = AudioDumpWriter.TryCreate(_info, crypto, macControlPort, DataPort, ControlPort, dumpAudio);

				CancellationToken token = _cts.Token;
				UdpClient data = _dataClient;
				UdpClient control = _controlClient;
				_dataTask = Task.Run(() => ReceiveDataLoopAsync(data, token));
				_controlTask = Task.Run(() => ReceiveControlLoopAsync(control, token));
			}
			catch (Exception ex)
			{
				AppLog.Write("AirPlay audio receiver failed to start: " + ex.Message);
				return false;
			}
		}

		AppLog.Write($"AirPlay audio receiver started: dataPort={DataPort}, controlPort={ControlPort}, macControlPort={macControlPort}, " +
			$"ct={info.CompressionType}, spf={info.SamplesPerFrame}, sr={info.SampleRate}, audioFormat=0x{info.AudioFormat:X8}, redundantAudio={info.RedundantAudio}, " +
			$"hasKey={(aesKey != null)}, hasEiv={(eiv != null)}, canDecrypt={(_decryptor != null)}, dump={(_dump != null)}.");
		StreamStarted?.Invoke(info);
		return true;
	}

	public void Stop()
	{
		StopInternal();
	}

	private void StopInternal()
	{
		CancellationTokenSource? cts;
		UdpClient? data;
		UdpClient? control;
		AudioDumpWriter? dump;
		AirPlayAudioDecryptor? decryptor;
		lock (_gate)
		{
			cts = _cts;
			data = _dataClient;
			control = _controlClient;
			dump = _dump;
			decryptor = _decryptor;
			_cts = null;
			_dataClient = null;
			_controlClient = null;
			_dump = null;
			_decryptor = null;
			_aesKey = null;
			_eiv = null;
		}

		try
		{
			cts?.Cancel();
		}
		catch
		{
		}

		data?.Dispose();
		control?.Dispose();
		dump?.Dispose();
		decryptor?.Dispose();
		cts?.Dispose();
	}

	private async Task ReceiveDataLoopAsync(UdpClient client, CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			try
			{
				UdpReceiveResult result = await client.ReceiveAsync(token);
				HandleDataPacket(result.Buffer);
			}
			catch (OperationCanceledException)
			{
				break;
			}
			catch (ObjectDisposedException)
			{
				break;
			}
			catch (SocketException ex)
			{
				AppLog.Write("AirPlay audio data socket error: " + ex.Message);
			}
			catch (Exception ex)
			{
				AppLog.Write("AirPlay audio data receive failed: " + ex.Message);
			}
		}
	}

	private async Task ReceiveControlLoopAsync(UdpClient client, CancellationToken token)
	{
		while (!token.IsCancellationRequested)
		{
			try
			{
				UdpReceiveResult result = await client.ReceiveAsync(token);
				HandleControlPacket(result.Buffer);
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
				AppLog.Write("AirPlay audio control receive failed: " + ex.Message);
			}
		}
	}

	private void HandleDataPacket(byte[] packet)
	{
		if (packet.Length < RtpHeaderLength)
		{
			return;
		}

		int version = (packet[0] >> 6) & 0x03;
		bool marker = (packet[1] & 0x80) != 0;
		int payloadType = packet[1] & 0x7F;
		int sequence = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2, 2));
		uint timestamp = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4, 4));
		uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(8, 4));
		int payloadLength = packet.Length - RtpHeaderLength;

		long count = Interlocked.Increment(ref _packetCount);
		_dump?.WriteRtpPacket(packet);

		lock (_gate)
		{
			_payloadTypeCounts[payloadType] = _payloadTypeCounts.TryGetValue(payloadType, out long existing) ? existing + 1 : 1;
		}

		if (count <= 10 || count % 500 == 0)
		{
			AppLog.Write($"AirPlay audio RTP #{count}: v={version}, m={(marker ? 1 : 0)}, pt={payloadType}, seq={sequence}, ts={timestamp}, ssrc=0x{ssrc:X8}, payload={payloadLength}, head={DescribeHead(packet, RtpHeaderLength, 16)}.");
		}

		// Only payloads that contain at least one AES block are real AAC-ELD audio frames; the small
		// (<16B) packets the Mac interleaves are keepalive/empty markers, not decodable audio.
		if (payloadLength >= 16)
		{
			AirPlayAudioDecryptor? decryptor;
			lock (_gate)
			{
				decryptor = _decryptor;
			}

			if (decryptor != null)
			{
				try
				{
					byte[] frame = decryptor.Decrypt(packet.AsSpan(RtpHeaderLength));
					long frameNumber = Interlocked.Increment(ref _decryptedFrameCount);
					_dump?.WriteClearFrame(frame);
					if (frameNumber == 1)
					{
						AppLog.Write($"AirPlay audio first AAC-ELD frame decrypted: {frame.Length} bytes (seq={sequence}, ts={timestamp}).");
					}
					AudioFrameReceived?.Invoke(frame, timestamp, (ushort)sequence);
				}
				catch (Exception ex)
				{
					AppLog.Write("AirPlay audio frame decrypt failed: " + ex.Message);
				}
			}
		}

		ReportThroughput(packet.Length);
	}

	private void HandleControlPacket(byte[] packet)
	{
		_dump?.WriteControlPacket(packet);
		long total = Interlocked.Read(ref _packetCount);
		if (total <= 4 && packet.Length >= 4)
		{
			int payloadType = packet.Length > 1 ? packet[1] & 0x7F : 0;
			AppLog.Write($"AirPlay audio control packet: pt={payloadType}, len={packet.Length}, head={DescribeHead(packet, 0, 16)}.");
		}
	}

	private void ReportThroughput(int bytes)
	{
		long now = System.Diagnostics.Stopwatch.GetTimestamp();
		string? summary = null;
		lock (_gate)
		{
			if (_statusWindowStartTick == 0)
			{
				_statusWindowStartTick = now;
			}
			_statusWindowBytes += bytes;
			_statusWindowPackets++;
			double seconds = (now - _statusWindowStartTick) / (double)System.Diagnostics.Stopwatch.Frequency;
			if (seconds >= 5.0)
			{
				summary = $"AirPlay audio RTP rate: {_statusWindowPackets / seconds:N1} pkt/s, {_statusWindowBytes / seconds / 1024.0:N1} KiB/s over {seconds:N1}s, " +
					$"payloadTypes=[{string.Join(",", DescribePayloadTypes())}].";
				_statusWindowStartTick = now;
				_statusWindowBytes = 0;
				_statusWindowPackets = 0;
			}
		}

		if (summary != null)
		{
			AppLog.Write(summary);
		}
	}

	private IEnumerable<string> DescribePayloadTypes()
	{
		List<string> items = new List<string>();
		foreach (KeyValuePair<int, long> entry in _payloadTypeCounts)
		{
			items.Add(entry.Key.ToString(CultureInfo.InvariantCulture) + ":" + entry.Value.ToString(CultureInfo.InvariantCulture));
		}
		return items;
	}

	private static string DescribeHead(byte[] payload, int offset, int count)
	{
		if (offset >= payload.Length)
		{
			return "(empty)";
		}
		int n = Math.Min(count, payload.Length - offset);
		return BitConverter.ToString(payload, offset, n);
	}

	public void Dispose()
	{
		StopInternal();
	}

	private static byte[] CopyOf(byte[] source)
	{
		byte[] copy = new byte[source.Length];
		Buffer.BlockCopy(source, 0, copy, 0, source.Length);
		return copy;
	}

	// Private local capture gated by Settings or IMIRROR_DUMP_AUDIO. Writes a length-framed raw RTP stream
	// (every packet, header included) plus a sidecar meta.json that records the FairPlay-unwrapped
	// AES key, eiv and stream parameters for offline diagnosis. These files contain key material
	// and audio; they are only written when explicitly enabled.
	private sealed class AudioDumpWriter : IDisposable
	{
		private readonly object _gate = new object();
		private FileStream? _rtpStream;
		private FileStream? _controlStream;
		private FileStream? _clearStream;
		private long _rtpBytes;
		private long _clearBytes;
		private bool _capLogged;

		private AudioDumpWriter(FileStream rtpStream, FileStream controlStream, FileStream clearStream)
		{
			_rtpStream = rtpStream;
			_controlStream = controlStream;
			_clearStream = clearStream;
		}

		public static AudioDumpWriter? TryCreate(
			AirPlayAudioStreamInfo info,
			AirPlayAudioCrypto? crypto,
			int macControlPort,
			int dataPort,
			int controlPort,
			bool dumpAudio = false)
		{
			byte[]? aesKey = crypto?.AesKey;
			byte[]? eiv = crypto?.Eiv;
			string? setting = Environment.GetEnvironmentVariable("IMIRROR_DUMP_AUDIO");
			bool envEnabled = !string.IsNullOrWhiteSpace(setting);
			if (!dumpAudio && !envEnabled)
			{
				return null;
			}

			try
			{
				string directory;
				string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
				if (string.IsNullOrWhiteSpace(setting) || setting == "1" || string.Equals(setting, "true", StringComparison.OrdinalIgnoreCase))
				{
					directory = AppContext.BaseDirectory;
				}
				else
				{
					directory = setting;
					Directory.CreateDirectory(directory);
				}

				string basePath = Path.Combine(directory, "imirror-audio-" + stamp);
				FileStream rtpStream = new FileStream(basePath + ".rtp", FileMode.Create, FileAccess.Write, FileShare.Read);
				FileStream controlStream = new FileStream(basePath + ".control", FileMode.Create, FileAccess.Write, FileShare.Read);
				FileStream clearStream = new FileStream(basePath + ".aacframes", FileMode.Create, FileAccess.Write, FileShare.Read);

				Dictionary<string, object?> meta = new Dictionary<string, object?>
				{
					["createdLocal"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
					["compressionType"] = info.CompressionType,
					["samplesPerFrame"] = info.SamplesPerFrame,
					["sampleRate"] = info.SampleRate,
					["channels"] = info.Channels,
					["audioFormat"] = info.AudioFormat,
					["audioFormatHex"] = "0x" + info.AudioFormat.ToString("X8", CultureInfo.InvariantCulture),
					["redundantAudio"] = info.RedundantAudio,
					["macControlPort"] = macControlPort,
					["dataPort"] = dataPort,
					["controlPort"] = controlPort,
					["aesKeyHex"] = aesKey == null ? null : Convert.ToHexString(aesKey),
					["aesKeyLength"] = aesKey?.Length,
					["eivHex"] = eiv == null ? null : Convert.ToHexString(eiv),
					["sharedSecretHex"] = crypto?.SharedSecret == null ? null : Convert.ToHexString(crypto.SharedSecret),
					["encryptedEkeyHex"] = crypto?.EncryptedEkey == null ? null : Convert.ToHexString(crypto.EncryptedEkey),
					["rtspTargetSessionId"] = crypto?.RtspTargetSessionId?.ToString(CultureInfo.InvariantCulture),
					["rtpFormat"] = "repeated [uint32 LE length][packet bytes], packet includes 12-byte RTP header",
					["aacFramesFormat"] = "repeated [uint32 LE length][decrypted AAC-ELD frame bytes]"
				};
				File.WriteAllText(basePath + ".meta.json", JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));

				AppLog.Write("AirPlay audio dump enabled: " + basePath + ".rtp (+ .control, .aacframes, .meta.json).");
				return new AudioDumpWriter(rtpStream, controlStream, clearStream);
			}
			catch (Exception ex)
			{
				AppLog.Write("AirPlay audio dump could not be opened: " + ex.Message);
				return null;
			}
		}

		public void WriteRtpPacket(byte[] packet)
		{
			lock (_gate)
			{
				if (_rtpStream == null)
				{
					return;
				}
				if (_rtpBytes + packet.Length + 4 > MaxDumpBytes)
				{
					if (!_capLogged)
					{
						_capLogged = true;
						AppLog.Write($"AirPlay audio dump reached {MaxDumpBytes / (1024 * 1024)}MB cap; further packets are not captured.");
					}
					return;
				}
				try
				{
					Span<byte> lengthPrefix = stackalloc byte[4];
					BinaryPrimitives.WriteUInt32LittleEndian(lengthPrefix, (uint)packet.Length);
					_rtpStream.Write(lengthPrefix);
					_rtpStream.Write(packet, 0, packet.Length);
					_rtpStream.Flush();
					_rtpBytes += packet.Length + 4;
				}
				catch
				{
				}
			}
		}

		public void WriteClearFrame(byte[] frame)
		{
			lock (_gate)
			{
				if (_clearStream == null)
				{
					return;
				}
				if (_clearBytes + frame.Length + 4 > MaxDumpBytes)
				{
					return;
				}
				try
				{
					Span<byte> lengthPrefix = stackalloc byte[4];
					BinaryPrimitives.WriteUInt32LittleEndian(lengthPrefix, (uint)frame.Length);
					_clearStream.Write(lengthPrefix);
					_clearStream.Write(frame, 0, frame.Length);
					_clearStream.Flush();
					_clearBytes += frame.Length + 4;
				}
				catch
				{
				}
			}
		}

		public void WriteControlPacket(byte[] packet)
		{
			lock (_gate)
			{
				if (_controlStream == null)
				{
					return;
				}
				try
				{
					Span<byte> lengthPrefix = stackalloc byte[4];
					BinaryPrimitives.WriteUInt32LittleEndian(lengthPrefix, (uint)packet.Length);
					_controlStream.Write(lengthPrefix);
					_controlStream.Write(packet, 0, packet.Length);
					_controlStream.Flush();
				}
				catch
				{
				}
			}
		}

		public void Dispose()
		{
			lock (_gate)
			{
				try
				{
					_rtpStream?.Dispose();
				}
				catch
				{
				}
				try
				{
					_controlStream?.Dispose();
				}
				catch
				{
				}
				try
				{
					_clearStream?.Dispose();
				}
				catch
				{
				}
				_rtpStream = null;
				_controlStream = null;
				_clearStream = null;
			}
		}
	}
}

internal readonly record struct AirPlayAudioStreamInfo(
	int CompressionType,
	int SamplesPerFrame,
	int SampleRate,
	int Channels,
	long AudioFormat,
	int RedundantAudio)
{
	public static readonly AirPlayAudioStreamInfo Empty = new AirPlayAudioStreamInfo(0, 0, 44100, 2, 0, 0);
}

// Crypto material for the audio stream. AesKey/Eiv are the raw FairPlay-unwrapped session key and
// IV. SharedSecret/EncryptedEkey/RtspTargetSessionId are extra derivation inputs captured for the
// confirmed audio key derivation. The type=96 stream carries no streamConnectionID, so the
// receiver uses SHA512(fairPlayAesKey[0:16] || sharedSecret)[0:16] directly as the CBC key.
internal sealed record AirPlayAudioCrypto(
	byte[] AesKey,
	byte[]? Eiv,
	byte[]? SharedSecret,
	byte[]? EncryptedEkey,
	ulong? RtspTargetSessionId);
