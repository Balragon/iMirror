using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using DrawingIcon = System.Drawing.Icon;
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
using Forms = System.Windows.Forms;
using Wpf.Ui.Controls;

namespace MacMirrorReceiver;

public partial class MainWindow : FluentWindow, ISettingsHost
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

	private const long HighResolutionMaxLiveFrameAgeMilliseconds = 200;

	private const int MaxAutoReconnectAttempts = 5;

	// Keep the initial SPS/PPS/IDR burst while the decoder/render sink spins up.
	// Small AirPlay slice packets can exceed single digits before WPF/FFmpeg/MF is ready;
	// the byte cap below remains the hard memory guard.
	private const int MaxPendingVideoPacketsBeforeSink = 512;

	private const long MaxPendingVideoBytesBeforeSink = 8L * 1024L * 1024L;

	private const int MinAudioSyncTargetMilliseconds = 120;

	private const int MaxAudioSyncTargetMilliseconds = 220;

	private const double RemoteCursorMinScale = 0.35;

	private const double RemoteCursorMaxScale = 1.0;

	private static readonly TimeSpan AutoReconnectDelay = TimeSpan.FromSeconds(2.0);

	private static readonly TimeSpan AudioRtpStartupTimeout = TimeSpan.FromSeconds(8.0);

	private const string AudioFirewallWarningMessage =
		"Audio is blocked by Windows Firewall. Allow iMirror for Private and Public networks.";

	private static readonly bool SyntheticCursorOverlayEnabled = false;

	private static readonly ReceiverSettingsSnapshot StartupReceiverSettings = ReceiverSettings.Load();

	// Audio sync offset applies live: the Settings slider writes this and the audio
	// thread reads it through ResolveAudioSyncTargetLatencyMilliseconds. Volatile so
	// the UI-thread write is visible to the audio pipeline without a reconnect.
	private int _audioSyncOffsetMilliseconds = StartupReceiverSettings.Effective.AudioSyncOffsetMs;

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

	private readonly ObservableCollection<MirrorDevice> _devices = new ObservableCollection<MirrorDevice>();

	private readonly MdnsBrowser _browser = new MdnsBrowser();

	private readonly AirPlayProbeService _airPlayProbe = new AirPlayProbeService(
		StartupReceiverSettings.Effective.ReceiverName,
		ResolveStartupAudioAdvertised(),
		StartupReceiverSettings.Effective.WriteDiagnostics,
		StartupReceiverSettings.Effective.DumpAudio);

	private readonly H264AnnexBStreamGate _h264Gate = new H264AnnexBStreamGate();

	private readonly object _h264GateLock = new object();

	private readonly object _frameGate = new object();

	private readonly object _cursorGate = new object();

	private readonly object _pendingVideoGate = new object();

	private readonly Queue<PendingVideoPayload> _pendingVideoBeforeSink = new Queue<PendingVideoPayload>();

	private volatile bool _videoSinkAcceptingInput;

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
	private IHighResolutionD3DPresenter? _highResolutionD3DPresenter;

	private IHighResolutionD3DDecoder? _mediaFoundationD3DDecoder;

	private IHighResolutionD3DFrame? _pendingD3DFrame;

	private long _staleD3DFramesDropped;

	private long _lastStaleD3DFrameDropLogTick;
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

	private CancellationTokenSource? _audioRtpWatchdogCts;

	private volatile bool _audioFirewallWarningActive;

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

	private int _airPlaySessionGeneration;

	private readonly SemaphoreSlim _disconnectGate = new SemaphoreSlim(1, 1);

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

	private readonly WindowLifecycleState _lifecycle = new WindowLifecycleState();

	private SettingsWindow? _settingsWindow;

	private readonly UpdateService _updateService = new UpdateService();

	private UpdateInfo? _pendingUpdate;

	private bool _startupUpdateCheckStarted;

	private Forms.NotifyIcon? _trayIcon;

	private Forms.ContextMenuStrip? _trayMenu;

	private DrawingIcon? _trayIconImage;

	private bool _shutdownStarted;

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
		_airPlayProbe.MirrorSessionStarted += HandleAirPlayMirrorSessionStarted;
		_airPlayProbe.MirrorSessionEnded += HandleAirPlayMirrorSessionEnded;
		_airPlayProbe.StreamConfigReceived += config => HandleAirPlayStreamConfig(config, Volatile.Read(ref _airPlaySessionGeneration));
		_airPlayProbe.VideoPayloadReceived += (payload, sourceTimestampNanos, receivedTick) =>
			HandleAirPlayVideoPayload(payload, sourceTimestampNanos, receivedTick, Volatile.Read(ref _airPlaySessionGeneration));
		_airPlayProbe.AudioStreamStarted += (sampleRate, channels, samplesPerFrame) =>
			HandleAirPlayAudioStreamStarted(sampleRate, channels, samplesPerFrame, Volatile.Read(ref _airPlaySessionGeneration));
		_airPlayProbe.AudioFrameReceived += (frame, rtpTimestamp, sequence) =>
			HandleAirPlayAudioFrame(frame, rtpTimestamp, sequence, Volatile.Read(ref _airPlaySessionGeneration));
		InitializeTrayIcon();
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
		PreflightReport report = await StartupDiagnostics.RunAsync(_airPlayProbe);
		AppLog.Write($"Preflight: {report.Worst} ??" +
			string.Join("; ", System.Linq.Enumerable.Select(report.Checks, check => $"{check.Id}={check.Status}")));
		BindReadinessStrip(report);
		_ = CheckForUpdatesOnStartupAsync();
	}

	private void BindReadinessStrip(PreflightReport report)
	{
		if (report.Worst == PreflightStatus.Ok)
		{
			ReadinessStripBorder.Visibility = Visibility.Collapsed;
			BindEmptyStateDiagnosticStatus(report);
			return;
		}

		ReadinessStripBorder.Visibility = Visibility.Visible;

		bool hasBlocked = report.Worst == PreflightStatus.Blocked;
		ReadinessStripHeaderText.Text = hasBlocked
			? "Setup needs attention"
			: "Minor setup notes";
		ReadinessStripHeaderText.Foreground = hasBlocked
			? (Brush)FindResource("DangerBrush")
			: (Brush)FindResource("WarningBrush");

		foreach (PreflightCheck check in report.Checks)
		{
			switch (check.Id)
			{
			case "ffmpeg":
				FfmpegCheckRow.Visibility = check.Status == PreflightStatus.Ok ? Visibility.Collapsed : Visibility.Visible;
				FfmpegCheckText.Text = check.Message;
				FfmpegCheckDetail.Text = check.Detail ?? string.Empty;
				FfmpegCheckDetail.Visibility = check.Detail != null ? Visibility.Visible : Visibility.Collapsed;
				FfmpegCheckRow.Tag = check.Status;
				break;
			case "listeners":
				ListenersCheckRow.Visibility = check.Status == PreflightStatus.Ok ? Visibility.Collapsed : Visibility.Visible;
				ListenersCheckText.Text = check.Message;
				ListenersCheckDetail.Text = check.Detail ?? string.Empty;
				ListenersCheckDetail.Visibility = check.Detail != null ? Visibility.Visible : Visibility.Collapsed;
				break;
			case "network":
				NetworkCheckRow.Visibility = check.Status == PreflightStatus.Ok ? Visibility.Collapsed : Visibility.Visible;
				NetworkCheckText.Text = check.Message;
				NetworkCheckDetail.Text = check.Detail ?? string.Empty;
				NetworkCheckDetail.Visibility = check.Detail != null ? Visibility.Visible : Visibility.Collapsed;
				break;
			}
		}

		BindEmptyStateDiagnosticStatus(report);
	}

	private void BindEmptyStateDiagnosticStatus(PreflightReport report)
	{
		SolidColorBrush brush = (SolidColorBrush)FindResource("WarningBrush");
		string text = "Checking network...";
		bool listenerBlocked = HasDiagnosticIssue(report, "listeners", PreflightStatus.Blocked);

		if (report.Worst == PreflightStatus.Ok)
		{
			brush = (SolidColorBrush)FindResource("SuccessBrush");
			text = "Ready on this network";
		}
		else if (HasDiagnosticIssue(report, "ffmpeg"))
		{
			brush = (SolidColorBrush)FindResource("DangerBrush");
			text = "FFmpeg not found ??video decode unavailable";
		}
		else if (listenerBlocked)
		{
			brush = (SolidColorBrush)FindResource("WarningBrush");
			text = "Firewall may be blocking AirPlay ??check Windows Firewall";
		}
		else if (HasDiagnosticIssue(report, "network"))
		{
			brush = (SolidColorBrush)FindResource("WarningBrush");
			text = "No suitable network interface found";
		}

		DiagStatusDot.Fill = brush;
		DiagStatusText.Text = text;
		FirewallAllowInlineButton.Visibility = listenerBlocked ? Visibility.Visible : Visibility.Collapsed;
	}

	private void SetEmptyStateDiagnosticRechecking()
	{
		DiagStatusDot.Fill = (SolidColorBrush)FindResource("WarningBrush");
		DiagStatusText.Text = "Re-checking...";
	}

	private static bool HasDiagnosticIssue(PreflightReport report, string id)
	{
		return report.Checks.Any(check =>
			string.Equals(check.Id, id, StringComparison.OrdinalIgnoreCase) &&
			check.Status != PreflightStatus.Ok);
	}

	private static bool HasDiagnosticIssue(PreflightReport report, string id, PreflightStatus status)
	{
		return report.Checks.Any(check =>
			string.Equals(check.Id, id, StringComparison.OrdinalIgnoreCase) &&
			check.Status == status);
	}

	private async void ReadinessRecheckButton_Click(object sender, RoutedEventArgs e)
	{
		await RecheckReadinessAsync();
	}

	private async void RecheckDiagLink_Click(object sender, RoutedEventArgs e)
	{
		e.Handled = true;
		await RecheckReadinessAsync();
	}

	private async Task RecheckReadinessAsync()
	{
		ReadinessRecheckButton.IsEnabled = false;
		RecheckDiagLink.IsEnabled = false;
		ReadinessRecheckButton.Content = "Checking...";
		RecheckDiagLink.Content = "Checking...";
		SetEmptyStateDiagnosticRechecking();

		try
		{
			PreflightReport report = await StartupDiagnostics.RunAsync(_airPlayProbe);
			BindReadinessStrip(report);
		}
		finally
		{
			ReadinessRecheckButton.IsEnabled = true;
			RecheckDiagLink.IsEnabled = true;
			ReadinessRecheckButton.Content = "Re-check";
			RecheckDiagLink.Content = "Re-check";
		}
	}

	private async void FirewallHelpButton_Click(object sender, RoutedEventArgs e)
	{
		await AllowInFirewallAsync();
	}

	private async void FirewallAllowButton_Click(object sender, RoutedEventArgs e)
	{
		await AllowInFirewallAsync();
	}

	private async Task AllowInFirewallAsync()
	{
		SetFirewallAllowButtonsEnabled(false);
		SetFirewallAllowButtonsContent("Allowing...");
		SetStatus("Requesting Windows Firewall access...");
		SetEmptyStateDiagnosticRechecking();

		try
		{
			FirewallRuleInstallResult result = await WindowsFirewallRuleInstaller.AllowCurrentExecutableAsync();
			AppLog.Write($"Windows Firewall allow rule result: {result.Status}: {result.Message}");

			if (result.Status == FirewallRuleInstallStatus.Success)
			{
				SetStatus("Windows Firewall now allows iMirror. Reconnect mirroring if audio does not start.");
				await Task.Delay(500);
				await RecheckReadinessAsync();
				return;
			}

			if (result.Status == FirewallRuleInstallStatus.Cancelled)
			{
				SetStatus("Windows Firewall permission was cancelled.");
				return;
			}

			SetStatus(result.Message);
			OpenWindowsFirewallSettings();
		}
		finally
		{
			SetFirewallAllowButtonsEnabled(true);
			SetFirewallAllowButtonsContent("Allow");
			FirewallAllowInlineButton.Content = "Allow in Firewall";
			FirewallHelpButton.Content = "Allow in Firewall";
		}
	}

	private void SetFirewallAllowButtonsEnabled(bool isEnabled)
	{
		FirewallAllowButton.IsEnabled = isEnabled;
		FirewallAllowInlineButton.IsEnabled = isEnabled;
		FirewallHelpButton.IsEnabled = isEnabled;
	}

	private void SetFirewallAllowButtonsContent(string content)
	{
		FirewallAllowButton.Content = content;
		FirewallAllowInlineButton.Content = content;
		FirewallHelpButton.Content = content;
	}

	private static void OpenWindowsFirewallSettings()
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = "ms-settings:windowsdefender",
				UseShellExecute = true
			});
		}
		catch
		{
		}
	}

	private void Window_Closing(object? sender, CancelEventArgs e)
	{
		if (_shutdownStarted)
		{
			// Shutdown already running (e.g. via tray Exit); let this close proceed.
			return;
		}

		// Closing the window quits the app (no hide-to-tray). Cancel this close and run the
		// orderly shutdown, which stops the receiver, disposes the tray icon, and calls
		// Application.Shutdown() so no hidden tray instance is left behind.
		e.Cancel = true;
		AppLog.Write("Close requested; shutting down the application.");
		_ = ShutdownApplicationAsync();
	}

	private void InitializeTrayIcon()
	{
		try
		{
			_trayIconImage = CreateTrayIconImage();
			_trayMenu = new Forms.ContextMenuStrip();
			var showItem = new Forms.ToolStripMenuItem("Show iMirror");
			showItem.Click += delegate
			{
				RestoreFromTray();
			};
			var settingsItem = new Forms.ToolStripMenuItem("Settings...");
			settingsItem.Click += delegate
			{
				RestoreFromTray();
				ShowSettingsWindow();
			};
			var exitItem = new Forms.ToolStripMenuItem("Exit");
			exitItem.Click += async delegate
			{
				await ShutdownApplicationAsync();
			};
			_trayMenu.Items.Add(showItem);
			_trayMenu.Items.Add(settingsItem);
			_trayMenu.Items.Add(new Forms.ToolStripSeparator());
			_trayMenu.Items.Add(exitItem);

			_trayIcon = new Forms.NotifyIcon
			{
				ContextMenuStrip = _trayMenu,
				Icon = _trayIconImage,
				Text = "iMirror",
				Visible = true
			};
			_trayIcon.MouseUp += TrayIcon_MouseUp;
			_trayIcon.DoubleClick += delegate
			{
				RestoreFromTray();
			};
		}
		catch (Exception ex)
		{
			AppLog.Write("Tray icon initialization failed: " + ex);
			DisposeTrayIcon();
		}
	}

	private static DrawingIcon CreateTrayIconImage()
	{
		try
		{
			string? executablePath = Environment.ProcessPath;
			if (!string.IsNullOrWhiteSpace(executablePath))
			{
				DrawingIcon? icon = DrawingIcon.ExtractAssociatedIcon(executablePath);
				if (icon != null)
				{
					return icon;
				}
			}
		}
		catch (Exception ex)
		{
			AppLog.Write("App icon extraction failed: " + ex.Message);
		}

		return (DrawingIcon)System.Drawing.SystemIcons.Application.Clone();
	}

	private void TrayIcon_MouseUp(object? sender, Forms.MouseEventArgs e)
	{
		if (e.Button == Forms.MouseButtons.Left)
		{
			RestoreFromTray();
		}
	}

	private void RestoreFromTray()
	{
		if (_shutdownStarted)
		{
			return;
		}

		Show();
		if (base.WindowState == WindowState.Minimized)
		{
			base.WindowState = WindowState.Normal;
		}
		Activate();
	}

	private async Task ShutdownApplicationAsync()
	{
		if (_shutdownStarted)
		{
			return;
		}

		_shutdownStarted = true;
		_lifecycle.MarkExplicitExit();
		try
		{
			_settingsWindow?.Close();
			await DisconnectAsync(null, userRequested: true);
		}
		catch (Exception ex)
		{
			AppLog.Write("Application shutdown disconnect failed: " + ex);
		}
		finally
		{
			CleanupGuards.RunStep("AirPlay probe dispose", () => _airPlayProbe.Dispose());
			CleanupGuards.RunStep("mDNS browser dispose", () => _browser.Dispose());
			CleanupGuards.RunStep("tray icon dispose", DisposeTrayIcon);
			Application.Current.Shutdown();
		}
	}

	private void DisposeTrayIcon()
	{
		Forms.NotifyIcon? trayIcon = _trayIcon;
		_trayIcon = null;
		if (trayIcon != null)
		{
			trayIcon.MouseUp -= TrayIcon_MouseUp;
			trayIcon.Visible = false;
			trayIcon.Dispose();
		}

		Forms.ContextMenuStrip? trayMenu = _trayMenu;
		_trayMenu = null;
		trayMenu?.Dispose();

		DrawingIcon? trayIconImage = _trayIconImage;
		_trayIconImage = null;
		trayIconImage?.Dispose();
	}

	ReceiverSettingsSnapshot ISettingsHost.StartupReceiverSettings => StartupReceiverSettings;

	RenderModeSettingsSnapshot ISettingsHost.StartupRenderModeSettings => StartupRenderModeSettings;

	bool ISettingsHost.GpuQualityRequested => GpuQualityRequested;

	bool ISettingsHost.QualityPathAvailable => QualityPathAvailable;

	int ISettingsHost.LiveAudioSyncOffsetMilliseconds => Volatile.Read(ref _audioSyncOffsetMilliseconds);

	void ISettingsHost.SetLiveAudioSyncOffsetMilliseconds(int value)
	{
		Volatile.Write(ref _audioSyncOffsetMilliseconds, ReceiverSettings.ClampAudioOffset(value));
		WasapiAudioOutput? output = _audioOutput;
		output?.SetSyncTargetLatencyMilliseconds(ResolveAudioSyncTargetLatencyMilliseconds());
	}

	Task ISettingsHost.RestartApplicationAsync()
	{
		return RestartApplicationAsync();
	}

	Task<UpdateCheckResult> ISettingsHost.CheckForUpdatesAsync(bool manual)
	{
		return CheckForUpdatesAsync(manual);
	}

	Task<bool> ISettingsHost.InstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress)
	{
		return InstallUpdateAsync(updateInfo, progress);
	}

	void ISettingsHost.SetStatusMessage(string message)
	{
		SetStatus(message);
	}

	private static bool ResolveStartupAudioAdvertised()
	{
		return StartupReceiverSettings.Overrides.AudioEnabled
			? StartupReceiverSettings.Persisted.AudioEnabled
			: StartupReceiverSettings.Effective.AudioEnabled;
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

	private void HandleAirPlayMirrorSessionStarted()
	{
		if (base.Dispatcher.CheckAccess())
		{
			ResetAirPlayMirrorSessionState(activeSession: true, "AirPlay session starting.", "AirPlay mirror session state reset for new sender/session.");
			return;
		}

		base.Dispatcher.Invoke(new Action(delegate
		{
			ResetAirPlayMirrorSessionState(activeSession: true, "AirPlay session starting.", "AirPlay mirror session state reset for new sender/session.");
		}), DispatcherPriority.Send);
	}

	private void HandleAirPlayMirrorSessionEnded()
	{
		if (base.Dispatcher.CheckAccess())
		{
			ResetAirPlayMirrorSessionState(activeSession: false, "AirPlay session ended.", "AirPlay mirror session state reset after sender ended the session.");
			return;
		}

		base.Dispatcher.Invoke(new Action(delegate
		{
			ResetAirPlayMirrorSessionState(activeSession: false, "AirPlay session ended.", "AirPlay mirror session state reset after sender ended the session.");
		}), DispatcherPriority.Send);
	}

	private void ResetAirPlayMirrorSessionState(bool activeSession, string statusMessage, string logMessage)
	{
		int generation = Interlocked.Increment(ref _connectionGeneration);
		Volatile.Write(ref _airPlaySessionGeneration, activeSession ? generation : 0);
		CleanupGuards.RunStep("video watchdog stop", StopVideoWatchdog);
		_videoSinkAcceptingInput = false;
		FfmpegDecoder? decoder = _decoder;
		_decoder = null;
		CleanupGuards.RunStep("ffmpeg decoder dispose", () => decoder?.Dispose());
#if HIGH_RESOLUTION_D3D
		IHighResolutionD3DDecoder? mediaFoundationD3DDecoder = _mediaFoundationD3DDecoder;
		_mediaFoundationD3DDecoder = null;
		CleanupGuards.RunStep("Media Foundation D3D decoder dispose", () => mediaFoundationD3DDecoder?.Dispose());
		CleanupGuards.RunStep("D3D presenter dispose", DisposeHighResolutionD3DPresenter);
		CleanupGuards.RunStep("pending D3D frame release", ReleasePendingD3DFrame);
#endif
		CleanupGuards.RunStep("pending frame release", ReleasePendingFrame);
		CleanupGuards.RunStep("audio pipeline stop", StopAudioPipeline);
		_bitmap = null;
		VideoImage.Source = null;
		VideoImage.Visibility = Visibility.Visible;
		ShowEmptyStateSurface();
		_decoderOutputFps = 0;
		_decoderMaxRenderWidth = RealtimeMaxRenderWidth;
		ResetStreamStateForNewConnection();
		SetStatus(statusMessage);
		AppLog.Write(logMessage);
		UpdateDiagnostics();
	}

	private void ShowVideoSurface()
	{
		if (VideoStage.Background != Brushes.Black)
		{
			VideoStage.Background = Brushes.Black;
		}
		if (EmptyStatePanel.Visibility != Visibility.Collapsed)
		{
			EmptyStatePanel.Visibility = Visibility.Collapsed;
		}
	}

	private void ShowEmptyStateSurface()
	{
		if (VideoStage.Background != Brushes.Transparent)
		{
			VideoStage.Background = Brushes.Transparent;
		}
		if (EmptyStatePanel.Visibility != Visibility.Visible)
		{
			EmptyStatePanel.Visibility = Visibility.Visible;
		}
	}

	private void HandleAirPlayStreamConfig(StreamConfig config, int generation)
	{
		void ApplyStreamConfig()
		{
			if (!IsCurrentGeneration(generation, Volatile.Read(ref _connectionGeneration)))
			{
				return;
			}

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
					AppLog.Write("AirPlay stream config repeated for high-resolution D3D path; keeping active renderer.");
					UpdateHighResolutionD3DPresenterLayout();
					UpdateDiagnostics();
					return;
				}
				else
#endif
				{
					return;
				}
			}

			ResetH264Gate();
			StartDecoder(config);
		}

		if (base.Dispatcher.CheckAccess())
		{
			ApplyStreamConfig();
			return;
		}

		// AirPlay emits SPS/PPS immediately after the config callback. Apply the reset synchronously
		// so those parameter sets seed the fresh H.264 gate before the next IDR arrives.
		base.Dispatcher.Invoke(new Action(ApplyStreamConfig), DispatcherPriority.Send);
	}

	private void HandleAirPlayVideoPayload(byte[] payload, ulong sourceTimestampNanos, long receivedTick, int generation)
	{
		if (!IsCurrentGeneration(generation, Volatile.Read(ref _connectionGeneration)))
		{
			return;
		}

		ProcessVideoPayload(payload, sourceTimestampNanos, receivedTick, countReceived: true, allowPreSinkBuffer: true);
	}

	private void HandleAirPlayAudioStreamStarted(int sampleRate, int channels, int samplesPerFrame, int generation)
	{
		if (!IsCurrentGeneration(generation, Volatile.Read(ref _connectionGeneration)))
		{
			return;
		}

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
			if (_audioDecoder != null && _audioOutput != null)
			{
				RestartAudioRtpWatchdogLocked(generation);
			}
		}
	}

	private void HandleAirPlayAudioFrame(byte[] frame, uint rtpTimestamp, ushort sequence, int generation)
	{
		if (!IsCurrentGeneration(generation, Volatile.Read(ref _connectionGeneration)))
		{
			return;
		}

		ClearAudioFirewallWarning();
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

	private void RestartAudioRtpWatchdogLocked(int generation)
	{
		CancelAudioRtpWatchdogLocked();
		long frameBaseline = Interlocked.Read(ref _audioFramesReceived);
		_audioRtpWatchdogCts = new CancellationTokenSource();
		_ = WatchAudioRtpStartupAsync(generation, frameBaseline, _audioRtpWatchdogCts.Token);
	}

	private void CancelAudioRtpWatchdogLocked()
	{
		CancellationTokenSource? cts = _audioRtpWatchdogCts;
		_audioRtpWatchdogCts = null;
		try
		{
			cts?.Cancel();
		}
		catch
		{
		}
	}

	private async Task WatchAudioRtpStartupAsync(int generation, long frameBaseline, CancellationToken token)
	{
		try
		{
			await Task.Delay(AudioRtpStartupTimeout, token);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (ObjectDisposedException)
		{
			return;
		}

		if (!IsCurrentGeneration(generation, Volatile.Read(ref _connectionGeneration)) ||
			Interlocked.Read(ref _audioFramesReceived) > frameBaseline)
		{
			return;
		}

		bool shouldNotify = false;
		lock (_audioGate)
		{
			if (token.IsCancellationRequested ||
				Interlocked.Read(ref _audioFramesReceived) > frameBaseline ||
				_audioFirewallWarningActive)
			{
				return;
			}

			_audioFirewallWarningActive = true;
			_audioStatus = AudioFirewallWarningMessage;
			shouldNotify = true;
		}

		if (!shouldNotify)
		{
			return;
		}

		AppLog.Write(
			$"AirPlay audio RTP did not arrive within {AudioRtpStartupTimeout.TotalSeconds:N0}s after SETUP; " +
			"Windows Firewall may be blocking the dynamic UDP audio ports. Allow iMirror for Private and Public networks.");
		_ = base.Dispatcher.BeginInvoke(new Action(delegate
		{
			SetStatus(AudioFirewallWarningMessage);
			UpdateDiagnostics();
		}), DispatcherPriority.Background);
	}

	private void ClearAudioFirewallWarning()
	{
		bool wasActive = false;
		lock (_audioGate)
		{
			CancelAudioRtpWatchdogLocked();
			if (_audioFirewallWarningActive)
			{
				_audioFirewallWarningActive = false;
				wasActive = true;
			}
		}

		if (!wasActive)
		{
			return;
		}

		AppLog.Write("AirPlay audio RTP reached the decoder; clearing firewall warning.");
		base.Dispatcher.BeginInvoke(new Action(delegate
		{
			UpdateReceiverChrome();
			UpdateDiagnostics();
		}), DispatcherPriority.Background);
	}

	internal static bool IsCurrentGeneration(int payloadGeneration, int currentGeneration)
	{
		return payloadGeneration != 0 && payloadGeneration == currentGeneration;
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
		CancelAudioRtpWatchdogLocked();
		_audioFirewallWarningActive = false;
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
			await DisconnectAsync(null);
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
					if (!ReferenceEquals(_client, client))
					{
						return;
					}

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
				if (!ReferenceEquals(_client, client) || generation != Volatile.Read(ref _connectionGeneration))
				{
					return;
				}

				HandleVideoPayload(payload, sourceTimestampNanos, receivedTick);
			};
			if (SyntheticCursorOverlayEnabled)
			{
				_client.CursorReceived += delegate(CursorState cursorState, ulong sourceTimestampNanos, long receivedTick)
				{
					if (!ReferenceEquals(_client, client) || generation != Volatile.Read(ref _connectionGeneration))
					{
						return;
					}

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
				await DisconnectAsync($"{verb} failed: " + ex.Message);
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
#if HIGH_RESOLUTION_D3D
			_staleD3DFramesDropped = 0L;
			_lastStaleD3DFrameDropLogTick = 0L;
#endif
			_audioFramesReceived = 0L;
			_audioFramesQueued = 0L;
			_audioPcmFrames = 0L;
			_audioPcmBytes = 0L;
			_audioStatus = "waiting for stream";
			StopAudioPipeline();
			_streamConfig = null;
			ResetRemoteCursorState();
			_decoderStatus = "waiting for stream config";
			_videoSinkAcceptingInput = false;
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
		IHighResolutionD3DDecoder? mediaFoundationD3DDecoder = _mediaFoundationD3DDecoder;
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
		if (!_videoSinkAcceptingInput)
		{
			return false;
		}

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

		await DisconnectAsync("Connection lost. Reconnecting...");
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
		if (e.Key == Key.Escape && _settingsWindow?.IsVisible == true)
		{
			e.Handled = true;
			_settingsWindow.Close();
			return;
		}

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
			MainTitleBar.Visibility = Visibility.Collapsed;
			FullscreenButton.Content = "Windowed";
			CompactFullscreenButton.Content = "W";
			CompactFullscreenButton.ToolTip = "Exit fullscreen";
		}
		else
		{
			base.WindowStyle = _previousWindowStyle;
			base.WindowState = _previousWindowState;
			_isFullscreen = false;
			MainTitleBar.Visibility = Visibility.Visible;
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

	private void ShowSettingsWindow()
	{
		if (_settingsWindow != null)
		{
			if (_settingsWindow.WindowState == WindowState.Minimized)
			{
				_settingsWindow.WindowState = WindowState.Normal;
			}
			_settingsWindow.Activate();
			return;
		}

		var settingsWindow = new SettingsWindow(this)
		{
			Owner = this
		};
		settingsWindow.Closed += delegate
		{
			if (ReferenceEquals(_settingsWindow, settingsWindow))
			{
				_settingsWindow = null;
			}
		};
		_settingsWindow = settingsWindow;
		settingsWindow.Show();
		settingsWindow.Activate();
	}

	private async Task RestartApplicationAsync()
	{
		string executablePath = ResolveExecutablePath();
		Process.Start(new ProcessStartInfo
		{
			FileName = executablePath,
			WorkingDirectory = AppContext.BaseDirectory,
			UseShellExecute = true
		});
		await ShutdownApplicationAsync();
	}

	private async Task CheckForUpdatesOnStartupAsync()
	{
		if (_startupUpdateCheckStarted || _shutdownStarted)
		{
			return;
		}

		_startupUpdateCheckStarted = true;
		try
		{
			await Task.Delay(1500);
			if (_shutdownStarted)
			{
				return;
			}

			UpdateCheckResult result = await CheckForUpdatesAsync(manual: false);
			UpdateInfo? update = result.Update;
			if (update?.IsNewer != true)
			{
				return;
			}

			DateTimeOffset now = DateTimeOffset.Now;
			if (!ReceiverSettings.ShouldShowAutomaticUpdateNotice(update, now))
			{
				return;
			}

			ReceiverSettings.MarkUpdateNoticeShown(update.LatestVersion, now);
			ShowUpdateNotice(update);
		}
		catch (Exception ex)
		{
			AppLog.Write("Startup update check failed: " + ex.Message);
		}
	}

	private async Task<UpdateCheckResult> CheckForUpdatesAsync(bool manual)
	{
		UpdateInfo? update = await _updateService.CheckAsync(includePrerelease: false, CancellationToken.None);
		if (update == null)
		{
			return new UpdateCheckResult(
				null,
				manual ? "Could not check for updates. Open Releases and install manually." : string.Empty,
				Failed: true);
		}

		if (!update.IsNewer)
		{
			return new UpdateCheckResult(null, "You're on the latest iMirror.", Failed: false);
		}

		_pendingUpdate = update;
		if (manual)
		{
			ShowUpdateNotice(update);
		}

		return new UpdateCheckResult(update, $"iMirror {update.LatestVersion} is available.", Failed: false);
	}

	private void ShowUpdateNotice(UpdateInfo update)
	{
		_pendingUpdate = update;
		UpdateNoticeTextBlock.Text = $"iMirror {update.LatestVersion} is available.";
		UpdateNoticeInstallButton.Content = "Update";
		UpdateNoticeInstallButton.IsEnabled = true;
		UpdateNoticeDismissButton.IsEnabled = true;
		UpdateNoticeBorder.Visibility = Visibility.Visible;
	}

	private void HideUpdateNotice()
	{
		UpdateNoticeBorder.Visibility = Visibility.Collapsed;
	}

	private async Task<bool> InstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress)
	{
		try
		{
			_pendingUpdate = updateInfo;
			UpdateNoticeBorder.Visibility = Visibility.Visible;
			UpdateNoticeInstallButton.IsEnabled = false;
			UpdateNoticeDismissButton.IsEnabled = false;
			UpdateNoticeTextBlock.Text = $"Downloading iMirror {updateInfo.LatestVersion}...";
			SetStatus($"Downloading iMirror {updateInfo.LatestVersion} update...");

			string setupPath = await _updateService.DownloadSetupAsync(updateInfo, progress, CancellationToken.None);
			UpdateNoticeTextBlock.Text = "Starting update installer...";
			SetStatus("Starting update installer...");
			UpdateLauncher.Launch(setupPath);
			SetStatus("Update installer started. iMirror will close when setup is ready.");
			return true;
		}
		catch (Exception ex)
		{
			AppLog.Write("Update install failed: " + ex);
			SetStatus("Update could not be installed: " + ex.Message);
			UpdateNoticeTextBlock.Text = "Update could not be installed. Open Releases and install manually.";
			UpdateNoticeInstallButton.Content = "Retry";
			UpdateNoticeInstallButton.IsEnabled = true;
			UpdateNoticeDismissButton.IsEnabled = true;
			return false;
		}
	}

	private async void UpdateNoticeInstallButton_Click(object sender, RoutedEventArgs e)
	{
		UpdateInfo? update = _pendingUpdate;
		if (update == null)
		{
			return;
		}

		var progress = new Progress<double>(value =>
		{
			int percent = Math.Clamp((int)Math.Round(value * 100.0), 0, 100);
			UpdateNoticeTextBlock.Text = $"Downloading iMirror {update.LatestVersion}... {percent}%";
		});
		await InstallUpdateAsync(update, progress);
	}

	private void UpdateNoticeDismissButton_Click(object sender, RoutedEventArgs e)
	{
		UpdateInfo? update = _pendingUpdate;
		if (update != null)
		{
			ReceiverSettings.DismissUpdateNotice(update.LatestVersion);
		}
		HideUpdateNotice();
	}

	private static string ResolveExecutablePath()
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

		return executablePath;
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
			ResizeWindowToStream(config);
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

	// Default window geometry mirrors the values declared in MainWindow.xaml.
	private const double DefaultWindowWidth = 1180;
	private const double DefaultWindowHeight = 760;
	private const double DefaultMinWindowWidth = 820;
	private const double DefaultMinWindowHeight = 620;
	// Lower floor used while mirroring so a portrait phone stream can use a
	// tall, narrow window instead of being letterboxed inside a wide one.
	private const double MirroringMinWindowEdge = 320;
	private const double PortraitMirroringMaxWorkAreaWidthRatio = 0.45;
	private const double PortraitMirroringMaxWorkAreaHeightRatio = 0.68;
	private const double PortraitMirroringMaxWidth = 520;
	private const double PortraitMirroringMaxHeight = 760;

	private void ResizeWindowToStream(StreamConfig config)
	{
		if (config.Width <= 0 || config.Height <= 0 || WindowState != WindowState.Normal)
		{
			return;
		}

		double aspect = (double)config.Width / config.Height;
		Rect work = SystemParameters.WorkArea;
		bool isPortrait = config.Height > config.Width;
		double maxWidth = work.Width * 0.9;
		double maxHeight = work.Height * 0.9;
		if (isPortrait)
		{
			maxWidth = Math.Min(
				maxWidth,
				Math.Min(work.Width * PortraitMirroringMaxWorkAreaWidthRatio, PortraitMirroringMaxWidth));
			maxHeight = Math.Min(
				maxHeight,
				Math.Min(work.Height * PortraitMirroringMaxWorkAreaHeightRatio, PortraitMirroringMaxHeight));
		}

		double targetWidth = maxWidth;
		double targetHeight = targetWidth / aspect;
		if (targetHeight > maxHeight)
		{
			targetHeight = maxHeight;
			targetWidth = targetHeight * aspect;
		}

		// Relax the empty-state minimums so phone aspect ratios fit snugly.
		MinWidth = MirroringMinWindowEdge;
		MinHeight = MirroringMinWindowEdge;
		Width = Math.Round(Math.Max(MirroringMinWindowEdge, targetWidth));
		Height = Math.Round(Math.Max(MirroringMinWindowEdge, targetHeight));
		Left = work.Left + (work.Width - Width) / 2.0;
		Top = work.Top + (work.Height - Height) / 2.0;

		string resizeProfile = isPortrait ? "portrait" : "landscape";
		AppLog.Write($"Resized window to {resizeProfile} stream {config.Width}x{config.Height} (aspect {aspect:F3}) -> {Width:F0}x{Height:F0}.");
	}

	private void RestoreDefaultWindowSize()
	{
		MinWidth = DefaultMinWindowWidth;
		MinHeight = DefaultMinWindowHeight;
		if (WindowState != WindowState.Normal)
		{
			return;
		}

		Width = DefaultWindowWidth;
		Height = DefaultWindowHeight;
		Rect work = SystemParameters.WorkArea;
		Left = work.Left + (work.Width - Width) / 2.0;
		Top = work.Top + (work.Height - Height) / 2.0;
	}

	private void StartFreshDecoder(StreamConfig config, string reason)
	{
		StopVideoWatchdog();
		_videoSinkAcceptingInput = false;
		_decoder?.Dispose();
		_decoder = null;
#if HIGH_RESOLUTION_D3D
		_mediaFoundationD3DDecoder?.Dispose();
		_mediaFoundationD3DDecoder = null;
		DisposeHighResolutionD3DPresenter();
		ReleasePendingD3DFrame();
#endif
		ReleasePendingFrame();
		ShowVideoSurface();
		// Restart the latency warmup grace so decoder/renderer spin-up and reconnect/session-refresh
		// catch-up are excluded from steady-state percentiles instead of inflating the first window.
		_receiveToPresentLatencyWindow.BeginWarmup();
#if HIGH_RESOLUTION_D3D
		bool requireHighResolutionD3DPath = ShouldUseHighResolutionD3DPath(config) && !_gpuPathDisabledThisSession;
		if (requireHighResolutionD3DPath && TryStartHighResolutionD3DPath(config, reason))
		{
			_decoderMaxRenderWidth = config.Width;
			_decoderOutputFps = Math.Clamp(config.Fps, 1, HighResolutionMaxRenderFps);
			_videoSinkAcceptingInput = true;
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
		_decoder.DumpH264Enabled = StartupReceiverSettings.Effective.DumpH264;
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
		_videoSinkAcceptingInput = true;
		FlushPendingVideoToSink();
		AppLog.Write("FFmpeg software decoder started for " + reason + ".");
		StartVideoWatchdog(config, _connectionGeneration);
	}

#if HIGH_RESOLUTION_D3D
	private static bool ShouldUseHighResolutionD3DPath(StreamConfig config)
	{
		return ShouldUseHighResolutionD3DPath(
			config,
			ReceiverSettings.Load().Effective.VideoEngine,
			QualityRenderModeEnabled,
			GpuQualityRequested);
	}

	internal static bool ShouldUseHighResolutionD3DPath(
		StreamConfig config,
		ReceiverVideoEngineSetting videoEngine,
		bool qualityRenderModeEnabled,
		bool gpuQualityRequested)
	{
		return videoEngine != ReceiverVideoEngineSetting.Software
			&& qualityRenderModeEnabled
			&& gpuQualityRequested
			&& (config.Width > ResponsiveMaxRenderWidth || config.Height > 1080);
	}

	private bool TryStartHighResolutionD3DPath(StreamConfig config, string reason)
	{
		try
		{
			D3DGpuBinding binding = D3DGpuBindingSelector.Current;
			IHighResolutionD3DPresenter presenter;
			IHighResolutionD3DDecoder decoder;
			if (binding == D3DGpuBinding.Vortice)
			{
				var vorticePresenter = new VorticeD3D11SwapChainVideoPresenter();
				presenter = vorticePresenter;
				decoder = new VorticeMediaFoundationD3D11Decoder(config.Width, config.Height, config.Fps, vorticePresenter.Device);
			}
			else
			{
				var sharpDxPresenter = new D3D11SwapChainVideoPresenter();
				presenter = sharpDxPresenter;
				decoder = new MediaFoundationD3D11Decoder(config.Width, config.Height, config.Fps, sharpDxPresenter.Device);
			}

			decoder.DumpH264Enabled = StartupReceiverSettings.Effective.DumpH264;
			_highResolutionD3DPresenter = presenter;
			_mediaFoundationD3DDecoder = decoder;
			// Host the swap-chain child window in the video stage (bypasses WPF D3DImage composition).
			VideoImage.Visibility = Visibility.Collapsed;
			VideoImage.Source = null;
			_bitmap = null;
			VideoStage.Children.Insert(0, presenter.View);
			presenter.View.HorizontalAlignment = HorizontalAlignment.Center;
			presenter.View.VerticalAlignment = VerticalAlignment.Center;
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
			decoder.Start();
			_decoderStatus = "Media Foundation D3D11 decoder started";
			AppLog.Write($"High-resolution D3D path active for {reason}: {config.Width}x{config.Height}@{config.Fps}, gpuBinding={D3DGpuBindingSelector.CurrentName}, d3d11MultithreadProtected={presenter.IsMultithreadProtected}.");
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
		IHighResolutionD3DPresenter? presenter = _highResolutionD3DPresenter;
		StreamConfig? config = _streamConfig;
		if (presenter == null || config == null)
		{
			return;
		}
		UpdateHighResolutionD3DPresenterLayout(presenter, config);
	}

	private void UpdateHighResolutionD3DPresenterLayout(IHighResolutionD3DPresenter presenter, StreamConfig config)
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
		double topInsetDip = ResolveHighResolutionD3DTopInsetDip(stageHeightDip);
		double availableHeightDip = Math.Max(1.0, stageHeightDip - topInsetDip);
		double availableHeightPixels = availableHeightDip * dpiScaleY;
		double scale = Math.Min(stageWidthPixels / config.Width, availableHeightPixels / config.Height);
		double fitWidthPixels = Math.Max(1.0, Math.Round(config.Width * scale));
		double fitHeightPixels = Math.Max(1.0, Math.Round(config.Height * scale));
		double fitWidthDip = Math.Min(stageWidthDip, fitWidthPixels / dpiScaleX);
		double fitHeightDip = Math.Min(availableHeightDip, fitHeightPixels / dpiScaleY);

		FrameworkElement presenterView = presenter.View;
		presenterView.Width = fitWidthDip;
		presenterView.Height = fitHeightDip;
		presenterView.Margin = new Thickness(0.0, topInsetDip, 0.0, 0.0);
		presenterView.HorizontalAlignment = HorizontalAlignment.Center;
		presenterView.VerticalAlignment = topInsetDip > 0.0 ? VerticalAlignment.Top : VerticalAlignment.Center;
	}

	private double ResolveHighResolutionD3DTopInsetDip(double stageHeightDip)
	{
		if (!_audioFirewallWarningActive || ReceiverCardBorder.Visibility != Visibility.Visible)
		{
			return 0.0;
		}

		double cardHeightDip = ReceiverCardBorder.ActualHeight > 0.0 ? ReceiverCardBorder.ActualHeight : 44.0;
		double desiredInsetDip = ReceiverCardBorder.Margin.Top + cardHeightDip + 12.0;
		return Math.Min(Math.Max(0.0, desiredInsetDip), Math.Max(0.0, stageHeightDip - 1.0));
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
			ShowEmptyStateSurface();
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
	private void QueueD3DFrameForPresentation(IHighResolutionD3DFrame frame)
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
		IHighResolutionD3DFrame? pendingD3DFrame;
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
		IHighResolutionD3DPresenter? d3dPresenter = _highResolutionD3DPresenter;
		FrameworkElement? d3dPresenterView = d3dPresenter?.View;
		if (d3dPresenterView != null && d3dPresenterView.ActualWidth > 0.0 && d3dPresenterView.ActualHeight > 0.0)
		{
			double d3dLeft = Math.Max(0.0, (VideoStage.ActualWidth - d3dPresenterView.ActualWidth) / 2.0);
			double d3dTop = Math.Max(0.0, (VideoStage.ActualHeight - d3dPresenterView.ActualHeight) / 2.0);
			return new Rect(d3dLeft, d3dTop, d3dPresenterView.ActualWidth, d3dPresenterView.ActualHeight);
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

	private async Task DisconnectAsync(string? finalStatus = "Disconnected.", bool userRequested = false)
	{
		if (userRequested)
		{
			_manualDisconnectRequested = true;
			_autoReconnectAttempts = 0;
			Interlocked.Increment(ref _connectionGeneration);
			Volatile.Write(ref _airPlaySessionGeneration, 0);
		}

		await _disconnectGate.WaitAsync();

		try
		{
			MirrorClient? client = _client;
			_client = null;
			await CleanupGuards.RunStepAsync("legacy client dispose", async delegate
			{
				if (client != null)
				{
					await client.DisposeAsync();
				}
			});

			CleanupGuards.RunStep("video watchdog stop", StopVideoWatchdog);
			_videoSinkAcceptingInput = false;
			FfmpegDecoder? decoder = _decoder;
			_decoder = null;
			CleanupGuards.RunStep("ffmpeg decoder dispose", () => decoder?.Dispose());
			CleanupGuards.RunStep("audio pipeline stop", StopAudioPipeline);
#if HIGH_RESOLUTION_D3D
			IHighResolutionD3DDecoder? mediaFoundationD3DDecoder = _mediaFoundationD3DDecoder;
			_mediaFoundationD3DDecoder = null;
			CleanupGuards.RunStep("Media Foundation D3D decoder dispose", () => mediaFoundationD3DDecoder?.Dispose());
#endif
#if HIGH_RESOLUTION_D3D
			CleanupGuards.RunStep("D3D presenter dispose", DisposeHighResolutionD3DPresenter);
#endif
			_bitmap = null;
			VideoImage.Source = null;
			VideoImage.Visibility = Visibility.Visible;
			ResetRemoteCursorState();
			ShowEmptyStateSurface();
			RestoreDefaultWindowSize();
			CleanupGuards.RunStep("pending frame release", ReleasePendingFrame);
#if HIGH_RESOLUTION_D3D
			CleanupGuards.RunStep("pending D3D frame release", ReleasePendingD3DFrame);
#endif
			_streamConfig = null;
			_decoderStatus = "not started";
			_decoderOutputFps = 0;
			_decoderMaxRenderWidth = RealtimeMaxRenderWidth;
			ResetH264Gate();
			SetConnectedUi(connected: false, finalStatus);
			UpdateDiagnostics();
		}
		finally
		{
			_disconnectGate.Release();
		}
	}

#if HIGH_RESOLUTION_D3D
	private void DisposeHighResolutionD3DPresenter()
	{
		IHighResolutionD3DPresenter? presenter = _highResolutionD3DPresenter;
		if (presenter == null)
		{
			return;
		}
		_highResolutionD3DPresenter = null;
		try
		{
			VideoStage.Children.Remove(presenter.View);
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

	private void SetConnectedUi(bool connected, string? disconnectedStatus = "Disconnected.")
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
		bool connected = IsMirroringActive();
		int deviceCount = _devices.Count;
		bool audioFirewallWarning = _audioFirewallWarningActive;

		HeaderSubtitleTextBlock.Text = ResolveReceiverSubtitle(connected, deviceCount, audioFirewallWarning);
		DeviceSummaryTextBlock.Text = ResolveDeviceSummary(deviceCount);
		ReceiverCardTitleTextBlock.Text = ResolveReceiverCardTitle(connected);
		ReceiverStateBadgeTextBlock.Text = ResolveReceiverBadgeText(connected, deviceCount);
		ReceiverStatusTextBlock.Text = _statusText;
		EmptyStateStatusTextBlock.Text = _statusText;
		EmptyStateTextBlock.Text = ResolveEmptyStateTitle(connected, deviceCount);
		EmptyStateDetailTextBlock.Text = ResolveEmptyStateDetail(connected, deviceCount);
		HowToMirrorCard.Visibility = connected || _isConnecting ? Visibility.Collapsed : Visibility.Visible;
		StatusSummaryPanel.Visibility = connected || _isConnecting ? Visibility.Collapsed : Visibility.Visible;
		// While mirroring, keep the stage clean unless there is an actionable warning.
		ReceiverCardBorder.Visibility = connected && !audioFirewallWarning ? Visibility.Collapsed : Visibility.Visible;
		FirewallAllowButton.Visibility = audioFirewallWarning ? Visibility.Visible : Visibility.Collapsed;

		Brush statusBrush = ResolveReceiverStatusBrush(connected, deviceCount, audioFirewallWarning);
		SidebarStatusDot.Fill = statusBrush;
		EmptyStateStatusDot.Fill = statusBrush;
		ReceiverStateBadge.Background = statusBrush;
#if HIGH_RESOLUTION_D3D
		UpdateHighResolutionD3DPresenterLayout();
#endif
	}

	private bool IsMirroringActive()
	{
		return _client != null ||
			Volatile.Read(ref _airPlaySessionGeneration) != 0 ||
			_streamConfig != null ||
			_decoder != null
#if HIGH_RESOLUTION_D3D
			|| _mediaFoundationD3DDecoder != null
#endif
			;
	}

	private string ResolveReceiverCardTitle(bool connected)
	{
		if (connected)
		{
			return "Mirroring";
		}
		if (_isConnecting)
		{
			return "Connecting";
		}
		return "Ready to receive";
	}

	private string ResolveReceiverBadgeText(bool connected, int deviceCount)
	{
		if (connected)
		{
			return "LIVE";
		}
		if (_isConnecting)
		{
			return "LINKING";
		}
		return deviceCount > 0 ? "LEGACY" : "READY";
	}

	private string ResolveReceiverSubtitle(bool connected, int deviceCount, bool audioFirewallWarning)
	{
		if (audioFirewallWarning)
		{
			return "Audio blocked by Windows Firewall";
		}
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

	private Brush ResolveReceiverStatusBrush(bool connected, int deviceCount, bool audioFirewallWarning)
	{
		if (audioFirewallWarning)
		{
			return (Brush)FindResource("WarningBrush");
		}
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
	private void PresentD3DFrame(IHighResolutionD3DFrame frame)
	{
		long renderStartTick = Stopwatch.GetTimestamp();
		try
		{
			if (frame.ReceivedTick > 0)
			{
				long ageMs = ElapsedMilliseconds(frame.ReceivedTick, renderStartTick);
				if (ageMs > HighResolutionMaxLiveFrameAgeMilliseconds)
				{
					Interlocked.Increment(ref _renderDroppedFrames);
					long staleDropped = Interlocked.Increment(ref _staleD3DFramesDropped);
					LogStaleD3DFrameDropThrottled(ageMs, staleDropped);
					QueueDiagnosticsUpdate();
					return;
				}
			}

			IHighResolutionD3DPresenter? presenter = _highResolutionD3DPresenter;
			if (presenter == null)
			{
				return;
			}

			try
			{
				presenter.PresentFrame(frame);
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
			LogRenderLatencyThrottled(renderStartTick, renderDoneTick, renderedFrames);
			QueueDiagnosticsUpdate();
		}
		finally
		{
			frame.Dispose();
		}
	}

	private void LogStaleD3DFrameDropThrottled(long ageMs, long staleDropped)
	{
		long tickCount = Environment.TickCount64;
		long previous = Interlocked.Read(ref _lastStaleD3DFrameDropLogTick);
		if (tickCount - previous < 1000)
		{
			return;
		}
		Interlocked.Exchange(ref _lastStaleD3DFrameDropLogTick, tickCount);
		AppLog.Write(
			$"Dropped stale D3D frame: age={ageMs}ms, staleDropped={staleDropped:N0}, " +
			$"decoderQueue={ActiveQueuedInputPackets} packets/{(double)ActiveQueuedInputBytes / 1024.0:N1}KB.");
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
		bool audioFirewallWarning;
		lock (_audioGate)
		{
			decoder = _audioDecoder;
			output = _audioOutput;
			audioFirewallWarning = _audioFirewallWarningActive;
		}

		string firewallHint = audioFirewallWarning
			? " (no audio RTP after SETUP; allow iMirror in Windows Firewall for Private and Public networks)"
			: string.Empty;

		if (decoder == null || output == null)
		{
			return $"audio: {_audioStatus}{firewallHint}, received={Interlocked.Read(ref _audioFramesReceived):N0}";
		}

		return $"audio: {_audioStatus}{firewallHint}, received/queued={Interlocked.Read(ref _audioFramesReceived):N0}/{Interlocked.Read(ref _audioFramesQueued):N0}, " +
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

		int target = checked((int)Math.Clamp(latestVideoLatencyMs, 0L, 1000L)) + Volatile.Read(ref _audioSyncOffsetMilliseconds);
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
