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

	private readonly IUpdateNoticeView _view;

	private readonly Func<Task> _shutdownApplicationAsync;

	private readonly Func<bool> _isShutdownStarted;

	private readonly Func<UpdateInfo, DateTimeOffset, bool> _shouldShowAutomaticNotice;

	private readonly Action<string, DateTimeOffset> _markNoticeShown;

	private readonly Action<string> _dismissNotice;

	private readonly Func<Task> _startupDelayAsync;

	private readonly Func<DateTimeOffset> _now;

	private UpdateInfo? _pendingUpdate;

	private bool _startupUpdateCheckStarted;

	public UpdateController(
		UpdateService updateService,
		IUpdateNoticeView view,
		Func<Task> shutdownApplicationAsync,
		Func<bool> isShutdownStarted)
		: this(
			cancellationToken => updateService.CheckAsync(includePrerelease: false, cancellationToken),
			updateService.DownloadSetupAsync,
			UpdateLauncher.Launch,
			view,
			shutdownApplicationAsync,
			isShutdownStarted,
			ReceiverSettings.ShouldShowAutomaticUpdateNotice,
			ReceiverSettings.MarkUpdateNoticeShown,
			ReceiverSettings.DismissUpdateNotice,
			() => Task.Delay(StartupUpdateCheckDelay),
			() => DateTimeOffset.Now)
	{
	}

	internal UpdateController(
		Func<CancellationToken, Task<UpdateInfo?>> checkForUpdateAsync,
		Func<UpdateInfo, IProgress<double>?, CancellationToken, Task<string>> downloadSetupAsync,
		Action<string> launchInstaller,
		IUpdateNoticeView view,
		Func<Task> shutdownApplicationAsync,
		Func<bool> isShutdownStarted,
		Func<UpdateInfo, DateTimeOffset, bool> shouldShowAutomaticNotice,
		Action<string, DateTimeOffset> markNoticeShown,
		Action<string> dismissNotice,
		Func<Task> startupDelayAsync,
		Func<DateTimeOffset> now)
	{
		_checkForUpdateAsync = checkForUpdateAsync ?? throw new ArgumentNullException(nameof(checkForUpdateAsync));
		_downloadSetupAsync = downloadSetupAsync ?? throw new ArgumentNullException(nameof(downloadSetupAsync));
		_launchInstaller = launchInstaller ?? throw new ArgumentNullException(nameof(launchInstaller));
		_view = view ?? throw new ArgumentNullException(nameof(view));
		_shutdownApplicationAsync = shutdownApplicationAsync ?? throw new ArgumentNullException(nameof(shutdownApplicationAsync));
		_isShutdownStarted = isShutdownStarted ?? throw new ArgumentNullException(nameof(isShutdownStarted));
		_shouldShowAutomaticNotice = shouldShowAutomaticNotice ?? throw new ArgumentNullException(nameof(shouldShowAutomaticNotice));
		_markNoticeShown = markNoticeShown ?? throw new ArgumentNullException(nameof(markNoticeShown));
		_dismissNotice = dismissNotice ?? throw new ArgumentNullException(nameof(dismissNotice));
		_startupDelayAsync = startupDelayAsync ?? throw new ArgumentNullException(nameof(startupDelayAsync));
		_now = now ?? throw new ArgumentNullException(nameof(now));
	}

	public UpdateInfo? PendingUpdate => _pendingUpdate;

	public async Task CheckForUpdatesOnStartupAsync()
	{
		if (_startupUpdateCheckStarted || _isShutdownStarted())
		{
			return;
		}

		_startupUpdateCheckStarted = true;
		try
		{
			await _startupDelayAsync();
			if (_isShutdownStarted())
			{
				return;
			}

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
		catch (Exception ex)
		{
			AppLog.Write("Startup update check failed: " + ex.Message);
		}
	}

	public async Task<UpdateCheckResult> CheckForUpdatesAsync(bool manual)
	{
		UpdateInfo? update = await _checkForUpdateAsync(CancellationToken.None);
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
		try
		{
			_pendingUpdate = updateInfo;
			_view.ShowDownloadStarted(updateInfo);
			_view.SetStatus($"Downloading iMirror {updateInfo.LatestVersion} update...");

			string setupPath = await _downloadSetupAsync(updateInfo, progress, CancellationToken.None);
			_view.ShowInstallerStarting();
			_view.SetStatus("Starting update installer...");
			_launchInstaller(setupPath);
			_view.ShowInstallerHandoff();
			_view.SetStatus("Update installer started. Closing iMirror so setup can finish and restart it.");
			AppLog.Write("Update installer started; shutting down iMirror for update handoff.");
			await _shutdownApplicationAsync();
			return true;
		}
		catch (Exception ex)
		{
			AppLog.Write("Update install failed: " + ex);
			_view.SetStatus("Update could not be installed: " + ex.Message);
			_view.ShowInstallFailed();
			return false;
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
