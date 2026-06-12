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
		Native4K = 2
	}

	private const int RealtimeMaxRenderWidth = 3840;

	private const int AutoMaxRenderWidth = 2560;

	private const int ResponsiveMaxRenderWidth = 1920;

	private const int RealtimeMaxRenderFps = 60;

	private const int HighResolutionMaxRenderFps = 30;

	private const int MaxAutoReconnectAttempts = 5;

	private const int MaxPendingVideoPacketsBeforeSink = 8;

	private const long MaxPendingVideoBytesBeforeSink = 8L * 1024L * 1024L;

	private const double RemoteCursorMinScale = 0.35;

	private const double RemoteCursorMaxScale = 1.0;

	private static readonly TimeSpan AutoReconnectDelay = TimeSpan.FromSeconds(2.0);

	private static readonly bool SyntheticCursorOverlayEnabled = false;

	private readonly ObservableCollection<MirrorDevice> _devices = new ObservableCollection<MirrorDevice>();

	private readonly MdnsBrowser _browser = new MdnsBrowser();

	private readonly AirPlayProbeService _airPlayProbe = new AirPlayProbeService("iMirror");

	private readonly H264AnnexBStreamGate _h264Gate = new H264AnnexBStreamGate();

	private readonly object _frameGate = new object();

	private readonly object _cursorGate = new object();

	private readonly object _pendingVideoGate = new object();

	private readonly Queue<PendingVideoPayload> _pendingVideoBeforeSink = new Queue<PendingVideoPayload>();

	private MirrorClient? _client;

	private FfmpegDecoder? _decoder;

	private MpvVideoPresenter? _mpvPresenter;

	private WriteableBitmap? _bitmap;

#if DIRECTX_PROBE
	private D3DImageFramePresenter? _d3dPresenter;
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

	private long _pendingVideoBytesBeforeSink;

	private long _pendingVideoDroppedBeforeSink;

	private long _lastPendingVideoDropLogTick;

	private long _decoderRestarts;

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

	private readonly record struct PendingVideoPayload(byte[] Payload, ulong SourceTimestampNanos, long ReceivedTick);

	private string _statusText = "Waiting for iPhone or iPad...";

	private bool _isFullscreen;

	private bool _isConnecting;

	private bool _manualDisconnectRequested = true;

#if DIRECTX_PROBE
	private bool _directXRendererFailed;
#endif

	private WindowStyle _previousWindowStyle;

	private WindowState _previousWindowState;

	public MainWindow()
	{
		AppLog.Write("MainWindow constructor entered.");
		InitializeComponent();
		AppLog.Write("MainWindow InitializeComponent returned.");
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

	private async void ConnectButton_Click(object sender, RoutedEventArgs e)
	{
		await ConnectToSelectedAsync();
	}

	private void HandleAirPlayStreamConfig(StreamConfig config)
	{
		base.Dispatcher.BeginInvoke(new Action(delegate
		{
			if (_streamConfig != null
				&& (_decoder != null || _mpvPresenter != null)
				&& _streamConfig.Width == config.Width
				&& _streamConfig.Height == config.Height
				&& _streamConfig.Fps == config.Fps
				&& string.Equals(_streamConfig.Codec, config.Codec, StringComparison.OrdinalIgnoreCase))
			{
				return;
			}

			_h264Gate.Reset();
			StartDecoder(config);
		}), DispatcherPriority.Background);
	}

	private void HandleAirPlayVideoPayload(byte[] payload, ulong sourceTimestampNanos, long receivedTick)
	{
		base.Dispatcher.BeginInvoke(new Action(delegate
		{
			if (_streamConfig == null && _decoder == null && _mpvPresenter == null)
			{
				StartDecoder(new StreamConfig
				{
					Width = 1920,
					Height = 1080,
					Fps = 60,
					Codec = "h264-annexb"
				});
			}

			ProcessVideoPayload(payload, sourceTimestampNanos, receivedTick, countReceived: true, allowPreSinkBuffer: true);
		}), DispatcherPriority.Background);
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
			_lastRenderLogTick = 0L;
			_decoderRestarts = 0L;
			_lastGateLogTick = 0L;
			_pendingVideoDroppedBeforeSink = 0L;
			_lastPendingVideoDropLogTick = 0L;
				_streamConfig = null;
				ResetRemoteCursorState();
				_decoderStatus = "waiting for stream config";
			ClearPendingVideoBeforeSink();
			_h264Gate.Reset();
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

		MpvVideoPresenter? mpvPresenter = _mpvPresenter;
		FfmpegDecoder? decoder = _decoder;
		byte[] array = _h264Gate.Process(payload);
		if (array != null && mpvPresenter != null)
		{
			if (_h264Gate.ForwardedPackets == 1)
			{
				AppLog.Write("First H264 payload forwarded to mpv: " + _h264Gate.LastDecision);
			}
			if (!mpvPresenter.QueueH264(array, sourceTimestampNanos, receivedTick))
			{
				_decoderStatus = "mpv input unavailable; waiting for renderer";
				AppLog.Write("mpv input unavailable; packet was not queued.");
			}
			else
			{
				base.Dispatcher.BeginInvoke(new Action(delegate
				{
					EmptyStatePanel.Visibility = Visibility.Collapsed;
					CompactStatusTextBlock.Text = "Live: mpv";
				}), DispatcherPriority.Background);
			}
		}
		else if (decoder != null && array != null)
		{
			if (_h264Gate.ForwardedPackets == 1)
			{
				AppLog.Write("First H264 payload forwarded to decoder: " + _h264Gate.LastDecision);
			}
				if (!decoder.QueueH264(array, sourceTimestampNanos, receivedTick))
				{
					_decoderStatus = "decoder input unavailable; waiting for decoder";
					AppLog.Write("Decoder input unavailable; packet was not queued.");
				}
			}
		else if (decoder != null)
		{
			_decoderStatus = _h264Gate.LastDecision;
			LogGateDecisionThrottled();
		}
		else if (mpvPresenter != null)
		{
			_decoderStatus = _h264Gate.LastDecision;
			LogGateDecisionThrottled();
		}
		QueueDiagnosticsUpdate();
	}

	private bool HasVideoSinkReady()
	{
		if (_decoder != null)
		{
			return true;
		}

		MpvVideoPresenter? presenter = _mpvPresenter;
		return presenter != null && presenter.CanAcceptInput;
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
		if (_streamConfig != null && (_decoder != null || _mpvPresenter != null))
		{
			Interlocked.Increment(ref _decoderRestarts);
			_h264Gate.RequireKeyframe();
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

	private RenderMode CurrentRenderMode
	{
		get
		{
			return RenderMode.Auto;
		}
	}

	private int ResolveMaxRenderWidth(StreamConfig config)
	{
		int sourceWidth = Math.Clamp(config.Width, 2, RealtimeMaxRenderWidth);
		switch (CurrentRenderMode)
		{
		case RenderMode.Responsive:
			return Math.Min(sourceWidth, ResponsiveMaxRenderWidth);
		case RenderMode.Native4K:
			return Math.Min(sourceWidth, RealtimeMaxRenderWidth);
		default:
			int stagePixels = GetVideoStagePixelWidth();
			if (stagePixels >= 2200 && sourceWidth >= 2200)
			{
				return Math.Min(sourceWidth, AutoMaxRenderWidth);
			}
			return Math.Min(sourceWidth, ResponsiveMaxRenderWidth);
		}
	}

	private int ResolveOutputFps(StreamConfig config, int maxRenderWidth)
	{
		int sourceFps = Math.Clamp(config.Fps, 1, RealtimeMaxRenderFps);
		if (config.Width >= 3000 || maxRenderWidth >= 3000)
		{
			return Math.Min(sourceFps, HighResolutionMaxRenderFps);
		}
		return sourceFps;
	}

	private int GetVideoStagePixelWidth()
	{
		double width = VideoStage?.ActualWidth > 0 ? VideoStage.ActualWidth : Math.Max(base.ActualWidth, base.Width);
		double dpiScale = 1.0;
		PresentationSource? source = PresentationSource.FromVisual(this);
		if (source?.CompositionTarget != null)
		{
			dpiScale = source.CompositionTarget.TransformToDevice.M11;
		}
		return Math.Clamp((int)Math.Round(width * dpiScale), 1280, RealtimeMaxRenderWidth);
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
			RenderMode.Native4K => "Uses the mpv GPU path for native 4K when available.",
			_ => "Automatically selects the best renderer for the incoming stream."
		};
	}

	private void UpdateRenderScalingMode()
	{
		if (VideoImage == null)
		{
			return;
		}
		RenderOptions.SetBitmapScalingMode(
			VideoImage,
			CurrentRenderMode == RenderMode.Auto ? BitmapScalingMode.HighQuality : BitmapScalingMode.LowQuality);
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
		_h264Gate.RequireKeyframe();
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
		DisposeMpvPresenter();
		ReleasePendingFrame();
		bool useMpvPresenter = ShouldUseMpvPresenter(config);
		_decoderMaxRenderWidth = useMpvPresenter ? Math.Min(config.Width, RealtimeMaxRenderWidth) : ResolveMaxRenderWidth(config);
		_decoderOutputFps = useMpvPresenter ? Math.Clamp(config.Fps, 1, RealtimeMaxRenderFps) : ResolveOutputFps(config, _decoderMaxRenderWidth);
		if (useMpvPresenter && TryStartMpvPresenter(config, reason))
		{
			FlushPendingVideoToSink();
			AppLog.Write("mpv presenter started for " + reason + ".");
			StartVideoWatchdog(config, _connectionGeneration);
			return;
		}
		_decoder = new FfmpegDecoder(config.Width, config.Height, config.Fps, _decoderMaxRenderWidth, _decoderOutputFps);
		_decoder.StatusChanged += delegate(string message)
		{
			string message2 = message;
			base.Dispatcher.Invoke(delegate
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
				UpdateDiagnostics();
			});
		};
		_decoder.FrameDecoded += QueueFrameForPresentation;
		_decoder.DecoderRestarted += delegate
		{
			base.Dispatcher.Invoke(delegate
			{
				Interlocked.Increment(ref _decoderRestarts);
				_h264Gate.RequireKeyframe();
				AppLog.Write("Decoder restarted (hwaccel fallback); requiring fresh SPS/PPS keyframe.");
				UpdateDiagnostics();
			});
		};
		_decoder.InputQueueOverflowed += delegate
		{
			base.Dispatcher.BeginInvoke(new Action(delegate
			{
				_h264Gate.RequireKeyframe();
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

	private bool ShouldUseMpvPresenter(StreamConfig config)
	{
		return config.Width >= 2560
			&& config.Height >= 1440;
	}

	private bool TryStartMpvPresenter(StreamConfig config, string reason)
	{
		try
		{
			var presenter = new MpvVideoPresenter(config.Width, config.Height, config.Fps);
			_mpvPresenter = presenter;
			presenter.StatusChanged += delegate(string message)
			{
				string message2 = message;
				base.Dispatcher.Invoke(delegate
				{
					_decoderStatus = message2;
					AppLog.Write("mpv status: " + message2);
					SetStatus(message2);
					UpdateDiagnostics();
				});
			};
			VideoImage.Visibility = Visibility.Collapsed;
			VideoImage.Source = null;
			_bitmap = null;
			VideoStage.Children.Insert(0, presenter);
			presenter.HorizontalAlignment = HorizontalAlignment.Stretch;
			presenter.VerticalAlignment = VerticalAlignment.Stretch;
			VideoStage.UpdateLayout();
			presenter.Start();
			_decoderStatus = "mpv started";
			AppLog.Write("mpv presenter selected for native GPU path: " + reason + ".");
			UpdateDiagnostics();
			return true;
		}
		catch (Exception ex)
		{
			AppLog.Write("mpv presenter failed; falling back to FFmpeg/WPF: " + ex);
			SetStatus("mpv unavailable; falling back to WPF renderer.");
			_decoderStatus = "mpv unavailable; fallback: " + ex.Message;
			DisposeMpvPresenter();
			VideoImage.Visibility = Visibility.Visible;
			return false;
		}
	}

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
		if (!message.Contains("non-existing PPS", StringComparison.OrdinalIgnoreCase) && !message.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase))
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

	private void ReleasePendingFrame()
	{
		lock (_frameGate)
		{
			_pendingFrame?.Release();
			_pendingFrame = null;
		}
	}

	private void DrainLatestFrame()
	{
		VideoFrame pendingFrame;
		lock (_frameGate)
		{
			pendingFrame = _pendingFrame;
			_pendingFrame = null;
		}
		if (pendingFrame != null)
		{
			PresentFrame(pendingFrame);
		}
		Interlocked.Exchange(ref _renderQueued, 0);
		lock (_frameGate)
		{
			if (_pendingFrame == null)
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
				Interlocked.Exchange(ref _latestReceiveToRenderMs, ElapsedMilliseconds(frame.ReceivedTick, renderDoneTick));
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
#if DIRECTX_PROBE
		if (!_directXRendererFailed)
		{
			try
			{
				if (_d3dPresenter == null)
				{
					_d3dPresenter = new D3DImageFramePresenter(new WindowInteropHelper(this).Handle);
					VideoImage.Source = _d3dPresenter.ImageSource;
					_bitmap = null;
					AppLog.Write("DX probe renderer active: D3DImage/Direct3D9 surface.");
				}
				_d3dPresenter.Present(frame);
				return;
			}
			catch (Exception ex)
			{
				_directXRendererFailed = true;
				AppLog.Write("DX probe renderer failed; falling back to WriteableBitmap: " + ex);
				DisposeDirectXPresenter();
			}
		}
#endif
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
			$"pendingRender={GetPendingRenderFrameCount()}, dispatcherQueued={Volatile.Read(ref _renderQueued)}, " +
			$"decoderQueue={_decoder?.QueuedInputPackets ?? 0} packets/{(double)(_decoder?.QueuedInputBytes ?? 0L) / 1024.0:N1}KB, " +
			$"h264 accepted/written/dropped={_decoder?.AcceptedInputPackets ?? 0:N0}/{_decoder?.WrittenInputPackets ?? 0:N0}/{_decoder?.DroppedInputPackets ?? 0:N0}, " +
			$"stdinWrite={_decoder?.LatestWriteMilliseconds ?? 0}ms max={_decoder?.MaxWriteMilliseconds ?? 0}ms stalls={_decoder?.WriteStalls ?? 0:N0}");
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
			DisposeMpvPresenter();
			DisposeDirectXPresenter();
			_bitmap = null;
			VideoImage.Source = null;
			VideoImage.Visibility = Visibility.Visible;
			ResetRemoteCursorState();
			EmptyStatePanel.Visibility = Visibility.Visible;
		ReleasePendingFrame();
		_streamConfig = null;
		_decoderStatus = "not started";
		_decoderOutputFps = 0;
		_decoderMaxRenderWidth = RealtimeMaxRenderWidth;
			_h264Gate.Reset();
#if DIRECTX_PROBE
			_directXRendererFailed = false;
#endif
			SetConnectedUi(connected: false, finalStatus, revealPanel);
			UpdateDiagnostics();
		}

	private void DisposeDirectXPresenter()
	{
#if DIRECTX_PROBE
		_d3dPresenter?.Dispose();
		_d3dPresenter = null;
#endif
	}

	private void DisposeMpvPresenter()
	{
		MpvVideoPresenter? presenter = _mpvPresenter;
		if (presenter == null)
		{
			return;
		}
		_mpvPresenter = null;
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
	}

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
					string cursorValue = SyntheticCursorOverlayEnabled
						? (_lastPresentedCursorState == null ? "none" : (_lastPresentedCursorState.Visible ? $"visible {_lastPresentedCursorState.X:P1}, {_lastPresentedCursorState.Y:P1}" : "hidden"))
						: "embedded in video";
					DiagnosticsTextBlock.Text = $"{value}\nrealtime output: {value2}\n{outputValue}\nreceived video: {value3:N0} / {(double)num / 1024.0:N1} KB\npre-render queue/dropped: {pendingVideoBeforeSinkPackets:N0} ({(double)pendingVideoBeforeSinkBytes / 1024.0:N1} KB) / {Interlocked.Read(ref _pendingVideoDroppedBeforeSink):N0}\ncursor: {cursorMessages:N0} / {cursorValue}\nforwarded/dropped h264: {_h264Gate.ForwardedPackets:N0} / {_h264Gate.DroppedPackets:N0}\ndecoded/rendered: {decodedFrames:N0} / {renderedFrames:N0}\nrender dropped: {Interlocked.Read(ref _renderDroppedFrames):N0}\nlatest latency: recv->render {Interlocked.Read(ref _latestReceiveToRenderMs)} ms, decode->render {Interlocked.Read(ref _latestDecodeToRenderMs)} ms\nrender queue: pending {pendingRenderFrames}, dispatcher {dispatcherQueued}\ndecoder input queued/dropped: {ActiveQueuedInputPackets:N0} ({(double)ActiveQueuedInputBytes / 1024.0:N1} KB) / {ActiveDroppedInputPackets:N0}\nh264 accepted/written: {ActiveAcceptedInputPackets:N0} / {ActiveWrittenInputPackets:N0}\nstdin write: {ActiveLatestWriteMilliseconds} ms, max {ActiveMaxWriteMilliseconds} ms, stalls {ActiveWriteStalls:N0}\ndecoder restarts: {Interlocked.Read(ref _decoderRestarts):N0}\ndecoder: {_decoderStatus}";
		CompactStatusTextBlock.Text = ((renderedFrames > 0) ? $"Live: {renderedFrames:N0} frames" : CompactStatus(_statusText));
	}

	private string ResolveOutputDiagnostics()
	{
		if (_mpvPresenter != null && _streamConfig != null)
		{
			return $"output: mpv native {_streamConfig.Width}x{_streamConfig.Height}, GPU renderer";
		}
		return (_decoder == null)
			? "output: none"
			: $"output: {_decoder.OutputWidth}x{_decoder.OutputHeight}, {(double)_decoder.OutputFrameBytes / 1024.0 / 1024.0:N1} MB/frame";
	}

	private int ActiveQueuedInputPackets => _mpvPresenter?.QueuedInputPackets ?? _decoder?.QueuedInputPackets ?? 0;

	private long ActiveQueuedInputBytes => _mpvPresenter?.QueuedInputBytes ?? _decoder?.QueuedInputBytes ?? 0L;

	private long ActiveDroppedInputPackets => _mpvPresenter?.DroppedInputPackets ?? _decoder?.DroppedInputPackets ?? 0L;

	private long ActiveAcceptedInputPackets => _mpvPresenter?.AcceptedInputPackets ?? _decoder?.AcceptedInputPackets ?? 0L;

	private long ActiveWrittenInputPackets => _mpvPresenter?.WrittenInputPackets ?? _decoder?.WrittenInputPackets ?? 0L;

	private long ActiveLatestWriteMilliseconds => _mpvPresenter?.LatestWriteMilliseconds ?? _decoder?.LatestWriteMilliseconds ?? 0L;

	private long ActiveMaxWriteMilliseconds => _mpvPresenter?.MaxWriteMilliseconds ?? _decoder?.MaxWriteMilliseconds ?? 0L;

	private long ActiveWriteStalls => _mpvPresenter?.WriteStalls ?? _decoder?.WriteStalls ?? 0L;

	private int GetPendingRenderFrameCount()
	{
		lock (_frameGate)
		{
			return _pendingFrame == null ? 0 : 1;
		}
	}

	private void LogGateDecisionThrottled()
	{
		long tickCount = Environment.TickCount64;
		long num = Interlocked.Read(ref _lastGateLogTick);
		if (tickCount - num >= 1000)
		{
			Interlocked.Exchange(ref _lastGateLogTick, tickCount);
			AppLog.Write("H264 gate: " + _h264Gate.LastDecision);
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
