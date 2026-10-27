using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
	private const string AirPlayServiceType = AirPlayMdnsResponder.AirPlayServiceType;
	private const string RaopServiceType = AirPlayMdnsResponder.RaopServiceType;
	private const int MdnsPort = AirPlayMdnsResponder.DefaultPort;
	// Ports advertised in the legacy XML /info body; the live listeners use _ports.
	private const int DataPort = 7100;
	private const int EventPort = 7101;
	private const int TimingPort = 7102;
	// Largest mirror packet payload the framer accepts (a 4K IDR at a high bitrate is a few MiB).
	private const int MirrorPacketHeaderBytes = 128;
	private const int MaxMirrorPayloadBytes = 8 * 1024 * 1024;
	// The reassembly buffer must be able to hold one full header + payload, otherwise a packet
	// larger than the buffer could never be reassembled and the framer would resync into garbage.
	private const int MaxPendingDataBytes = MirrorPacketHeaderBytes + MaxMirrorPayloadBytes;
	private const int PendingDataTailBytes = 128 * 1024;
	// Bound on the exhaustive key/IV/offset candidate sweep (~4,000 AES-CTR passes over the
	// packet per attempt). It exists to recover from an unknown key derivation once per
	// session, not to run on every packet that fails H.264 normalization.
	private const int MaxDecryptorSweepAttemptsPerSession = 4;
	private const int StableDisplayWidth = 1920;
	private const int StableDisplayHeight = 1080;
	// GPU (MediaFoundation/D3D11) path advertised geometry. Set to the Mac's native 2560x1440 so the
	// sender does not rescale its desktop and the receiver decodes the native stream.
	private const int GpuDisplayWidth = 2560;
	private const int GpuDisplayHeight = 1440;
	private const int StableDisplayFps = 60;
	private const int GpuDisplayFps = 30;
	private const string LegacyAirTunesVersion = AirPlayRtspCodec.AirTunesVersion;
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

	// Evaluated on first use rather than at type load: constructing the service (e.g. in a unit
	// test) must not read the developer's real settings file.
	private static readonly Lazy<bool> QualityRenderModeLazy = new Lazy<bool>(
		() => RenderModeSettings.Load().EffectiveMode == ReceiverRenderModeSetting.Quality);
	private static bool QualityRenderMode => QualityRenderModeLazy.Value;
	// GPU engine advertises native 2560x1440 by default so the Mac sends native and the GPU decodes
	// it. The quality flag can still force the path on for local validation.
	private static readonly Lazy<bool> GpuDisplayAvailableLazy = new Lazy<bool>(
		() => (QualityRenderMode && RenderModeSettings.ExperimentalQualityEnabled) || RenderModeSettings.GpuVideoEngineEnabled);
	private static bool GpuDisplayAvailable => GpuDisplayAvailableLazy.Value;

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	// Limits for the RTSP/HTTP control connections. Every LAN host can open these ports, so a
	// request must not be able to pin an unbounded buffer or a thread for an unbounded time.
	// Real AirPlay bodies (SETUP plists, pair-* blobs, feedback) are well under 64 KiB.
	internal const int MaxControlConnections = 32;
	private int _activeControlConnections;
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
	private readonly AirPlayListenerPorts _ports;
	private readonly AirPlayAudioReceiver _audioReceiver = new AirPlayAudioReceiver();
	private readonly AirPlaySetupContext _setup;
	private readonly object _mirrorKeyGate = new object();

	private readonly AirPlayMdnsResponder _mdns;
	private TcpListener? _airPlayListener;
	private TcpListener? _raopListener;
	private string? _airPlayListenerBindError;
	private string? _raopListenerBindError;
	private TcpListener? _dataListener;
	private TcpListener? _eventListener;
	private TcpListener? _timingListener;

	// Expose bind results for startup diagnostics — read after StartAsync().
	public bool IsMdnsBound => _mdns.IsBound;
	public bool IsAirPlayListenerBound => _airPlayListener != null;
	public bool IsRaopListenerBound => _raopListener != null;
	public int AirPlayListenerPort => BoundPort(_airPlayListener) ?? _ports.AirPlay;
	public int RaopListenerPort => BoundPort(_raopListener) ?? _ports.Raop;

	private static int? BoundPort(TcpListener? listener)
	{
		return listener?.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : null;
	}
	public string? AirPlayListenerBindError => _airPlayListenerBindError;
	public string? RaopListenerBindError => _raopListenerBindError;

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
	private int _decryptorSweepAttempts;
	// Address of the host whose SETUP most recently established the session. Only this host may
	// open the TCP data stream; any other LAN host could otherwise supersede (and so terminate)
	// the active sender's video connection just by connecting to the data port.
	private IPAddress? _mirrorSetupAddress;
	// The control connection whose mirror SETUP established the session (0 = none). Only its close or
	// full TEARDOWN ends the session: the Mac runs the whole session on the RAOP port, and any other
	// LAN host can open and drop a connection on the AirPlay port at will.
	private long _mirrorOwnerConnectionId;
	private long _nextControlConnectionId;
	// Open control connections by id, so a sender whose session another sender took over can be
	// disconnected; otherwise it keeps its control connection and still shows "mirroring".
	private readonly System.Collections.Concurrent.ConcurrentDictionary<long, TcpClient> _controlConnections = new();
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
		bool dumpAudio = false,
		int mdnsPort = MdnsPort,
		AirPlayListenerPorts? listenerPorts = null)
	{
		_ports = listenerPorts ?? AirPlayListenerPorts.Default;
		_displayName = SanitizeLabel(displayName);
		_hostName = SanitizeLabel(Environment.MachineName) + ".local";
		_deviceId = ResolveDeviceId();
		_pairingId = Guid.NewGuid().ToString("D");
		_airPlayInstanceName = _displayName + "." + AirPlayServiceType;
		_raopInstanceName = _deviceId.Replace(":", "", StringComparison.Ordinal) + "@" + _displayName + "." + RaopServiceType;
		_mdns = new AirPlayMdnsResponder(
			mdnsPort,
			_hostName,
			_airPlayInstanceName,
			_raopInstanceName,
			() => AirPlayListenerPort,
			() => RaopListenerPort,
			BuildAirPlayTxt,
			BuildRaopTxt);
		_audioAdvertised = advertiseAudio;
		_writeDiagnostics = writeDiagnostics;
		_dumpAudio = dumpAudio;
		_audioReceiver.StreamStarted += info => AudioStreamStarted?.Invoke(info.SampleRate, info.Channels, info.SamplesPerFrame);
		_audioReceiver.AudioFrameReceived += (frame, timestamp, sequence) => AudioFrameReceived?.Invoke(frame, timestamp, sequence);
		_setup = new AirPlaySetupContext(_audioReceiver, TryGetAudioCrypto, _dumpAudio);
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
			encryptedKey = _encryptedMirrorAesKey == null ? null : MirrorH264Payloads.CopyOf(_encryptedMirrorAesKey);
			eiv = _mirrorEiv == null ? null : MirrorH264Payloads.CopyOf(_mirrorEiv);
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
		if (_mdns.IsBound)
		{
			return;
		}

		try
		{
			// A bind/join failure (most commonly UDP 5353 already held by Bonjour/iTunes) throws
			// with the responder still unbound, so the entry guard does not wedge a half-started
			// receiver and StartAsync can be retried.
			_mdns.Bind();

			_airPlayListener = TryStartListener(_ports.AirPlay, "AirPlay", error => _airPlayListenerBindError = error);
			_raopListener = TryStartListener(_ports.Raop, "RAOP", error => _raopListenerBindError = error);
			_dataListener = TryStartListener(_ports.Data, "AirPlay data", null);
			_eventListener = TryStartListener(_ports.Event, "AirPlay event", null);
			_timingListener = TryStartListener(_ports.Timing, "AirPlay timing", null);

			_mdns.StartLoops(_cts.Token);
			_airPlayAcceptTask = StartAcceptLoop(_airPlayListener, "AirPlay");
			_raopAcceptTask = StartAcceptLoop(_raopListener, "RAOP");
			_dataAcceptTask = StartAcceptLoop(_dataListener, "AirPlay data");
			_eventAcceptTask = StartAcceptLoop(_eventListener, "AirPlay event");
			_timingAcceptTask = StartAcceptLoop(_timingListener, "AirPlay timing");

			await _mdns.SendAnnouncementAsync(_cts.Token);
			SetStatus($"AirPlay receiver advertising as \"{_displayName}\".");
		}
		catch (Exception ex)
		{
			SetStatus("AirPlay receiver unavailable: " + ex.Message);
			AppLog.Write("AirPlay receiver failed to start: " + ex);
			CleanupPartialStart();
		}
	}

	// Undo a partially completed StartAsync so the entry guard does not wedge a half-started receiver
	// and a later StartAsync can retry cleanly (e.g. after UDP 5353 was momentarily held). Any
	// listeners that started are stopped; their accept loops and the mDNS receive loop terminate on
	// the resulting ObjectDisposedException. _cts is the shared session token and is left untouched so a
	// retry can reuse it.
	private void CleanupPartialStart()
	{
		_mdns.Reset();

		_airPlayListener?.Stop();
		_raopListener?.Stop();
		_dataListener?.Stop();
		_eventListener?.Stop();
		_timingListener?.Stop();
		_airPlayListener = null;
		_raopListener = null;
		_dataListener = null;
		_eventListener = null;
		_timingListener = null;
	}

	private static TcpListener? TryStartListener(int port, string label, Action<string?>? setBindError)
	{
		try
		{
			var listener = new TcpListener(IPAddress.Any, port);
			listener.Start();
			setBindError?.Invoke(null);
			AppLog.Write($"{label} receiver listener started on port {BoundPort(listener) ?? port}.");
			return listener;
		}
		catch (Exception ex)
		{
			setBindError?.Invoke(ex.Message);
			AppLog.Write($"{label} receiver listener unavailable on port {port}: {ex.Message}");
			return null;
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
		bool countedControlConnection = false;
		long connectionId = 0;
		using (client)
		{
			try
			{
				if (label.Contains("data", StringComparison.OrdinalIgnoreCase))
				{
					await HandleDataClientAsync(client);
					return;
				}

				if (Interlocked.Increment(ref _activeControlConnections) > MaxControlConnections)
				{
					Interlocked.Decrement(ref _activeControlConnections);
					AppLog.Write($"{label} control connection from {client.Client.RemoteEndPoint} rejected: {MaxControlConnections} connections already open.");
					return;
				}
				countedControlConnection = true;
				connectionId = Interlocked.Increment(ref _nextControlConnectionId);
				_controlConnections[connectionId] = client;

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
					AirPlayRequest? request = await AirPlayRtspCodec.ReadRequestAsync(stream, _cts.Token);
					if (request == null)
					{
						AppLog.Write($"{label} control connection closed by peer ({client.Client.RemoteEndPoint}) after {servedRequests} request(s).");
						EndMirrorSessionIfOwnedBy(connectionId, $"{label} control connection closed by peer");
						break;
					}

					servedRequests++;
					AppLog.Write($"{label} request from {client.Client.RemoteEndPoint}: {request.FirstLine}");
					SetStatus($"{label}: {request.Method} {request.Target}");

					byte[] response = BuildResponse(request, client.Client.RemoteEndPoint, connectionId);
					await stream.WriteAsync(response, _cts.Token);
					// The Mac also tears down just the audio stream mid-session (and SETUPs it again
					// later on the same connection), so only a TEARDOWN that covers the video stream
					// ends the session. The connection stays open: the sender closes it.
					if (request.Method == "TEARDOWN" && AirPlaySetupPlist.IsMirrorTeardown(request.Body))
					{
						EndMirrorSessionIfOwnedBy(connectionId, $"{label} TEARDOWN");
					}
					if (request.Headers.TryGetValue("Connection", out string? connection) &&
						connection.Contains("close", StringComparison.OrdinalIgnoreCase))
					{
						AppLog.Write($"{label} control connection closed on request (Connection: close) after {servedRequests} request(s).");
						EndMirrorSessionIfOwnedBy(connectionId, $"{label} control connection close requested");
						break;
					}
				}
			}
			catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
			{
				AppLog.Write($"{label} receiver client closed: {ex.Message}");
				EndMirrorSessionIfOwnedBy(connectionId, $"{label} receiver client closed");
			}
			catch (Exception ex)
			{
				// Anything else (a parser bug, a malformed request that slipped past the plist
				// guards, an event subscriber throwing) used to escape the fire-and-forget Task.Run
				// in AcceptLoopAsync: the connection vanished with no log line and the mirror
				// session state was never reset. Log it with the stack and tear down explicitly.
				AppLog.Write($"{label} receiver client failed with an unexpected error: {ex}");
				EndMirrorSessionIfOwnedBy(connectionId, $"{label} receiver client failed: {ex.GetType().Name}");
			}
			finally
			{
				if (connectionId != 0)
				{
					_controlConnections.TryRemove(connectionId, out _);
				}
				if (countedControlConnection)
				{
					Interlocked.Decrement(ref _activeControlConnections);
				}
			}
		}
	}

	/// <summary>
	/// Disconnects the sender whose session <paramref name="newOwnerConnectionId"/> just took over:
	/// its owning control connection and, when it is another host, that host's other control
	/// connections (the Mac keeps a second idle one). The replaced sender's video connection is
	/// already superseded by the new data stream; closing its control connection is what makes it
	/// stop and show the session as ended. These connections are no longer the owner, so their
	/// close does not end the new session.
	/// </summary>
	private void DisconnectReplacedSender(long previousOwnerConnectionId, long newOwnerConnectionId)
	{
		if (!_controlConnections.TryGetValue(previousOwnerConnectionId, out TcpClient? previous))
		{
			return;
		}

		IPAddress? previousAddress = TryGetRemoteAddress(previous);
		IPAddress? newAddress = _controlConnections.TryGetValue(newOwnerConnectionId, out TcpClient? current)
			? TryGetRemoteAddress(current)
			: null;
		bool otherHost = previousAddress != null && !PeerAddress.Matches(previousAddress, newAddress);

		int closed = 0;
		foreach (KeyValuePair<long, TcpClient> entry in _controlConnections)
		{
			bool replaced = entry.Key == previousOwnerConnectionId ||
				(otherHost && entry.Key != newOwnerConnectionId && PeerAddress.Matches(previousAddress, TryGetRemoteAddress(entry.Value)));
			if (replaced)
			{
				try
				{
					entry.Value.Close();
					closed++;
				}
				catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
				{
				}
			}
		}

		AppLog.Write($"AirPlay mirror session taken over by another SETUP; closed {closed} control connection(s) of the previous sender ({previousAddress?.ToString() ?? "unknown"}).");
	}

	private static IPAddress? TryGetRemoteAddress(TcpClient client)
	{
		try
		{
			return (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
		}
		catch (ObjectDisposedException)
		{
			return null;
		}
	}

	private async Task HandleDataClientAsync(TcpClient client)
	{
		EndPoint? remote = client.Client.RemoteEndPoint;
		IPAddress? setupAddress;
		lock (_mirrorKeyGate)
		{
			setupAddress = _mirrorSetupAddress;
		}
		if (!PeerAddress.Matches(setupAddress, (remote as IPEndPoint)?.Address))
		{
			AppLog.Write($"AirPlay data stream from {remote} rejected: it is not the host that performed SETUP ({setupAddress?.ToString() ?? "none"}).");
			return;
		}

		SetStatus("AirPlay data stream connected.");
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

		try
		{
			await ReadDataStreamAsync(client, remote, myDataGeneration);
		}
		finally
		{
			lock (_mirrorKeyGate)
			{
				if (ReferenceEquals(_activeDataClient, client))
				{
					_activeDataClient = null;
				}
			}
		}
	}

	private async Task ReadDataStreamAsync(TcpClient client, EndPoint? remote, int myDataGeneration)
	{
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

			if (pending.Count > MaxPendingDataBytes)
			{
				AppLog.Write($"AirPlay data buffer exceeded {MaxPendingDataBytes:N0} bytes without a complete packet; discarding all but the last {PendingDataTailBytes:N0} bytes.");
				pending.RemoveRange(0, pending.Count - PendingDataTailBytes);
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
			if (payloadSize < 0 || payloadSize > MaxMirrorPayloadBytes)
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

					if (MirrorH264Payloads.TryNormalizeClearH264Payload(videoPayload, out byte[] annexB, out string clearFormat))
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
							AppLog.Write($"AirPlay mirror video type={payloadType} payload could not be converted after decrypt attempt: size={payloadSize}, probe={MirrorH264Payloads.DescribeH264Probe(videoPayload)}.");
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
					AppLog.Write($"AUDIO-DISCOVERY data-stream packet: type={payloadType}, option={payloadOption}, size={payloadSize}, count={discoveryCount}, head={MirrorH264Payloads.DescribeHeadBytes(payload, 24)}");
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
				if (MirrorH264Payloads.TryNormalizeClearH264Payload(preview, out byte[] candidateAnnexB, out string candidateFormat))
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
					report.Add(decryptor.Name + "/" + MirrorH264Payloads.DescribeH264Probe(preview));
				}
			}

			if (_loggedDecryptCandidateReportTypes.Add(payloadType))
			{
				AppLog.Write($"AirPlay mirror stateful decryptor probes did not produce clear H.264 for type={payloadType}. tested={tested}, probes=" + string.Join(", ", report));
				shouldWriteDiagnostic = true;
			}

			encryptedKey = _encryptedMirrorAesKey == null ? null : MirrorH264Payloads.CopyOf(_encryptedMirrorAesKey);
			aesKey = _mirrorFairPlayAesKey == null ? null : MirrorH264Payloads.CopyOf(_mirrorFairPlayAesKey);
			sharedSecret = _mirrorSharedSecret == null ? null : MirrorH264Payloads.CopyOf(_mirrorSharedSecret);
			eiv = _mirrorEiv == null ? null : MirrorH264Payloads.CopyOf(_mirrorEiv);
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
			aesKey = _mirrorFairPlayAesKey == null ? null : MirrorH264Payloads.CopyOf(_mirrorFairPlayAesKey);
			sharedSecret = _mirrorSharedSecret == null ? null : MirrorH264Payloads.CopyOf(_mirrorSharedSecret);
			eiv = _mirrorEiv == null ? null : MirrorH264Payloads.CopyOf(_mirrorEiv);
			encryptedKey = _encryptedMirrorAesKey == null ? null : MirrorH264Payloads.CopyOf(_encryptedMirrorAesKey);
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
			if (_mirrorDecryptor != null)
			{
				// A decryptor is already selected for this session. A packet that still fails
				// H.264 normalization (e.g. a NAL type outside the plausibility whitelist) is a
				// dropped packet, not evidence that the key derivation changed; re-running the
				// full sweep here cost ~4,000 AES passes per packet and stalled the data loop.
				return false;
			}
			if (_decryptorSweepAttempts >= MaxDecryptorSweepAttemptsPerSession)
			{
				return false;
			}
			_decryptorSweepAttempts++;
			rtspTargetSessionId = _rtspTargetSessionId;
		}

		IReadOnlyList<AirPlayMirrorDecryptorCandidate> baseCandidates = AirPlayMirrorDecryptor.BuildCandidates(aesKey, sharedSecret, streamConnectionId.Value, eiv, rtspTargetSessionId);
		long searchStartTick = Stopwatch.GetTimestamp();
		if (TryFindMirrorDecryptor(baseCandidates, encryptedPayload, report, out AirPlayMirrorDecryptor? decryptor, out byte[] selectedAnnexB, out string selectedPayloadFormat, out int tested))
		{
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
			selectedName = decryptor.Name;
			selectedFormat = selectedPayloadFormat;
			long searchMs = (long)((Stopwatch.GetTimestamp() - searchStartTick) * 1000.0 / Stopwatch.Frequency);
			AppLog.Write($"AirPlay mirror decryptor auto-selected '{decryptor.Name}' for type={payloadType}, streamConnectionID={streamConnectionId.Value}, format={selectedPayloadFormat}, tested={tested}, payloadBytes={encryptedPayload.Length}, search={searchMs}ms.");
			return true;
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

	// Finds the first candidate, in BuildCandidates order with each base expanded over payload offsets,
	// whose decryption of encryptedPayload normalizes to clear H.264, and returns a decryptor that has
	// consumed encryptedPayload. A Mac sender's 'standard' key comes after ~2,400 candidates, and
	// decrypting the whole first IDR for each of them took seconds on the data-receive thread, so each
	// candidate is screened on a short decrypted prefix and only survivors decrypt the full payload.
	internal static bool TryFindMirrorDecryptor(
		IReadOnlyList<AirPlayMirrorDecryptorCandidate> baseCandidates,
		byte[] encryptedPayload,
		List<string> report,
		[NotNullWhen(true)] out AirPlayMirrorDecryptor? decryptor,
		out byte[] annexB,
		out string format,
		out int tested)
	{
		decryptor = null;
		annexB = Array.Empty<byte>();
		format = string.Empty;
		tested = 0;
		foreach (AirPlayMirrorDecryptorCandidate baseCandidate in baseCandidates)
		{
			foreach (AirPlayMirrorDecryptorCandidate candidate in ExpandPayloadOffsetCandidates(baseCandidate, encryptedPayload.Length))
			{
				tested++;
				byte[] preview = AirPlayMirrorDecryptor.DecryptPrefixOnce(candidate, encryptedPayload, ClearH264ProbeBytes);
				int decryptedLength = encryptedPayload.Length - Math.Max(0, candidate.PayloadOffset);
				if (MirrorH264Payloads.MayBeClearH264Payload(preview, decryptedLength))
				{
					AirPlayMirrorDecryptor candidateDecryptor = AirPlayMirrorDecryptor.Create(candidate);
					if (MirrorH264Payloads.TryNormalizeClearH264Payload(candidateDecryptor.Decrypt(encryptedPayload), out byte[] candidateAnnexB, out string candidateFormat))
					{
						decryptor = candidateDecryptor;
						annexB = candidateAnnexB;
						format = candidateFormat;
						return true;
					}

					candidateDecryptor.Dispose();
				}

				if (report.Count < 32)
				{
					report.Add(candidate.Name + "/" + MirrorH264Payloads.DescribeH264Probe(preview));
				}
			}
		}

		return false;
	}

	// TryNormalizeClearH264Payload settles on a first NAL unit using only this many leading bytes (an
	// AVCC length + NAL header at each probed offset, or a leading Annex B start code), and
	// DescribeH264Probe reads no further either.
	internal const int ClearH264ProbeBytes = MirrorH264Payloads.MaxH264PrefixProbeBytes + 5;

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

	internal static IEnumerable<AirPlayMirrorDecryptorCandidate> ExpandPayloadOffsetCandidates(AirPlayMirrorDecryptorCandidate candidate, int payloadLength)
	{
		yield return candidate;
		if (candidate.InitialSkipBytes != 0 || candidate.PayloadOffset != 0)
		{
			yield break;
		}

		int maxOffset = Math.Min(MirrorH264Payloads.MaxH264PrefixProbeBytes, Math.Max(0, payloadLength - 5));
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
			int start = MirrorH264Payloads.FindStartCode(annexB, offset, annexB.Length - 3);
			if (start < 0)
			{
				break;
			}
			int nalOffset = start + MirrorH264Payloads.StartCodeLengthAt(annexB, start);
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





	private void ObserveSetupRequest(byte[] requestBody, string target, long connectionId)
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
			AppLog.Write("AUDIO-DISCOVERY SETUP plist: " + AirPlaySetupPlist.DescribePlistObject(request, 0));
		}

		bool changed = false;
		ulong? observedRtspTargetSessionId = null;
		ulong? observedStreamConnectionId = null;
		bool mirrorSetup = AirPlaySetupPlist.ContainsMirrorVideoStream(request);
		if (AirPlaySetupPlist.TryReadRtspTargetSessionId(target, out ulong rtspTargetSessionId))
		{
			observedRtspTargetSessionId = rtspTargetSessionId;
			lock (_mirrorKeyGate)
			{
				_rtspTargetSessionId = rtspTargetSessionId;
			}
			AppLog.Write($"AirPlay SETUP RTSP target session id received: {rtspTargetSessionId}.");
		}

		if (AirPlaySetupPlist.TryReadData(request, "ekey", out byte[] encryptedKey))
		{
			mirrorSetup = true;
			lock (_mirrorKeyGate)
			{
				_mirrorDecryptor?.Dispose();
				_mirrorDecryptor = null;
				DisposeMirrorProbePoolLocked();
				_streamConnectionId = null;
				_decryptorStreamConnectionId = null;
				_encryptedMirrorAesKey = MirrorH264Payloads.CopyOf(encryptedKey);
				_mirrorFairPlayAesKey = null;
				_mirrorSharedSecret = null;
				_loggedWaitingForMirrorDecryptor = false;
				_loggedDecryptPayloadFailureTypes.Clear();
				_loggedDecryptCandidateReportTypes.Clear();
				_decryptorSweepAttempts = 0;
				_loggedSkippedPacketTypes.Clear();
				_loggedDecryptorInUse = false;
				_mirrorDiagnosticWritten = false;
			}
			AppLog.Write($"AirPlay SETUP mirror ekey received: {encryptedKey.Length} bytes.");
			changed = true;
		}

		if (AirPlaySetupPlist.TryReadData(request, "eiv", out byte[] mirrorEiv))
		{
			mirrorSetup = true;
			lock (_mirrorKeyGate)
			{
				_mirrorEiv = MirrorH264Payloads.CopyOf(mirrorEiv);
			}
			AppLog.Write($"AirPlay SETUP mirror eiv received: {mirrorEiv.Length} bytes.");
			changed = true;
		}

		if (AirPlaySetupPlist.TryReadStreamConnectionId(request, out ulong streamConnectionId))
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
					_decryptorSweepAttempts = 0;
					_loggedSkippedPacketTypes.Clear();
					_loggedDecryptorInUse = false;
					_mirrorDiagnosticWritten = false;
				}
				_streamConnectionId = streamConnectionId;
			}
			AppLog.Write($"AirPlay SETUP streamConnectionID received: {streamConnectionId}.");
			changed = true;
		}

		if (mirrorSetup)
		{
			// Before announcing, so the previous owner closing now cannot end the new session.
			long previousOwnerConnectionId;
			lock (_mirrorKeyGate)
			{
				previousOwnerConnectionId = _mirrorOwnerConnectionId;
				_mirrorOwnerConnectionId = connectionId;
			}

			if (previousOwnerConnectionId != 0 && previousOwnerConnectionId != connectionId)
			{
				DisconnectReplacedSender(previousOwnerConnectionId, connectionId);
			}
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

	private void EndMirrorSessionIfOwnedBy(long connectionId, string reason)
	{
		if (connectionId == 0 || !ResetMirrorSessionState(connectionId))
		{
			return;
		}

		AppLog.Write("AirPlay mirror session ended: " + reason + ".");
		MirrorSessionEnded?.Invoke();
	}

	private bool ResetMirrorSessionState(long ownerConnectionId)
	{
		bool hadAnnouncedSession;
		lock (_mirrorKeyGate)
		{
			if (_mirrorOwnerConnectionId != ownerConnectionId)
			{
				return false;
			}

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
			_mirrorSetupAddress = null;
			_mirrorOwnerConnectionId = 0;
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

				encryptedKey = _encryptedMirrorAesKey == null ? null : MirrorH264Payloads.CopyOf(_encryptedMirrorAesKey);
				streamConnectionId = _streamConnectionId;
				eiv = _mirrorEiv == null ? null : MirrorH264Payloads.CopyOf(_mirrorEiv);
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
			_mirrorFairPlayAesKey = MirrorH264Payloads.CopyOf(aesKey);
			_mirrorSharedSecret = MirrorH264Payloads.CopyOf(sharedSecret);
			_mirrorDecryptorProbePool = probePool;
			_decryptorStreamConnectionId = streamConnectionId.Value;
			_loggedWaitingForMirrorDecryptor = false;
			_loggedDecryptPayloadFailureTypes.Clear();
			_loggedDecryptCandidateReportTypes.Clear();
			_decryptorSweepAttempts = 0;
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

	private byte[] BuildResponse(AirPlayRequest request, EndPoint? remoteEndPoint, long connectionId)
	{
		string method = request.Method.ToUpperInvariant();
		string target = request.Target.ToLowerInvariant();
		if (method == "OPTIONS")
		{
			return AirPlayRtspCodec.BuildRtspResponse(request, 200, "OK", Array.Empty<byte>(), "text/plain", new Dictionary<string, string>
			{
				["Public"] = "ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, TEARDOWN, OPTIONS, GET_PARAMETER, SET_PARAMETER"
			});
			}
			if (method == "GET" && (target == "/info" || target == "/server-info"))
			{
				byte[] body = BuildInfoBinaryPlist();
				return AirPlayRtspCodec.BuildRtspResponse(request, 200, "OK", body, "application/x-apple-binary-plist");
			}
			if (method == "POST" && target.Contains("pair-setup", StringComparison.OrdinalIgnoreCase))
			{
				AirPlayRtspCodec.LogPairBody("pair-setup", request.Body);
				return AirPlayRtspCodec.BuildRtspResponse(request, 200, "OK", _pairing.BuildPairSetupResponse(request.Body), "application/octet-stream");
			}
			if (method == "POST" && target.Contains("pair-verify", StringComparison.OrdinalIgnoreCase))
			{
				AirPlayRtspCodec.LogPairBody("pair-verify", request.Body);
				return AirPlayRtspCodec.BuildRtspResponse(request, 200, "OK", _pairing.BuildPairVerifyResponse(request.Body), "application/octet-stream");
			}
			if (method == "POST" && target.Contains("fp-setup", StringComparison.OrdinalIgnoreCase))
			{
				AirPlayRtspCodec.LogPairBody("fp-setup", request.Body);
				return AirPlayRtspCodec.BuildRtspResponse(request, 200, "OK", _fairPlay.BuildSetupResponse(request.Body), "application/octet-stream");
			}
			if (method == "SETUP")
			{
				AirPlayRtspCodec.LogPairBody("setup", request.Body);
				ObserveSetupRequest(request.Body, request.Target, connectionId);
				IPAddress? remoteAddress = (remoteEndPoint as IPEndPoint)?.Address;
				lock (_mirrorKeyGate)
				{
					_mirrorSetupAddress = remoteAddress;
				}
				byte[] body = _setup.BuildSetupResponse(request.Body, remoteAddress);
				return AirPlayRtspCodec.BuildRtspResponse(request, 200, "OK", body, "application/x-apple-binary-plist", new Dictionary<string, string>
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
				return AirPlayRtspCodec.BuildRtspResponse(request, 200, "OK", Array.Empty<byte>(), "text/plain", recordHeaders);
			}
			if (method == "TEARDOWN")
			{
				// Some senders keep posting feedback after RTSP TEARDOWN on the shared RAOP
				// control session. Acknowledge it without tearing down the active mirror path.
				return AirPlayRtspCodec.BuildRtspResponse(request, 200, "OK", Array.Empty<byte>(), "text/plain");
			}
			if (method is "FLUSH" or "SET_PARAMETER" || target.Contains("feedback", StringComparison.OrdinalIgnoreCase))
			{
				return AirPlayRtspCodec.BuildRtspResponse(request, 200, "OK", Array.Empty<byte>(), "text/plain");
			}
			return AirPlayRtspCodec.BuildRtspResponse(request, 501, "Not Implemented", Array.Empty<byte>(), "text/plain");
		}

	private byte[] BuildGetParameterResponse(AirPlayRequest request)
	{
		string bodyText = request.Body.Length == 0 ? string.Empty : Encoding.UTF8.GetString(request.Body);
		AppLog.Write("AirPlay get-parameter body=" + bodyText.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal));

		if (bodyText.Contains("volume", StringComparison.OrdinalIgnoreCase))
		{
			return AirPlayRtspCodec.BuildRtspResponse(request, 200, "OK", Encoding.ASCII.GetBytes("volume: 0.0\r\n"), "text/parameters");
		}

		return AirPlayRtspCodec.BuildRtspResponse(request, 200, "OK", Array.Empty<byte>(), "text/parameters");
	}


	private byte[] BuildInfoBinaryPlist()
	{
		ulong features = ((ulong)0x1E << 32) | 0x5A7FFFF7UL;
		byte[] publicKey = Convert.FromHexString(_pairing.PublicKeyHex);
		byte[] txtAirPlay = AirPlayMdnsResponder.BuildTxtData(BuildAirPlayTxt());
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
			_mdns.SendGoodbye();
		}
		catch
		{
		}

		_airPlayListener?.Stop();
		_raopListener?.Stop();
		_dataListener?.Stop();
		_eventListener?.Stop();
		_timingListener?.Stop();
		_audioReceiver.Dispose();
			lock (_mirrorKeyGate)
				{
					_mirrorDecryptor?.Dispose();
					_mirrorDecryptor = null;
					DisposeMirrorProbePoolLocked();
					_decryptorStreamConnectionId = null;
				}
		_mdns.Dispose();
		try
		{
			Task.WaitAll(
				_mdns.BackgroundTasks.Concat(new[] { _airPlayAcceptTask, _raopAcceptTask, _dataAcceptTask, _eventAcceptTask, _timingAcceptTask }.Where(task => task != null).Cast<Task>()).ToArray(),
				TimeSpan.FromSeconds(1.0));
		}
		catch
		{
		}
		_cts.Dispose();
	}
}
