using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using MacMirrorReceiver.Audio;
using MacMirrorReceiver.Models;
using MacMirrorReceiver.Networking;
using MacMirrorReceiver.Protocol;
using MacMirrorReceiver.Video;

namespace MacMirrorReceiver;

public partial class MainWindow : Window
{
	private enum RenderMode
	{
		Auto = 0,
		Responsive = 1,
		Native4K = 2,
		Quality = 3
	}

	private const int RealtimeMaxRenderWidth = 3840;

	private const int ResponsiveMaxRenderWidth = 1920;

	private const int QualityMaxRenderWidth = 2560;

	private const int RealtimeMaxRenderFps = 60;

	private const int HighResolutionMaxRenderFps = 30;

	private const int MaxAutoReconnectAttempts = 5;

	private const int MaxPendingVideoPacketsBeforeSink = 8;

	private const long MaxPendingVideoBytesBeforeSink = 8L * 1024L * 1024L;

	private const int DefaultAudioSyncOffsetMilliseconds = 120;

	private const int MinAudioSyncTargetMilliseconds = 120;

	private const int MaxAudioSyncTargetMilliseconds = 220;

	private const double RemoteCursorMinScale = 0.35;

	private const double RemoteCursorMaxScale = 1.0;

	private static readonly TimeSpan AutoReconnectDelay = TimeSpan.FromSeconds(2.0);

	private static readonly bool SyntheticCursorOverlayEnabled = false;

	private static readonly int AudioSyncOffsetMilliseconds = ReadBoundedIntegerEnvironment(
		"IMIRROR_AUDIO_SYNC_OFFSET_MS",
		DefaultAudioSyncOffsetMilliseconds,
		60,
		220);

	private static readonly RenderModeSettingsSnapshot StartupRenderModeSettings = RenderModeSettings.Load();

	private static readonly bool QualityRenderModeEnabled = StartupRenderModeSettings.EffectiveMode == ReceiverRenderModeSetting.Quality;

#if HIGH_RESOLUTION_D3D
	private static readonly bool GpuQualityRequested = RenderModeSettings.ExperimentalQualityEnabled
		|| RenderModeSettings.GpuVideoEngineEnabled;
#else
	private static readonly bool GpuQualityRequested = false;
#endif

	private static readonly bool QualityPathAvailable = QualityRenderModeEnabled
		&& GpuQualityRequested;

	private static int ReadBoundedIntegerEnvironment(string name, int defaultValue, int minValue, int maxValue)
	{
		string? value = Environment.GetEnvironmentVariable(name);
		if (string.IsNullOrWhiteSpace(value) || !int.TryParse(value, out int parsed))
		{
			return defaultValue;
		}
		return Math.Clamp(parsed, minValue, maxValue);
	}

	private readonly ObservableCollection<MirrorDevice> _devices = new ObservableCollection<MirrorDevice>();

	private readonly MdnsBrowser _browser = new MdnsBrowser();

	private readonly AirPlayProbeService _airPlayProbe = new AirPlayProbeService("iMirror");

	private readonly H264AnnexBStreamGate _h264Gate = new H264AnnexBStreamGate();

	private readonly object _h264GateLock = new object();

	private readonly object _frameGate = new object();

	private readonly object _cursorGate = new object();

	private readonly object _pendingVideoGate = new object();

	private readonly Queue<PendingVideoPayload> _pendingVideoBeforeSink = new Queue<PendingVideoPayload>();

	private readonly object _audioGate = new object();

	// 3s warmup grace: BeginWarmup fires at decoder (re)start, but frames only flow after FFmpeg/MF
	// spin-up (~0.8s) plus decryptor settling, so a shorter grace lets a startup straggler inflate
	// the first window's max. Measured on a real-device run (first-window straggler ~248ms at ~1.6s).
	private readonly LatencyWindow _receiveToPresentLatencyWindow = new LatencyWindow(TimeSpan.FromSeconds(10.0), TimeSpan.FromSeconds(3.0));

	private MirrorClient? _client;

	private FfmpegDecoder? _decoder;

	private FfmpegAudioDecoder? _audioDecoder;

	private WasapiAudioOutput? _audioOutput;

	private WriteableBitmap? _bitmap;

#if HIGH_RESOLUTION_D3D
	private D3D11SwapChainVideoPresenter? _highResolutionD3DPresenter;

	private MediaFoundationD3D11Decoder? _mediaFoundationD3DDecoder;

	private D3D11VideoFrame? _pendingD3DFrame;
#endif

	private VideoFrame? _pendingFrame;

	private CursorState? _pendingCursorState;

	private CursorState? _lastPresentedCursorState;

	private StreamConfig? _streamConfig;

	private long _videoPackets;

	private long _videoBytes;

	private long _cursorMessages;

	private long _decodedFrames;

	private long _renderedFrames;

	private long _renderDroppedFrames;

	private long _latestReceiveToRenderMs;

	private long _latestDecodeToRenderMs;

	private long _lastDiagnosticsTick;

	private long _lastGateLogTick;

	private long _lastRenderLogTick;

	private long _lastVideoHealthLogTick;

	private long _lastVideoHealthPackets;

	private long _lastVideoHealthDecodedFrames;

	private long _lastVideoHealthRenderedFrames;

	private long _pendingVideoBytesBeforeSink;

	private long _pendingVideoDroppedBeforeSink;

	private long _lastPendingVideoDropLogTick;

	private long _decoderRestarts;

	private long _audioFramesReceived;

	private long _audioFramesQueued;

	private long _audioPcmFrames;

	private long _audioPcmBytes;

	// Set when the GPU engine faults at runtime (device-lost/TDR); the session then stays on the
	// software decoder until the next connection. Reset in ResetStreamStateForNewConnection.
	private bool _gpuPathDisabledThisSession;

	private CancellationTokenSource? _videoWatchdogCts;

	private int _renderQueued;

	private int _cursorQueued;

	private int _diagnosticsQueued;

	private int _decoderOutputFps;

	private int _decoderMaxRenderWidth = RealtimeMaxRenderWidth;

	private int _autoRenderResizeVersion;

	private int _connectionGeneration;

	private int _autoReconnectAttempts;

	private IPEndPoint? _lastEndpoint;

	private string? _lastPin;

	private string _decoderStatus = "not started";

	private string _audioStatus = "not started";

	private int _audioSampleRate = 44100;

	private int _audioChannels = 2;

	private int _audioSamplesPerFrame = 480;

	private readonly record struct PendingVideoPayload(byte[] Payload, ulong SourceTimestampNanos, long ReceivedTick);

	private string _statusText = "Waiting for iPhone or iPad...";

	private bool _isFullscreen;

	private bool _isConnecting;

	private bool _manualDisconnectRequested = true;

	private ReceiverRenderModeSetting _pendingRenderModeSetting = StartupRenderModeSettings.PersistedMode;

	private bool _renderModeSettingsUiReady;

	private WindowStyle _previousWindowStyle;

	private WindowState _previousWindowState;

	public MainWindow()
	{
		AppLog.Write("MainWindow constructor entered.");
		InitializeComponent();
		AppLog.Write("MainWindow InitializeComponent returned.");
#if HIGH_RESOLUTION_D3D
		VideoStage.SizeChanged += VideoStage_SizeChanged;
#endif
		DeviceComboBox.ItemsSource = _devices;
		DeviceComboBox.SelectionChanged += delegate
		{
			UpdateEndpointFieldsFromSelectedDevice();
		};
		AirPlayProbeTextBlock.Text = _airPlayProbe.StatusText;
		_airPlayProbe.StatusChanged += delegate(string message)
		{
			base.Dispatcher.BeginInvoke(new Action(delegate
			{
				AirPlayProbeTextBlock.Text = message;
			}), DispatcherPriority.Background);
		};
		_airPlayProbe.StreamConfigReceived += HandleAirPlayStreamConfig;
		_airPlayProbe.VideoPayloadReceived += HandleAirPlayVideoPayload;
		_airPlayProbe.AudioStreamStarted += HandleAirPlayAudioStreamStarted;
		_airPlayProbe.AudioFrameReceived += HandleAirPlayAudioFrame;
		InitializeRenderModeSettingsUi();
		RenderModeComboBox.SelectedIndex = 0;
		UpdateRenderModeDetail();
		UpdateRenderScalingMode();
		UpdateDiagnostics();
		UpdateReceiverChrome();
		_browser.DeviceFound += delegate(MirrorDevice device)
		{
			MirrorDevice device2 = device;
			base.Dispatcher.Invoke(delegate
			{
				if (!_devices.Any((MirrorDevice existing) => existing.EndpointKey == device2.EndpointKey))
				{
					_devices.Add(device2);
						ComboBox deviceComboBox = DeviceComboBox;
						if (deviceComboBox.SelectedItem == null)
						{
							object obj = (deviceComboBox.SelectedItem = device2);
						}
							SetStatus("Legacy sender found. Enter its PIN if you want to use it.");
						AppLog.Write("mDNS device found: " + device2.DisplayName);
						UpdateDiagnostics();
					}
				});
			};
	}

	private async void Window_Loaded(object sender, RoutedEventArgs e)
	{
		AppLog.Write("MainWindow Loaded entered.");
		await _browser.StartAsync();
		await _airPlayProbe.StartAsync();
	}

	private async void Window_Closing(object? sender, CancelEventArgs e)
	{
		await DisconnectAsync(null, userRequested: true);
		_airPlayProbe.Dispose();
		_browser.Dispose();
	}

	private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		RefreshRemoteCursorPosition();
		if (CurrentRenderMode != RenderMode.Auto || _streamConfig == null || _decoder == null)
		{
			return;
		}
		int version = Interlocked.Increment(ref _autoRenderResizeVersion);
		_ = Task.Delay(450).ContinueWith(_ =>
		{
			base.Dispatcher.BeginInvoke(new Action(delegate
			{
				if (version == _autoRenderResizeVersion)
				{
					RestartDecoderIfRenderWidthChanged("window size changed");
				}
			}), DispatcherPriority.Background);
		});
	}

#if HIGH_RESOLUTION_D3D
	private void VideoStage_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		UpdateHighResolutionD3DPresenterLayout();
		RefreshRemoteCursorPosition();
	}
#endif

	private async void ConnectButton_Click(object sender, RoutedEventArgs e)
	{
		await ConnectToSelectedAsync();
	}

	private void HandleAirPlayStreamConfig(StreamConfig config)
	{
		base.Dispatcher.BeginInvoke(new Action(delegate
		{
			bool sameStreamConfig = _streamConfig != null
				&& (_decoder != null
#if HIGH_RESOLUTION_D3D
					|| _mediaFoundationD3DDecoder != null
#endif
				)
				&& _streamConfig.Width == config.Width
				&& _streamConfig.Height == config.Height
				&& _streamConfig.Fps == config.Fps
				&& string.Equals(_streamConfig.Codec, config.Codec, StringComparison.OrdinalIgnoreCase);
			if (sameStreamConfig)
			{
#if HIGH_RESOLUTION_D3D
				if (_mediaFoundationD3DDecoder != null)
				{
					AppLog.Write("AirPlay stream config repeated for high-resolution D3D path; refreshing renderer for new sender session.");
				}
				else
#endif
				{
					return;
				}
			}

#if HIGH_RESOLUTION_D3D
			if (sameStreamConfig && _mediaFoundationD3DDecoder == null)
			{
				return;
			}
#endif

			if (sameStreamConfig)
			{
#if HIGH_RESOLUTION_D3D
				RequireH264Keyframe();
#else
				ResetH264Gate();
#endif
				StartFreshDecoder(config, "AirPlay session refresh");
				UpdateDiagnostics();
			}
			else
			{
				ResetH264Gate();
				StartDecoder(config);
			}
		}), DispatcherPriority.Background);
	}

	private void HandleAirPlayVideoPayload(byte[] payload, ulong sourceTimestampNanos, long receivedTick)
	{
		ProcessVideoPayload(payload, sourceTimestampNanos, receivedTick, countReceived: true, allowPreSinkBuffer: true);
	}

	private void HandleAirPlayAudioStreamStarted(int sampleRate, int channels, int samplesPerFrame)
	{
		lock (_audioGate)
		{
			bool formatChanged = _audioDecoder != null
				&& (_audioSampleRate != sampleRate || _audioChannels != channels || _audioSamplesPerFrame != samplesPerFrame);
			_audioSampleRate = sampleRate > 0 ? sampleRate : 44100;
			_audioChannels = channels > 0 ? channels : 2;
			_audioSamplesPerFrame = samplesPerFrame > 0 ? samplesPerFrame : 480;
			if (formatChanged)
			{
				StopAudioPipelineLocked();
			}
			StartAudioPipelineLocked("AirPlay audio stream setup");
		}
	}

	private void HandleAirPlayAudioFrame(byte[] frame, uint rtpTimestamp, ushort sequence)
	{
		Interlocked.Increment(ref _audioFramesReceived);
		long receivedTick = Stopwatch.GetTimestamp();
		FfmpegAudioDecoder? decoder;
		lock (_audioGate)
		{
			if (_audioDecoder == null)
			{
				StartAudioPipelineLocked("first AirPlay audio frame");
			}
			decoder = _audioDecoder;
		}

		if (decoder == null)
		{
			QueueDiagnosticsUpdate();
			return;
		}

		if (decoder.QueueFrame(frame, rtpTimestamp, sequence, receivedTick))
		{
			Interlocked.Increment(ref _audioFramesQueued);
		}
		QueueDiagnosticsUpdate();
	}

	private void StartAudioPipelineLocked(string reason)
	{
		if (_audioDecoder != null)
		{
			return;
		}

		try
		{
			var output = new WasapiAudioOutput(_audioSampleRate, _audioChannels);
			output.StatusChanged += HandleAudioStatus;
			_audioOutput = output;

			var decoder = new FfmpegAudioDecoder(_audioSampleRate, _audioChannels, _audioSamplesPerFrame);
			decoder.StatusChanged += HandleAudioStatus;
			decoder.PcmFrameDecoded += HandleAudioPcmFrame;
			_audioDecoder = decoder;
			decoder.Start();

			_audioStatus = $"started ({_audioSampleRate}Hz/{_audioChannels}ch/{_audioSamplesPerFrame}spf)";
			AppLog.Write("Audio pipeline started for " + reason + ".");
			QueueDiagnosticsUpdate();
		}
		catch (Exception ex)
		{
			_audioStatus = "failed: " + ex.Message;
			AppLog.Write("Audio pipeline failed to start: " + ex);
			StopAudioPipelineLocked();
			QueueDiagnosticsUpdate();
		}
	}

	private void HandleAudioStatus(string message)
	{
		base.Dispatcher.BeginInvoke(new Action(delegate
		{
			_audioStatus = message;
			AppLog.Write("Audio status: " + message);
			UpdateDiagnostics();
		}), DispatcherPriority.Background);
	}

	private void HandleAudioPcmFrame(AudioPcmFrame frame)
	{
		try
		{
			WasapiAudioOutput? output;
			lock (_audioGate)
			{
				output = _audioOutput;
			}
			if (output == null)
			{
				return;
			}

			output.SetSyncTargetLatencyMilliseconds(ResolveAudioSyncTargetLatencyMilliseconds());
			output.Submit(frame);
			Interlocked.Increment(ref _audioPcmFrames);
			Interlocked.Add(ref _audioPcmBytes, frame.ByteCount);
		}
		finally
		{
			frame.Release();
		}
	}

	private void StopAudioPipeline()
	{
		lock (_audioGate)
		{
			StopAudioPipelineLocked();
		}
	}

	private void StopAudioPipelineLocked()
	{
		FfmpegAudioDecoder? decoder = _audioDecoder;
		WasapiAudioOutput? output = _audioOutput;
		_audioDecoder = null;
		_audioOutput = null;
		try
		{
			decoder?.Dispose();
		}
		catch
		{
		}
		try
		{
			output?.Dispose();
		}
		catch
		{
		}
	}

	private async Task ConnectToSelectedAsync()
	{
		try
		{
			IPEndPoint endpoint = ResolveEndpoint();
			string pin = PinTextBox.Text.Trim();
			if (pin.Length != 6 || !pin.All(char.IsDigit))
			{
				SetStatus("Enter the 6-digit sender PIN.");
				return;
			}
			int generation = Interlocked.Increment(ref _connectionGeneration);
			_lastEndpoint = endpoint;
			_lastPin = pin;
			_autoReconnectAttempts = 0;
			_manualDisconnectRequested = false;
			await ConnectToEndpointAsync(endpoint, pin, generation, isReconnect: false);
		}
		catch (Exception ex)
		{
			await DisconnectAsync("Connect failed: " + ex.Message);
		}
	}

	private async Task<bool> ConnectToEndpointAsync(IPEndPoint endpoint, string pin, int generation, bool isReconnect)
	{
		if (_isConnecting)
		{
			return false;
		}

		_isConnecting = true;
		UpdateConnectionButtons(connected: false);
		try
		{
			await DisconnectAsync(null, revealPanel: !isReconnect);
			if (generation != _connectionGeneration)
			{
				return false;
			}

			ResetStreamStateForNewConnection();
			SetConnectedUi(connected: true);
			string verb = isReconnect ? "Reconnecting" : "Connecting";
				SetStatus($"{verb} to legacy sender...");
			AppLog.Write($"{verb} to {endpoint.Address}:{endpoint.Port}.");

			var client = new MirrorClient();
			_client = client;
			_client.StatusChanged += delegate(string message)
			{
				string message2 = message;
				base.Dispatcher.Invoke(delegate
				{
					SetStatus(message2);
					AppLog.Write("Client status: " + message2);
					UpdateDiagnostics();
				});
			};
			_client.ConnectionClosed += delegate
			{
				base.Dispatcher.BeginInvoke(new Action(async delegate
				{
					if (ReferenceEquals(_client, client))
					{
						await HandleUnexpectedDisconnectAsync(generation);
					}
				}));
			};
				_client.ConfigReceived += delegate(StreamConfig config)
				{
					StreamConfig config2 = config;
					base.Dispatcher.BeginInvoke(new Action(delegate
					{
						if (ReferenceEquals(_client, client))
						{
							StartDecoder(config2);
						}
					}));
				};
					_client.VideoReceived += delegate(byte[] payload, ulong sourceTimestampNanos, long receivedTick)
					{
						HandleVideoPayload(payload, sourceTimestampNanos, receivedTick);
					};
					if (SyntheticCursorOverlayEnabled)
					{
						_client.CursorReceived += delegate(CursorState cursorState, ulong sourceTimestampNanos, long receivedTick)
						{
							HandleCursorState(cursorState, sourceTimestampNanos, receivedTick);
						};
					}

				await _client.ConnectAsync(endpoint, pin);
			return true;
		}
		catch (Exception ex)
		{
			string verb = isReconnect ? "Reconnect" : "Connect";
			if (generation == _connectionGeneration)
			{
				await DisconnectAsync($"{verb} failed: " + ex.Message, revealPanel: !isReconnect);
			}
			return false;
		}
		finally
		{
			_isConnecting = false;
			UpdateConnectionButtons(_client != null);
		}
	}

		private void ResetStreamStateForNewConnection()
		{
			StopVideoWatchdog();
				_videoPackets = 0L;
				_videoBytes = 0L;
				_cursorMessages = 0L;
			_decodedFrames = 0L;
		_renderedFrames = 0L;
		_renderDroppedFrames = 0L;
		_latestReceiveToRenderMs = 0L;
		_latestDecodeToRenderMs = 0L;
		_receiveToPresentLatencyWindow.Reset();
		_lastRenderLogTick = 0L;
			_lastVideoHealthLogTick = 0L;
			_lastVideoHealthPackets = 0L;
			_lastVideoHealthDecodedFrames = 0L;
			_lastVideoHealthRenderedFrames = 0L;
			_decoderRestarts = 0L;
			_gpuPathDisabledThisSession = false;
			_lastGateLogTick = 0L;
			_pendingVideoDroppedBeforeSink = 0L;
			_lastPendingVideoDropLogTick = 0L;
			_audioFramesReceived = 0L;
			_audioFramesQueued = 0L;
			_audioPcmFrames = 0L;
			_audioPcmBytes = 0L;
			_audioStatus = "waiting for stream";
			StopAudioPipeline();
				_streamConfig = null;
				ResetRemoteCursorState();
				_decoderStatus = "waiting for stream config";
			ClearPendingVideoBeforeSink();
			ResetH264Gate();
			UpdateDiagnostics();
		}

	private void HandleVideoPayload(byte[] payload, ulong sourceTimestampNanos, long receivedTick)
	{
		ProcessVideoPayload(payload, sourceTimestampNanos, receivedTick, countReceived: true, allowPreSinkBuffer: true);
	}

	private void ProcessVideoPayload(byte[] payload, ulong sourceTimestampNanos, long receivedTick, bool countReceived, bool allowPreSinkBuffer)
	{
		if (countReceived)
		{
			long num = Interlocked.Increment(ref _videoPackets);
			Interlocked.Add(ref _videoBytes, payload.Length);
			if (num == 1)
			{
				StopVideoWatchdog();
				AppLog.Write($"First video packet received: {payload.Length} bytes.");
			}
		}

		if (allowPreSinkBuffer && !HasVideoSinkReady())
		{
			BufferVideoUntilSinkReady(payload, sourceTimestampNanos, receivedTick);
			QueueDiagnosticsUpdate();
			return;
		}

		FfmpegDecoder? decoder = _decoder;
#if HIGH_RESOLUTION_D3D
		MediaFoundationD3D11Decoder? mediaFoundationD3DDecoder = _mediaFoundationD3DDecoder;
#endif
		byte[]? array = ProcessH264Payload(payload, out long h264ForwardedPackets, out string h264LastDecision);
#if HIGH_RESOLUTION_D3D
		if (mediaFoundationD3DDecoder != null && array != null)
		{
			if (h264ForwardedPackets == 1)
			{
				AppLog.Write("First H264 payload forwarded to Media Foundation D3D11 decoder: " + h264LastDecision);
			}
			if (!mediaFoundationD3DDecoder.QueueH264(array, sourceTimestampNanos, receivedTick))
			{
				_decoderStatus = "Media Foundation D3D11 decoder input unavailable; waiting for decoder";
				AppLog.Write("Media Foundation D3D11 decoder input unavailable; packet was not queued.");
			}
		}
#endif
#if HIGH_RESOLUTION_D3D
		else if (decoder != null && array != null)
#else
		if (decoder != null && array != null)
#endif
		{
			if (h264ForwardedPackets == 1)
			{
				AppLog.Write("First H264 payload forwarded to decoder: " + h264LastDecision);
			}
				if (!decoder.QueueH264(array, sourceTimestampNanos, receivedTick))
				{
					_decoderStatus = "decoder input unavailable; waiting for decoder";
					AppLog.Write("Decoder input unavailable; packet was not queued.");
				}
			}
		else if (decoder != null)
		{
			_decoderStatus = h264LastDecision;
			LogGateDecisionThrottled();
		}
#if HIGH_RESOLUTION_D3D
		else if (mediaFoundationD3DDecoder != null)
		{
			_decoderStatus = h264LastDecision;
			LogGateDecisionThrottled();
		}
#endif
		if (countReceived)
		{
			LogVideoHealthThrottled();
		}
		QueueDiagnosticsUpdate();
	}

	private bool HasVideoSinkReady()
	{
		if (_decoder != null)
		{
			return true;
		}
#if HIGH_RESOLUTION_D3D
		if (_mediaFoundationD3DDecoder != null)
		{
			return true;
		}
#endif

		return false;
	}

	private byte[]? ProcessH264Payload(byte[] payload, out long forwardedPackets, out string lastDecision)
	{
		lock (_h264GateLock)
		{
			byte[]? processed = _h264Gate.Process(payload);
			forwardedPackets = _h264Gate.ForwardedPackets;
			lastDecision = _h264Gate.LastDecision;
			return processed;
		}
	}

	private void ResetH264Gate()
	{
		lock (_h264GateLock)
		{
			_h264Gate.Reset();
		}
	}

	private void RequireH264Keyframe()
	{
		lock (_h264GateLock)
		{
			_h264Gate.RequireKeyframe();
		}
	}

	private string GetH264GateLastDecision()
	{
		lock (_h264GateLock)
		{
			return _h264Gate.LastDecision;
		}
	}

	private (long Forwarded, long Dropped) GetH264GateCounts()
	{
		lock (_h264GateLock)
		{
			return (_h264Gate.ForwardedPackets, _h264Gate.DroppedPackets);
		}
	}

	private void BufferVideoUntilSinkReady(byte[] payload, ulong sourceTimestampNanos, long receivedTick)
	{
		lock (_pendingVideoGate)
		{
			_pendingVideoBeforeSink.Enqueue(new PendingVideoPayload(payload, sourceTimestampNanos, receivedTick));
			_pendingVideoBytesBeforeSink += payload.Length;
			while (_pendingVideoBeforeSink.Count > MaxPendingVideoPacketsBeforeSink || _pendingVideoBytesBeforeSink > MaxPendingVideoBytesBeforeSink)
			{
				PendingVideoPayload dropped = _pendingVideoBeforeSink.Dequeue();
				_pendingVideoBytesBeforeSink = Math.Max(0L, _pendingVideoBytesBeforeSink - dropped.Payload.Length);
				Interlocked.Increment(ref _pendingVideoDroppedBeforeSink);
			}
		}
		LogPendingVideoDropsThrottled();
	}

	private void FlushPendingVideoToSink()
	{
		PendingVideoPayload[] pending;
		lock (_pendingVideoGate)
		{
			if (_pendingVideoBeforeSink.Count == 0)
			{
				return;
			}
			pending = _pendingVideoBeforeSink.ToArray();
			_pendingVideoBeforeSink.Clear();
			_pendingVideoBytesBeforeSink = 0L;
		}

		AppLog.Write($"Flushing {pending.Length:N0} pending video packets to renderer.");
		foreach (PendingVideoPayload packet in pending)
		{
			ProcessVideoPayload(packet.Payload, packet.SourceTimestampNanos, packet.ReceivedTick, countReceived: false, allowPreSinkBuffer: false);
		}
	}

	private void ClearPendingVideoBeforeSink()
	{
		lock (_pendingVideoGate)
		{
			_pendingVideoBeforeSink.Clear();
			_pendingVideoBytesBeforeSink = 0L;
		}
	}

	private void HandleCursorState(CursorState cursorState, ulong sourceTimestampNanos, long receivedTick)
	{
		if (!SyntheticCursorOverlayEnabled)
		{
			ResetRemoteCursorState();
			return;
		}

		long count = Interlocked.Increment(ref _cursorMessages);
		if (count == 1)
		{
			AppLog.Write("First cursor state received.");
		}
		lock (_cursorGate)
		{
			_pendingCursorState = cursorState;
		}
		if (Interlocked.Exchange(ref _cursorQueued, 1) == 0)
		{
			base.Dispatcher.BeginInvoke(new Action(DrainLatestCursorState), DispatcherPriority.Render);
		}
		QueueDiagnosticsUpdate();
	}

	private async Task ReconnectNowAsync()
	{
		if (_lastEndpoint == null || string.IsNullOrWhiteSpace(_lastPin))
		{
			await ConnectToSelectedAsync();
			return;
		}

		int generation = Interlocked.Increment(ref _connectionGeneration);
		_manualDisconnectRequested = false;
		_autoReconnectAttempts = 0;
		await ConnectToEndpointAsync(_lastEndpoint, _lastPin, generation, isReconnect: true);
	}

	private async Task HandleUnexpectedDisconnectAsync(int generation)
	{
		if (generation != _connectionGeneration)
		{
			return;
		}

		await DisconnectAsync("Connection lost. Reconnecting...", revealPanel: false);
		_ = ScheduleAutoReconnectAsync(generation);
	}

	private async Task ScheduleAutoReconnectAsync(int generation)
	{
		if (!CanAutoReconnect(generation))
		{
			return;
		}

		int attempt = ++_autoReconnectAttempts;
		SetStatus($"Connection lost. Reconnecting in 2s ({attempt}/{MaxAutoReconnectAttempts})...");
		UpdateConnectionButtons(connected: false);
		await Task.Delay(AutoReconnectDelay);

		if (!CanAutoReconnect(generation))
		{
			return;
		}

		IPEndPoint endpoint = _lastEndpoint!;
		string pin = _lastPin!;
		bool connected = await ConnectToEndpointAsync(endpoint, pin, generation, isReconnect: true);
		if (!connected && CanAutoReconnect(generation))
		{
			await ScheduleAutoReconnectAsync(generation);
		}
		else if (!connected && generation == _connectionGeneration && !_manualDisconnectRequested)
		{
			SetStatus("Reconnect failed. Press Retry when the Mac is ready.");
			UpdateConnectionButtons(connected: false);
		}
	}

	private bool CanAutoReconnect(int generation)
	{
		return generation == _connectionGeneration
			&& !_manualDisconnectRequested
			&& !_isConnecting
			&& _client == null
			&& _autoReconnectAttempts < MaxAutoReconnectAttempts
			&& _lastEndpoint != null
			&& !string.IsNullOrWhiteSpace(_lastPin);
	}

	private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
	{
		await DisconnectAsync(userRequested: true);
	}

	private async void CompactDisconnectButton_Click(object sender, RoutedEventArgs e)
	{
		await DisconnectAsync(userRequested: true);
	}

	private async void ReconnectButton_Click(object sender, RoutedEventArgs e)
	{
		await ReconnectNowAsync();
	}

	private async void CompactReconnectButton_Click(object sender, RoutedEventArgs e)
	{
		await ReconnectNowAsync();
	}

	private void PinTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
	{
		e.Handled = !e.Text.All(char.IsDigit);
	}

	private async void PinTextBox_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter && ConnectButton.IsEnabled)
		{
			e.Handled = true;
			await ConnectToSelectedAsync();
		}
	}

	private void Window_KeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Escape && _isFullscreen)
		{
			e.Handled = true;
			ToggleFullscreen();
		}
	}

	private void VideoStage_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
	{
		if (e.ClickCount == 2)
		{
			e.Handled = true;
			ToggleFullscreen();
		}
	}

	private void HidePanelButton_Click(object sender, RoutedEventArgs e)
	{
		SetSidebarCollapsed(collapsed: true);
	}

	private void ShowPanelButton_Click(object sender, RoutedEventArgs e)
	{
		SetSidebarCollapsed(collapsed: false);
	}

	private void CompactFullscreenButton_Click(object sender, RoutedEventArgs e)
	{
		FullscreenButton_Click(sender, e);
	}

	private void FullscreenButton_Click(object sender, RoutedEventArgs e)
	{
		ToggleFullscreen();
	}

	private void ToggleFullscreen()
	{
		if (!_isFullscreen)
		{
			_previousWindowStyle = base.WindowStyle;
			_previousWindowState = base.WindowState;
				base.WindowStyle = WindowStyle.None;
				base.WindowState = WindowState.Maximized;
				_isFullscreen = true;
				FullscreenButton.Content = "Windowed";
				CompactFullscreenButton.Content = "W";
				CompactFullscreenButton.ToolTip = "Exit fullscreen";
			}
			else
			{
				base.WindowStyle = _previousWindowStyle;
				base.WindowState = _previousWindowState;
				_isFullscreen = false;
				FullscreenButton.Content = "Fullscreen";
				CompactFullscreenButton.Content = "F";
				CompactFullscreenButton.ToolTip = "Fullscreen";
			}
		base.Dispatcher.BeginInvoke(new Action(delegate
		{
			RestartDecoderIfRenderWidthChanged("display mode changed");
		}), DispatcherPriority.Background);
	}

	private void RenderModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		UpdateRenderModeDetail();
		UpdateRenderScalingMode();
		if (_streamConfig != null && (_decoder != null
#if HIGH_RESOLUTION_D3D
			|| _mediaFoundationD3DDecoder != null
#endif
			))
		{
			Interlocked.Increment(ref _decoderRestarts);
			RequireH264Keyframe();
			StartFreshDecoder(_streamConfig, "render mode changed");
		}
		UpdateDiagnostics();
	}

	private IPEndPoint ResolveEndpoint()
	{
		UpdateEndpointFieldsFromSelectedDevice();
		string text = HostTextBox.Text.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			throw new InvalidOperationException("Choose a discovered sender or enter a sender address.");
		}
		if (!int.TryParse(PortTextBox.Text.Trim(), out var result))
		{
			throw new InvalidOperationException("Port must be a number.");
		}
		IPAddress[] array = (from address in Dns.GetHostAddresses(text)
			where address.AddressFamily == AddressFamily.InterNetwork
			select address).ToArray();
		if (array.Length == 0)
		{
			throw new InvalidOperationException("Could not resolve host.");
		}
		return new IPEndPoint(array[0], result);
	}

	private void UpdateEndpointFieldsFromSelectedDevice()
	{
		if (DeviceComboBox.SelectedItem is MirrorDevice mirrorDevice)
		{
			HostTextBox.Text = mirrorDevice.Address.ToString();
			PortTextBox.Text = mirrorDevice.Port.ToString();
		}
	}

	private void InitializeRenderModeSettingsUi()
	{
		_renderModeSettingsUiReady = false;
		ReceiverRenderModeSetting displayedMode = StartupRenderModeSettings.HasEnvironmentOverride
			? StartupRenderModeSettings.EffectiveMode
			: StartupRenderModeSettings.PersistedMode;
		if (!StartupRenderModeSettings.HasEnvironmentOverride
			&& !QualityPathAvailable
			&& displayedMode == ReceiverRenderModeSetting.Quality)
		{
			displayedMode = ReceiverRenderModeSetting.Stable;
		}

		_pendingRenderModeSetting = displayedMode;
		StableRenderModeRadioButton.IsChecked = displayedMode == ReceiverRenderModeSetting.Stable;
		QualityRenderModeRadioButton.IsChecked = displayedMode == ReceiverRenderModeSetting.Quality;
		bool canEditSetting = !StartupRenderModeSettings.HasEnvironmentOverride;
		StableRenderModeRadioButton.IsEnabled = canEditSetting;
		QualityRenderModeRadioButton.IsEnabled = canEditSetting && QualityPathAvailable;
		RenderModeOverrideTextBlock.Visibility = StartupRenderModeSettings.HasEnvironmentOverride
			? Visibility.Visible
			: Visibility.Collapsed;
		if (StartupRenderModeSettings.HasEnvironmentOverride)
		{
			RenderModeOverrideTextBlock.Text = $"{RenderModeSettings.EnvironmentVariableName}={StartupRenderModeSettings.EnvironmentOverrideValue} is overriding this setting.";
		}
		_renderModeSettingsUiReady = true;
		UpdateRenderModeSettingsUi();
	}

	private void RenderModeSettingRadioButton_Checked(object sender, RoutedEventArgs e)
	{
		if (!_renderModeSettingsUiReady || StartupRenderModeSettings.HasEnvironmentOverride)
		{
			return;
		}

		_pendingRenderModeSetting = QualityRenderModeRadioButton.IsChecked == true
			? ReceiverRenderModeSetting.Quality
			: ReceiverRenderModeSetting.Stable;
		UpdateRenderModeSettingsUi();
	}

	private void UpdateRenderModeSettingsUi()
	{
		bool qualitySelected = _pendingRenderModeSetting == ReceiverRenderModeSetting.Quality;
		if (!qualitySelected)
		{
			RenderModeSettingDetailTextBlock.Text = "Compatibility mode advertises a 1080p AirPlay display.";
			RenderModeNoteTextBlock.Visibility = Visibility.Collapsed;
		}
		else if (GpuQualityRequested)
		{
#if HIGH_RESOLUTION_D3D
			RenderModeSettingDetailTextBlock.Text = "Native GPU mode uses Media Foundation/D3D11 decode and present.";
			RenderModeNoteTextBlock.Text = "Falls back to the software decoder if GPU startup fails.";
#else
			RenderModeSettingDetailTextBlock.Text = "Native GPU mode requires a HIGH_RESOLUTION_D3D build.";
			RenderModeNoteTextBlock.Text = "This build will use the 1080p compatibility path.";
#endif
			RenderModeNoteTextBlock.Visibility = Visibility.Visible;
		}
		else
		{
			RenderModeSettingDetailTextBlock.Text = "GPU native mode is unavailable; iMirror will advertise 1080p.";
#if HIGH_RESOLUTION_D3D
			RenderModeNoteTextBlock.Text = "Set IMIRROR_EXPERIMENTAL_QUALITY=1 to force the GPU path for local validation.";
#else
			RenderModeNoteTextBlock.Text = "Build with HIGH_RESOLUTION_D3D to enable the GPU path.";
#endif
			RenderModeNoteTextBlock.Visibility = Visibility.Visible;
		}
		bool restartRequired = !StartupRenderModeSettings.HasEnvironmentOverride
			&& _pendingRenderModeSetting != StartupRenderModeSettings.PersistedMode;
		RenderModeRestartPanel.Visibility = restartRequired
			? Visibility.Visible
			: Visibility.Collapsed;
	}

	private void RestartForRenderModeButton_Click(object sender, RoutedEventArgs e)
	{
		if (StartupRenderModeSettings.HasEnvironmentOverride)
		{
			SetStatus($"{RenderModeSettings.EnvironmentVariableName} is overriding the saved render mode.");
			return;
		}

		try
		{
			RenderModeSettings.SavePersistedMode(_pendingRenderModeSetting);
			AppLog.Write($"Render mode setting saved as {RenderModeSettings.ToSettingValue(_pendingRenderModeSetting)}; restarting.");
			RestartApplication();
		}
		catch (Exception ex)
		{
			AppLog.Write("Render mode restart failed: " + ex);
			SetStatus("Could not restart iMirror: " + ex.Message);
		}
	}

	private static void RestartApplication()
	{
		string? executablePath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(executablePath))
		{
			executablePath = Process.GetCurrentProcess().MainModule?.FileName;
		}
		if (string.IsNullOrWhiteSpace(executablePath))
		{
			throw new InvalidOperationException("Could not find the iMirror executable path.");
		}

		Process.Start(new ProcessStartInfo
		{
			FileName = executablePath,
			WorkingDirectory = AppContext.BaseDirectory,
			UseShellExecute = true
		});
		Application.Current.Shutdown();
	}

	private RenderMode CurrentRenderMode
	{
		get
		{
			return QualityPathAvailable
				? RenderMode.Quality
				: RenderMode.Auto;
		}
	}

	private int ResolveMaxRenderWidth(StreamConfig config)
	{
		int sourceWidth = Math.Clamp(config.Width, 2, RealtimeMaxRenderWidth);
		switch (CurrentRenderMode)
		{
		case RenderMode.Responsive:
			return Math.Min(sourceWidth, ResponsiveMaxRenderWidth);
		case RenderMode.Quality:
			return Math.Min(sourceWidth, QualityMaxRenderWidth);
		case RenderMode.Native4K:
			return Math.Min(sourceWidth, RealtimeMaxRenderWidth);
		default:
			return Math.Min(sourceWidth, ResponsiveMaxRenderWidth);
		}
	}

	private int ResolveOutputFps(StreamConfig config, int maxRenderWidth)
	{
		int sourceFps = Math.Clamp(config.Fps, 1, RealtimeMaxRenderFps);
		if (CurrentRenderMode == RenderMode.Quality)
		{
			return Math.Min(sourceFps, HighResolutionMaxRenderFps);
		}

		if (config.Width >= QualityMaxRenderWidth || maxRenderWidth >= QualityMaxRenderWidth)
		{
			return Math.Min(sourceFps, HighResolutionMaxRenderFps);
		}
		return sourceFps;
	}

	private void UpdateRenderModeDetail()
	{
		if (RenderModeDetailTextBlock == null)
		{
			return;
		}
		RenderModeDetailTextBlock.Text = CurrentRenderMode switch
		{
			RenderMode.Responsive => "Caps viewer output at 1080p for faster pointer motion.",
#if HIGH_RESOLUTION_D3D
			RenderMode.Quality => "Advertises the native GPU Media Foundation/D3D11 path.",
#else
			RenderMode.Quality => "GPU mode requires a HIGH_RESOLUTION_D3D build.",
#endif
			RenderMode.Native4K => "Uses the native GPU receiver path when available.",
			_ => QualityRenderModeEnabled
				? "GPU mode requested, but unavailable; using the 1080p compatibility path."
				: "Uses the 1080p compatibility receiver path."
		};
	}

	private void UpdateRenderScalingMode()
	{
		if (VideoImage == null)
		{
			return;
		}
		// Use fast (bilinear) scaling for the live video element. HighQuality re-resamples the whole
		// frame on every WPF composite, which throttled the present to ~9-14fps (Tier-2 hardware,
		// 1-2ms present cost, yet only ~9 composites/sec). For real-time mirroring a fast blit at the
		// display refresh rate matters far more than per-frame resample quality; the GPU video
		// processor already scales to the present resolution.
		RenderOptions.SetBitmapScalingMode(VideoImage, BitmapScalingMode.LowQuality);
	}

	private void RestartDecoderIfRenderWidthChanged(string reason)
	{
		if (_streamConfig == null || _decoder == null)
		{
			return;
		}
		int newMaxWidth = ResolveMaxRenderWidth(_streamConfig);
		if (newMaxWidth == _decoderMaxRenderWidth)
		{
			return;
		}
		Interlocked.Increment(ref _decoderRestarts);
		RequireH264Keyframe();
		StartFreshDecoder(_streamConfig, reason);
		UpdateDiagnostics();
	}

	private void StartDecoder(StreamConfig config)
	{
		try
		{
			_autoReconnectAttempts = 0;
			Interlocked.Exchange(ref _cursorMessages, 0L);
			ResetRemoteCursorState();
			_streamConfig = config;
			AppLog.Write($"Stream config received: {config.Width}x{config.Height} @ {config.Fps} codec={config.Codec}.");
			StartFreshDecoder(config, "stream config");
			SetStatus($"Mirroring {config.Width}x{config.Height} @ {config.Fps} fps.");
			UpdateDiagnostics();
		}
		catch (Exception ex)
		{
			_decoderStatus = "failed: " + ex.Message;
			SetStatus("Decoder failed: " + ex.Message);
			AppLog.Write("Decoder failed: " + ex);
			UpdateDiagnostics();
		}
	}

	private void StartFreshDecoder(StreamConfig config, string reason)
	{
		StopVideoWatchdog();
		_decoder?.Dispose();
		_decoder = null;
#if HIGH_RESOLUTION_D3D
		_mediaFoundationD3DDecoder?.Dispose();
		_mediaFoundationD3DDecoder = null;
		DisposeHighResolutionD3DPresenter();
		ReleasePendingD3DFrame();
#endif
		ReleasePendingFrame();
		// Restart the latency warmup grace so decoder/renderer spin-up and reconnect/session-refresh
		// catch-up are excluded from steady-state percentiles instead of inflating the first window.
		_receiveToPresentLatencyWindow.BeginWarmup();
#if HIGH_RESOLUTION_D3D
		bool requireHighResolutionD3DPath = ShouldUseHighResolutionD3DPath(config) && !_gpuPathDisabledThisSession;
		if (requireHighResolutionD3DPath && TryStartHighResolutionD3DPath(config, reason))
		{
			_decoderMaxRenderWidth = config.Width;
			_decoderOutputFps = Math.Clamp(config.Fps, 1, HighResolutionMaxRenderFps);
			FlushPendingVideoToSink();
			AppLog.Write("Media Foundation D3D11 renderer started for " + reason + ".");
			StartVideoWatchdog(config, _connectionGeneration);
			return;
		}
		if (requireHighResolutionD3DPath)
		{
			// GPU engine init failed (no/blocked hardware decode, driver issue). Fall through to the
			// software FFmpeg path, which decodes the (native) stream and downscales for present.
			_decoderStatus = "GPU decode unavailable; falling back to software decoder.";
			AppLog.Write("High-resolution D3D path failed; falling back to FFmpeg software decoder for " + reason + ".");
		}
#endif
		// Software (FFmpeg) decode of >1080 streams (e.g. GPU-fault fallback at native res) is heavy;
		// cap the decoded/presented output to keep decode + present load manageable.
		_decoderMaxRenderWidth = Math.Min(ResolveMaxRenderWidth(config), ResponsiveMaxRenderWidth);
		_decoderOutputFps = ResolveOutputFps(config, _decoderMaxRenderWidth);
		_decoder = new FfmpegDecoder(config.Width, config.Height, config.Fps, _decoderMaxRenderWidth, _decoderOutputFps);
		_decoder.StatusChanged += delegate(string message)
		{
			string message2 = message;
			base.Dispatcher.BeginInvoke(new Action(delegate
			{
				_decoderStatus = message2;
				AppLog.Write("Decoder status: " + message2);
				if (IsDecoderResyncError(message2))
				{
					_decoderStatus = "decoder resync warning: " + CompactStatus(message2);
				}
				else
				{
					SetStatus(message2);
				}
				// NOTE: Do NOT restart the decoder on mb_width/height overflow or
				// sps_id-out-of-range. Those are symptoms of mid-stream bitstream corruption,
				// not a genuine SPS change (no SPS NAL actually arrives). Restarting only
				// re-arms the keyframe gate, and the Mac sender does not emit a fresh IDR on
				// demand, so the gate gets stuck dropping P-slices ("saw NAL 1") and the
				// decoder never recovers. Let ffmpeg keep its state and resync on the next IDR.
				UpdateDiagnostics();
			}), DispatcherPriority.Background);
		};
		_decoder.FrameDecoded += QueueFrameForPresentation;
		_decoder.DecoderRestarted += delegate
		{
			base.Dispatcher.Invoke(delegate
			{
				Interlocked.Increment(ref _decoderRestarts);
				RequireH264Keyframe();
				AppLog.Write("Decoder restarted (hwaccel fallback); requiring fresh SPS/PPS keyframe.");
				UpdateDiagnostics();
			});
		};
		_decoder.InputQueueOverflowed += delegate
		{
			base.Dispatcher.BeginInvoke(new Action(delegate
			{
				RequireH264Keyframe();
				AppLog.Write("Decoder input queue overflowed; holding video until next keyframe.");
				UpdateDiagnostics();
			}));
		};
		_decoder.Start();
		_decoderStatus = "started";
		FlushPendingVideoToSink();
		AppLog.Write("Decoder started for " + reason + ".");
		StartVideoWatchdog(config, _connectionGeneration);
	}

#if HIGH_RESOLUTION_D3D
	private static bool ShouldUseHighResolutionD3DPath(StreamConfig config)
	{
		return QualityRenderModeEnabled
			&& GpuQualityRequested
			&& config.Width > ResponsiveMaxRenderWidth
			&& config.Height > 1080;
	}

	private bool TryStartHighResolutionD3DPath(StreamConfig config, string reason)
	{
		try
		{
			var presenter = new D3D11SwapChainVideoPresenter();
			var decoder = new MediaFoundationD3D11Decoder(config.Width, config.Height, config.Fps, presenter.Device);
			_highResolutionD3DPresenter = presenter;
			_mediaFoundationD3DDecoder = decoder;
			// Host the swap-chain child window in the video stage (bypasses WPF D3DImage composition).
			VideoImage.Visibility = Visibility.Collapsed;
			VideoImage.Source = null;
			_bitmap = null;
			VideoStage.Children.Insert(0, presenter);
			presenter.HorizontalAlignment = HorizontalAlignment.Center;
			presenter.VerticalAlignment = VerticalAlignment.Center;
			UpdateHighResolutionD3DPresenterLayout(presenter, config);
			VideoStage.UpdateLayout();
			presenter.StatusChanged += delegate(string message)
			{
				string message2 = message;
				base.Dispatcher.BeginInvoke(new Action(delegate
				{
					AppLog.Write("D3D11 presenter status: " + message2);
					UpdateDiagnostics();
				}), DispatcherPriority.Background);
			};
			decoder.StatusChanged += delegate(string message)
			{
				string message2 = message;
				base.Dispatcher.BeginInvoke(new Action(delegate
				{
					_decoderStatus = message2;
					AppLog.Write("Media Foundation D3D11 decoder status: " + message2);
					SetStatus(message2);
					UpdateDiagnostics();
				}), DispatcherPriority.Background);
			};
			decoder.Faulted += delegate(string message)
			{
				string message2 = message;
				base.Dispatcher.BeginInvoke(new Action(delegate
				{
					HandleHighResolutionD3DFatal("High-resolution D3D decoder faulted: " + message2);
				}), DispatcherPriority.Background);
			};
			decoder.FrameDecoded += QueueD3DFrameForPresentation;
			decoder.InputQueueOverflowed += delegate
			{
				base.Dispatcher.BeginInvoke(new Action(delegate
				{
					RequireH264Keyframe();
					AppLog.Write("Media Foundation D3D11 decoder input queue overflowed; holding video until next keyframe.");
					UpdateDiagnostics();
				}));
			};
			decoder.Start();
			_decoderStatus = "Media Foundation D3D11 decoder started";
			AppLog.Write($"High-resolution D3D path active for {reason}: {config.Width}x{config.Height}@{config.Fps}, d3d11MultithreadProtected={presenter.IsMultithreadProtected}.");
			return true;
		}
		catch (Exception ex)
		{
			_decoderStatus = "Media Foundation D3D11 path failed: " + ex.Message;
			AppLog.Write("Media Foundation D3D11 path failed; high-resolution fallback is blocked: " + ex);
			_mediaFoundationD3DDecoder?.Dispose();
			_mediaFoundationD3DDecoder = null;
			DisposeHighResolutionD3DPresenter();
			ReleasePendingD3DFrame();
			return false;
		}
	}

	private void UpdateHighResolutionD3DPresenterLayout()
	{
		D3D11SwapChainVideoPresenter? presenter = _highResolutionD3DPresenter;
		StreamConfig? config = _streamConfig;
		if (presenter == null || config == null)
		{
			return;
		}
		UpdateHighResolutionD3DPresenterLayout(presenter, config);
	}

	private void UpdateHighResolutionD3DPresenterLayout(D3D11SwapChainVideoPresenter presenter, StreamConfig config)
	{
		double stageWidthDip = VideoStage.ActualWidth;
		double stageHeightDip = VideoStage.ActualHeight;
		if (stageWidthDip <= 0.0 || stageHeightDip <= 0.0 || config.Width <= 0 || config.Height <= 0)
		{
			return;
		}

		DpiScale dpi = VisualTreeHelper.GetDpi(VideoStage);
		double dpiScaleX = dpi.DpiScaleX > 0.0 ? dpi.DpiScaleX : 1.0;
		double dpiScaleY = dpi.DpiScaleY > 0.0 ? dpi.DpiScaleY : 1.0;
		double stageWidthPixels = stageWidthDip * dpiScaleX;
		double stageHeightPixels = stageHeightDip * dpiScaleY;
		double scale = Math.Min(stageWidthPixels / config.Width, stageHeightPixels / config.Height);
		double fitWidthPixels = Math.Max(1.0, Math.Round(config.Width * scale));
		double fitHeightPixels = Math.Max(1.0, Math.Round(config.Height * scale));
		double fitWidthDip = Math.Min(stageWidthDip, fitWidthPixels / dpiScaleX);
		double fitHeightDip = Math.Min(stageHeightDip, fitHeightPixels / dpiScaleY);

		presenter.Width = fitWidthDip;
		presenter.Height = fitHeightDip;
		presenter.HorizontalAlignment = HorizontalAlignment.Center;
		presenter.VerticalAlignment = VerticalAlignment.Center;
	}

	private void HandleHighResolutionD3DFatal(string message)
	{
		AppLog.Write(message);
		_decoderStatus = message;
		// Disable the GPU engine for the rest of this session (reset on the next connection) and
		// restart on the software decoder so a runtime GPU fault (device-lost/TDR) does not kill
		// the mirror.
		_gpuPathDisabledThisSession = true;
		_mediaFoundationD3DDecoder?.Dispose();
		_mediaFoundationD3DDecoder = null;
		DisposeHighResolutionD3DPresenter();
		ReleasePendingD3DFrame();
		RequireH264Keyframe();
		if (_streamConfig != null)
		{
			SetStatus("GPU decode faulted; switching to software decoder.");
			AppLog.Write("GPU decode faulted; restarting on FFmpeg software decoder.");
			StartFreshDecoder(_streamConfig, "GPU fault fallback");
		}
		else
		{
			SetStatus("GPU decode failed.");
			VideoImage.Source = null;
			EmptyStatePanel.Visibility = Visibility.Visible;
		}
		UpdateDiagnostics();
	}
#endif

	private void StartVideoWatchdog(StreamConfig config, int generation)
	{
		StopVideoWatchdog();
		var cts = new CancellationTokenSource();
		_videoWatchdogCts = cts;
		Task.Run(async delegate
		{
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(2.5), cts.Token);
				if (cts.IsCancellationRequested ||
					generation != Volatile.Read(ref _connectionGeneration) ||
					Interlocked.Read(ref _videoPackets) > 0)
				{
					return;
				}

				await base.Dispatcher.BeginInvoke(new Action(delegate
				{
					if (cts.IsCancellationRequested ||
						generation != _connectionGeneration ||
						Interlocked.Read(ref _videoPackets) > 0)
					{
						return;
					}

						_decoderStatus = $"waiting for video packets ({config.Width}x{config.Height} @ {config.Fps})";
						SetStatus("Connected, waiting for video frames.");
						AppLog.Write("No video packets received 2.5s after stream config. Sender sent config/control data, but no H.264 video payload yet.");
					UpdateDiagnostics();
				}));
			}
			catch (OperationCanceledException)
			{
			}
		});
	}

	private void StopVideoWatchdog()
	{
		var cts = Interlocked.Exchange(ref _videoWatchdogCts, null);
		if (cts == null)
		{
			return;
		}

		cts.Cancel();
		cts.Dispose();
	}

	private static bool IsDecoderResyncError(string message)
	{
		if (!message.Contains("non-existing PPS", StringComparison.OrdinalIgnoreCase)
			&& !message.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase)
			&& !message.Contains("mb_width/height overflow", StringComparison.OrdinalIgnoreCase)
			&& !message.Contains("reference count overflow", StringComparison.OrdinalIgnoreCase)
			&& !(message.Contains("sps_id", StringComparison.OrdinalIgnoreCase) && message.Contains("out of range", StringComparison.OrdinalIgnoreCase)))
		{
			return message.Contains("no frame", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}

	private void QueueFrameForPresentation(VideoFrame frame)
	{
		Interlocked.Increment(ref _decodedFrames);
		lock (_frameGate)
		{
			if (_pendingFrame != null)
			{
				Interlocked.Increment(ref _renderDroppedFrames);
				_pendingFrame.Release();
			}
			_pendingFrame = frame;
		}
		if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
		{
			base.Dispatcher.BeginInvoke(new Action(DrainLatestFrame), DispatcherPriority.Render);
		}
	}

#if HIGH_RESOLUTION_D3D
	private void QueueD3DFrameForPresentation(D3D11VideoFrame frame)
	{
		Interlocked.Increment(ref _decodedFrames);
		lock (_frameGate)
		{
			if (_pendingD3DFrame != null)
			{
				Interlocked.Increment(ref _renderDroppedFrames);
				_pendingD3DFrame.Dispose();
			}
			_pendingD3DFrame = frame;
		}
		if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
		{
			base.Dispatcher.BeginInvoke(new Action(DrainLatestFrame), DispatcherPriority.Render);
		}
	}
#endif

	private void ReleasePendingFrame()
	{
		lock (_frameGate)
		{
			_pendingFrame?.Release();
			_pendingFrame = null;
		}
	}

#if HIGH_RESOLUTION_D3D
	private void ReleasePendingD3DFrame()
	{
		lock (_frameGate)
		{
			_pendingD3DFrame?.Dispose();
			_pendingD3DFrame = null;
		}
	}
#endif

	private void DrainLatestFrame()
	{
		VideoFrame? pendingFrame;
#if HIGH_RESOLUTION_D3D
		D3D11VideoFrame? pendingD3DFrame;
#endif
		lock (_frameGate)
		{
			pendingFrame = _pendingFrame;
			_pendingFrame = null;
#if HIGH_RESOLUTION_D3D
			pendingD3DFrame = _pendingD3DFrame;
			_pendingD3DFrame = null;
#endif
		}
		if (pendingFrame != null)
		{
			PresentFrame(pendingFrame);
		}
#if HIGH_RESOLUTION_D3D
		if (pendingD3DFrame != null)
		{
			PresentD3DFrame(pendingD3DFrame);
		}
#endif
		Interlocked.Exchange(ref _renderQueued, 0);
		lock (_frameGate)
		{
			if (_pendingFrame == null
#if HIGH_RESOLUTION_D3D
				&& _pendingD3DFrame == null
#endif
			)
			{
				return;
			}
		}
		if (Interlocked.Exchange(ref _renderQueued, 1) == 0)
		{
			base.Dispatcher.BeginInvoke(new Action(DrainLatestFrame), DispatcherPriority.Render);
		}
	}

	private void DrainLatestCursorState()
	{
		if (!SyntheticCursorOverlayEnabled)
		{
			ResetRemoteCursorState();
			return;
		}

		CursorState? pendingCursorState;
		lock (_cursorGate)
		{
			pendingCursorState = _pendingCursorState;
			_pendingCursorState = null;
		}
		if (pendingCursorState != null)
		{
			PresentCursor(pendingCursorState);
		}
		Interlocked.Exchange(ref _cursorQueued, 0);
		lock (_cursorGate)
		{
			if (_pendingCursorState == null)
			{
				return;
			}
		}
		if (Interlocked.Exchange(ref _cursorQueued, 1) == 0)
		{
			base.Dispatcher.BeginInvoke(new Action(DrainLatestCursorState), DispatcherPriority.Render);
		}
	}

	private void PresentFrame(VideoFrame frame)
	{
		long renderStartTick = Stopwatch.GetTimestamp();
		try
		{
			PresentFrameToActiveRenderer(frame);
			long renderDoneTick = Stopwatch.GetTimestamp();
			long renderedFrames = Interlocked.Increment(ref _renderedFrames);
			if (frame.ReceivedTick > 0)
			{
				long receiveToRenderMs = ElapsedMilliseconds(frame.ReceivedTick, renderDoneTick);
				Interlocked.Exchange(ref _latestReceiveToRenderMs, receiveToRenderMs);
				LatencyWindowSnapshot? completedWindow = _receiveToPresentLatencyWindow.Record(receiveToRenderMs, renderDoneTick);
				if (completedWindow.HasValue)
				{
					AppLog.Write("Presentation latency window: " + FormatLatencyWindow(completedWindow.Value));
				}
			}
			if (frame.DecodedTick > 0)
			{
				Interlocked.Exchange(ref _latestDecodeToRenderMs, ElapsedMilliseconds(frame.DecodedTick, renderDoneTick));
			}
			EmptyStatePanel.Visibility = Visibility.Collapsed;
			LogRenderLatencyThrottled(renderStartTick, renderDoneTick, renderedFrames);
			QueueDiagnosticsUpdate();
		}
		finally
		{
			frame.Release();
		}
	}

	private void PresentCursor(CursorState cursorState)
	{
		if (!SyntheticCursorOverlayEnabled)
		{
			_lastPresentedCursorState = null;
			HideRemoteCursor();
			return;
		}

		_lastPresentedCursorState = cursorState;
		if (!cursorState.Visible || _streamConfig == null || !IsFinite(cursorState.X) || !IsFinite(cursorState.Y))
		{
			HideRemoteCursor();
			return;
		}

		Rect rect = GetVideoContentRect();
		if (rect.IsEmpty)
		{
			HideRemoteCursor();
			return;
		}

		double x = rect.Left + rect.Width * Math.Clamp(cursorState.X, 0.0, 1.0);
		double y = rect.Top + rect.Height * Math.Clamp(cursorState.Y, 0.0, 1.0);
		double cursorScale = ResolveRemoteCursorScale(rect);
		RemoteCursorScale.ScaleX = cursorScale;
		RemoteCursorScale.ScaleY = cursorScale;
		Canvas.SetLeft(RemoteCursor, Math.Round(x - cursorScale));
		Canvas.SetTop(RemoteCursor, Math.Round(y - cursorScale));
		RemoteCursorLayer.Visibility = Visibility.Visible;
	}

	private double ResolveRemoteCursorScale(Rect videoRect)
	{
		StreamConfig? config = _streamConfig;
		if (config == null || config.Width <= 0 || videoRect.Width <= 0)
		{
			return 0.45;
		}

		double scale = videoRect.Width / config.Width;
		return Math.Clamp(scale, RemoteCursorMinScale, RemoteCursorMaxScale);
	}

	private void RefreshRemoteCursorPosition()
	{
		if (!SyntheticCursorOverlayEnabled)
		{
			HideRemoteCursor();
			return;
		}

		if (_lastPresentedCursorState != null)
		{
			PresentCursor(_lastPresentedCursorState);
		}
	}

	private void ResetRemoteCursorState()
	{
		lock (_cursorGate)
		{
			_pendingCursorState = null;
		}
		_lastPresentedCursorState = null;
		Interlocked.Exchange(ref _cursorQueued, 0);
		HideRemoteCursor();
	}

	private void HideRemoteCursor()
	{
		if (RemoteCursorLayer != null)
		{
			RemoteCursorLayer.Visibility = Visibility.Collapsed;
		}
	}

	private Rect GetVideoContentRect()
	{
		StreamConfig? config = _streamConfig;
		if (config == null)
		{
			return Rect.Empty;
		}

#if HIGH_RESOLUTION_D3D
		D3D11SwapChainVideoPresenter? d3dPresenter = _highResolutionD3DPresenter;
		if (d3dPresenter != null && d3dPresenter.ActualWidth > 0.0 && d3dPresenter.ActualHeight > 0.0)
		{
			double d3dLeft = Math.Max(0.0, (VideoStage.ActualWidth - d3dPresenter.ActualWidth) / 2.0);
			double d3dTop = Math.Max(0.0, (VideoStage.ActualHeight - d3dPresenter.ActualHeight) / 2.0);
			return new Rect(d3dLeft, d3dTop, d3dPresenter.ActualWidth, d3dPresenter.ActualHeight);
		}
#endif

		double hostWidth = VideoImage?.ActualWidth > 0 ? VideoImage.ActualWidth : VideoStage.ActualWidth;
		double hostHeight = VideoImage?.ActualHeight > 0 ? VideoImage.ActualHeight : VideoStage.ActualHeight;
		if (hostWidth <= 0.0 || hostHeight <= 0.0 || config.Width <= 0 || config.Height <= 0)
		{
			return Rect.Empty;
		}

		double sourceAspect = (double)config.Width / config.Height;
		double hostAspect = hostWidth / hostHeight;
		double width;
		double height;
		double left;
		double top;
		if (hostAspect > sourceAspect)
		{
			height = hostHeight;
			width = height * sourceAspect;
			left = (hostWidth - width) / 2.0;
			top = 0.0;
		}
		else
		{
			width = hostWidth;
			height = width / sourceAspect;
			left = 0.0;
			top = (hostHeight - height) / 2.0;
		}
		return new Rect(left, top, width, height);
	}

	private static bool IsFinite(double value)
	{
		return !double.IsNaN(value) && !double.IsInfinity(value);
	}

	private void PresentFrameToActiveRenderer(VideoFrame frame)
	{
		if (VideoImage.Visibility != Visibility.Visible)
		{
			VideoImage.Visibility = Visibility.Visible;
		}
		PresentFrameToWriteableBitmap(frame);
	}

	private void PresentFrameToWriteableBitmap(VideoFrame frame)
	{
		if (_bitmap == null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height)
		{
			_bitmap = new WriteableBitmap(frame.Width, frame.Height, 96.0, 96.0, PixelFormats.Bgra32, null);
			VideoImage.Source = _bitmap;
		}
		CopyFrameToBitmap(frame);
	}

	private void CopyFrameToBitmap(VideoFrame frame)
	{
		if (_bitmap == null)
		{
			return;
		}

		int sourceStride = frame.Width * 4;
		int byteCount = sourceStride * frame.Height;
		_bitmap.Lock();
		try
		{
			if (_bitmap.BackBufferStride == sourceStride)
			{
				Marshal.Copy(frame.Buffer, 0, _bitmap.BackBuffer, byteCount);
			}
			else
			{
				for (int y = 0; y < frame.Height; y++)
				{
					Marshal.Copy(
						frame.Buffer,
						y * sourceStride,
						IntPtr.Add(_bitmap.BackBuffer, y * _bitmap.BackBufferStride),
						sourceStride);
				}
			}
			_bitmap.AddDirtyRect(new Int32Rect(0, 0, frame.Width, frame.Height));
		}
		finally
		{
			_bitmap.Unlock();
		}
	}

	private static long ElapsedMilliseconds(long startTick, long endTick)
	{
		if (endTick <= startTick)
		{
			return 0L;
		}
		return Math.Max(0L, (long)Math.Round((double)(endTick - startTick) * 1000.0 / Stopwatch.Frequency));
	}

	private void LogRenderLatencyThrottled(long renderStartTick, long renderDoneTick, long renderedFrames)
	{
		long tickCount = Environment.TickCount64;
		long previous = Interlocked.Read(ref _lastRenderLogTick);
		if (tickCount - previous < 1000)
		{
			return;
		}
		Interlocked.Exchange(ref _lastRenderLogTick, tickCount);
		AppLog.Write(
			$"Render stats: received={Interlocked.Read(ref _videoPackets):N0}, decoded={Interlocked.Read(ref _decodedFrames):N0}, rendered={renderedFrames:N0}, " +
			$"renderDropped={Interlocked.Read(ref _renderDroppedFrames):N0}, receive->render={Interlocked.Read(ref _latestReceiveToRenderMs)}ms, " +
			$"decode->render={Interlocked.Read(ref _latestDecodeToRenderMs)}ms, render={ElapsedMilliseconds(renderStartTick, renderDoneTick)}ms, " +
			$"receive->present window={FormatLatencyWindow(_receiveToPresentLatencyWindow.GetCurrentOrLastSnapshot(Stopwatch.GetTimestamp()))}, " +
			$"pendingRender={GetPendingRenderFrameCount()}, dispatcherQueued={Volatile.Read(ref _renderQueued)}, " +
			$"decoderQueue={ActiveQueuedInputPackets} packets/{(double)ActiveQueuedInputBytes / 1024.0:N1}KB, " +
			$"ffmpegPipeline={ActiveFfmpegPipelineDepth} frames, " +
			$"h264 accepted/written/dropped={ActiveAcceptedInputPackets:N0}/{ActiveWrittenInputPackets:N0}/{ActiveDroppedInputPackets:N0}, " +
			$"stdinWrite={ActiveLatestWriteMilliseconds}ms max={ActiveMaxWriteMilliseconds}ms stalls={ActiveWriteStalls:N0}");
	}

	private void LogVideoHealthThrottled()
	{
		long tickCount = Environment.TickCount64;
		long previous = Interlocked.Read(ref _lastVideoHealthLogTick);
		if (tickCount - previous < 10000)
		{
			return;
		}
		if (Interlocked.CompareExchange(ref _lastVideoHealthLogTick, tickCount, previous) != previous)
		{
			return;
		}

		long received = Interlocked.Read(ref _videoPackets);
		long decoded = Interlocked.Read(ref _decodedFrames);
		long rendered = Interlocked.Read(ref _renderedFrames);
		long previousReceived = Interlocked.Exchange(ref _lastVideoHealthPackets, received);
		long previousDecoded = Interlocked.Exchange(ref _lastVideoHealthDecodedFrames, decoded);
		long previousRendered = Interlocked.Exchange(ref _lastVideoHealthRenderedFrames, rendered);
		long receivedDelta = Math.Max(0L, received - previousReceived);
		long decodedDelta = Math.Max(0L, decoded - previousDecoded);
		long renderedDelta = Math.Max(0L, rendered - previousRendered);

		AppLog.Write(
			$"Video health: received={received:N0} (+{receivedDelta:N0}), decoded={decoded:N0} (+{decodedDelta:N0}), rendered={rendered:N0} (+{renderedDelta:N0}), " +
			$"decoderQueue={ActiveQueuedInputPackets} packets/{(double)ActiveQueuedInputBytes / 1024.0:N1}KB, " +
			$"h264 accepted/written/dropped={ActiveAcceptedInputPackets:N0}/{ActiveWrittenInputPackets:N0}/{ActiveDroppedInputPackets:N0}, " +
			$"gate={GetH264GateLastDecision()}");
	}

	private async Task DisconnectAsync(string? finalStatus = "Disconnected.", bool userRequested = false, bool revealPanel = true)
	{
		if (userRequested)
		{
			_manualDisconnectRequested = true;
			_autoReconnectAttempts = 0;
			Interlocked.Increment(ref _connectionGeneration);
		}
			if (_client != null)
			{
				await _client.DisposeAsync();
				_client = null;
			}
				StopVideoWatchdog();
				_decoder?.Dispose();
				_decoder = null;
				StopAudioPipeline();
#if HIGH_RESOLUTION_D3D
			_mediaFoundationD3DDecoder?.Dispose();
			_mediaFoundationD3DDecoder = null;
#endif
#if HIGH_RESOLUTION_D3D
			DisposeHighResolutionD3DPresenter();
#endif
			_bitmap = null;
			VideoImage.Source = null;
			VideoImage.Visibility = Visibility.Visible;
			ResetRemoteCursorState();
			EmptyStatePanel.Visibility = Visibility.Visible;
		ReleasePendingFrame();
#if HIGH_RESOLUTION_D3D
		ReleasePendingD3DFrame();
#endif
		_streamConfig = null;
		_decoderStatus = "not started";
		_decoderOutputFps = 0;
		_decoderMaxRenderWidth = RealtimeMaxRenderWidth;
			ResetH264Gate();
			SetConnectedUi(connected: false, finalStatus, revealPanel);
			UpdateDiagnostics();
		}

#if HIGH_RESOLUTION_D3D
	private void DisposeHighResolutionD3DPresenter()
	{
		D3D11SwapChainVideoPresenter? presenter = _highResolutionD3DPresenter;
		if (presenter == null)
		{
			return;
		}
		_highResolutionD3DPresenter = null;
		try
		{
			VideoStage.Children.Remove(presenter);
		}
		catch
		{
		}
		try
		{
			presenter.Dispose();
		}
		catch
		{
		}
		VideoImage.Visibility = Visibility.Visible;
	}
#endif

	private void SetConnectedUi(bool connected, string? disconnectedStatus = "Disconnected.", bool revealPanel = true)
	{
		UpdateConnectionButtons(connected);
		DeviceComboBox.IsEnabled = !connected;
		HostTextBox.IsEnabled = !connected;
		PortTextBox.IsEnabled = !connected;
		PinTextBox.IsEnabled = !connected;
		if (!connected)
		{
			if (disconnectedStatus != null)
			{
				SetStatus(disconnectedStatus);
			}
			if (revealPanel)
			{
				SetSidebarCollapsed(collapsed: false);
			}
		}
		else
		{
			SetSidebarCollapsed(collapsed: true);
		}
	}

	private void UpdateConnectionButtons(bool connected)
	{
		bool canReconnect = !connected && !_isConnecting && _lastEndpoint != null && !string.IsNullOrWhiteSpace(_lastPin);
		ConnectButton.IsEnabled = !connected && !_isConnecting;
		DisconnectButton.IsEnabled = connected || _isConnecting;
		ReconnectButton.IsEnabled = canReconnect;
		CompactDisconnectButton.IsEnabled = connected || _isConnecting;
		CompactReconnectButton.IsEnabled = canReconnect;
		UpdateReceiverChrome();
	}

	private void SetSidebarCollapsed(bool collapsed)
	{
		SidebarColumn.Width = (collapsed ? new GridLength(48.0) : new GridLength(340.0));
		SidebarPanel.Visibility = (collapsed ? Visibility.Collapsed : Visibility.Visible);
		CompactControlBar.Visibility = (collapsed ? Visibility.Visible : Visibility.Collapsed);
	}

	private void SetStatus(string message)
	{
		_statusText = message;
		StatusTextBlock.Text = message;
		CompactStatusTextBlock.Text = CompactStatus(message);
		CompactControlBar.ToolTip = message;
		UpdateReceiverChrome();
	}

	private static string CompactStatus(string message)
	{
		if (message.Length > 46)
		{
			return message.Substring(0, 43) + "...";
		}
		return message;
	}

	private void UpdateReceiverChrome()
	{
		bool connected = _client != null;
		int deviceCount = _devices.Count;

		HeaderSubtitleTextBlock.Text = ResolveReceiverSubtitle(connected, deviceCount);
		DeviceSummaryTextBlock.Text = ResolveDeviceSummary(deviceCount);
		ReceiverStatusTextBlock.Text = _statusText;
		EmptyStateStatusTextBlock.Text = _statusText;
		EmptyStateTextBlock.Text = ResolveEmptyStateTitle(connected, deviceCount);
		EmptyStateDetailTextBlock.Text = ResolveEmptyStateDetail(connected, deviceCount);

		Brush statusBrush = ResolveReceiverStatusBrush(connected, deviceCount);
		SidebarStatusDot.Fill = statusBrush;
		EmptyStateStatusDot.Fill = statusBrush;
	}

	private string ResolveReceiverSubtitle(bool connected, int deviceCount)
	{
		if (connected)
		{
				return "Mirroring";
		}
		if (_isConnecting)
		{
			return "Connecting";
		}
			return deviceCount > 0 ? "Legacy sender available" : "Ready for AirPlay";
	}

	private string ResolveDeviceSummary(int deviceCount)
	{
		return deviceCount switch
		{
				0 => "No legacy sender found yet. iPhone mirroring does not need this field.",
				1 => "1 legacy sender is ready. Enter its PIN to use the old path.",
			_ => $"{deviceCount} senders are available. Choose one, then enter its PIN."
		};
	}

	private string ResolveEmptyStateTitle(bool connected, int deviceCount)
	{
		if (connected)
		{
			return "Preparing the stream";
		}
		if (_isConnecting)
		{
			return "Connecting";
		}
			return deviceCount > 0 ? "Legacy sender found" : "Ready to mirror";
	}

	private string ResolveEmptyStateDetail(bool connected, int deviceCount)
	{
		if (connected)
		{
			return "The mirrored screen will appear here as soon as video frames arrive.";
		}
		if (_isConnecting)
		{
			return "Keep both devices on the same network while the secure session starts.";
		}
		return deviceCount > 0
				? "Enter the sender PIN, then press Connect Sender."
				: "Open Control Center, tap Screen Mirroring, then choose iMirror.";
	}

	private Brush ResolveReceiverStatusBrush(bool connected, int deviceCount)
	{
		if (connected)
		{
			return (Brush)FindResource("SuccessBrush");
		}
		if (_isConnecting)
		{
			return (Brush)FindResource("WarningBrush");
		}
		if (deviceCount > 0)
		{
			return new SolidColorBrush(Color.FromRgb(96, 205, 255));
		}
		return (Brush)FindResource("MutedTextBrush");
	}

	private void UpdateDiagnostics()
	{
		string value = ((_streamConfig == null) ? "stream: none" : $"stream: {_streamConfig.Width}x{_streamConfig.Height} @ {_streamConfig.Fps} fps");
		string renderMode = CurrentRenderMode switch
		{
			RenderMode.Responsive => "responsive",
			RenderMode.Native4K => "native 4K",
			RenderMode.Quality => "quality",
			_ => "auto"
		};
		string value2 = ((_decoderOutputFps > 0) ? $"<= {_decoderMaxRenderWidth}px @ {_decoderOutputFps} fps ({renderMode})" : $"<= {RealtimeMaxRenderWidth}px @ up to {RealtimeMaxRenderFps} fps ({renderMode})");
		string outputValue = ResolveOutputDiagnostics();
			long value3 = Interlocked.Read(ref _videoPackets);
			long num = Interlocked.Read(ref _videoBytes);
				long cursorMessages = SyntheticCursorOverlayEnabled ? Interlocked.Read(ref _cursorMessages) : 0L;
			long decodedFrames = Interlocked.Read(ref _decodedFrames);
				long renderedFrames = Interlocked.Read(ref _renderedFrames);
				int pendingRenderFrames = GetPendingRenderFrameCount();
				int dispatcherQueued = Volatile.Read(ref _renderQueued);
				int pendingVideoBeforeSinkPackets;
				long pendingVideoBeforeSinkBytes;
				lock (_pendingVideoGate)
				{
					pendingVideoBeforeSinkPackets = _pendingVideoBeforeSink.Count;
					pendingVideoBeforeSinkBytes = _pendingVideoBytesBeforeSink;
				}
					(long h264Forwarded, long h264Dropped) = GetH264GateCounts();
					string cursorValue = SyntheticCursorOverlayEnabled
						? (_lastPresentedCursorState == null ? "none" : (_lastPresentedCursorState.Visible ? $"visible {_lastPresentedCursorState.X:P1}, {_lastPresentedCursorState.Y:P1}" : "hidden"))
						: "embedded in video";
					string latencyWindow = FormatLatencyWindow(_receiveToPresentLatencyWindow.GetCurrentOrLastSnapshot(Stopwatch.GetTimestamp()));
					DiagnosticsTextBlock.Text = $"{value}\nrealtime output: {value2}\n{outputValue}\n{ResolveAudioDiagnostics()}\nreceived video: {value3:N0} / {(double)num / 1024.0:N1} KB\npre-render queue/dropped: {pendingVideoBeforeSinkPackets:N0} ({(double)pendingVideoBeforeSinkBytes / 1024.0:N1} KB) / {Interlocked.Read(ref _pendingVideoDroppedBeforeSink):N0}\ncursor: {cursorMessages:N0} / {cursorValue}\nforwarded/dropped h264: {h264Forwarded:N0} / {h264Dropped:N0}\ndecoded/rendered: {decodedFrames:N0} / {renderedFrames:N0}\nrender dropped: {Interlocked.Read(ref _renderDroppedFrames):N0}\nlatest latency: recv->render {Interlocked.Read(ref _latestReceiveToRenderMs)} ms, decode->render {Interlocked.Read(ref _latestDecodeToRenderMs)} ms\nlatency window: {latencyWindow}\nrender queue: pending {pendingRenderFrames}, dispatcher {dispatcherQueued}\ndecoder input queued/dropped: {ActiveQueuedInputPackets:N0} ({(double)ActiveQueuedInputBytes / 1024.0:N1} KB) / {ActiveDroppedInputPackets:N0}\nh264 accepted/written: {ActiveAcceptedInputPackets:N0} / {ActiveWrittenInputPackets:N0}\nstdin write: {ActiveLatestWriteMilliseconds} ms, max {ActiveMaxWriteMilliseconds} ms, stalls {ActiveWriteStalls:N0}\ndecoder restarts: {Interlocked.Read(ref _decoderRestarts):N0}\ndecoder: {_decoderStatus}";
		CompactStatusTextBlock.Text = ((renderedFrames > 0) ? $"Live: {renderedFrames:N0} frames" : CompactStatus(_statusText));
	}

#if HIGH_RESOLUTION_D3D
	private void PresentD3DFrame(D3D11VideoFrame frame)
	{
		long renderStartTick = Stopwatch.GetTimestamp();
		try
		{
			D3D11SwapChainVideoPresenter? presenter = _highResolutionD3DPresenter;
			if (presenter == null)
			{
				return;
			}

			try
			{
				presenter.PresentNv12Texture(frame.Texture, frame.SubresourceIndex, frame.Width, frame.Height, frame.Fps);
			}
			catch (Exception ex)
			{
				HandleHighResolutionD3DFatal("High-resolution D3D present failed: " + ex.Message);
				return;
			}
			long renderDoneTick = Stopwatch.GetTimestamp();
			long renderedFrames = Interlocked.Increment(ref _renderedFrames);
			if (frame.ReceivedTick > 0)
			{
				long receiveToRenderMs = ElapsedMilliseconds(frame.ReceivedTick, renderDoneTick);
				Interlocked.Exchange(ref _latestReceiveToRenderMs, receiveToRenderMs);
				LatencyWindowSnapshot? completedWindow = _receiveToPresentLatencyWindow.Record(receiveToRenderMs, renderDoneTick);
				if (completedWindow.HasValue)
				{
					AppLog.Write("Presentation latency window: " + FormatLatencyWindow(completedWindow.Value));
				}
			}
			if (frame.DecodedTick > 0)
			{
				Interlocked.Exchange(ref _latestDecodeToRenderMs, ElapsedMilliseconds(frame.DecodedTick, renderDoneTick));
			}
			EmptyStatePanel.Visibility = Visibility.Collapsed;
			LogRenderLatencyThrottled(renderStartTick, renderDoneTick, renderedFrames);
			QueueDiagnosticsUpdate();
		}
		finally
		{
			frame.Dispose();
		}
	}
#endif

	private static string FormatLatencyWindow(LatencyWindowSnapshot snapshot)
	{
		if (!snapshot.HasSamples)
		{
			return "n/a";
		}

		string label = snapshot.Completed ? "last" : "current";
		string text = $"{label} {snapshot.WindowSeconds:N1}s n={snapshot.SampleCount:N0} p50={snapshot.P50Milliseconds}ms p95={snapshot.P95Milliseconds}ms max={snapshot.MaxMilliseconds}ms";
		if (snapshot.WarmupSamplesSkipped > 0)
		{
			text += $" warmupSkipped={snapshot.WarmupSamplesSkipped:N0} warmupMax={snapshot.WarmupMaxMilliseconds}ms";
		}
		return text;
	}

	private string ResolveOutputDiagnostics()
	{
#if HIGH_RESOLUTION_D3D
		if (_mediaFoundationD3DDecoder != null)
		{
			return $"output: Media Foundation D3D11 {_mediaFoundationD3DDecoder.OutputWidth}x{_mediaFoundationD3DDecoder.OutputHeight}, GPU NV12";
		}
#endif
		return (_decoder == null)
			? "output: none"
			: $"output: {_decoder.OutputWidth}x{_decoder.OutputHeight}, {(double)_decoder.OutputFrameBytes / 1024.0 / 1024.0:N1} MB/frame";
	}

	private string ResolveAudioDiagnostics()
	{
		FfmpegAudioDecoder? decoder;
		WasapiAudioOutput? output;
		lock (_audioGate)
		{
			decoder = _audioDecoder;
			output = _audioOutput;
		}

		if (decoder == null || output == null)
		{
			return $"audio: {_audioStatus}, received={Interlocked.Read(ref _audioFramesReceived):N0}";
		}

		return $"audio: {_audioStatus}, received/queued={Interlocked.Read(ref _audioFramesReceived):N0}/{Interlocked.Read(ref _audioFramesQueued):N0}, " +
			$"dedup={decoder.DuplicateInputFrames:N0}, decoded={Interlocked.Read(ref _audioPcmFrames):N0} ({(double)Interlocked.Read(ref _audioPcmBytes) / 1024.0:N1} KB), " +
			$"decoderQueue={decoder.QueuedInputFrames:N0}, audioBuffer={output.BufferedMilliseconds:N0}ms, " +
			$"audioLatency={output.LatestEstimatedLatencyMilliseconds}/{output.SyncTargetLatencyMilliseconds}ms, " +
			$"audioDropped={output.DroppedFrames:N0} sync={output.SyncDroppedFrames:N0}, clears={output.BufferClears:N0}, low={output.LowBufferEvents:N0}";
	}

	private int ResolveAudioSyncTargetLatencyMilliseconds()
	{
		long latestVideoLatencyMs = Interlocked.Read(ref _latestReceiveToRenderMs);
		if (latestVideoLatencyMs <= 0)
		{
			LatencyWindowSnapshot snapshot = _receiveToPresentLatencyWindow.GetCurrentOrLastSnapshot(Stopwatch.GetTimestamp());
			if (snapshot.HasSamples)
			{
				latestVideoLatencyMs = snapshot.P50Milliseconds;
			}
		}

		int target = checked((int)Math.Clamp(latestVideoLatencyMs, 0L, 1000L)) + AudioSyncOffsetMilliseconds;
		return Math.Clamp(target, MinAudioSyncTargetMilliseconds, MaxAudioSyncTargetMilliseconds);
	}

	private int ActiveQueuedInputPackets =>
#if HIGH_RESOLUTION_D3D
		_mediaFoundationD3DDecoder?.QueuedInputPackets
		??
#endif
		_decoder?.QueuedInputPackets ?? 0;

	private long ActiveQueuedInputBytes =>
#if HIGH_RESOLUTION_D3D
		_mediaFoundationD3DDecoder?.QueuedInputBytes
		??
#endif
		_decoder?.QueuedInputBytes ?? 0L;

	private long ActiveDroppedInputPackets =>
#if HIGH_RESOLUTION_D3D
		_mediaFoundationD3DDecoder?.DroppedInputPackets
		??
#endif
		_decoder?.DroppedInputPackets ?? 0L;

	private long ActiveAcceptedInputPackets =>
#if HIGH_RESOLUTION_D3D
		_mediaFoundationD3DDecoder?.AcceptedInputPackets
		??
#endif
		_decoder?.AcceptedInputPackets ?? 0L;

	private long ActiveWrittenInputPackets =>
#if HIGH_RESOLUTION_D3D
		_mediaFoundationD3DDecoder?.WrittenInputPackets
		??
#endif
		_decoder?.WrittenInputPackets ?? 0L;

	private long ActiveLatestWriteMilliseconds =>
#if HIGH_RESOLUTION_D3D
		_mediaFoundationD3DDecoder?.LatestWriteMilliseconds
		??
#endif
		_decoder?.LatestWriteMilliseconds ?? 0L;

	private long ActiveMaxWriteMilliseconds =>
#if HIGH_RESOLUTION_D3D
		_mediaFoundationD3DDecoder?.MaxWriteMilliseconds
		??
#endif
		_decoder?.MaxWriteMilliseconds ?? 0L;

	private int ActiveFfmpegPipelineDepth => _decoder?.PipelineDepthFrames ?? 0;

	private long ActiveWriteStalls =>
#if HIGH_RESOLUTION_D3D
		_mediaFoundationD3DDecoder?.WriteStalls
		??
#endif
		_decoder?.WriteStalls ?? 0L;

	private int GetPendingRenderFrameCount()
	{
		lock (_frameGate)
		{
			int count = _pendingFrame == null ? 0 : 1;
#if HIGH_RESOLUTION_D3D
			if (_pendingD3DFrame != null)
			{
				count++;
			}
#endif
			return count;
		}
	}

	private void LogGateDecisionThrottled()
	{
		long tickCount = Environment.TickCount64;
		long num = Interlocked.Read(ref _lastGateLogTick);
		if (tickCount - num >= 1000)
		{
			Interlocked.Exchange(ref _lastGateLogTick, tickCount);
			AppLog.Write("H264 gate: " + GetH264GateLastDecision());
		}
	}

	private void LogPendingVideoDropsThrottled()
	{
		long dropped = Interlocked.Read(ref _pendingVideoDroppedBeforeSink);
		if (dropped == 0)
		{
			return;
		}

		long tickCount = Environment.TickCount64;
		long previous = Interlocked.Read(ref _lastPendingVideoDropLogTick);
		if (tickCount - previous >= 1000)
		{
			Interlocked.Exchange(ref _lastPendingVideoDropLogTick, tickCount);
			AppLog.Write($"Pre-render video queue dropped={dropped:N0}, pending={PendingVideoBeforeSinkCount:N0} packets/{(double)PendingVideoBeforeSinkBytes / 1024.0:N1} KB");
		}
	}

	private int PendingVideoBeforeSinkCount
	{
		get
		{
			lock (_pendingVideoGate)
			{
				return _pendingVideoBeforeSink.Count;
			}
		}
	}

	private long PendingVideoBeforeSinkBytes
	{
		get
		{
			lock (_pendingVideoGate)
			{
				return _pendingVideoBytesBeforeSink;
			}
		}
	}

	private void QueueDiagnosticsUpdate()
	{
		long tickCount = Environment.TickCount64;
		long num = Interlocked.Read(ref _lastDiagnosticsTick);
		if (tickCount - num < 250)
		{
			return;
		}
		Interlocked.Exchange(ref _lastDiagnosticsTick, tickCount);
		if (Interlocked.Exchange(ref _diagnosticsQueued, 1) != 0)
		{
			return;
		}
		base.Dispatcher.BeginInvoke((Action)delegate
		{
			try
			{
				UpdateDiagnostics();
			}
			finally
			{
				Interlocked.Exchange(ref _diagnosticsQueued, 0);
			}
		}, DispatcherPriority.Background);
	}

}
