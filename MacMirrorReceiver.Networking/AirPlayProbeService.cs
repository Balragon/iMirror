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
	private readonly MirrorDecryptorSelector _decryptors;
	private readonly MirrorPacketFramer _framer;

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
	private ulong? _announcedMirrorRtspTargetSessionId;
	private ulong? _announcedMirrorStreamConnectionId;

	// Last-connected-sender arbitration: the receiver has a single mirror decode pipeline, so two
	// senders streaming at once interleave and starve the gate (decrypt keyed to one stream, one
	// gate, one decoder). Track the active data connection and a monotonically increasing
	// generation; a newer data connection supersedes older ones so only the latest sender feeds it.
	private int _activeDataGeneration;
	private TcpClient? _activeDataClient;
	private bool _announcedIdentitylessMirrorSession;
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
		_decryptors = new MirrorDecryptorSelector(_fairPlay.TryDecryptAesKey, _pairing.TryGetSharedSecret, _fairPlay.TryGetKeyMessage, _writeDiagnostics);
		_framer = new MirrorPacketFramer(_decryptors, ResolveAdvertisedFps, AudioDiscoveryLogging, MaxMirrorPayloadBytes);
		_framer.StreamConfigReceived += config => StreamConfigReceived?.Invoke(config);
		_framer.VideoPayloadReceived += (payload, sourceTimestampNanos, receivedTick) => VideoPayloadReceived?.Invoke(payload, sourceTimestampNanos, receivedTick);
		_audioReceiver.StreamStarted += info => AudioStreamStarted?.Invoke(info.SampleRate, info.Channels, info.SamplesPerFrame);
		_audioReceiver.AudioFrameReceived += (frame, timestamp, sequence) => AudioFrameReceived?.Invoke(frame, timestamp, sequence);
		_setup = new AirPlaySetupContext(_audioReceiver, _decryptors.TryGetAudioCrypto, _dumpAudio);
		if (AudioDiscoveryLogging)
		{
			AppLog.Write("IMIRROR_AUDIO_DISCOVERY enabled: verbose data-stream logging only.");
		}
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

			int emitted = _framer.Emit(pending);
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
			_decryptors.ObserveRtspTargetSessionId(rtspTargetSessionId);
			AppLog.Write($"AirPlay SETUP RTSP target session id received: {rtspTargetSessionId}.");
		}

		if (AirPlaySetupPlist.TryReadData(request, "ekey", out byte[] encryptedKey))
		{
			mirrorSetup = true;
			_decryptors.ObserveEncryptedKey(encryptedKey);
			ResetDataStreamLogging();
			AppLog.Write($"AirPlay SETUP mirror ekey received: {encryptedKey.Length} bytes.");
			changed = true;
		}

		if (AirPlaySetupPlist.TryReadData(request, "eiv", out byte[] mirrorEiv))
		{
			mirrorSetup = true;
			_decryptors.ObserveEiv(mirrorEiv);
			AppLog.Write($"AirPlay SETUP mirror eiv received: {mirrorEiv.Length} bytes.");
			changed = true;
		}

		if (AirPlaySetupPlist.TryReadStreamConnectionId(request, out ulong streamConnectionId))
		{
			observedStreamConnectionId = streamConnectionId;
			if (_decryptors.ObserveStreamConnectionId(streamConnectionId))
			{
				ResetDataStreamLogging();
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

		if (changed && _decryptors.TryInitializeMirrorDecryptor())
		{
			ResetDataStreamLogging();
		}
	}

	private void ResetDataStreamLogging()
	{
		_framer.ResetNotices();
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

			_announcedMirrorRtspTargetSessionId = null;
			_announcedMirrorStreamConnectionId = null;
			_announcedIdentitylessMirrorSession = false;
			_mirrorSetupAddress = null;
			_mirrorOwnerConnectionId = 0;
		}

		_decryptors.ClearRtspTargetSessionId();
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
		_decryptors.Dispose();
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
