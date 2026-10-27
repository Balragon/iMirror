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
using System.Windows.Shell;
using System.Windows.Threading;
using MacMirrorReceiver.Audio;
using MacMirrorReceiver.Models;
using MacMirrorReceiver.Networking;
using MacMirrorReceiver.Protocol;
using MacMirrorReceiver.Video;
using Wpf.Ui.Controls;

namespace MacMirrorReceiver;

public partial class MainWindow : FluentWindow, ISettingsHost, IUpdateNoticeView, IVideoEngineView
{
	private enum RenderMode
	{
		Auto = 0,
		Quality = 3
	}

	private const int RealtimeMaxRenderWidth = 3840;

	private const int ResponsiveMaxRenderWidth = 1920;

	private const int QualityMaxRenderWidth = 2560;

	private const int RealtimeMaxRenderFps = 60;

	private const int HighResolutionMaxRenderFps = 30;

	private const long HighResolutionMaxLiveFrameAgeMilliseconds = 200;

	private const long SoftwareMaxLiveFrameAgeMilliseconds = 240;

	private const long SoftwareMaxPresentGapMilliseconds = 250;

	private const int MaxAutoReconnectAttempts = 5;

	// Keep the initial SPS/PPS/IDR burst while the decoder/render sink spins up.
	// Small AirPlay slice packets can exceed single digits before WPF/FFmpeg/MF is ready;
	// the byte cap below remains the hard memory guard.
	private const int MaxPendingVideoPacketsBeforeSink = 512;

	private const long MaxPendingVideoBytesBeforeSink = 8L * 1024L * 1024L;

	private const int MinAudioSyncTargetMilliseconds = 120;

	private const int MaxAudioSyncTargetMilliseconds = 220;

	private static readonly TimeSpan AutoReconnectDelay = TimeSpan.FromSeconds(2.0);

	private static readonly ReceiverSettingsSnapshot StartupReceiverSettings = ReceiverSettings.Load();

	// Audio sync offset applies live: the Settings slider writes this and the audio
	// thread reads it through ResolveAudioSyncTargetLatencyMilliseconds. Volatile so
	// the UI-thread write is visible to the audio pipeline without a reconnect.
	private int _audioSyncOffsetMilliseconds = StartupReceiverSettings.Effective.AudioSyncOffsetMs;

	private static readonly RenderModeSettingsSnapshot StartupRenderModeSettings = RenderModeSettings.Load();

	private static readonly bool QualityRenderModeEnabled = StartupRenderModeSettings.EffectiveMode == ReceiverRenderModeSetting.Quality;

	private static readonly bool GpuQualityRequested = RenderModeSettings.ExperimentalQualityEnabled
		|| RenderModeSettings.GpuVideoEngineEnabled;

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

	private readonly LatestFrameMailbox _frameMailbox;
	private readonly ThrottledLogGate _renderLogGate = new ThrottledLogGate(TimeSpan.FromSeconds(1));
	private readonly ThrottledLogGate _videoHealthLogGate = new ThrottledLogGate(TimeSpan.FromSeconds(10));
	private readonly ThrottledLogGate _staleD3DDropLogGate = new ThrottledLogGate(TimeSpan.FromSeconds(1));
	private readonly ThrottledLogGate _staleSoftwareDropLogGate = new ThrottledLogGate(TimeSpan.FromSeconds(1));
	private readonly ThrottledLogGate _gateDecisionLogGate = new ThrottledLogGate(TimeSpan.FromSeconds(1));
	private readonly ThrottledLogGate _pendingVideoDropLogGate = new ThrottledLogGate(TimeSpan.FromSeconds(1));
	private readonly ThrottledLogGate _diagnosticsUpdateGate = new ThrottledLogGate(TimeSpan.FromMilliseconds(250));


	private readonly PendingVideoBuffer _pendingVideoBeforeSink = new PendingVideoBuffer(MaxPendingVideoPacketsBeforeSink, MaxPendingVideoBytesBeforeSink);

	private readonly AudioSessionHost _audioSession;

	private readonly VideoEngineHost _videoEngine;

	// 3s warmup grace: BeginWarmup fires at decoder (re)start, but frames only flow after FFmpeg/MF
	// spin-up (~0.8s) plus decryptor settling, so a shorter grace lets a startup straggler inflate
	// the first window's max. Measured on a real-device run (first-window straggler ~248ms at ~1.6s).
	private readonly LatencyWindow _receiveToPresentLatencyWindow = new LatencyWindow(TimeSpan.FromSeconds(10.0), TimeSpan.FromSeconds(3.0));

	private MirrorClient? _client;

	private WriteableBitmap? _bitmap;

	private long _staleD3DFramesDropped;

	private StreamConfig? _streamConfig;

	private long _videoPackets;

	private long _videoBytes;


	private long _decodedFrames;

	private long _renderedFrames;

	private long _renderDroppedFrames;

	private long _staleSoftwareFramesDropped;

	// UI thread only.
	private long _lastSoftwarePresentTick;

	private long _latestReceiveToRenderMs;

	private long _latestDecodeToRenderMs;

	private long _lastVideoHealthPackets;

	private long _lastVideoHealthDecodedFrames;

	private long _lastVideoHealthRenderedFrames;

	private int _diagnosticsQueued;

	private int _autoRenderResizeVersion;

	private int _connectionGeneration;

	private int _airPlaySessionGeneration;

	private readonly SemaphoreSlim _disconnectGate = new SemaphoreSlim(1, 1);

	private int _autoReconnectAttempts;

	private IPEndPoint? _lastEndpoint;

	private string? _lastPin;

	private string _statusText = "Waiting for iPhone or iPad...";

	private bool _isFullscreen;

	private bool _isConnecting;

	private bool _manualDisconnectRequested = true;

	private SettingsWindow? _settingsWindow;

	private readonly UpdateController _updateController;

	private TrayIconHost? _trayIconHost;

	private readonly NativeWindowFrame _nativeFrame = new NativeWindowFrame();

	private bool? _appliedLightThemeIcon;

	private bool _shutdownStarted;

	private WindowStyle _previousWindowStyle;

	private ResizeMode _previousResizeMode;

	private WindowState _previousWindowState;

	private bool _previousTopmost;

	private bool _previousExtendsContentIntoTitleBar;

	private WindowChrome? _previousWindowChrome;

	private Rect _previousWindowBounds;

	private Stretch _previousVideoStretch = Stretch.Uniform;

	public MainWindow()
	{
		AppLog.Write("MainWindow constructor entered.");
		InitializeComponent();
		AppLog.Write("MainWindow InitializeComponent returned.");
		_updateController = new UpdateController(
			new UpdateService(),
			this,
			ShutdownApplicationAsync,
			() => _shutdownStarted);
		_frameMailbox = new LatestFrameMailbox(
			() => base.Dispatcher.BeginInvoke(new Action(DrainLatestFrame), DispatcherPriority.Render));
		_videoEngine = new VideoEngineHost(
			this,
			new DefaultVideoDecoderFactory(),
			ShouldUseHighResolutionD3DPath,
			config => Math.Min(ResolveMaxRenderWidth(config), ResponsiveMaxRenderWidth),
			ResolveOutputFps,
			HighResolutionMaxRenderFps,
			RealtimeMaxRenderWidth,
			StartupReceiverSettings.Effective.DumpH264);
		_audioSession = new AudioSessionHost(
			() => Volatile.Read(ref _connectionGeneration),
			ResolveAudioSyncTargetLatencyMilliseconds);
		_audioSession.DiagnosticsChanged += QueueDiagnosticsUpdate;
		_audioSession.StatusReported += HandleAudioStatusReported;
		_audioSession.FirewallWarningRaised += HandleAudioFirewallWarningRaised;
		_audioSession.FirewallWarningCleared += HandleAudioFirewallWarningCleared;
		VideoStage.SizeChanged += VideoStage_SizeChanged;
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
			_audioSession.HandleStreamStarted(sampleRate, channels, samplesPerFrame, Volatile.Read(ref _airPlaySessionGeneration));
		_airPlayProbe.AudioFrameReceived += (frame, rtpTimestamp, sequence) =>
			_audioSession.HandleFrame(frame, rtpTimestamp, sequence, Volatile.Read(ref _airPlaySessionGeneration));
		InitializeTrayIcon();
		ApplyThemedAppIcon();
		// Fires on the UI thread when the user changes the Windows theme (Settings >
		// Personalization > Colors), so the window and tray icons follow it live.
		Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
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
		// async void: an unhandled exception here goes to DispatcherUnhandledException, which
		// only logs it, so the process would exit at startup. Each step is isolated so a
		// failing discovery/listener/preflight step degrades the UI instead of killing the app.
		AppLog.Write("MainWindow Loaded entered.");
		await RunStartupStepAsync("mDNS browser start", () => _browser.StartAsync());
		await RunStartupStepAsync("AirPlay probe start", () => _airPlayProbe.StartAsync());
		PreflightReport? report = null;
		await RunStartupStepAsync("startup preflight", async () => report = await StartupDiagnostics.RunAsync(_airPlayProbe));
		if (report != null)
		{
			LogPreflight("Preflight", report);
			BindReadinessStrip(report);
		}
		else
		{
			SetStatus("Startup checks could not run; see the log for details.");
		}
		_ = _updateController.CheckForUpdatesOnStartupAsync();
	}

	private static void LogPreflight(string label, PreflightReport report)
	{
		AppLog.Write($"{label}: {report.Worst} - " +
			string.Join("; ", System.Linq.Enumerable.Select(report.Checks, check => $"{check.Id}={check.Status}")));
	}

	private static async Task RunStartupStepAsync(string label, Func<Task> step)
	{
		try
		{
			await step();
		}
		catch (Exception ex)
		{
			AppLog.Write($"Startup step '{label}' failed: {ex}");
		}
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
			text = "FFmpeg not found - video decode unavailable";
		}
		else if (listenerBlocked)
		{
			brush = (SolidColorBrush)FindResource("WarningBrush");
			text = "Firewall may be blocking AirPlay - check Windows Firewall";
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
			LogPreflight("Preflight re-check", report);
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
				_audioSession.MarkFirewallAllowRuleKnown();
				_audioSession.ClearFirewallWarning(
					"Windows Firewall allow rule completed; clearing audio firewall warning.",
					"waiting for audio after firewall allow");
				SetStatus("Windows Firewall now allows iMirror. Reconnect only if audio does not resume.");
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
		_trayIconHost = new TrayIconHost(
			RestoreFromTray,
			() =>
			{
				RestoreFromTray();
				ShowSettingsWindow();
			},
			ShutdownApplicationAsync);
	}

	private void SystemEvents_UserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
	{
		if (e.Category is Microsoft.Win32.UserPreferenceCategory.General
			or Microsoft.Win32.UserPreferenceCategory.VisualStyle
			or Microsoft.Win32.UserPreferenceCategory.Color)
		{
			ApplyThemedAppIcon();
		}
	}

	/// <summary>
	/// Sets the window and tray icons to the variant matching the Windows system theme.
	/// The executable and installer icons stay dark; only these two runtime surfaces switch.
	/// </summary>
	private void ApplyThemedAppIcon()
	{
		if (_shutdownStarted)
		{
			return;
		}

		bool light = AppIconTheme.IsSystemLightTheme();
		if (_appliedLightThemeIcon == light)
		{
			return;
		}

		Uri iconUri = AppIconTheme.IconUriFor(light);
		try
		{
			base.Icon = BitmapFrame.Create(iconUri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
		}
		catch (Exception ex)
		{
			AppLog.Write($"Window icon update failed ({iconUri}): {ex.Message}");
		}

		_trayIconHost?.ApplyIcon(iconUri);

		_appliedLightThemeIcon = light;
		AppLog.Write("App icon set for " + (light ? "light" : "dark") + " Windows theme.");
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
		_updateController.CancelPendingOperations();
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
			CleanupGuards.RunStep("theme watcher unsubscribe", () => Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged);
			CleanupGuards.RunStep("tray icon dispose", DisposeTrayIcon);
			AppLog.Flush();
			Application.Current.Shutdown();
		}
	}

	private void DisposeTrayIcon()
	{
		TrayIconHost? host = _trayIconHost;
		_trayIconHost = null;
		host?.Dispose();
	}

	ReceiverSettingsSnapshot ISettingsHost.StartupReceiverSettings => StartupReceiverSettings;

	RenderModeSettingsSnapshot ISettingsHost.StartupRenderModeSettings => StartupRenderModeSettings;

	bool ISettingsHost.GpuQualityRequested => GpuQualityRequested;

	bool ISettingsHost.QualityPathAvailable => QualityPathAvailable;

	int ISettingsHost.LiveAudioSyncOffsetMilliseconds => Volatile.Read(ref _audioSyncOffsetMilliseconds);

	void ISettingsHost.SetLiveAudioSyncOffsetMilliseconds(int value)
	{
		Volatile.Write(ref _audioSyncOffsetMilliseconds, ReceiverSettings.ClampAudioOffset(value));
		_audioSession.ApplySyncTargetLatency();
	}

	Task ISettingsHost.RestartApplicationAsync()
	{
		return RestartApplicationAsync();
	}

	Task<UpdateCheckResult> ISettingsHost.CheckForUpdatesAsync(bool manual)
	{
		return _updateController.CheckForUpdatesAsync(manual);
	}

	Task<bool> ISettingsHost.InstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress)
	{
		return _updateController.InstallUpdateAsync(updateInfo, progress);
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
		if (CurrentRenderMode != RenderMode.Auto || _streamConfig == null || _videoEngine.SoftwareDecoder == null)
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

	private void VideoStage_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		UpdateHighResolutionD3DPresenterLayout();
	}

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

		if (Volatile.Read(ref _shutdownStarted))
		{
			// During shutdown the UI thread is blocked inside _airPlayProbe.Dispose() (which
			// waits up to 1 s for the network tasks that raise this event), so a blocking
			// Invoke could never be pumped and only burned the whole wait. The session state
			// is about to be torn down anyway.
			AppLog.Write("AirPlay mirror session ended during shutdown; skipping UI state reset.");
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
		_videoEngine.Stop();
		CleanupGuards.RunStep("pending D3D frame release", ReleasePendingD3DFrame);
		CleanupGuards.RunStep("pending frame release", ReleasePendingFrame);
		CleanupGuards.RunStep("audio pipeline stop", _audioSession.Stop);
		_bitmap = null;
		VideoImage.Source = null;
		VideoImage.Visibility = Visibility.Visible;
		ShowEmptyStateSurface();
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
				&& _videoEngine.HasDecoder
				&& _streamConfig.Width == config.Width
				&& _streamConfig.Height == config.Height
				&& _streamConfig.Fps == config.Fps
				&& string.Equals(_streamConfig.Codec, config.Codec, StringComparison.OrdinalIgnoreCase);
			if (sameStreamConfig)
			{
				if (_videoEngine.GpuDecoder != null)
				{
					AppLog.Write("AirPlay stream config repeated for high-resolution D3D path; keeping active renderer.");
					UpdateHighResolutionD3DPresenterLayout();
					UpdateDiagnostics();
					return;
				}
				else
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

	internal static bool IsCurrentGeneration(int payloadGeneration, int currentGeneration)
	{
		return AudioSessionHost.IsCurrentGeneration(payloadGeneration, currentGeneration);
	}

	private void HandleAudioStatusReported(string message)
	{
		base.Dispatcher.BeginInvoke(new Action(delegate
		{
			AppLog.Write("Audio status: " + message);
			UpdateDiagnostics();
		}), DispatcherPriority.Background);
	}

	private void HandleAudioFirewallWarningRaised()
	{
		_ = base.Dispatcher.BeginInvoke(new Action(delegate
		{
			SetStatus(AudioSessionHost.FirewallWarningMessage);
			UpdateDiagnostics();
		}), DispatcherPriority.Background);
	}

	private void HandleAudioFirewallWarningCleared()
	{
		base.Dispatcher.BeginInvoke(new Action(delegate
		{
			UpdateReceiverChrome();
			UpdateDiagnostics();
		}), DispatcherPriority.Background);
	}

	private async Task ConnectToSelectedAsync()
	{
		try
		{
			IPEndPoint endpoint = await ResolveEndpointAsync();
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
			_videoEngine.ResetForNewConnection();
				_videoPackets = 0L;
				_videoBytes = 0L;
			_decodedFrames = 0L;
			_renderedFrames = 0L;
			_renderDroppedFrames = 0L;
			_staleSoftwareFramesDropped = 0L;
			_staleSoftwareDropLogGate.Reset();
			_lastSoftwarePresentTick = 0L;
			_latestReceiveToRenderMs = 0L;
			_latestDecodeToRenderMs = 0L;
			_receiveToPresentLatencyWindow.Reset();
			_renderLogGate.Reset();
			_videoHealthLogGate.Reset();
			_lastVideoHealthPackets = 0L;
			_lastVideoHealthDecodedFrames = 0L;
			_lastVideoHealthRenderedFrames = 0L;
			_gateDecisionLogGate.Reset();
			_pendingVideoBeforeSink.ResetDroppedCount();
			_pendingVideoDropLogGate.Reset();
			_staleD3DFramesDropped = 0L;
			_staleD3DDropLogGate.Reset();
			_audioSession.ResetForNewConnection();
			_streamConfig = null;
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
				_videoEngine.StopWatchdog();
				AppLog.Write($"First video packet received: {payload.Length} bytes.");
			}
		}

		if (allowPreSinkBuffer && !HasVideoSinkReady())
		{
			BufferVideoUntilSinkReady(payload, sourceTimestampNanos, receivedTick);
			QueueDiagnosticsUpdate();
			return;
		}

		ISoftwareVideoDecoder? decoder = _videoEngine.SoftwareDecoder;
		IHighResolutionD3DDecoder? mediaFoundationD3DDecoder = _videoEngine.GpuDecoder;
		byte[]? array = ProcessH264Payload(payload, out long h264ForwardedPackets, out string h264LastDecision);
		if (mediaFoundationD3DDecoder != null && array != null)
		{
			if (h264ForwardedPackets == 1)
			{
				AppLog.Write("First H264 payload forwarded to Media Foundation D3D11 decoder: " + h264LastDecision);
			}
			if (!mediaFoundationD3DDecoder.QueueH264(array, sourceTimestampNanos, receivedTick))
			{
				_videoEngine.Status = "Media Foundation D3D11 decoder input unavailable; waiting for decoder";
				AppLog.Write("Media Foundation D3D11 decoder input unavailable; packet was not queued.");
			}
		}
		else if (decoder != null && array != null)
		{
			if (h264ForwardedPackets == 1)
			{
				AppLog.Write("First H264 payload forwarded to decoder: " + h264LastDecision);
			}
				if (!decoder.QueueH264(array, sourceTimestampNanos, receivedTick))
				{
					_videoEngine.Status = "decoder input unavailable; waiting for decoder";
					AppLog.Write("Decoder input unavailable; packet was not queued.");
				}
			}
		else if (decoder != null)
		{
			_videoEngine.Status = h264LastDecision;
			LogGateDecisionThrottled();
		}
		else if (mediaFoundationD3DDecoder != null)
		{
			_videoEngine.Status = h264LastDecision;
			LogGateDecisionThrottled();
		}
		if (countReceived)
		{
			LogVideoHealthThrottled();
		}
		QueueDiagnosticsUpdate();
	}

	private bool HasVideoSinkReady()
	{
		return _videoEngine.SinkAcceptingInput && _videoEngine.HasDecoder;
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
		_pendingVideoBeforeSink.Enqueue(new PendingVideoPayload(payload, sourceTimestampNanos, receivedTick));
		LogPendingVideoDropsThrottled();
	}

	private void FlushPendingVideoToSink()
	{
		PendingVideoPayload[] pending = _pendingVideoBeforeSink.Drain();
		if (pending.Length == 0)
		{
			return;
		}

		AppLog.Write($"Flushing {pending.Length:N0} pending video packets to renderer.");
		foreach (PendingVideoPayload packet in pending)
		{
			ProcessVideoPayload(packet.Payload, packet.SourceTimestampNanos, packet.ReceivedTick, countReceived: false, allowPreSinkBuffer: false);
		}
	}

	private void ClearPendingVideoBeforeSink()
	{
		_pendingVideoBeforeSink.Clear();
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
			return;
		}

		if (e.Key == Key.F11)
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
			_previousResizeMode = base.ResizeMode;
			_previousWindowState = base.WindowState;
			_previousTopmost = base.Topmost;
			_previousExtendsContentIntoTitleBar = ExtendsContentIntoTitleBar;
			_previousWindowChrome = WindowChrome.GetWindowChrome(this);
			_isFullscreen = true;
			EnterFullscreenChrome();
			FullscreenButton.Content = "Windowed";
			CompactFullscreenButton.Content = "W";
			CompactFullscreenButton.ToolTip = "Exit fullscreen";
		}
		else
		{
			_isFullscreen = false;
			ExitFullscreenChrome();
			FullscreenButton.Content = "Fullscreen";
			CompactFullscreenButton.Content = "F";
			CompactFullscreenButton.ToolTip = "Fullscreen";
		}
		base.Dispatcher.BeginInvoke(new Action(delegate
		{
			RestartDecoderIfRenderWidthChanged("display mode changed");
		}), DispatcherPriority.Background);
	}

	private void EnterFullscreenChrome()
	{
		IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
		_nativeFrame.CaptureStyles(hwnd);
		_previousWindowBounds = new Rect(Left, Top, Width, Height);
		_previousVideoStretch = VideoImage.Stretch;

		MainTitleBar.Visibility = Visibility.Collapsed;
		ExtendsContentIntoTitleBar = false;
		base.WindowState = WindowState.Normal;
		base.WindowStyle = WindowStyle.None;
		base.ResizeMode = ResizeMode.NoResize;
		VideoImage.Stretch = Stretch.UniformToFill;
		WindowChrome.SetWindowChrome(this, new WindowChrome
		{
			CaptionHeight = 0,
			ResizeBorderThickness = new Thickness(0),
			GlassFrameThickness = new Thickness(0),
			CornerRadius = new CornerRadius(0),
			UseAeroCaptionButtons = false
		});
		base.Topmost = true;
		_nativeFrame.ApplyFullscreenFrame(hwnd);
		NativeWindowFrame.MoveToCurrentMonitor(hwnd);
		UpdateHighResolutionD3DPresenterLayout();
		ScheduleFullscreenChromeEnforcement(hwnd);
	}

	private void ExitFullscreenChrome()
	{
		IntPtr hwnd = new WindowInteropHelper(this).EnsureHandle();
		base.WindowState = WindowState.Normal;
		base.Topmost = _previousTopmost;
		VideoImage.Stretch = _previousVideoStretch;
		ExtendsContentIntoTitleBar = _previousExtendsContentIntoTitleBar;
		WindowChrome.SetWindowChrome(this, _previousWindowChrome);
		base.WindowStyle = _previousWindowStyle;
		base.ResizeMode = _previousResizeMode;
		_nativeFrame.RestoreStyles(hwnd);
		if (_previousWindowState == WindowState.Normal && _previousWindowBounds.Width > 0.0 && _previousWindowBounds.Height > 0.0)
		{
			Left = _previousWindowBounds.Left;
			Top = _previousWindowBounds.Top;
			Width = _previousWindowBounds.Width;
			Height = _previousWindowBounds.Height;
		}
		base.WindowState = _previousWindowState;
		MainTitleBar.Visibility = Visibility.Visible;
		if (!_previousTopmost)
		{
			NativeWindowFrame.ClearTopmost(hwnd);
		}
		UpdateHighResolutionD3DPresenterLayout();
	}

	private void ScheduleFullscreenChromeEnforcement(IntPtr hwnd)
	{
		base.Dispatcher.BeginInvoke(new Action(delegate
		{
			if (!_isFullscreen)
			{
				return;
			}

			MainTitleBar.Visibility = Visibility.Collapsed;
			ExtendsContentIntoTitleBar = false;
			base.WindowState = WindowState.Normal;
			base.WindowStyle = WindowStyle.None;
			base.ResizeMode = ResizeMode.NoResize;
			VideoImage.Stretch = Stretch.UniformToFill;
			_nativeFrame.ApplyFullscreenFrame(hwnd);
			NativeWindowFrame.MoveToCurrentMonitor(hwnd);
			UpdateHighResolutionD3DPresenterLayout();
		}), DispatcherPriority.ApplicationIdle);
	}


	private async Task<IPEndPoint> ResolveEndpointAsync()
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
		// Async DNS: the synchronous lookup froze the window for the resolver timeout when a
		// typed host name did not resolve.
		IPAddress[] array = (from address in await Dns.GetHostAddressesAsync(text)
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

	void IUpdateNoticeView.ShowUpdateAvailable(UpdateInfo update)
	{
		UpdateNoticeTextBlock.Text = $"iMirror {update.LatestVersion} is available.";
		UpdateNoticeInstallButton.Content = "Update";
		UpdateNoticeInstallButton.IsEnabled = true;
		UpdateNoticeDismissButton.IsEnabled = true;
		UpdateNoticeBorder.Visibility = Visibility.Visible;
	}

	void IUpdateNoticeView.ShowDownloadStarted(UpdateInfo update)
	{
		UpdateNoticeBorder.Visibility = Visibility.Visible;
		UpdateNoticeInstallButton.IsEnabled = false;
		UpdateNoticeDismissButton.IsEnabled = false;
		UpdateNoticeTextBlock.Text = $"Downloading iMirror {update.LatestVersion}...";
	}

	void IUpdateNoticeView.ShowInstallerStarting()
	{
		UpdateNoticeTextBlock.Text = "Starting update installer...";
	}

	void IUpdateNoticeView.ShowInstallerHandoff()
	{
		UpdateNoticeTextBlock.Text = "Installing update. iMirror will close and restart automatically...";
	}

	void IUpdateNoticeView.ShowInstallFailed()
	{
		UpdateNoticeTextBlock.Text = "Update could not be installed. Open Releases and install manually.";
		UpdateNoticeInstallButton.Content = "Retry";
		UpdateNoticeInstallButton.IsEnabled = true;
		UpdateNoticeDismissButton.IsEnabled = true;
	}

	void IUpdateNoticeView.HideNotice()
	{
		UpdateNoticeBorder.Visibility = Visibility.Collapsed;
	}

	void IUpdateNoticeView.SetStatus(string message)
	{
		SetStatus(message);
	}

	private async void UpdateNoticeInstallButton_Click(object sender, RoutedEventArgs e)
	{
		UpdateInfo? update = _updateController.PendingUpdate;
		if (update == null)
		{
			return;
		}

		var progress = new Progress<double>(value =>
		{
			int percent = Math.Clamp((int)Math.Round(value * 100.0), 0, 100);
			UpdateNoticeTextBlock.Text = $"Downloading iMirror {update.LatestVersion}... {percent}%";
		});
		await _updateController.InstallUpdateAsync(update, progress);
	}

	private void UpdateNoticeDismissButton_Click(object sender, RoutedEventArgs e)
	{
		_updateController.DismissUpdateNotice();
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
		case RenderMode.Quality:
			return Math.Min(sourceWidth, QualityMaxRenderWidth);
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
		if (_streamConfig == null || _videoEngine.SoftwareDecoder == null)
		{
			return;
		}
		int newMaxWidth = ResolveMaxRenderWidth(_streamConfig);
		if (newMaxWidth == _videoEngine.MaxRenderWidth)
		{
			return;
		}
		_videoEngine.CountRestart();
		RequireH264Keyframe();
		_videoEngine.StartFresh(_streamConfig, reason);
		UpdateDiagnostics();
	}

	private void StartDecoder(StreamConfig config)
	{
		try
		{
			_autoReconnectAttempts = 0;
			_streamConfig = config;
			AppLog.Write($"Stream config received: {config.Width}x{config.Height} @ {config.Fps} codec={config.Codec}.");
			ResizeWindowToStream(config);
			_videoEngine.StartFresh(config, "stream config");
			SetStatus($"Mirroring {config.Width}x{config.Height} @ {config.Fps} fps.");
			UpdateDiagnostics();
		}
		catch (Exception ex)
		{
			_videoEngine.Status = "failed: " + ex.Message;
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

	private void UpdateHighResolutionD3DPresenterLayout()
	{
		IHighResolutionD3DPresenter? presenter = _videoEngine.GpuPresenter;
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
		bool fillStage = _isFullscreen;
		double scale = fillStage
			? Math.Max(stageWidthPixels / config.Width, availableHeightPixels / config.Height)
			: Math.Min(stageWidthPixels / config.Width, availableHeightPixels / config.Height);
		double fitWidthPixels = Math.Max(1.0, Math.Round(config.Width * scale));
		double fitHeightPixels = Math.Max(1.0, Math.Round(config.Height * scale));
		double fitWidthDip = fitWidthPixels / dpiScaleX;
		double fitHeightDip = fitHeightPixels / dpiScaleY;
		if (!fillStage)
		{
			fitWidthDip = Math.Min(stageWidthDip, fitWidthDip);
			fitHeightDip = Math.Min(availableHeightDip, fitHeightDip);
		}

		FrameworkElement presenterView = presenter.View;
		presenterView.Width = fitWidthDip;
		presenterView.Height = fitHeightDip;
		presenterView.Margin = fillStage ? new Thickness(0.0) : new Thickness(0.0, topInsetDip, 0.0, 0.0);
		presenterView.HorizontalAlignment = HorizontalAlignment.Center;
		presenterView.VerticalAlignment = !fillStage && topInsetDip > 0.0 ? VerticalAlignment.Top : VerticalAlignment.Center;
	}

	private double ResolveHighResolutionD3DTopInsetDip(double stageHeightDip)
	{
		if (!_audioSession.FirewallWarningActive || ReceiverCardBorder.Visibility != Visibility.Visible)
		{
			return 0.0;
		}

		double cardHeightDip = ReceiverCardBorder.ActualHeight > 0.0 ? ReceiverCardBorder.ActualHeight : 44.0;
		double desiredInsetDip = ReceiverCardBorder.Margin.Top + cardHeightDip + 12.0;
		return Math.Min(Math.Max(0.0, desiredInsetDip), Math.Max(0.0, stageHeightDip - 1.0));
	}

	private void PresentFrame(VideoFrame frame)
	{
		long renderStartTick = Stopwatch.GetTimestamp();
		try
		{
			if (frame.ReceivedTick > 0)
			{
				long ageMs = ElapsedMilliseconds(frame.ReceivedTick, renderStartTick);
				// The pending frame is always the newest one, so dropping it never shows anything
				// fresher; under sustained latency every frame would be dropped and the screen would
				// freeze. Present a stale frame anyway once the screen has gone too long without one.
				long sinceLastPresentMs = _lastSoftwarePresentTick == 0
					? 0
					: ElapsedMilliseconds(_lastSoftwarePresentTick, renderStartTick);
				if (ageMs > SoftwareMaxLiveFrameAgeMilliseconds && sinceLastPresentMs < SoftwareMaxPresentGapMilliseconds)
				{
					Interlocked.Increment(ref _renderDroppedFrames);
					long staleDropped = Interlocked.Increment(ref _staleSoftwareFramesDropped);
					LogStaleSoftwareFrameDropThrottled(ageMs, staleDropped);
					QueueDiagnosticsUpdate();
					return;
				}
			}

			PresentFrameToActiveRenderer(frame);
			long renderDoneTick = Stopwatch.GetTimestamp();
			_lastSoftwarePresentTick = renderDoneTick;
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








	private void QueueFrameForPresentation(VideoFrame frame)
	{
		Interlocked.Increment(ref _decodedFrames);
		if (_frameMailbox.Offer(frame))
		{
			Interlocked.Increment(ref _renderDroppedFrames);
		}
	}

	private void QueueD3DFrameForPresentation(IHighResolutionD3DFrame frame)
	{
		Interlocked.Increment(ref _decodedFrames);
		if (_frameMailbox.Offer(frame))
		{
			Interlocked.Increment(ref _renderDroppedFrames);
		}
	}

	private void ReleasePendingFrame()
	{
		_frameMailbox.ReleasePendingSoftwareFrame();
	}

	private void ReleasePendingD3DFrame()
	{
		_frameMailbox.ReleasePendingD3DFrame();
	}

	private void DrainLatestFrame()
	{
		_frameMailbox.Drain(PresentFrame, PresentD3DFrame);
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
		if (!_renderLogGate.TryPass())
		{
			return;
		}
		AppLog.Write(
			$"Render stats: received={Interlocked.Read(ref _videoPackets):N0}, decoded={Interlocked.Read(ref _decodedFrames):N0}, rendered={renderedFrames:N0}, " +
			$"renderDropped={Interlocked.Read(ref _renderDroppedFrames):N0}, receive->render={Interlocked.Read(ref _latestReceiveToRenderMs)}ms, " +
			$"decode->render={Interlocked.Read(ref _latestDecodeToRenderMs)}ms, render={ElapsedMilliseconds(renderStartTick, renderDoneTick)}ms, " +
			$"receive->present window={FormatLatencyWindow(_receiveToPresentLatencyWindow.GetCurrentOrLastSnapshot(Stopwatch.GetTimestamp()))}, " +
			$"pendingRender={GetPendingRenderFrameCount()}, dispatcherQueued={_frameMailbox.DrainQueued}, " +
			$"decoderQueue={ActiveQueuedInputPackets} packets/{(double)ActiveQueuedInputBytes / 1024.0:N1}KB, " +
			$"ffmpegPipeline={ActiveFfmpegPipelineDepth} frames, " +
			$"h264 accepted/written/dropped={ActiveAcceptedInputPackets:N0}/{ActiveWrittenInputPackets:N0}/{ActiveDroppedInputPackets:N0}, " +
			$"stdinWrite={ActiveLatestWriteMilliseconds}ms max={ActiveMaxWriteMilliseconds}ms stalls={ActiveWriteStalls:N0}");
	}

	private void LogVideoHealthThrottled()
	{
		if (!_videoHealthLogGate.TryPass())
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

			_videoEngine.Stop();
			CleanupGuards.RunStep("audio pipeline stop", _audioSession.Stop);
			_bitmap = null;
			VideoImage.Source = null;
			VideoImage.Visibility = Visibility.Visible;
			ShowEmptyStateSurface();
			RestoreDefaultWindowSize();
			CleanupGuards.RunStep("pending frame release", ReleasePendingFrame);
			CleanupGuards.RunStep("pending D3D frame release", ReleasePendingD3DFrame);
			_streamConfig = null;
			_videoEngine.Status = "not started";
			ResetH264Gate();
			SetConnectedUi(connected: false, finalStatus);
			UpdateDiagnostics();
		}
		finally
		{
			_disconnectGate.Release();
		}
	}

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
		CompactStatusTextBlock.Text = ReceiverChromeText.CompactStatus(message);
		CompactControlBar.ToolTip = message;
		UpdateReceiverChrome();
	}

	private void UpdateReceiverChrome()
	{
		bool connected = IsMirroringActive();
		int deviceCount = _devices.Count;
		bool audioFirewallWarning = _audioSession.FirewallWarningActive;
		var state = new ReceiverChromeState(connected, _isConnecting, deviceCount, audioFirewallWarning);

		HeaderSubtitleTextBlock.Text = ReceiverChromeText.Subtitle(state);
		DeviceSummaryTextBlock.Text = ReceiverChromeText.DeviceSummary(deviceCount);
		ReceiverCardTitleTextBlock.Text = ReceiverChromeText.CardTitle(state);
		ReceiverStateBadgeTextBlock.Text = ReceiverChromeText.BadgeText(state);
		ReceiverStatusTextBlock.Text = _statusText;
		EmptyStateStatusTextBlock.Text = _statusText;
		EmptyStateTextBlock.Text = ReceiverChromeText.EmptyStateTitle(state);
		EmptyStateDetailTextBlock.Text = ReceiverChromeText.EmptyStateDetail(state);
		HowToMirrorCard.Visibility = connected || _isConnecting ? Visibility.Collapsed : Visibility.Visible;
		StatusSummaryPanel.Visibility = connected || _isConnecting ? Visibility.Collapsed : Visibility.Visible;
		// While mirroring, keep the stage clean unless there is an actionable warning.
		ReceiverCardBorder.Visibility = connected && !audioFirewallWarning ? Visibility.Collapsed : Visibility.Visible;
		FirewallAllowButton.Visibility = audioFirewallWarning ? Visibility.Visible : Visibility.Collapsed;

		Brush statusBrush = ResolveReceiverStatusBrush(ReceiverChromeText.Tone(state));
		SidebarStatusDot.Fill = statusBrush;
		EmptyStateStatusDot.Fill = statusBrush;
		ReceiverStateBadge.Background = statusBrush;
		UpdateHighResolutionD3DPresenterLayout();
	}

	private bool IsMirroringActive()
	{
		return _client != null ||
			Volatile.Read(ref _airPlaySessionGeneration) != 0 ||
			_streamConfig != null ||
			_videoEngine.HasDecoder;
	}

	private Brush ResolveReceiverStatusBrush(ReceiverStatusTone tone)
	{
		return tone switch
		{
			ReceiverStatusTone.Warning => (Brush)FindResource("WarningBrush"),
			ReceiverStatusTone.Live => (Brush)FindResource("SuccessBrush"),
			ReceiverStatusTone.Connecting => (Brush)FindResource("WarningBrush"),
			ReceiverStatusTone.LegacySenderAvailable => new SolidColorBrush(Color.FromRgb(96, 205, 255)),
			_ => (Brush)FindResource("MutedTextBrush")
		};
	}

	private void UpdateDiagnostics()
	{
		string value = ((_streamConfig == null) ? "stream: none" : $"stream: {_streamConfig.Width}x{_streamConfig.Height} @ {_streamConfig.Fps} fps");
		string renderMode = CurrentRenderMode switch
		{
			RenderMode.Quality => "quality",
			_ => "auto"
		};
		string value2 = ((_videoEngine.OutputFps > 0) ? $"<= {_videoEngine.MaxRenderWidth}px @ {_videoEngine.OutputFps} fps ({renderMode})" : $"<= {RealtimeMaxRenderWidth}px @ up to {RealtimeMaxRenderFps} fps ({renderMode})");
		string outputValue = ResolveOutputDiagnostics();
			long value3 = Interlocked.Read(ref _videoPackets);
			long num = Interlocked.Read(ref _videoBytes);
			long decodedFrames = Interlocked.Read(ref _decodedFrames);
				long renderedFrames = Interlocked.Read(ref _renderedFrames);
				int pendingRenderFrames = GetPendingRenderFrameCount();
				int dispatcherQueued = _frameMailbox.DrainQueued;
				int pendingVideoBeforeSinkPackets = _pendingVideoBeforeSink.Count;
				long pendingVideoBeforeSinkBytes = _pendingVideoBeforeSink.Bytes;
					(long h264Forwarded, long h264Dropped) = GetH264GateCounts();
					string latencyWindow = FormatLatencyWindow(_receiveToPresentLatencyWindow.GetCurrentOrLastSnapshot(Stopwatch.GetTimestamp()));
					DiagnosticsTextBlock.Text = $"{value}\nrealtime output: {value2}\n{outputValue}\n{ResolveAudioDiagnostics()}\nreceived video: {value3:N0} / {(double)num / 1024.0:N1} KB\npre-render queue/dropped: {pendingVideoBeforeSinkPackets:N0} ({(double)pendingVideoBeforeSinkBytes / 1024.0:N1} KB) / {_pendingVideoBeforeSink.Dropped:N0}\nforwarded/dropped h264: {h264Forwarded:N0} / {h264Dropped:N0}\ndecoded/rendered: {decodedFrames:N0} / {renderedFrames:N0}\nrender dropped: {Interlocked.Read(ref _renderDroppedFrames):N0}\nlatest latency: recv->render {Interlocked.Read(ref _latestReceiveToRenderMs)} ms, decode->render {Interlocked.Read(ref _latestDecodeToRenderMs)} ms\nlatency window: {latencyWindow}\nrender queue: pending {pendingRenderFrames}, dispatcher {dispatcherQueued}\ndecoder input queued/dropped: {ActiveQueuedInputPackets:N0} ({(double)ActiveQueuedInputBytes / 1024.0:N1} KB) / {ActiveDroppedInputPackets:N0}\nh264 accepted/written: {ActiveAcceptedInputPackets:N0} / {ActiveWrittenInputPackets:N0}\nstdin write: {ActiveLatestWriteMilliseconds} ms, max {ActiveMaxWriteMilliseconds} ms, stalls {ActiveWriteStalls:N0}\ndecoder restarts: {_videoEngine.Restarts:N0}\ndecoder: {_videoEngine.Status}";
		CompactStatusTextBlock.Text = ((renderedFrames > 0) ? $"Live: {renderedFrames:N0} frames" : ReceiverChromeText.CompactStatus(_statusText));
	}

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

			IHighResolutionD3DPresenter? presenter = _videoEngine.GpuPresenter;
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
				_videoEngine.HandleGpuFatal("High-resolution D3D present failed: " + ex.Message);
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
		if (!_staleD3DDropLogGate.TryPass())
		{
			return;
		}
		AppLog.Write(
			$"Dropped stale D3D frame: age={ageMs}ms, staleDropped={staleDropped:N0}, " +
			$"decoderQueue={ActiveQueuedInputPackets} packets/{(double)ActiveQueuedInputBytes / 1024.0:N1}KB.");
	}

	private void LogStaleSoftwareFrameDropThrottled(long ageMs, long staleDropped)
	{
		if (!_staleSoftwareDropLogGate.TryPass())
		{
			return;
		}
		AppLog.Write(
			$"Dropped stale FFmpeg frame: age={ageMs}ms, staleDropped={staleDropped:N0}, " +
			$"decoderQueue={ActiveQueuedInputPackets} packets/{(double)ActiveQueuedInputBytes / 1024.0:N1}KB.");
	}

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
		IHighResolutionD3DDecoder? gpuDecoder = _videoEngine.GpuDecoder;
		if (gpuDecoder != null)
		{
			return $"output: Media Foundation D3D11 {gpuDecoder.OutputWidth}x{gpuDecoder.OutputHeight}, GPU NV12";
		}
		ISoftwareVideoDecoder? decoder = _videoEngine.SoftwareDecoder;
		return (decoder == null)
			? "output: none"
			: $"output: {decoder.OutputWidth}x{decoder.OutputHeight}, {(double)decoder.OutputFrameBytes / 1024.0 / 1024.0:N1} MB/frame";
	}

	internal string ResolveAudioDiagnostics()
	{
		return _audioSession.ResolveDiagnostics();
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
		_videoEngine.GpuDecoder?.QueuedInputPackets
		??
		_videoEngine.SoftwareDecoder?.QueuedInputPackets ?? 0;

	private long ActiveQueuedInputBytes =>
		_videoEngine.GpuDecoder?.QueuedInputBytes
		??
		_videoEngine.SoftwareDecoder?.QueuedInputBytes ?? 0L;

	private long ActiveDroppedInputPackets =>
		_videoEngine.GpuDecoder?.DroppedInputPackets
		??
		_videoEngine.SoftwareDecoder?.DroppedInputPackets ?? 0L;

	private long ActiveAcceptedInputPackets =>
		_videoEngine.GpuDecoder?.AcceptedInputPackets
		??
		_videoEngine.SoftwareDecoder?.AcceptedInputPackets ?? 0L;

	private long ActiveWrittenInputPackets =>
		_videoEngine.GpuDecoder?.WrittenInputPackets
		??
		_videoEngine.SoftwareDecoder?.WrittenInputPackets ?? 0L;

	private long ActiveLatestWriteMilliseconds =>
		_videoEngine.GpuDecoder?.LatestWriteMilliseconds
		??
		_videoEngine.SoftwareDecoder?.LatestWriteMilliseconds ?? 0L;

	private long ActiveMaxWriteMilliseconds =>
		_videoEngine.GpuDecoder?.MaxWriteMilliseconds
		??
		_videoEngine.SoftwareDecoder?.MaxWriteMilliseconds ?? 0L;

	private int ActiveFfmpegPipelineDepth => _videoEngine.SoftwareDecoder?.PipelineDepthFrames ?? 0;

	private long ActiveWriteStalls =>
		_videoEngine.GpuDecoder?.WriteStalls
		??
		_videoEngine.SoftwareDecoder?.WriteStalls ?? 0L;

	// IVideoEngineView: what VideoEngineHost needs from the window.

	void IVideoEngineView.Post(Action action, DispatcherPriority priority)
	{
		base.Dispatcher.BeginInvoke(action, priority);
	}

	int IVideoEngineView.ConnectionGeneration => Volatile.Read(ref _connectionGeneration);

	long IVideoEngineView.ReceivedVideoPackets => Interlocked.Read(ref _videoPackets);

	StreamConfig? IVideoEngineView.CurrentStreamConfig => _streamConfig;

	void IVideoEngineView.AttachGpuPresenter(IHighResolutionD3DPresenter presenter, StreamConfig config)
	{
		VideoImage.Visibility = Visibility.Collapsed;
		VideoImage.Source = null;
		_bitmap = null;
		VideoStage.Children.Insert(0, presenter.View);
		presenter.View.HorizontalAlignment = HorizontalAlignment.Center;
		presenter.View.VerticalAlignment = VerticalAlignment.Center;
		UpdateHighResolutionD3DPresenterLayout(presenter, config);
		VideoStage.UpdateLayout();
	}

	void IVideoEngineView.DetachGpuPresenter(IHighResolutionD3DPresenter presenter)
	{
		try
		{
			VideoStage.Children.Remove(presenter.View);
		}
		catch
		{
		}
		VideoImage.Visibility = Visibility.Visible;
	}

	void IVideoEngineView.ShowVideoSurface()
	{
		ShowVideoSurface();
	}

	void IVideoEngineView.ShowEmptyStateAfterGpuFailure()
	{
		VideoImage.Source = null;
		ShowEmptyStateSurface();
	}

	void IVideoEngineView.ReleasePendingFrames()
	{
		ReleasePendingD3DFrame();
		ReleasePendingFrame();
	}

	void IVideoEngineView.QueueSoftwareFrame(VideoFrame frame)
	{
		QueueFrameForPresentation(frame);
	}

	void IVideoEngineView.QueueGpuFrame(IHighResolutionD3DFrame frame)
	{
		QueueD3DFrameForPresentation(frame);
	}

	void IVideoEngineView.FlushPendingVideo()
	{
		FlushPendingVideoToSink();
	}

	void IVideoEngineView.RequireH264Keyframe()
	{
		RequireH264Keyframe();
	}

	void IVideoEngineView.BeginLatencyWarmup()
	{
		_receiveToPresentLatencyWindow.BeginWarmup();
	}

	void IVideoEngineView.SetStatus(string message)
	{
		SetStatus(message);
	}

	void IVideoEngineView.UpdateDiagnostics()
	{
		UpdateDiagnostics();
	}

	private int GetPendingRenderFrameCount()
	{
		return _frameMailbox.PendingCount;
	}

	private void LogGateDecisionThrottled()
	{
		if (_gateDecisionLogGate.TryPass())
		{
			AppLog.Write("H264 gate: " + GetH264GateLastDecision());
		}
	}

	private void LogPendingVideoDropsThrottled()
	{
		long dropped = _pendingVideoBeforeSink.Dropped;
		if (dropped == 0)
		{
			return;
		}

		if (_pendingVideoDropLogGate.TryPass())
		{
			AppLog.Write($"Pre-render video queue dropped={dropped:N0}, pending={_pendingVideoBeforeSink.Count:N0} packets/{(double)_pendingVideoBeforeSink.Bytes / 1024.0:N1} KB");
		}
	}

	private void QueueDiagnosticsUpdate()
	{
		if (!_diagnosticsUpdateGate.TryPass())
		{
			return;
		}
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
