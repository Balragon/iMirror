using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MacMirrorReceiver;
using MacMirrorReceiver.Protocol;
using MacMirrorReceiver.Video;

namespace MacMirrorReceiver.Networking;

public sealed class AirPlayProbeService : IDisposable
{
	private const string AirPlayServiceType = "_airplay._tcp.local";
	private const string RaopServiceType = "_raop._tcp.local";
	private const int MdnsPort = 5353;
	private const int AirPlayPort = 7000;
	private const int RaopPort = 5000;
	private const int DataPort = 7100;
	private const int EventPort = 7101;
	private const int TimingPort = 7102;
	private const int MaxH264PrefixProbeBytes = 64;
	private const int MaxH264NalUnitLength = 8 * 1024 * 1024;
	private const int StableDisplayWidth = 1920;
	private const int StableDisplayHeight = 1080;
	// GPU (MediaFoundation/D3D11) path advertised geometry. Set to the Mac's native 2560x1440 so the
	// sender does not rescale its desktop and the receiver decodes the native stream.
	private const int GpuDisplayWidth = 2560;
	private const int GpuDisplayHeight = 1440;
	private const int StableDisplayFps = 60;
	private const int GpuDisplayFps = 30;
	private const string LegacyAirTunesVersion = "220.68";
	private const string LegacyAirPlayModel = "AppleTV2,1";
	private const string LegacyAirPlayFeatures = "0x5A7FFEE6";
	private const string LegacyRaopPublicKey = "b07727d6f6cd6e08b58ede525ec3cdeaa252ad9f683feb212ef8a205246554e7";

	// Product default: advertise audio now that the receiver has a decode/output pipeline. Failures in
	// the downstream audio path fall back to silence while video keeps running.
	private const bool AdvertiseAudioCapabilitiesDefault = true;

	// Optional verbose discovery diagnostics. Set IMIRROR_AUDIO_DISCOVERY=1 to log non-video data-stream
	// packets and full SETUP plists while investigating sender behavior.
	// Diagnostic-only: verbose AirPlay audio/data-stream logging, never audio behavior.
	private static readonly bool AudioDiscoveryLogging = string.Equals(
		Environment.GetEnvironmentVariable("IMIRROR_AUDIO_DISCOVERY"), "1", StringComparison.OrdinalIgnoreCase);

	private static readonly IPAddress MulticastAddress = IPAddress.Parse("224.0.0.251");
	private static readonly IPEndPoint MulticastEndpoint = new IPEndPoint(MulticastAddress, MdnsPort);
	private static readonly bool QualityRenderMode = RenderModeSettings.Load().EffectiveMode == ReceiverRenderModeSetting.Quality;
#if HIGH_RESOLUTION_D3D
	// GPU engine advertises native 2560x1440 by default so the Mac sends native and the GPU decodes
	// it. The quality flag can still force the path on for local validation.
	private static readonly bool GpuDisplayAvailable = (QualityRenderMode && RenderModeSettings.ExperimentalQualityEnabled)
		|| RenderModeSettings.GpuVideoEngineEnabled;
#else
	private static readonly bool GpuDisplayAvailable = false;
#endif

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();
	private readonly string _displayName;
	private readonly string _hostName;
	private readonly string _airPlayInstanceName;
	private readonly string _raopInstanceName;
	private readonly string _deviceId;
	private readonly string _pairingId;
	private readonly bool _audioAdvertised;
	private readonly bool _writeDiagnostics;
	private readonly bool _dumpAudio;
	private readonly AirPlayPairingContext _pairing = new AirPlayPairingContext();
	private readonly AirPlayFairPlayContext _fairPlay = new AirPlayFairPlayContext();
	private readonly AirPlayTimingClient _timingClient = new AirPlayTimingClient(TimingPort);
	private readonly AirPlayAudioReceiver _audioReceiver = new AirPlayAudioReceiver();
	private readonly AirPlaySetupContext _setup;
	private readonly object _mirrorKeyGate = new object();

	private UdpClient? _mdnsClient;
	private bool _mdnsBound;
	private TcpListener? _airPlayListener;
	private TcpListener? _raopListener;
	private string? _airPlayListenerBindError;
	private string? _raopListenerBindError;
	private TcpListener? _dataListener;
	private TcpListener? _eventListener;
	private TcpListener? _timingListener;

	// Expose bind results for startup diagnostics — read after StartAsync().
	// _mdnsClient is assigned before Bind(), so a bind failure (e.g. port 5353
	// already held by Bonjour/iTunes) would leave it non-null; track success with
	// a dedicated flag set only after the multicast join completes.
	public bool IsMdnsBound => _mdnsBound;
	public bool IsAirPlayListenerBound => _airPlayListener != null;
	public bool IsRaopListenerBound => _raopListener != null;
	public int AirPlayListenerPort => AirPlayPort;
	public int RaopListenerPort => RaopPort;
	public string? AirPlayListenerBindError => _airPlayListenerBindError;
	public string? RaopListenerBindError => _raopListenerBindError;

	private Task? _receiveTask;
	private Task? _announceTask;
	private Task? _airPlayAcceptTask;
	private Task? _raopAcceptTask;
	private Task? _dataAcceptTask;
	private Task? _eventAcceptTask;
	private Task? _timingAcceptTask;
	private byte[]? _encryptedMirrorAesKey;
	private byte[]? _mirrorFairPlayAesKey;
	private byte[]? _mirrorSharedSecret;
	private byte[]? _mirrorEiv;
	private ulong? _streamConnectionId;
	private ulong? _rtspTargetSessionId;
	private ulong? _decryptorStreamConnectionId;
	private ulong? _announcedMirrorRtspTargetSessionId;
	private ulong? _announcedMirrorStreamConnectionId;

	// Last-connected-sender arbitration: the receiver has a single mirror decode pipeline, so two
	// senders streaming at once interleave and starve the gate (decrypt keyed to one stream, one
	// gate, one decoder). Track the active data connection and a monotonically increasing
	// generation; a newer data connection supersedes older ones so only the latest sender feeds it.
	private int _activeDataGeneration;
	private TcpClient? _activeDataClient;
	private bool _announcedIdentitylessMirrorSession;
	private AirPlayMirrorDecryptor? _mirrorDecryptor;
	private List<AirPlayMirrorDecryptor>? _mirrorDecryptorProbePool;
	private bool _loggedWaitingForMirrorDecryptor;
	private readonly HashSet<int> _loggedDecryptPayloadFailureTypes = new HashSet<int>();
	private readonly HashSet<int> _loggedDecryptCandidateReportTypes = new HashSet<int>();
	private readonly HashSet<int> _loggedSkippedPacketTypes = new HashSet<int>();

	private readonly Dictionary<int, long> _audioDiscoveryPacketCounts = new Dictionary<int, long>();
	private bool _loggedDecryptorInUse;
	// Instrumentation for the decryptor warm-up keyframe-loss race. A mirror stream sends a single IDR
	// at session start, so if it arrives while the FairPlay stream decryptor is still being selected it
	// cannot be decrypted and is dropped, and the decoder then never receives a keyframe. These track,
	// per warm-up, how many video packets were dropped before the first decodable frame and whether that
	// first surviving frame was actually a keyframe.
	private bool _loggedFirstDecodableVideoFrame;
	private long _droppedVideoBeforeFirstFrame;
	private long _firstEncryptedWaitTick;
	private bool _mirrorDiagnosticWritten;
	private long _lastVideoDataStatusTick;

	public AirPlayProbeService(
		string displayName,
		bool advertiseAudio = AdvertiseAudioCapabilitiesDefault,
		bool writeDiagnostics = false,
		bool dumpAudio = false)
	{
		_displayName = SanitizeLabel(displayName);
		_hostName = SanitizeLabel(Environment.MachineName) + ".local";
		_deviceId = ResolveDeviceId();
		_pairingId = Guid.NewGuid().ToString("D");
		_airPlayInstanceName = _displayName + "." + AirPlayServiceType;
		_raopInstanceName = _deviceId.Replace(":", "", StringComparison.Ordinal) + "@" + _displayName + "." + RaopServiceType;
		_audioAdvertised = advertiseAudio;
		_writeDiagnostics = writeDiagnostics;
		_dumpAudio = dumpAudio;
		_audioReceiver.StreamStarted += info => AudioStreamStarted?.Invoke(info.SampleRate, info.Channels, info.SamplesPerFrame);
		_audioReceiver.AudioFrameReceived += (frame, timestamp, sequence) => AudioFrameReceived?.Invoke(frame, timestamp, sequence);
		_setup = new AirPlaySetupContext(_timingClient, _audioReceiver, TryGetAudioCrypto, _dumpAudio);
		if (AudioDiscoveryLogging)
		{
			AppLog.Write("IMIRROR_AUDIO_DISCOVERY enabled: verbose data-stream logging only.");
		}
	}

	// Provides the FairPlay-unwrapped session AES key + eiv to the audio receiver. The audio
	// realtime stream (type=96) reuses the same et=32 session ekey/eiv the video path already
	// captures in ObserveSetupRequest. The receiver combines the first 16 bytes of that key with
	// the pair-verify shared secret for the confirmed AAC-ELD RTP CBC key derivation.
	private AirPlayAudioCrypto? TryGetAudioCrypto()
	{
		byte[]? encryptedKey;
		byte[]? eiv;
		ulong? rtspTargetSessionId;
		lock (_mirrorKeyGate)
		{
			encryptedKey = _encryptedMirrorAesKey == null ? null : CopyOf(_encryptedMirrorAesKey);
			eiv = _mirrorEiv == null ? null : CopyOf(_mirrorEiv);
			rtspTargetSessionId = _rtspTargetSessionId;
		}

		if (encryptedKey == null)
		{
			return null;
		}

		byte[]? aesKey = _fairPlay.TryDecryptAesKey(encryptedKey);
		if (aesKey == null)
		{
			return null;
		}

		byte[]? sharedSecret = _pairing.TryGetSharedSecret();
		return new AirPlayAudioCrypto(aesKey, eiv, sharedSecret, encryptedKey, rtspTargetSessionId);
	}

	public event Action<string>? StatusChanged;

	public event Action? MirrorSessionStarted;

	public event Action? MirrorSessionEnded;

	public event Action<StreamConfig>? StreamConfigReceived;

	public event Action<byte[], ulong, long>? VideoPayloadReceived;

	public event Action<int, int, int>? AudioStreamStarted;

	public event Action<byte[], uint, ushort>? AudioFrameReceived;

	public string StatusText { get; private set; } = "AirPlay receiver not started.";

	public async Task StartAsync()
	{
		if (_mdnsClient != null)
		{
			return;
		}

		try
		{
			_mdnsClient = new UdpClient(AddressFamily.InterNetwork);
			_mdnsClient.Client.ExclusiveAddressUse = false;
			_mdnsClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, optionValue: true);
			_mdnsClient.Client.Bind(new IPEndPoint(IPAddress.Any, MdnsPort));
			_mdnsClient.JoinMulticastGroup(MulticastAddress);
			_mdnsBound = true;

			_airPlayListener = TryStartListener(AirPlayPort, "AirPlay");
			_raopListener = TryStartListener(RaopPort, "RAOP");
			_dataListener = TryStartListener(DataPort, "AirPlay data");
			_eventListener = TryStartListener(EventPort, "AirPlay event");
			_timingListener = TryStartListener(TimingPort, "AirPlay timing");

			_receiveTask = Task.Run(ReceiveLoopAsync);
			_announceTask = Task.Run(AnnounceLoopAsync);
			_airPlayAcceptTask = StartAcceptLoop(_airPlayListener, "AirPlay");
			_raopAcceptTask = StartAcceptLoop(_raopListener, "RAOP");
			_dataAcceptTask = StartAcceptLoop(_dataListener, "AirPlay data");
			_eventAcceptTask = StartAcceptLoop(_eventListener, "AirPlay event");
			_timingAcceptTask = StartAcceptLoop(_timingListener, "AirPlay timing");

			await SendAnnouncementAsync(_cts.Token);
			SetStatus($"AirPlay receiver advertising as \"{_displayName}\".");
		}
		catch (Exception ex)
		{
			SetStatus("AirPlay receiver unavailable: " + ex.Message);
			AppLog.Write("AirPlay receiver failed to start: " + ex);
		}
	}

	private TcpListener? TryStartListener(int port, string label)
	{
		try
		{
			var listener = new TcpListener(IPAddress.Any, port);
			listener.Start();
			SetListenerBindError(port, null);
			AppLog.Write($"{label} receiver listener started on port {port}.");
			return listener;
		}
		catch (Exception ex)
		{
			SetListenerBindError(port, ex.Message);
			AppLog.Write($"{label} receiver listener unavailable on port {port}: {ex.Message}");
			return null;
		}
	}

	private void SetListenerBindError(int port, string? error)
	{
		switch (port)
		{
		case AirPlayPort:
			_airPlayListenerBindError = error;
			break;
		case RaopPort:
			_raopListenerBindError = error;
			break;
		}
	}

	private Task? StartAcceptLoop(TcpListener? listener, string label)
	{
		return listener == null ? null : Task.Run(() => AcceptLoopAsync(listener, label));
	}

	private async Task AcceptLoopAsync(TcpListener listener, string label)
	{
		while (!_cts.IsCancellationRequested)
		{
			try
			{
				TcpClient client = await listener.AcceptTcpClientAsync(_cts.Token);
				_ = Task.Run(() => HandleProbeClientAsync(client, label));
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
				AppLog.Write($"{label} receiver accept failed: {ex.Message}");
			}
		}
	}

	private async Task HandleProbeClientAsync(TcpClient client, string label)
	{
		using (client)
		{
			try
			{
				if (label.Contains("data", StringComparison.OrdinalIgnoreCase))
				{
					await HandleDataClientAsync(client);
					return;
				}

				client.ReceiveTimeout = 2500;
				client.SendTimeout = 2500;
				using NetworkStream stream = client.GetStream();
				// The RTSP control connection must stay open for the whole mirroring session:
				// the sender keeps it alive with POST /feedback every ~2s and treats a close as
				// session teardown. (An earlier per-connection request cap of 16 made the
				// receiver close this connection mid-session, which silently ended the mirror
				// stream after a few seconds of video.)
				int servedRequests = 0;
				while (!_cts.IsCancellationRequested)
				{
					AirPlayRequest? request = await ReadRequestAsync(stream, _cts.Token);
					if (request == null)
					{
						AppLog.Write($"{label} control connection closed by peer ({client.Client.RemoteEndPoint}) after {servedRequests} request(s).");
						if (IsMirrorControlLabel(label))
						{
							EndMirrorSessionIfAnnounced($"{label} control connection closed by peer");
						}
						break;
					}

					servedRequests++;
					AppLog.Write($"{label} request from {client.Client.RemoteEndPoint}: {request.FirstLine}");
					SetStatus($"{label}: {request.Method} {request.Target}");

					byte[] response = BuildResponse(request, client.Client.RemoteEndPoint);
					await stream.WriteAsync(response, _cts.Token);
					if (request.Method == "TEARDOWN" && IsMirrorControlLabel(label))
					{
						EndMirrorSessionIfAnnounced("TEARDOWN");
						break;
					}
					if (request.Headers.TryGetValue("Connection", out string? connection) &&
						connection.Contains("close", StringComparison.OrdinalIgnoreCase))
					{
						AppLog.Write($"{label} control connection closed on request (Connection: close) after {servedRequests} request(s).");
						if (IsMirrorControlLabel(label))
						{
							EndMirrorSessionIfAnnounced($"{label} control connection close requested");
						}
						break;
					}
				}
			}
			catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
			{
				AppLog.Write($"{label} receiver client closed: {ex.Message}");
				if (IsMirrorControlLabel(label))
				{
					EndMirrorSessionIfAnnounced($"{label} receiver client closed");
				}
			}
		}
	}

	internal static bool IsMirrorControlLabel(string label)
	{
		return string.Equals(label, "AirPlay", StringComparison.OrdinalIgnoreCase);
	}

	private async Task HandleDataClientAsync(TcpClient client)
	{
		SetStatus("AirPlay data stream connected.");
		EndPoint? remote = client.Client.RemoteEndPoint;
		AppLog.Write("AirPlay data stream connected from " + remote);

		// Supersede any previous sender's data connection so only the newest one feeds the pipeline.
		// The pipeline itself is reset for the new session by the SETUP path (MirrorSessionStarted).
		int myDataGeneration;
		TcpClient? superseded;
		lock (_mirrorKeyGate)
		{
			myDataGeneration = ++_activeDataGeneration;
			superseded = _activeDataClient;
			_activeDataClient = client;
		}
		if (superseded != null && !ReferenceEquals(superseded, client))
		{
			AppLog.Write("AirPlay: newer sender connected; closing the previous data connection.");
			try { superseded.Close(); } catch { }
		}

		using NetworkStream stream = client.GetStream();
		byte[] buffer = new byte[64 * 1024];
		List<byte> pending = new List<byte>();
		bool loggedUnknown = false;
		long totalBytes = 0;
		long startTick = Stopwatch.GetTimestamp();

		while (!_cts.IsCancellationRequested)
		{
			if (Volatile.Read(ref _activeDataGeneration) != myDataGeneration)
			{
				AppLog.Write($"AirPlay data stream from {remote} superseded by a newer sender; stopping.");
				break;
			}

			int count = await stream.ReadAsync(buffer, _cts.Token);
			if (count <= 0)
			{
				double seconds = (Stopwatch.GetTimestamp() - startTick) / (double)Stopwatch.Frequency;
				AppLog.Write($"AirPlay data stream closed by sender (EOF) from {remote}: totalBytes={totalBytes:N0}, duration={seconds:N1}s, pendingTail={pending.Count}.");
				SetStatus("AirPlay data stream ended (sender closed connection).");
				break;
			}
			totalBytes += count;

			for (int i = 0; i < count; i++)
			{
				pending.Add(buffer[i]);
			}

			int emitted = EmitMirrorPackets(pending);
			if (emitted > 0)
			{
				ReportVideoDataReceived(emitted);
			}
			else if (!loggedUnknown && pending.Count >= 32)
			{
				loggedUnknown = true;
				AppLog.Write($"AirPlay data did not look like clear H.264 yet: pendingBytes={pending.Count:N0}.");
				SetStatus("AirPlay data arrived, but payload is not clear H.264 yet.");
			}

			if (pending.Count > 1024 * 1024)
			{
				pending.RemoveRange(0, pending.Count - 128 * 1024);
			}
		}

		lock (_mirrorKeyGate)
		{
			if (ReferenceEquals(_activeDataClient, client))
			{
				_activeDataClient = null;
			}
		}
	}

	private int EmitMirrorPackets(List<byte> pending)
	{
		int emitted = 0;
		while (pending.Count >= 128)
		{
			byte[] header = pending.Take(128).ToArray();
			int payloadSize = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(0, 4));
			int payloadType = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2)) & 0xFF;
			int payloadOption = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6, 2));
			if (payloadSize < 0 || payloadSize > 8 * 1024 * 1024)
			{
				AppLog.Write($"AirPlay mirror packet header invalid: size={payloadSize}, type={payloadType}, option={payloadOption}.");
				pending.RemoveAt(0);
				continue;
			}

			if (pending.Count < 128 + payloadSize)
			{
				return emitted;
			}

			byte[] payload = pending.Skip(128).Take(payloadSize).ToArray();
			pending.RemoveRange(0, 128 + payloadSize);

			if (payloadType == 1)
			{
				if (TryBuildSpsPpsPayload(header, payload, out byte[] spsPps, out StreamConfig streamConfig))
				{
					StreamConfigReceived?.Invoke(streamConfig);
					VideoPayloadReceived?.Invoke(spsPps, 0, Stopwatch.GetTimestamp());
					emitted++;
				}
				continue;
			}

			if (payloadType == 0)
			{
				byte[] videoPayload = payload;
				if (TryDecryptMirrorPayload(payload, out byte[] decryptedPayload))
				{
					videoPayload = decryptedPayload;
				}
				else if (!_loggedWaitingForMirrorDecryptor)
				{
					_loggedWaitingForMirrorDecryptor = true;
					// Start of a decryptor warm-up window: reset the keyframe-loss instrumentation so it
					// reports once per session (this branch re-fires after each session resets the flag).
					_firstEncryptedWaitTick = Stopwatch.GetTimestamp();
					_loggedFirstDecodableVideoFrame = false;
					_droppedVideoBeforeFirstFrame = 0;
					AppLog.Write("AirPlay mirror video payload is encrypted; waiting for FairPlay stream decryptor.");
				}

					if (TryNormalizeClearH264Payload(videoPayload, out byte[] annexB, out string clearFormat))
					{
						RecordVideoPayloadShape(annexB);
						VideoPayloadReceived?.Invoke(annexB, 0, Stopwatch.GetTimestamp());
						emitted++;
					}
						else if (TryProbeMirrorDecryptors(payloadType, header, payload, out annexB, out string selectedName, out string selectedFormat))
						{
							RecordVideoPayloadShape(annexB);
							VideoPayloadReceived?.Invoke(annexB, 0, Stopwatch.GetTimestamp());
							AppLog.Write($"AirPlay mirror video type={payloadType} decrypted with stateful candidate '{selectedName}' ({selectedFormat}).");
							emitted++;
						}
						else if (TrySelectMirrorDecryptor(payloadType, header, payload, out annexB, out selectedName, out selectedFormat))
						{
							RecordVideoPayloadShape(annexB);
							VideoPayloadReceived?.Invoke(annexB, 0, Stopwatch.GetTimestamp());
							AppLog.Write($"AirPlay mirror video type={payloadType} decrypted with candidate '{selectedName}' ({selectedFormat}).");
						emitted++;
					}
					else
					{
						// This type=0 video packet could not be decrypted/normalized and is dropped. Count
						// drops before the first decodable frame to quantify keyframe loss during warm-up.
						if (!_loggedFirstDecodableVideoFrame)
						{
							_droppedVideoBeforeFirstFrame++;
						}
						if (ShouldLogDecryptPayloadFailure(payloadType))
						{
							AppLog.Write($"AirPlay mirror video type={payloadType} payload could not be converted after decrypt attempt: size={payloadSize}, probe={DescribeH264Probe(videoPayload)}.");
						}
					}
				continue;
			}

			if (AudioDiscoveryLogging)
			{
				long discoveryCount = _audioDiscoveryPacketCounts.TryGetValue(payloadType, out long existing) ? existing + 1 : 1;
				_audioDiscoveryPacketCounts[payloadType] = discoveryCount;
				// Log the first few of each non-video type (with head bytes to reveal RTP/ADTS/codec
				// structure) and then periodically, to identify the audio stream on the TCP data path.
				if (discoveryCount <= 5 || discoveryCount % 250 == 0)
				{
					AppLog.Write($"AUDIO-DISCOVERY data-stream packet: type={payloadType}, option={payloadOption}, size={payloadSize}, count={discoveryCount}, head={DescribeHeadBytes(payload, 24)}");
				}
				continue;
			}

			if (_loggedSkippedPacketTypes.Add(payloadType))
			{
				AppLog.Write($"AirPlay mirror packet skipped: type={payloadType}, option={payloadOption}, size={payloadSize}");
			}
		}

		return emitted;
	}

	private static string DescribeHeadBytes(byte[] payload, int count)
	{
		if (payload == null || payload.Length == 0)
		{
			return "(empty)";
		}
		int n = Math.Min(count, payload.Length);
		return BitConverter.ToString(payload, 0, n);
	}

	// Compact recursive description of a parsed plist (for audio-discovery logging). Byte arrays are
	// shown as length + a short hex prefix so keys like ekey/eiv are visible without huge dumps.
	private static string DescribePlistObject(object? value, int depth)
	{
		if (depth > 6)
		{
			return "...";
		}
		switch (value)
		{
			case null:
				return "null";
			case byte[] bytes:
				return $"bytes[{bytes.Length}]{(bytes.Length > 0 ? " " + BitConverter.ToString(bytes, 0, Math.Min(16, bytes.Length)) : string.Empty)}";
			case Dictionary<string, object?> dict:
				return "{" + string.Join(", ", dict.Select(kv => kv.Key + "=" + DescribePlistObject(kv.Value, depth + 1))) + "}";
			case System.Collections.IEnumerable enumerable when value is not string:
				return "[" + string.Join(", ", enumerable.Cast<object?>().Select(item => DescribePlistObject(item, depth + 1))) + "]";
			default:
				return value.ToString() ?? "?";
		}
	}

	private bool TryDecryptMirrorPayload(byte[] payload, out byte[] decryptedPayload)
	{
		lock (_mirrorKeyGate)
		{
			if (_mirrorDecryptor == null)
			{
				decryptedPayload = Array.Empty<byte>();
				return false;
			}

			decryptedPayload = _mirrorDecryptor.Decrypt(payload);
			if (!_loggedDecryptorInUse)
			{
				_loggedDecryptorInUse = true;
				AppLog.Write($"AirPlay mirror decryptor in use: {_mirrorDecryptor.Name}.");
			}
			return true;
		}
	}

	private bool ShouldLogDecryptPayloadFailure(int payloadType)
	{
		lock (_mirrorKeyGate)
		{
			return _loggedDecryptPayloadFailureTypes.Add(payloadType);
		}
	}

	private bool TryProbeMirrorDecryptors(int payloadType, byte[] packetHeader, byte[] encryptedPayload, out byte[] annexB, out string selectedName, out string selectedFormat)
	{
		annexB = Array.Empty<byte>();
		selectedName = string.Empty;
		selectedFormat = string.Empty;

		byte[]? encryptedKey = null;
		byte[]? aesKey = null;
		byte[]? sharedSecret = null;
		byte[]? eiv = null;
		ulong? streamConnectionId = null;
		ulong? rtspTargetSessionId = null;
		List<string> report = new List<string>();
		int tested = 0;
		int baseCandidateCount = 0;
		bool shouldWriteDiagnostic = false;

		lock (_mirrorKeyGate)
		{
			if (_mirrorDecryptorProbePool == null || _mirrorDecryptorProbePool.Count == 0)
			{
				return false;
			}

			baseCandidateCount = _mirrorDecryptorProbePool.Count;
			foreach (AirPlayMirrorDecryptor decryptor in _mirrorDecryptorProbePool)
			{
				tested++;
				byte[] preview = decryptor.Decrypt(encryptedPayload);
				if (TryNormalizeClearH264Payload(preview, out byte[] candidateAnnexB, out string candidateFormat))
				{
					annexB = candidateAnnexB;
					selectedName = decryptor.Name;
					selectedFormat = candidateFormat;

					foreach (AirPlayMirrorDecryptor other in _mirrorDecryptorProbePool)
					{
						if (!ReferenceEquals(other, decryptor))
						{
							other.Dispose();
						}
					}

					_mirrorDecryptorProbePool = null;
					_mirrorDecryptor?.Dispose();
					_mirrorDecryptor = decryptor;
					_decryptorStreamConnectionId = _streamConnectionId;
					_loggedDecryptPayloadFailureTypes.Remove(payloadType);
					_loggedDecryptCandidateReportTypes.Add(payloadType);
					AppLog.Write($"AirPlay mirror decryptor stateful auto-selected '{decryptor.Name}' for type={payloadType}, streamConnectionID={_streamConnectionId}, format={candidateFormat}.");
					return true;
				}

				if (report.Count < 32)
				{
					report.Add(decryptor.Name + "/" + DescribeH264Probe(preview));
				}
			}

			if (_loggedDecryptCandidateReportTypes.Add(payloadType))
			{
				AppLog.Write($"AirPlay mirror stateful decryptor probes did not produce clear H.264 for type={payloadType}. tested={tested}, probes=" + string.Join(", ", report));
				shouldWriteDiagnostic = true;
			}

			encryptedKey = _encryptedMirrorAesKey == null ? null : CopyOf(_encryptedMirrorAesKey);
			aesKey = _mirrorFairPlayAesKey == null ? null : CopyOf(_mirrorFairPlayAesKey);
			sharedSecret = _mirrorSharedSecret == null ? null : CopyOf(_mirrorSharedSecret);
			eiv = _mirrorEiv == null ? null : CopyOf(_mirrorEiv);
			streamConnectionId = _streamConnectionId;
			rtspTargetSessionId = _rtspTargetSessionId;
		}

		if (shouldWriteDiagnostic && aesKey != null && sharedSecret != null && streamConnectionId != null)
		{
			TryWriteMirrorDiagnosticSnapshot(payloadType, packetHeader, encryptedPayload, encryptedKey, aesKey, sharedSecret, eiv, streamConnectionId.Value, rtspTargetSessionId, tested, baseCandidateCount, report);
		}

		return false;
	}

	private bool TrySelectMirrorDecryptor(int payloadType, byte[] packetHeader, byte[] encryptedPayload, out byte[] annexB, out string selectedName, out string selectedFormat)
	{
		byte[]? aesKey;
		byte[]? sharedSecret;
		byte[]? eiv;
		byte[]? encryptedKey;
		ulong? streamConnectionId;
		lock (_mirrorKeyGate)
		{
			aesKey = _mirrorFairPlayAesKey == null ? null : CopyOf(_mirrorFairPlayAesKey);
			sharedSecret = _mirrorSharedSecret == null ? null : CopyOf(_mirrorSharedSecret);
			eiv = _mirrorEiv == null ? null : CopyOf(_mirrorEiv);
			encryptedKey = _encryptedMirrorAesKey == null ? null : CopyOf(_encryptedMirrorAesKey);
			streamConnectionId = _streamConnectionId;
		}

		annexB = Array.Empty<byte>();
		selectedName = string.Empty;
		selectedFormat = string.Empty;
		if (aesKey == null || sharedSecret == null || streamConnectionId == null)
		{
			return false;
		}

		List<string> report = new List<string>();
		ulong? rtspTargetSessionId;
		lock (_mirrorKeyGate)
		{
			rtspTargetSessionId = _rtspTargetSessionId;
		}

		IReadOnlyList<AirPlayMirrorDecryptorCandidate> baseCandidates = AirPlayMirrorDecryptor.BuildCandidates(aesKey, sharedSecret, streamConnectionId.Value, eiv, rtspTargetSessionId);
		int tested = 0;
		foreach (AirPlayMirrorDecryptorCandidate baseCandidate in baseCandidates)
		{
			foreach (AirPlayMirrorDecryptorCandidate candidate in ExpandPayloadOffsetCandidates(baseCandidate, encryptedPayload.Length))
			{
				tested++;
				byte[] preview = AirPlayMirrorDecryptor.DecryptOnce(candidate, encryptedPayload);
				if (TryNormalizeClearH264Payload(preview, out _, out string previewFormat))
				{
					AirPlayMirrorDecryptor decryptor = AirPlayMirrorDecryptor.Create(candidate);
					byte[] selectedPayload = decryptor.Decrypt(encryptedPayload);
					if (!TryNormalizeClearH264Payload(selectedPayload, out byte[] selectedAnnexB, out string selectedPayloadFormat))
					{
						decryptor.Dispose();
						continue;
					}

						lock (_mirrorKeyGate)
							{
								_mirrorDecryptor?.Dispose();
								DisposeMirrorProbePoolLocked();
								_mirrorDecryptor = decryptor;
								_decryptorStreamConnectionId = streamConnectionId.Value;
						_loggedDecryptPayloadFailureTypes.Remove(payloadType);
							_loggedDecryptCandidateReportTypes.Add(payloadType);
						}

					annexB = selectedAnnexB;
					selectedName = candidate.Name;
					selectedFormat = selectedPayloadFormat;
					AppLog.Write($"AirPlay mirror decryptor auto-selected '{candidate.Name}' for type={payloadType}, streamConnectionID={streamConnectionId.Value}, format={previewFormat}.");
					return true;
				}

				if (report.Count < 32)
				{
					report.Add(candidate.Name + "/" + DescribeH264Probe(preview));
				}
			}
		}

		lock (_mirrorKeyGate)
		{
			if (_loggedDecryptCandidateReportTypes.Add(payloadType))
			{
				AppLog.Write($"AirPlay mirror decryptor candidates did not produce clear H.264 for type={payloadType}. tested={tested}, baseCandidates={baseCandidates.Count}, probes=" + string.Join(", ", report));
			}
		}

		TryWriteMirrorDiagnosticSnapshot(payloadType, packetHeader, encryptedPayload, encryptedKey, aesKey, sharedSecret, eiv, streamConnectionId.Value, rtspTargetSessionId, tested, baseCandidates.Count, report);
		return false;
	}

	private void TryWriteMirrorDiagnosticSnapshot(
		int payloadType,
		byte[] packetHeader,
		byte[] encryptedPayload,
		byte[]? encryptedKey,
		byte[] aesKey,
		byte[] sharedSecret,
		byte[]? eiv,
		ulong streamConnectionId,
		ulong? rtspTargetSessionId,
		int testedCandidates,
		int baseCandidateCount,
		IReadOnlyList<string> candidateReport)
	{
		lock (_mirrorKeyGate)
		{
			if (_mirrorDiagnosticWritten)
			{
				return;
			}
			_mirrorDiagnosticWritten = true;
		}

		if (!ShouldWriteMirrorDiagnostics())
		{
			AppLog.Write("AirPlay mirror diagnostic snapshot skipped; enable Write diagnostics in Settings or set IMIRROR_WRITE_DIAGNOSTICS=1.");
			return;
		}

		try
		{
			byte[]? keyMessage = _fairPlay.TryGetKeyMessage();
			Dictionary<string, object?> snapshot = new Dictionary<string, object?>
			{
				["createdLocal"] = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
				["payloadType"] = payloadType,
				["testedCandidates"] = testedCandidates,
				["baseCandidateCount"] = baseCandidateCount,
				["streamConnectionId"] = streamConnectionId.ToString(CultureInfo.InvariantCulture),
				["rtspTargetSessionId"] = rtspTargetSessionId?.ToString(CultureInfo.InvariantCulture),
				["packetHeaderHex"] = Convert.ToHexString(packetHeader),
				["encryptedPayloadHex"] = Convert.ToHexString(encryptedPayload),
				["fpSetupKeyMessageHex"] = HexOrNull(keyMessage),
				["encryptedEkeyHex"] = HexOrNull(encryptedKey),
				["eivHex"] = HexOrNull(eiv),
				["fairPlayAesKeyHex"] = HexOrNull(aesKey),
				["fairPlayAesKeySha256"] = Sha256HexOrNull(aesKey),
				["pairVerifySharedSecretSha256"] = Sha256HexOrNull(sharedSecret),
				["candidateReport"] = candidateReport.ToArray()
			};

			string directory = AppPaths.DiagnosticsDirectory;
			Directory.CreateDirectory(directory);
			string fileName = "airplay-mirror-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".json";
			string path = Path.Combine(directory, fileName);
			File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
			AppLog.Write("AirPlay mirror diagnostic snapshot written: " + path);
		}
		catch (Exception ex)
		{
			AppLog.Write("AirPlay mirror diagnostic snapshot failed: " + ex.Message);
		}
	}

	private bool ShouldWriteMirrorDiagnostics() => _writeDiagnostics;

	private static string? HexOrNull(byte[]? bytes)
	{
		return bytes == null ? null : Convert.ToHexString(bytes);
	}

	private static string? Sha256HexOrNull(byte[]? bytes)
	{
		return bytes == null ? null : Convert.ToHexString(SHA256.HashData(bytes));
	}

	private static IEnumerable<AirPlayMirrorDecryptorCandidate> ExpandPayloadOffsetCandidates(AirPlayMirrorDecryptorCandidate candidate, int payloadLength)
	{
		yield return candidate;
		if (candidate.InitialSkipBytes != 0 || candidate.PayloadOffset != 0)
		{
			yield break;
		}

		int maxOffset = Math.Min(MaxH264PrefixProbeBytes, Math.Max(0, payloadLength - 5));
		for (int offset = 1; offset <= maxOffset; offset++)
		{
			yield return candidate with
			{
				Name = candidate.Name + "-payload-offset" + offset.ToString(CultureInfo.InvariantCulture),
				PayloadOffset = offset
			};
		}
	}

	private static bool TryBuildSpsPpsPayload(byte[] header, byte[] payload, out byte[] spsPps, out StreamConfig streamConfig)
	{
		spsPps = Array.Empty<byte>();
		streamConfig = new StreamConfig
		{
			Width = 1920,
			Height = 1080,
			Fps = StableDisplayFps,
			Codec = "h264-annexb"
		};

		if (payload.Length < 11)
		{
			return false;
		}

		float sourceWidth = BitConverter.ToSingle(header, 40);
		float sourceHeight = BitConverter.ToSingle(header, 44);
		float width = BitConverter.ToSingle(header, 56);
		float height = BitConverter.ToSingle(header, 60);

		int spsSize = (payload[6] << 8) | payload[7];
		if (spsSize <= 0 || payload.Length < 8 + spsSize + 3)
		{
			return false;
		}

		int ppsCountOffset = 8 + spsSize;
		int ppsSizeOffset = ppsCountOffset + 1;
		int ppsOffset = ppsSizeOffset + 2;
		int ppsSize = (payload[ppsSizeOffset] << 8) | payload[ppsSizeOffset + 1];
		if (ppsSize <= 0 || payload.Length < ppsOffset + ppsSize)
		{
			return false;
		}

		spsPps = new byte[8 + spsSize + ppsSize];
		spsPps[0] = 0;
		spsPps[1] = 0;
		spsPps[2] = 0;
		spsPps[3] = 1;
		Buffer.BlockCopy(payload, 8, spsPps, 4, spsSize);
		spsPps[4 + spsSize] = 0;
		spsPps[5 + spsSize] = 0;
		spsPps[6 + spsSize] = 0;
		spsPps[7 + spsSize] = 1;
		Buffer.BlockCopy(payload, ppsOffset, spsPps, 8 + spsSize, ppsSize);
		int streamWidth = RoundPositiveDimension(sourceWidth, RoundPositiveDimension(width, StableDisplayWidth));
		int streamHeight = RoundPositiveDimension(sourceHeight, RoundPositiveDimension(height, StableDisplayHeight));
		streamConfig = new StreamConfig
		{
			Width = streamWidth,
			Height = streamHeight,
			Fps = ResolveAdvertisedFps(streamWidth, streamHeight),
			Codec = "h264-annexb"
		};
		AppLog.Write($"AirPlay mirror SPS/PPS received: source={sourceWidth}x{sourceHeight}, display={width}x{height}, sps={spsSize}, pps={ppsSize}.");
		return true;
	}

	private static int ResolveAdvertisedFps(int width, int height)
	{
		if (!QualityRenderMode || !GpuDisplayAvailable)
		{
			return StableDisplayFps;
		}
		if (GpuDisplayAvailable && width >= GpuDisplayWidth && height >= GpuDisplayHeight)
		{
			return GpuDisplayFps;
		}
		return StableDisplayFps;
	}

	private static int RoundPositiveDimension(float value, int fallback)
	{
		if (float.IsNaN(value) || float.IsInfinity(value) || value <= 0)
		{
			return fallback;
		}

		return Math.Clamp((int)Math.Round(value), 2, 8192);
	}

	private static bool TryNormalizeClearH264Payload(byte[] payload, out byte[] annexB, out string format)
	{
		annexB = Array.Empty<byte>();
		format = string.Empty;
		if (payload.Length < 5)
		{
			return false;
		}

		// Try AVCC (length-prefixed) FIRST. Mac mirror video is AVCC, and probing Annex B
		// first mis-fires when an AVCC big-endian length prefix coincides with a start code
		// (NAL size 1 -> 00 00 00 01, size 256..511 -> 00 00 01 xx). That made
		// TryUseAnnexBPayloadAtOffset copy the raw AVCC bytes verbatim, leaving the 4-byte
		// length prefix in the stream and shifting every NAL by a byte, which corrupts slice
		// headers ("non-intra slice in an IDR NAL unit", first_mb_in_slice != 0). AVCC parsing
		// requires the whole payload to resolve exactly (cursor == length), so it is the
		// stricter, safer check and must run before the lenient Annex B probe.
		int maxOffset = Math.Min(MaxH264PrefixProbeBytes, Math.Max(0, payload.Length - 5));
		for (int offset = 0; offset <= maxOffset; offset++)
		{
			if (TryConvertAvccPayloadAtOffset(payload, offset, littleEndianLength: false, out annexB, out int nalCount))
			{
				format = (offset == 0 ? "avcc-be" : "avcc-be@" + offset.ToString(CultureInfo.InvariantCulture)) + "/nal=" + nalCount.ToString(CultureInfo.InvariantCulture);
				return true;
			}
		}

		// Fallback: a genuine Annex B payload (no AVCC length framing resolved above).
		if (TryUseAnnexBPayloadAtOffset(payload, 0, out annexB))
		{
			format = "annexb";
			return true;
		}

		return false;
	}

	private static bool TryConvertAvccPayloadAtOffset(byte[] payload, int offset, bool littleEndianLength, out byte[] annexB, out int nalCount)
	{
		annexB = Array.Empty<byte>();
		nalCount = 0;
		if (offset < 0 || offset + 5 > payload.Length)
		{
			return false;
		}

		List<byte> output = new List<byte>();
		int cursor = offset;
		while (cursor + 4 <= payload.Length)
		{
			int nalSize = littleEndianLength
				? BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(cursor, 4))
				: BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(cursor, 4));
			int nalOffset = cursor + 4;
			if (nalSize <= 0 || nalSize > MaxH264NalUnitLength || nalSize > payload.Length - nalOffset)
			{
				return false;
			}
			if (!IsPlausibleH264NalHeader(payload[nalOffset]))
			{
				return false;
			}

			output.Add(0);
			output.Add(0);
			output.Add(0);
			output.Add(1);
			for (int i = 0; i < nalSize; i++)
			{
				output.Add(payload[nalOffset + i]);
			}
			cursor = nalOffset + nalSize;
			nalCount++;
		}

		if (nalCount == 0 || cursor != payload.Length)
		{
			return false;
		}

		annexB = output.ToArray();
		return true;
	}

	private static bool TryUseAnnexBPayloadAtOffset(byte[] payload, int offset, out byte[] annexB)
	{
		annexB = Array.Empty<byte>();
		if (!IsStartCodeAt(payload, offset))
		{
			return false;
		}

		int nalOffset = offset + StartCodeLengthAt(payload, offset);
		if (nalOffset >= payload.Length || !IsPlausibleH264NalHeader(payload[nalOffset]))
		{
			return false;
		}

		annexB = new byte[payload.Length - offset];
		Buffer.BlockCopy(payload, offset, annexB, 0, annexB.Length);
		return true;
	}

	// Lightweight access-unit shape telemetry for the type=0 video path. Logs one summary
	// line every ~10s with NAL composition counts only (no payload bytes, no key material,
	// no screen content). Used to compare sender behaviour (e.g. Mac vs iPhone): whether a
	// sender splits access units across payloads, how often IDR/parameter sets arrive, and
	// whether the received-payload rate matches the decoded-frame rate.
	private readonly object _payloadShapeGate = new object();

	private long _shapeWindowStartTick;

	private int _shapePayloads;

	private int _shapeIdr;

	private int _shapeSlice;

	private int _shapeParamSetOnly;

	private int _shapeSeiOnly;

	private int _shapeOther;

	private int _shapeMultiNal;

	private long _shapeBytes;

	private void RecordVideoPayloadShape(byte[] annexB)
	{
		bool hasIdr = false;
		bool hasSlice = false;
		bool hasParamSet = false;
		bool hasSei = false;
		int nalCount = 0;
		int offset = 0;
		while (true)
		{
			int start = FindStartCode(annexB, offset, annexB.Length - 3);
			if (start < 0)
			{
				break;
			}
			int nalOffset = start + StartCodeLengthAt(annexB, start);
			if (nalOffset >= annexB.Length)
			{
				break;
			}
			nalCount++;
			switch (annexB[nalOffset] & 0x1F)
			{
				case 5:
					hasIdr = true;
					break;
				case 1:
					hasSlice = true;
					break;
				case 7:
				case 8:
					hasParamSet = true;
					break;
				case 6:
					hasSei = true;
					break;
			}
			offset = nalOffset + 1;
		}

		if (!_loggedFirstDecodableVideoFrame)
		{
			_loggedFirstDecodableVideoFrame = true;
			long warmupMs = _firstEncryptedWaitTick == 0
				? 0L
				: (long)((Stopwatch.GetTimestamp() - _firstEncryptedWaitTick) * 1000.0 / Stopwatch.Frequency);
			AppLog.Write(
				$"AirPlay mirror first decodable video frame: keyframe(IDR)={hasIdr}, " +
				$"droppedBeforeFirst={_droppedVideoBeforeFirstFrame}, decryptorWarmup={warmupMs}ms." +
				(hasIdr
					? string.Empty
					: " WARNING: first surviving frame is not a keyframe; the single initial IDR was lost during decryptor warm-up and AirPlay mirror streams send no further keyframes, so the decoder stays starved."));
		}

		string? summary = null;
		lock (_payloadShapeGate)
		{
			long now = Stopwatch.GetTimestamp();
			if (_shapeWindowStartTick == 0)
			{
				_shapeWindowStartTick = now;
			}

			_shapePayloads++;
			_shapeBytes += annexB.Length;
			if (hasIdr)
			{
				_shapeIdr++;
			}
			else if (hasSlice)
			{
				_shapeSlice++;
			}
			else if (hasParamSet)
			{
				_shapeParamSetOnly++;
			}
			else if (hasSei)
			{
				_shapeSeiOnly++;
			}
			else
			{
				_shapeOther++;
			}
			if (nalCount > 1)
			{
				_shapeMultiNal++;
			}

			double seconds = (now - _shapeWindowStartTick) / (double)Stopwatch.Frequency;
			if (seconds >= 10.0)
			{
				summary = $"AirPlay mirror video payload shape ({seconds:N1}s): payloads={_shapePayloads} ({_shapePayloads / seconds:N1}/s), " +
					$"idr={_shapeIdr}, slice={_shapeSlice}, paramSetOnly={_shapeParamSetOnly}, seiOnly={_shapeSeiOnly}, other={_shapeOther}, " +
					$"multiNal={_shapeMultiNal}, avgSize={(_shapePayloads > 0 ? _shapeBytes / _shapePayloads : 0):N0}.";
				_shapeWindowStartTick = now;
				_shapePayloads = 0;
				_shapeIdr = 0;
				_shapeSlice = 0;
				_shapeParamSetOnly = 0;
				_shapeSeiOnly = 0;
				_shapeOther = 0;
				_shapeMultiNal = 0;
				_shapeBytes = 0;
			}
		}

		if (summary != null)
		{
			AppLog.Write(summary);
		}
	}

	private static string DescribeH264Probe(byte[] payload)
	{
		if (payload.Length < 5)
		{
			return "short";
		}

		int beLength = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));
		int leLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
		int nalTypeAt4 = payload[4] & 0x1F;
		int startCodeOffset = FindStartCode(payload, 0, Math.Min(MaxH264PrefixProbeBytes, payload.Length - 4));
		string startCode = startCodeOffset >= 0 ? startCodeOffset.ToString(CultureInfo.InvariantCulture) : "none";
		return "beLen=" + beLength.ToString(CultureInfo.InvariantCulture) +
			",leLen=" + leLength.ToString(CultureInfo.InvariantCulture) +
			",nal4=" + nalTypeAt4.ToString(CultureInfo.InvariantCulture) +
			",start=" + startCode;
	}

	private static bool IsPlausibleH264NalHeader(byte header)
	{
		if ((header & 0x80) != 0)
		{
			return false;
		}

		int nalType = header & 0x1F;
		return nalType == 1 ||
			nalType == 5 ||
			nalType == 6 ||
			nalType == 7 ||
			nalType == 8 ||
			nalType == 9 ||
			nalType == 14;
	}

	private static bool IsStartCodeAt(byte[] payload, int offset)
	{
		return StartCodeLengthAt(payload, offset) > 0;
	}

	private static int StartCodeLengthAt(byte[] payload, int offset)
	{
		if (offset + 3 <= payload.Length &&
			payload[offset] == 0 &&
			payload[offset + 1] == 0 &&
			payload[offset + 2] == 1)
		{
			return 3;
		}

		if (offset + 4 <= payload.Length &&
			payload[offset] == 0 &&
			payload[offset + 1] == 0 &&
			payload[offset + 2] == 0 &&
			payload[offset + 3] == 1)
		{
			return 4;
		}

		return 0;
	}

	private static int FindStartCode(byte[] payload, int start, int endInclusive)
	{
		int end = Math.Min(endInclusive, payload.Length - 3);
		for (int i = Math.Max(0, start); i <= end; i++)
		{
			if (IsStartCodeAt(payload, i))
			{
				return i;
			}
		}

		return -1;
	}

	private int EmitAnnexBPayloads(List<byte> pending)
	{
		int emitted = 0;
		while (true)
		{
			int first = FindStartCode(pending, 0);
			if (first < 0)
			{
				if (pending.Count > 4)
				{
					pending.RemoveRange(0, pending.Count - 4);
				}
				return emitted;
			}
			if (first > 0)
			{
				pending.RemoveRange(0, first);
			}

			int next = FindStartCode(pending, 4);
			if (next < 0)
			{
				return emitted;
			}

			byte[] payload = pending.Take(next).ToArray();
			pending.RemoveRange(0, next);
			VideoPayloadReceived?.Invoke(payload, 0, Stopwatch.GetTimestamp());
			emitted++;
		}
	}

	private int EmitAvccPayloads(List<byte> pending)
	{
		int emitted = 0;
		while (pending.Count >= 5)
		{
			int length = BinaryPrimitives.ReadInt32BigEndian(CollectionsMarshalAsSpan(pending).Slice(0, 4));
			if (length <= 0 || length > 1024 * 1024)
			{
				return emitted;
			}
			if (pending.Count < 4 + length)
			{
				return emitted;
			}

			byte nalHeader = pending[4];
			if ((nalHeader & 0x1F) == 0 || (nalHeader & 0x1F) > 23)
			{
				return emitted;
			}

			byte[] payload = new byte[4 + length];
			payload[0] = 0;
			payload[1] = 0;
			payload[2] = 0;
			payload[3] = 1;
			pending.CopyTo(4, payload, 4, length);
			pending.RemoveRange(0, 4 + length);
			VideoPayloadReceived?.Invoke(payload, 0, Stopwatch.GetTimestamp());
			emitted++;
		}
		return emitted;
	}

	private static ReadOnlySpan<byte> CollectionsMarshalAsSpan(List<byte> values)
	{
		return values.ToArray();
	}

	private static int FindStartCode(List<byte> bytes, int start)
	{
		for (int i = start; i + 3 < bytes.Count; i++)
		{
			if (bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 1)
			{
				return i;
			}
			if (i + 4 < bytes.Count && bytes[i] == 0 && bytes[i + 1] == 0 && bytes[i + 2] == 0 && bytes[i + 3] == 1)
			{
				return i;
			}
		}
		return -1;
	}

	private static async Task<AirPlayRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
	{
		List<byte> headerBytes = new List<byte>();
		byte[] single = new byte[1];
		while (headerBytes.Count < 64 * 1024)
		{
			int count = await stream.ReadAsync(single, token);
			if (count == 0)
			{
				return null;
			}
			headerBytes.Add(single[0]);
			int n = headerBytes.Count;
			if (n >= 4 &&
				headerBytes[n - 4] == '\r' &&
				headerBytes[n - 3] == '\n' &&
				headerBytes[n - 2] == '\r' &&
				headerBytes[n - 1] == '\n')
			{
				break;
			}
		}

		string headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
		string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
		string firstLine = lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line)) ?? string.Empty;
		string[] parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
		if (parts.Length < 2)
		{
			return null;
		}

		Dictionary<string, string> headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		foreach (string line in lines.Skip(1))
		{
			int colon = line.IndexOf(':');
			if (colon <= 0)
			{
				continue;
			}
			headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
		}

		int contentLength = 0;
		if (headers.TryGetValue("Content-Length", out string? contentLengthValue))
		{
			int.TryParse(contentLengthValue, out contentLength);
		}

		byte[] body = new byte[Math.Max(0, contentLength)];
		int offset = 0;
		while (offset < body.Length)
		{
			int count = await stream.ReadAsync(body.AsMemory(offset, body.Length - offset), token);
			if (count == 0)
			{
				break;
			}
			offset += count;
		}

		return new AirPlayRequest(parts[0], parts[1], parts.Length > 2 ? parts[2] : "RTSP/1.0", firstLine, headers, body);
	}

	private void ObserveSetupRequest(byte[] requestBody, string target)
	{
		Dictionary<string, object?>? request = AirPlayBinaryPlist.Read(requestBody) as Dictionary<string, object?>;
		if (request == null)
		{
			return;
		}

		if (AudioDiscoveryLogging)
		{
			// Dump the full SETUP plist so we can read the audio stream descriptor (type, codec/ct,
			// audioFormat, sample rate, channels, requested UDP ports) and the encryption fields.
			AppLog.Write("AUDIO-DISCOVERY SETUP plist: " + DescribePlistObject(request, 0));
		}

		bool changed = false;
		ulong? observedRtspTargetSessionId = null;
		ulong? observedStreamConnectionId = null;
		bool mirrorSetup = ContainsMirrorVideoStream(request);
		if (TryReadRtspTargetSessionId(target, out ulong rtspTargetSessionId))
		{
			observedRtspTargetSessionId = rtspTargetSessionId;
			lock (_mirrorKeyGate)
			{
				_rtspTargetSessionId = rtspTargetSessionId;
			}
			AppLog.Write($"AirPlay SETUP RTSP target session id received: {rtspTargetSessionId}.");
		}

		if (TryReadData(request, "ekey", out byte[] encryptedKey))
		{
			mirrorSetup = true;
			lock (_mirrorKeyGate)
			{
				_mirrorDecryptor?.Dispose();
				_mirrorDecryptor = null;
				DisposeMirrorProbePoolLocked();
				_streamConnectionId = null;
				_decryptorStreamConnectionId = null;
				_encryptedMirrorAesKey = CopyOf(encryptedKey);
				_mirrorFairPlayAesKey = null;
				_mirrorSharedSecret = null;
				_loggedWaitingForMirrorDecryptor = false;
				_loggedDecryptPayloadFailureTypes.Clear();
				_loggedDecryptCandidateReportTypes.Clear();
				_loggedSkippedPacketTypes.Clear();
				_loggedDecryptorInUse = false;
				_mirrorDiagnosticWritten = false;
			}
			AppLog.Write($"AirPlay SETUP mirror ekey received: {encryptedKey.Length} bytes.");
			changed = true;
		}

		if (TryReadData(request, "eiv", out byte[] mirrorEiv))
		{
			mirrorSetup = true;
			lock (_mirrorKeyGate)
			{
				_mirrorEiv = CopyOf(mirrorEiv);
			}
			AppLog.Write($"AirPlay SETUP mirror eiv received: {mirrorEiv.Length} bytes.");
			changed = true;
		}

		if (TryReadStreamConnectionId(request, out ulong streamConnectionId))
		{
			observedStreamConnectionId = streamConnectionId;
			lock (_mirrorKeyGate)
			{
				if (_streamConnectionId != null && _streamConnectionId.Value != streamConnectionId)
				{
					_mirrorDecryptor?.Dispose();
					_mirrorDecryptor = null;
					DisposeMirrorProbePoolLocked();
					_decryptorStreamConnectionId = null;
					_loggedWaitingForMirrorDecryptor = false;
					_loggedDecryptPayloadFailureTypes.Clear();
					_loggedDecryptCandidateReportTypes.Clear();
					_loggedSkippedPacketTypes.Clear();
					_loggedDecryptorInUse = false;
					_mirrorDiagnosticWritten = false;
				}
				_streamConnectionId = streamConnectionId;
			}
			AppLog.Write($"AirPlay SETUP streamConnectionID received: {streamConnectionId}.");
			changed = true;
		}

		if (mirrorSetup && TryAnnounceMirrorSessionStarted(observedRtspTargetSessionId, observedStreamConnectionId))
		{
			_audioReceiver.Stop();
			AppLog.Write("AirPlay mirror session started; receiver pipeline reset requested.");
			MirrorSessionStarted?.Invoke();
		}

		if (changed)
		{
			TryInitializeMirrorDecryptor();
		}
	}

	private bool TryAnnounceMirrorSessionStarted(ulong? rtspTargetSessionId, ulong? streamConnectionId)
	{
		lock (_mirrorKeyGate)
		{
			if (rtspTargetSessionId != null)
			{
				if (_announcedMirrorRtspTargetSessionId == rtspTargetSessionId)
				{
					if (streamConnectionId != null)
					{
						_announcedMirrorStreamConnectionId = streamConnectionId;
					}

					return false;
				}

				if (_announcedMirrorRtspTargetSessionId == null &&
					streamConnectionId != null &&
					_announcedMirrorStreamConnectionId == streamConnectionId)
				{
					_announcedMirrorRtspTargetSessionId = rtspTargetSessionId;
					return false;
				}

				_announcedIdentitylessMirrorSession = false;
				_announcedMirrorRtspTargetSessionId = rtspTargetSessionId;
				if (streamConnectionId != null)
				{
					_announcedMirrorStreamConnectionId = streamConnectionId;
				}

				return true;
			}

			if (streamConnectionId != null)
			{
				if (_announcedMirrorStreamConnectionId == streamConnectionId)
				{
					return false;
				}

				_announcedIdentitylessMirrorSession = false;
				_announcedMirrorStreamConnectionId = streamConnectionId;
				return true;
			}

			if (_announcedIdentitylessMirrorSession)
			{
				return false;
			}

			_announcedIdentitylessMirrorSession = true;
			return true;
		}
	}

	private void EndMirrorSessionIfAnnounced(string reason)
	{
		if (!ResetMirrorSessionState())
		{
			return;
		}

		AppLog.Write("AirPlay mirror session ended: " + reason + ".");
		MirrorSessionEnded?.Invoke();
	}

	private bool ResetMirrorSessionState()
	{
		bool hadAnnouncedSession;
		lock (_mirrorKeyGate)
		{
			hadAnnouncedSession =
				_announcedMirrorRtspTargetSessionId != null ||
				_announcedMirrorStreamConnectionId != null ||
				_announcedIdentitylessMirrorSession;
			if (!hadAnnouncedSession)
			{
				return false;
			}

			_rtspTargetSessionId = null;
			_announcedMirrorRtspTargetSessionId = null;
			_announcedMirrorStreamConnectionId = null;
			_announcedIdentitylessMirrorSession = false;
		}

		try
		{
			_audioReceiver.Stop();
		}
		catch (Exception ex)
		{
			AppLog.Write("AirPlay audio receiver stop failed during session reset: " + ex);
		}

		return true;
	}

	private void TryInitializeMirrorDecryptor()
	{
		byte[]? encryptedKey;
		ulong? streamConnectionId;
		byte[]? eiv;
		ulong? rtspTargetSessionId;
				lock (_mirrorKeyGate)
				{
					if ((_mirrorDecryptor != null || _mirrorDecryptorProbePool != null) && _decryptorStreamConnectionId == _streamConnectionId)
					{
						return;
					}

				encryptedKey = _encryptedMirrorAesKey == null ? null : CopyOf(_encryptedMirrorAesKey);
				streamConnectionId = _streamConnectionId;
				eiv = _mirrorEiv == null ? null : CopyOf(_mirrorEiv);
				rtspTargetSessionId = _rtspTargetSessionId;
			}

		if (encryptedKey == null || streamConnectionId == null)
		{
			return;
		}

		byte[]? sharedSecret = _pairing.TryGetSharedSecret();
		if (sharedSecret == null)
		{
			AppLog.Write("AirPlay mirror decryptor waiting for pair-verify shared secret.");
			return;
		}

		byte[]? aesKey = _fairPlay.TryDecryptAesKey(encryptedKey);
		if (aesKey == null)
		{
			return;
		}

		List<AirPlayMirrorDecryptor> probePool = AirPlayMirrorDecryptor.BuildCandidates(aesKey, sharedSecret, streamConnectionId.Value, eiv, rtspTargetSessionId)
			.Where(IsStatefulMirrorProbeCandidate)
			.Select(AirPlayMirrorDecryptor.Create)
			.ToList();

		if (probePool.Count == 0)
		{
			AppLog.Write("AirPlay mirror decryptor probe pool could not be built.");
			return;
		}

		lock (_mirrorKeyGate)
		{
			DisposeMirrorProbePoolLocked();
			_mirrorFairPlayAesKey = CopyOf(aesKey);
			_mirrorSharedSecret = CopyOf(sharedSecret);
			_mirrorDecryptorProbePool = probePool;
			_decryptorStreamConnectionId = streamConnectionId.Value;
			_loggedWaitingForMirrorDecryptor = false;
			_loggedDecryptPayloadFailureTypes.Clear();
			_loggedDecryptCandidateReportTypes.Clear();
			_loggedSkippedPacketTypes.Clear();
			_loggedDecryptorInUse = false;
			_mirrorDiagnosticWritten = false;
			AppLog.Write($"AirPlay mirror decryptor probe pool ready for streamConnectionID={streamConnectionId.Value}; candidates={probePool.Count}.");
		}
	}

	private static bool IsStatefulMirrorProbeCandidate(AirPlayMirrorDecryptorCandidate candidate)
	{
		return candidate.InitialSkipBytes == 0 &&
			candidate.PayloadOffset == 0 &&
			candidate.Name.Contains("uxplay-direct", StringComparison.Ordinal);
	}

	private void DisposeMirrorProbePoolLocked()
	{
		if (_mirrorDecryptorProbePool == null)
		{
			return;
		}

		foreach (AirPlayMirrorDecryptor decryptor in _mirrorDecryptorProbePool)
		{
			decryptor.Dispose();
		}
		_mirrorDecryptorProbePool = null;
	}

	private static bool TryReadData(Dictionary<string, object?> dictionary, string key, out byte[] data)
	{
		if (dictionary.TryGetValue(key, out object? value) && value is byte[] bytes)
		{
			data = bytes;
			return true;
		}

		data = Array.Empty<byte>();
		return false;
	}

	private static bool TryReadStreamConnectionId(Dictionary<string, object?> request, out ulong streamConnectionId)
	{
		streamConnectionId = 0;
		if (!request.TryGetValue("streams", out object? streamsObject) || streamsObject is not List<object?> streams)
		{
			return false;
		}

		foreach (Dictionary<string, object?> stream in streams.OfType<Dictionary<string, object?>>())
		{
			if (TryReadUInt64(stream, "streamConnectionID", out streamConnectionId))
			{
				return true;
			}
		}

		return false;
	}

	private static bool ContainsMirrorVideoStream(Dictionary<string, object?> request)
	{
		if (!request.TryGetValue("streams", out object? streamsObject) || streamsObject is not List<object?> streams)
		{
			return false;
		}

		foreach (Dictionary<string, object?> stream in streams.OfType<Dictionary<string, object?>>())
		{
			if (TryReadInt32(stream, "type", out int streamType) && streamType == 110)
			{
				return true;
			}
		}

		return false;
	}

	private static bool TryReadRtspTargetSessionId(string target, out ulong sessionId)
	{
		sessionId = 0;
		int slash = target.LastIndexOf('/');
		if (slash < 0 || slash + 1 >= target.Length)
		{
			return false;
		}

		return ulong.TryParse(target.AsSpan(slash + 1), out sessionId);
	}

	private static bool TryReadInt32(Dictionary<string, object?> dictionary, string key, out int value)
	{
		value = 0;
		if (!dictionary.TryGetValue(key, out object? rawValue))
		{
			return false;
		}

		switch (rawValue)
		{
			case byte number:
				value = number;
				return true;
			case short number:
				value = number;
				return true;
			case int number:
				value = number;
				return true;
			case long number when number >= int.MinValue && number <= int.MaxValue:
				value = (int)number;
				return true;
			case ulong number when number <= int.MaxValue:
				value = (int)number;
				return true;
			default:
				return false;
		}
	}

	private static bool TryReadUInt64(Dictionary<string, object?> dictionary, string key, out ulong value)
	{
		value = 0;
		if (!dictionary.TryGetValue(key, out object? rawValue))
		{
			return false;
		}

		switch (rawValue)
		{
			case byte number:
				value = number;
				return true;
			case short number when number >= 0:
				value = (ulong)number;
				return true;
			case int number when number >= 0:
				value = (ulong)number;
				return true;
			case long number when number >= 0:
				value = (ulong)number;
				return true;
			case long number:
				value = unchecked((ulong)number);
				return true;
			case ulong number:
				value = number;
				return true;
			default:
				return false;
		}
	}

	private static byte[] CopyOf(byte[] source)
	{
		byte[] copy = new byte[source.Length];
		Buffer.BlockCopy(source, 0, copy, 0, source.Length);
		return copy;
	}

	private byte[] BuildResponse(AirPlayRequest request, EndPoint? remoteEndPoint)
	{
		string method = request.Method.ToUpperInvariant();
		string target = request.Target.ToLowerInvariant();
		if (method == "OPTIONS")
		{
			return BuildRtspResponse(request, 200, "OK", Array.Empty<byte>(), "text/plain", new Dictionary<string, string>
			{
				["Public"] = "ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER"
			});
			}
			if (method == "GET" && (target == "/info" || target == "/server-info"))
			{
				byte[] body = BuildInfoBinaryPlist();
				return BuildRtspResponse(request, 200, "OK", body, "application/x-apple-binary-plist");
			}
			if (method == "POST" && target.Contains("pair-setup", StringComparison.OrdinalIgnoreCase))
			{
				LogPairBody("pair-setup", request.Body);
				return BuildRtspResponse(request, 200, "OK", _pairing.BuildPairSetupResponse(request.Body), "application/octet-stream");
			}
			if (method == "POST" && target.Contains("pair-verify", StringComparison.OrdinalIgnoreCase))
			{
				LogPairBody("pair-verify", request.Body);
				return BuildRtspResponse(request, 200, "OK", _pairing.BuildPairVerifyResponse(request.Body), "application/octet-stream");
			}
			if (method == "POST" && target.Contains("fp-setup", StringComparison.OrdinalIgnoreCase))
			{
				LogPairBody("fp-setup", request.Body);
				return BuildRtspResponse(request, 200, "OK", _fairPlay.BuildSetupResponse(request.Body), "application/octet-stream");
			}
			if (method == "SETUP")
			{
				LogPairBody("setup", request.Body);
				ObserveSetupRequest(request.Body, request.Target);
				IPAddress? remoteAddress = (remoteEndPoint as IPEndPoint)?.Address;
				byte[] body = _setup.BuildSetupResponse(request.Body, remoteAddress);
				return BuildRtspResponse(request, 200, "OK", body, "application/x-apple-binary-plist", new Dictionary<string, string>
				{
					["Session"] = "1"
				});
			}
			if (method == "GET_PARAMETER")
			{
				return BuildGetParameterResponse(request);
			}
			if (method == "RECORD")
			{
				// Video-only: omit audio jack/latency headers unless audio is advertised.
				Dictionary<string, string> recordHeaders = _audioAdvertised
					? new Dictionary<string, string>
					{
						["Audio-Latency"] = "11025",
						["Audio-Jack-Status"] = "connected; type=analog"
					}
					: new Dictionary<string, string>();
				return BuildRtspResponse(request, 200, "OK", Array.Empty<byte>(), "text/plain", recordHeaders);
			}
			if (method == "TEARDOWN")
			{
				// Some senders keep posting feedback after RTSP TEARDOWN on the shared RAOP
				// control session. Acknowledge it without tearing down the active mirror path.
				return BuildRtspResponse(request, 200, "OK", Array.Empty<byte>(), "text/plain");
			}
			if (method is "FLUSH" or "SET_PARAMETER" || target.Contains("feedback", StringComparison.OrdinalIgnoreCase))
			{
				return BuildRtspResponse(request, 200, "OK", Array.Empty<byte>(), "text/plain");
			}
			return BuildRtspResponse(request, 501, "Not Implemented", Array.Empty<byte>(), "text/plain");
		}

	private byte[] BuildGetParameterResponse(AirPlayRequest request)
	{
		string bodyText = request.Body.Length == 0 ? string.Empty : Encoding.UTF8.GetString(request.Body);
		AppLog.Write("AirPlay get-parameter body=" + bodyText.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal));

		if (bodyText.Contains("volume", StringComparison.OrdinalIgnoreCase))
		{
			return BuildRtspResponse(request, 200, "OK", Encoding.ASCII.GetBytes("volume: 0.0\r\n"), "text/parameters");
		}

		return BuildRtspResponse(request, 200, "OK", Array.Empty<byte>(), "text/parameters");
	}

	private byte[] BuildRtspResponse(
		AirPlayRequest request,
		int statusCode,
		string reason,
		byte[] body,
		string contentType,
		IReadOnlyDictionary<string, string>? extraHeaders = null)
	{
		string protocol = request.Protocol.StartsWith("HTTP", StringComparison.OrdinalIgnoreCase) ? "HTTP/1.1" : "RTSP/1.0";
		StringBuilder builder = new StringBuilder();
		builder.Append(protocol).Append(' ').Append(statusCode).Append(' ').Append(reason).Append("\r\n");
		if (request.Headers.TryGetValue("CSeq", out string? cseq))
		{
			builder.Append("CSeq: ").Append(cseq).Append("\r\n");
		}
	builder.Append("Server: AirTunes/").Append(LegacyAirTunesVersion).Append("\r\n");
		builder.Append("Date: ").Append(DateTimeOffset.UtcNow.ToString("r")).Append("\r\n");
		if (extraHeaders != null)
		{
			foreach (KeyValuePair<string, string> header in extraHeaders)
			{
				builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
			}
		}
		builder.Append("Content-Type: ").Append(contentType).Append("\r\n");
		builder.Append("Content-Length: ").Append(body.Length).Append("\r\n");
		builder.Append("\r\n");
		byte[] headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
		byte[] response = new byte[headerBytes.Length + body.Length];
		Buffer.BlockCopy(headerBytes, 0, response, 0, headerBytes.Length);
		Buffer.BlockCopy(body, 0, response, headerBytes.Length, body.Length);
		return response;
	}

	private string BuildInfoPlist()
	{
		return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
			"<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">" +
			"<plist version=\"1.0\"><dict>" +
			PlistString("deviceid", _deviceId) +
			PlistString("features", "0x5A7FFFF7,0x1E") +
			PlistInteger("flags", 68) +
				PlistString("model", "AppleTV3,2") +
				PlistString("name", _displayName) +
				PlistString("pi", _pairingId) +
				PlistString("pk", _pairing.PublicKeyHex) +
				PlistString("protovers", "1.1") +
			PlistString("srcvers", "220.68") +
			PlistInteger("statusFlags", 4) +
			PlistInteger("vv", 2) +
			"<key>displays</key><array><dict>" +
			PlistInteger("width", 1920) +
			PlistInteger("height", 1080) +
			PlistInteger("widthPhysical", 1920) +
			PlistInteger("heightPhysical", 1080) +
			PlistInteger("refreshRate", 60) +
			"</dict></array>" +
				"</dict></plist>";
	}

	private byte[] BuildInfoBinaryPlist()
	{
		ulong features = ((ulong)0x1E << 32) | 0x5A7FFFF7UL;
		byte[] publicKey = Convert.FromHexString(_pairing.PublicKeyHex);
		byte[] txtAirPlay = BuildTxtData(BuildAirPlayTxt());
		int displayWidth = StableDisplayWidth;
		int displayHeight = StableDisplayHeight;
		int displayFps = StableDisplayFps;
		if (QualityRenderMode && GpuDisplayAvailable)
		{
			displayWidth = GpuDisplayWidth;
			displayHeight = GpuDisplayHeight;
			displayFps = GpuDisplayFps;
			AppLog.Write($"AirPlay /info GPU display advertise: {displayWidth}x{displayHeight} @ {displayFps}.");
		}
		else if (QualityRenderMode)
		{
			AppLog.Write("AirPlay /info GPU display unavailable; advertising stable 1920x1080.");
		}

		Dictionary<string, object> info = new Dictionary<string, object>
		{
			["txtAirPlay"] = txtAirPlay,
			["features"] = features,
			["name"] = _displayName,
			["pi"] = _pairingId,
			["vv"] = 2,
			["statusFlags"] = 68,
			["keepAliveLowPower"] = true,
			["sourceVersion"] = LegacyAirTunesVersion,
			["pk"] = publicKey,
			["keepAliveSendStatsAsBody"] = true,
			["deviceID"] = _deviceId,
			["model"] = LegacyAirPlayModel,
			["macAddress"] = _deviceId,
			["audioFormats"] = new List<object>
			{
				new Dictionary<string, object>
				{
					["type"] = 100,
					["audioInputFormats"] = 67108860,
					["audioOutputFormats"] = 67108860
				},
				new Dictionary<string, object>
				{
					["type"] = 101,
					["audioInputFormats"] = 67108860,
					["audioOutputFormats"] = 67108860
				}
			},
			["audioLatencies"] = new List<object>
			{
				new Dictionary<string, object>
				{
					["type"] = 100,
					["audioType"] = "default",
					["inputLatencyMicros"] = 0,
					["outputLatencyMicros"] = 0
				},
				new Dictionary<string, object>
				{
					["type"] = 101,
					["audioType"] = "default",
					["inputLatencyMicros"] = 0,
					["outputLatencyMicros"] = 0
				}
			},
			["displays"] = new List<object>
			{
				new Dictionary<string, object>
				{
					["uuid"] = "e0ff8a27-6738-3d56-8a16-cc53aacee925",
					["widthPhysical"] = 0,
					["heightPhysical"] = 0,
					["width"] = displayWidth,
					["height"] = displayHeight,
					["widthPixels"] = displayWidth,
					["heightPixels"] = displayHeight,
					["rotation"] = false,
					["refreshRate"] = 1.0 / displayFps,
					["overscanned"] = false,
					["features"] = 14
				}
			}
		};

		if (!_audioAdvertised)
		{
			// Video-only: drop the audio capability claim from the response. The negotiation
			// fields above are left in source so re-enabling is a one-flag change.
			info.Remove("audioFormats");
			info.Remove("audioLatencies");
		}

		return AirPlayBinaryPlist.Write(info);
	}

	private static string BuildSetupPlist()
	{
		return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
			"<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">" +
			"<plist version=\"1.0\"><dict>" +
			PlistInteger("eventPort", EventPort) +
			PlistInteger("timingPort", TimingPort) +
			PlistInteger("dataPort", DataPort) +
			PlistString("sessionUUID", Guid.NewGuid().ToString("D")) +
			"</dict></plist>";
	}

	private static string PlistString(string key, string value) => "<key>" + EscapeXml(key) + "</key><string>" + EscapeXml(value) + "</string>";

	private static string PlistInteger(string key, int value) => "<key>" + EscapeXml(key) + "</key><integer>" + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "</integer>";

	private static string EscapeXml(string value)
	{
		return value
			.Replace("&", "&amp;", StringComparison.Ordinal)
			.Replace("<", "&lt;", StringComparison.Ordinal)
			.Replace(">", "&gt;", StringComparison.Ordinal)
			.Replace("\"", "&quot;", StringComparison.Ordinal)
			.Replace("'", "&apos;", StringComparison.Ordinal);
	}

	private static void LogPairBody(string label, byte[] body)
	{
		AppLog.Write($"AirPlay {label} body length={body.Length}.");
	}

	private async Task ReceiveLoopAsync()
	{
		while (!_cts.IsCancellationRequested)
		{
			try
			{
				UdpReceiveResult result = await _mdnsClient!.ReceiveAsync(_cts.Token);
				if (PacketAsksForProbe(result.Buffer))
				{
					await SendAnnouncementAsync(_cts.Token);
				}
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

	private async Task AnnounceLoopAsync()
	{
		TimeSpan delay = TimeSpan.FromSeconds(3.0);
		while (!_cts.IsCancellationRequested)
		{
			try
			{
				await Task.Delay(delay, _cts.Token);
				await SendAnnouncementAsync(_cts.Token);
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

	private async Task SendAnnouncementAsync(CancellationToken token)
	{
		if (_mdnsClient == null)
		{
			return;
		}

		byte[] packet = BuildMdnsResponse(ttlSeconds: 120);
		await _mdnsClient.SendAsync(packet, MulticastEndpoint, token);
	}

	private byte[] BuildMdnsResponse(uint ttlSeconds)
	{
		List<DnsRecord> records = new List<DnsRecord>
		{
			DnsRecord.Ptr(AirPlayServiceType, _airPlayInstanceName, ttlSeconds),
			DnsRecord.Ptr(RaopServiceType, _raopInstanceName, ttlSeconds),
			DnsRecord.Srv(_airPlayInstanceName, AirPlayPort, _hostName, ttlSeconds),
			DnsRecord.Txt(_airPlayInstanceName, BuildAirPlayTxt(), ttlSeconds),
			DnsRecord.Srv(_raopInstanceName, RaopPort, _hostName, ttlSeconds),
			DnsRecord.Txt(_raopInstanceName, BuildRaopTxt(), ttlSeconds)
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

	private IReadOnlyList<string> BuildAirPlayTxt()
	{
		return new[]
		{
			"deviceid=" + _deviceId,
				"features=" + LegacyAirPlayFeatures,
				"flags=0x4",
				"model=" + LegacyAirPlayModel,
				"srcvers=" + LegacyAirTunesVersion,
					"vv=2",
					"pi=" + _pairingId,
					"pk=" + _pairing.PublicKeyHex,
				"pw=false"
			};
	}

	private IReadOnlyList<string> BuildRaopTxt()
	{
		return new[]
		{
			"txtvers=1",
			"ch=2",
			"cn=0,1,2,3",
				"da=true",
				"et=0,3,5",
				"ft=" + LegacyAirPlayFeatures,
				"md=0,1,2",
				"pw=false",
				"rhd=5.6.0.0",
				"sr=44100",
				"ss=16",
				"sv=false",
				"tp=UDP",
				"vn=65537",
				"vs=" + LegacyAirTunesVersion,
				"am=" + LegacyAirPlayModel,
				"sf=0x4",
				"vv=2",
				"pk=" + LegacyRaopPublicKey
			};
	}

	private static bool PacketAsksForProbe(byte[] packet)
	{
		if (packet.Length < 12)
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
				if (IsProbeName(name))
				{
					return true;
				}
			}
		}
		catch
		{
		}

		string payload = Encoding.ASCII.GetString(packet);
		return payload.Contains("_airplay", StringComparison.OrdinalIgnoreCase) ||
			payload.Contains("_raop", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsProbeName(string name)
	{
		return name.EndsWith(AirPlayServiceType, StringComparison.OrdinalIgnoreCase) ||
			name.EndsWith(RaopServiceType, StringComparison.OrdinalIgnoreCase) ||
			name.EndsWith(".local", StringComparison.OrdinalIgnoreCase);
	}

	private static IReadOnlyList<IPAddress> ResolveLocalIPv4Addresses()
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

	private static string ResolveDeviceId()
	{
		PhysicalAddress? address = NetworkInterface.GetAllNetworkInterfaces()
			.Where(networkInterface => networkInterface.OperationalStatus == OperationalStatus.Up)
			.Select(networkInterface => networkInterface.GetPhysicalAddress())
			.FirstOrDefault(physicalAddress => physicalAddress.GetAddressBytes().Length >= 6);

		byte[] bytes = address?.GetAddressBytes().Take(6).ToArray() ??
			SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName)).Take(6).ToArray();

		return string.Join(":", bytes.Select(value => value.ToString("X2")));
	}

	private static string SanitizeLabel(string value)
	{
		string cleaned = new string(value.Select(ch => char.IsLetterOrDigit(ch) || ch == '-' || ch == ' ' ? ch : '-').ToArray()).Trim();
		if (string.IsNullOrWhiteSpace(cleaned))
		{
			cleaned = "iMirror";
		}
		return cleaned.Length <= 40 ? cleaned : cleaned.Substring(0, 40);
	}

	private static string ReadName(byte[] packet, ref int offset)
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

	private static void WriteName(Stream stream, string name)
	{
		foreach (string label in name.TrimEnd('.').Split('.'))
		{
			byte[] bytes = Encoding.UTF8.GetBytes(label);
			stream.WriteByte((byte)bytes.Length);
			stream.Write(bytes);
		}
		stream.WriteByte(0);
	}

	private static void WriteUInt16(Stream stream, ushort value)
	{
		Span<byte> buffer = stackalloc byte[2];
		BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
		stream.Write(buffer);
	}

	private static void WriteUInt32(Stream stream, uint value)
	{
		Span<byte> buffer = stackalloc byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
		stream.Write(buffer);
	}

	private static byte[] BuildNameData(string name)
	{
		using MemoryStream stream = new MemoryStream();
		WriteName(stream, name);
		return stream.ToArray();
	}

	private static byte[] BuildSrvData(int port, string target)
	{
		using MemoryStream stream = new MemoryStream();
		WriteUInt16(stream, 0);
		WriteUInt16(stream, 0);
		WriteUInt16(stream, (ushort)port);
		WriteName(stream, target);
		return stream.ToArray();
	}

	private static byte[] BuildTxtData(IEnumerable<string> values)
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

	private void SetStatus(string message)
	{
		StatusText = message;
		AppLog.Write(message);
		StatusChanged?.Invoke(message);
	}

	private void ReportVideoDataReceived(int emitted)
	{
		long now = Stopwatch.GetTimestamp();
		long previous = Interlocked.Read(ref _lastVideoDataStatusTick);
		if (previous != 0 && (now - previous) < Stopwatch.Frequency)
		{
			return;
		}

		Interlocked.Exchange(ref _lastVideoDataStatusTick, now);
		SetStatus($"AirPlay stream data received ({emitted} H.264 payloads).");
	}

	public void Dispose()
	{
		_cts.Cancel();
		try
		{
			if (_mdnsClient != null)
			{
				byte[] goodbye = BuildMdnsResponse(ttlSeconds: 0);
				_mdnsClient.Send(goodbye, goodbye.Length, MulticastEndpoint);
			}
		}
		catch
		{
		}

		_airPlayListener?.Stop();
		_raopListener?.Stop();
		_dataListener?.Stop();
		_eventListener?.Stop();
		_timingListener?.Stop();
		_timingClient.Dispose();
		_audioReceiver.Dispose();
			lock (_mirrorKeyGate)
				{
					_mirrorDecryptor?.Dispose();
					_mirrorDecryptor = null;
					DisposeMirrorProbePoolLocked();
					_decryptorStreamConnectionId = null;
				}
		_mdnsClient?.Dispose();
		try
		{
			Task.WaitAll(
				new[] { _receiveTask, _announceTask, _airPlayAcceptTask, _raopAcceptTask, _dataAcceptTask, _eventAcceptTask, _timingAcceptTask }.Where(task => task != null).Cast<Task>().ToArray(),
				TimeSpan.FromSeconds(1.0));
		}
		catch
		{
		}
		_cts.Dispose();
	}

	private sealed record AirPlayRequest(
		string Method,
		string Target,
		string Protocol,
		string FirstLine,
		Dictionary<string, string> Headers,
		byte[] Body);

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
