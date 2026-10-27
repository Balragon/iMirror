using System;
using System.Threading;
using System.Threading.Tasks;

namespace MacMirrorReceiver;

/// <summary>
/// UI surface the update workflow drives. MainWindow implements this against the
/// update-notice controls; tests substitute a fake so the controller runs without WPF.
/// </summary>
internal interface IUpdateNoticeView
{
	void ShowUpdateAvailable(UpdateInfo update);

	void ShowDownloadStarted(UpdateInfo update);

	void ShowInstallerStarting();

	void ShowInstallerHandoff();

	void ShowInstallFailed();

	void HideNotice();

	void SetStatus(string message);
}

/// <summary>
/// Orchestrates the update workflow: startup/manual checks, the automatic-notice policy,
/// download, installer handoff, and the graceful shutdown that lets the silent installer
/// replace files and relaunch iMirror with /IMIRROR_LAUNCH=1. All members are expected to
/// run on the UI thread, matching the MainWindow code this was extracted from.
/// </summary>
internal sealed class UpdateController
{
	private static readonly TimeSpan StartupUpdateCheckDelay = TimeSpan.FromMilliseconds(1500);

	private readonly Func<CancellationToken, Task<UpdateInfo?>> _checkForUpdateAsync;

	private readonly Func<UpdateInfo, IProgress<double>?, CancellationToken, Task<string>> _downloadSetupAsync;

	private readonly Action<string> _launchInstaller;

	private readonly Action _releaseSingleInstance;

	private readonly IUpdateNoticeView _view;

	private readonly Func<Task> _shutdownApplicationAsync;

	private readonly Func<bool> _isShutdownStarted;

	private readonly Func<UpdateInfo, DateTimeOffset, bool> _shouldShowAutomaticNotice;

	private readonly Action<string, DateTimeOffset> _markNoticeShown;

	private readonly Action<string> _dismissNotice;

	private readonly Func<CancellationToken, Task> _startupDelayAsync;

	private readonly Func<DateTimeOffset> _now;

	private readonly Action _cleanUpDownloads;

	// Canceled when the application starts shutting down so an in-flight update check or
	// installer download stops promptly instead of racing process teardown.
	private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();

	private UpdateInfo? _pendingUpdate;

	private bool _startupUpdateCheckStarted;

	private bool _installInProgress;

	public UpdateController(
		UpdateService updateService,
		IUpdateNoticeView view,
		Func<Task> shutdownApplicationAsync,
		Func<bool> isShutdownStarted)
		: this(
			cancellationToken => updateService.CheckAsync(includePrerelease: false, cancellationToken),
			updateService.DownloadSetupAsync,
			UpdateLauncher.Launch,
			App.ReleaseSingleInstanceMutex,
			view,
			shutdownApplicationAsync,
			isShutdownStarted,
			ReceiverSettings.ShouldShowAutomaticUpdateNotice,
			ReceiverSettings.MarkUpdateNoticeShown,
			ReceiverSettings.DismissUpdateNotice,
			cancellationToken => Task.Delay(StartupUpdateCheckDelay, cancellationToken),
			() => DateTimeOffset.Now,
			updateService.TryCleanUpDownloadedInstallers)
	{
	}

	internal UpdateController(
		Func<CancellationToken, Task<UpdateInfo?>> checkForUpdateAsync,
		Func<UpdateInfo, IProgress<double>?, CancellationToken, Task<string>> downloadSetupAsync,
		Action<string> launchInstaller,
		Action releaseSingleInstance,
		IUpdateNoticeView view,
		Func<Task> shutdownApplicationAsync,
		Func<bool> isShutdownStarted,
		Func<UpdateInfo, DateTimeOffset, bool> shouldShowAutomaticNotice,
		Action<string, DateTimeOffset> markNoticeShown,
		Action<string> dismissNotice,
		Func<CancellationToken, Task> startupDelayAsync,
		Func<DateTimeOffset> now,
		Action cleanUpDownloads)
	{
		_checkForUpdateAsync = checkForUpdateAsync ?? throw new ArgumentNullException(nameof(checkForUpdateAsync));
		_downloadSetupAsync = downloadSetupAsync ?? throw new ArgumentNullException(nameof(downloadSetupAsync));
		_launchInstaller = launchInstaller ?? throw new ArgumentNullException(nameof(launchInstaller));
		_releaseSingleInstance = releaseSingleInstance ?? throw new ArgumentNullException(nameof(releaseSingleInstance));
		_view = view ?? throw new ArgumentNullException(nameof(view));
		_shutdownApplicationAsync = shutdownApplicationAsync ?? throw new ArgumentNullException(nameof(shutdownApplicationAsync));
		_isShutdownStarted = isShutdownStarted ?? throw new ArgumentNullException(nameof(isShutdownStarted));
		_shouldShowAutomaticNotice = shouldShowAutomaticNotice ?? throw new ArgumentNullException(nameof(shouldShowAutomaticNotice));
		_markNoticeShown = markNoticeShown ?? throw new ArgumentNullException(nameof(markNoticeShown));
		_dismissNotice = dismissNotice ?? throw new ArgumentNullException(nameof(dismissNotice));
		_startupDelayAsync = startupDelayAsync ?? throw new ArgumentNullException(nameof(startupDelayAsync));
		_now = now ?? throw new ArgumentNullException(nameof(now));
		_cleanUpDownloads = cleanUpDownloads ?? throw new ArgumentNullException(nameof(cleanUpDownloads));
	}

	public UpdateInfo? PendingUpdate => _pendingUpdate;

	/// <summary>
	/// Cancels in-flight update work (startup delay, check, download). Called when the
	/// application begins shutting down. Canceling after the installer has launched is
	/// harmless: nothing update-related is in flight by then.
	/// </summary>
	public void CancelPendingOperations()
	{
		_shutdownCts.Cancel();
	}

	public async Task CheckForUpdatesOnStartupAsync()
	{
		if (_startupUpdateCheckStarted || _isShutdownStarted())
		{
			return;
		}

		_startupUpdateCheckStarted = true;
		try
		{
			await _startupDelayAsync(_shutdownCts.Token);
			if (_isShutdownStarted())
			{
				return;
			}

			// Off the UI thread: purges leftover setup.exe/.download files from previous
			// updates before this session potentially downloads a new one.
			await Task.Run(_cleanUpDownloads);

			UpdateCheckResult result = await CheckForUpdatesAsync(manual: false);
			UpdateInfo? update = result.Update;
			if (update?.IsNewer != true)
			{
				return;
			}

			DateTimeOffset now = _now();
			if (!_shouldShowAutomaticNotice(update, now))
			{
				return;
			}

			_markNoticeShown(update.LatestVersion, now);
			ShowUpdateNotice(update);
		}
		catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
		{
			AppLog.Write("Startup update check canceled by application shutdown.");
		}
		catch (Exception ex)
		{
			AppLog.Write("Startup update check failed: " + ex.Message);
		}
	}

	public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool manual)
	{
		UpdateInfo? update = await _checkForUpdateAsync(_shutdownCts.Token);
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

	public async Task<bool> InstallUpdateAsync(UpdateInfo updateInfo, IProgress<double>? progress)
	{
		if (_installInProgress)
		{
			// The main window and the settings window both offer Update; a second click while
			// the first download runs used to hit the exclusive .download file, throw, and flip
			// the notice to "Retry" while the first download went on to launch the installer.
			AppLog.Write("Update install already in progress; ignoring a second request.");
			return false;
		}

		_installInProgress = true;
		try
		{
			_pendingUpdate = updateInfo;
			_view.ShowDownloadStarted(updateInfo);
			_view.SetStatus($"Downloading iMirror {updateInfo.LatestVersion} update...");

			string setupPath = await _downloadSetupAsync(updateInfo, progress, _shutdownCts.Token);
			_view.ShowInstallerStarting();
			_view.SetStatus("Starting update installer...");
			// The installer checks the single-instance mutex as soon as it starts, before this
			// process has shut down, so the mutex must be gone before the installer is launched.
			_releaseSingleInstance();
			_launchInstaller(setupPath);
			_view.ShowInstallerHandoff();
			_view.SetStatus("Update installer started. Closing iMirror so setup can finish and restart it.");
			AppLog.Write("Update installer started; shutting down iMirror for update handoff.");
			await _shutdownApplicationAsync();
			return true;
		}
		catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
		{
			// Shutdown is already in progress; skip the retry UI on the closing window.
			AppLog.Write("Update install canceled by application shutdown.");
			return false;
		}
		catch (Exception ex)
		{
			AppLog.Write("Update install failed: " + ex);
			_view.SetStatus("Update could not be installed: " + ex.Message);
			_view.ShowInstallFailed();
			return false;
		}
		finally
		{
			_installInProgress = false;
		}
	}

	public void DismissUpdateNotice()
	{
		UpdateInfo? update = _pendingUpdate;
		if (update != null)
		{
			_dismissNotice(update.LatestVersion);
		}
		_view.HideNotice();
	}

	private void ShowUpdateNotice(UpdateInfo update)
	{
		_pendingUpdate = update;
		_view.ShowUpdateAvailable(update);
	}
}
