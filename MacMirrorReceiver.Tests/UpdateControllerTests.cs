using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class UpdateControllerTests
{
	private sealed class FakeUpdateNoticeView : IUpdateNoticeView
	{
		public List<string> Calls { get; } = new List<string>();

		public List<string> StatusMessages { get; } = new List<string>();

		public void ShowUpdateAvailable(UpdateInfo update)
		{
			Calls.Add("ShowUpdateAvailable:" + update.LatestVersion);
		}

		public void ShowDownloadStarted(UpdateInfo update)
		{
			Calls.Add("ShowDownloadStarted:" + update.LatestVersion);
		}

		public void ShowInstallerStarting()
		{
			Calls.Add("ShowInstallerStarting");
		}

		public void ShowInstallerHandoff()
		{
			Calls.Add("ShowInstallerHandoff");
		}

		public void ShowInstallFailed()
		{
			Calls.Add("ShowInstallFailed");
		}

		public void HideNotice()
		{
			Calls.Add("HideNotice");
		}

		public void SetStatus(string message)
		{
			StatusMessages.Add(message);
		}
	}

	private sealed class Harness
	{
		public FakeUpdateNoticeView View { get; } = new FakeUpdateNoticeView();

		public UpdateInfo? CheckResult { get; set; }

		public Exception? CheckException { get; set; }

		public string DownloadedSetupPath { get; set; } = @"C:\downloads\iMirror-9.9.9-setup.exe";

		public Exception? DownloadException { get; set; }

		public Exception? LaunchException { get; set; }

		public bool ShutdownStarted { get; set; }

		public bool ShouldShowAutomaticNotice { get; set; } = true;

		public int CheckCalls { get; private set; }

		public int DownloadCalls { get; private set; }

		public List<string> LaunchedSetupPaths { get; } = new List<string>();

		public List<string> HandoffSequence { get; } = new List<string>();

		public int ShutdownCalls { get; private set; }

		public List<(string Version, DateTimeOffset At)> NoticesMarkedShown { get; } = new List<(string, DateTimeOffset)>();

		public List<string> DismissedVersions { get; } = new List<string>();

		public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);

		public int CleanUpCalls { get; private set; }

		public UpdateController CreateController()
		{
			return new UpdateController(
				token =>
				{
					CheckCalls++;
					token.ThrowIfCancellationRequested();
					return CheckException != null
						? Task.FromException<UpdateInfo?>(CheckException)
						: Task.FromResult(CheckResult);
				},
				(_, _, token) =>
				{
					DownloadCalls++;
					token.ThrowIfCancellationRequested();
					return DownloadException != null
						? Task.FromException<string>(DownloadException)
						: Task.FromResult(DownloadedSetupPath);
				},
				setupPath =>
				{
					HandoffSequence.Add("launch");
					if (LaunchException != null)
					{
						throw LaunchException;
					}
					LaunchedSetupPaths.Add(setupPath);
				},
				() => HandoffSequence.Add("release-mutex"),
				View,
				() =>
				{
					ShutdownCalls++;
					return Task.CompletedTask;
				},
				() => ShutdownStarted,
				(_, _) => ShouldShowAutomaticNotice,
				(version, at) => NoticesMarkedShown.Add((version, at)),
				version => DismissedVersions.Add(version),
				token =>
				{
					token.ThrowIfCancellationRequested();
					return Task.CompletedTask;
				},
				() => Now,
				() => CleanUpCalls++);
		}
	}

	private static UpdateInfo CreateUpdate(bool isNewer = true, string version = "9.9.9")
	{
		return new UpdateInfo(
			version,
			isNewer,
			IsPrerelease: false,
			SetupAssetName: $"iMirror-{version}-setup.exe",
			SetupAssetUrl: "https://example.invalid/setup.exe",
			SetupAssetSize: 1234,
			Sha256SumsAssetUrl: "https://example.invalid/SHA256SUMS",
			ReleaseHtmlUrl: "https://example.invalid/releases");
	}

	[Fact]
	public async Task StartupCheck_ShowsNoticeAndMarksShown_WhenUpdateIsNewer()
	{
		var harness = new Harness { CheckResult = CreateUpdate() };
		UpdateController controller = harness.CreateController();

		await controller.CheckForUpdatesOnStartupAsync();

		Assert.Equal(new[] { ("9.9.9", harness.Now) }, harness.NoticesMarkedShown);
		Assert.Contains("ShowUpdateAvailable:9.9.9", harness.View.Calls);
		Assert.Equal("9.9.9", controller.PendingUpdate?.LatestVersion);
	}

	[Fact]
	public async Task StartupCheck_DoesNotShowNotice_WhenNoticePolicyDeclines()
	{
		var harness = new Harness
		{
			CheckResult = CreateUpdate(),
			ShouldShowAutomaticNotice = false
		};
		UpdateController controller = harness.CreateController();

		await controller.CheckForUpdatesOnStartupAsync();

		Assert.Empty(harness.NoticesMarkedShown);
		Assert.Empty(harness.View.Calls);
	}

	[Fact]
	public async Task StartupCheck_DoesNotShowNotice_WhenAlreadyOnLatest()
	{
		var harness = new Harness { CheckResult = CreateUpdate(isNewer: false) };
		UpdateController controller = harness.CreateController();

		await controller.CheckForUpdatesOnStartupAsync();

		Assert.Empty(harness.View.Calls);
		Assert.Null(controller.PendingUpdate);
	}

	[Fact]
	public async Task StartupCheck_RunsOnlyOnce()
	{
		var harness = new Harness { CheckResult = CreateUpdate() };
		UpdateController controller = harness.CreateController();

		await controller.CheckForUpdatesOnStartupAsync();
		await controller.CheckForUpdatesOnStartupAsync();

		Assert.Equal(1, harness.CheckCalls);
	}

	[Fact]
	public async Task StartupCheck_DoesNothing_WhenShutdownAlreadyStarted()
	{
		var harness = new Harness
		{
			CheckResult = CreateUpdate(),
			ShutdownStarted = true
		};
		UpdateController controller = harness.CreateController();

		await controller.CheckForUpdatesOnStartupAsync();

		Assert.Equal(0, harness.CheckCalls);
		Assert.Empty(harness.View.Calls);
	}

	[Fact]
	public async Task StartupCheck_SwallowsCheckFailure()
	{
		var harness = new Harness { CheckException = new InvalidOperationException("network down") };
		UpdateController controller = harness.CreateController();

		await controller.CheckForUpdatesOnStartupAsync();

		Assert.Empty(harness.View.Calls);
	}

	[Fact]
	public async Task ManualCheck_ReturnsFailure_WhenCheckReturnsNull()
	{
		var harness = new Harness { CheckResult = null };
		UpdateController controller = harness.CreateController();

		UpdateCheckResult result = await controller.CheckForUpdatesAsync(manual: true);

		Assert.True(result.Failed);
		Assert.Null(result.Update);
		Assert.Equal("Could not check for updates. Open Releases and install manually.", result.Message);
	}

	[Fact]
	public async Task AutomaticCheck_ReturnsEmptyMessage_WhenCheckReturnsNull()
	{
		var harness = new Harness { CheckResult = null };
		UpdateController controller = harness.CreateController();

		UpdateCheckResult result = await controller.CheckForUpdatesAsync(manual: false);

		Assert.True(result.Failed);
		Assert.Equal(string.Empty, result.Message);
	}

	[Fact]
	public async Task ManualCheck_ReportsLatest_WhenNotNewer()
	{
		var harness = new Harness { CheckResult = CreateUpdate(isNewer: false) };
		UpdateController controller = harness.CreateController();

		UpdateCheckResult result = await controller.CheckForUpdatesAsync(manual: true);

		Assert.False(result.Failed);
		Assert.Null(result.Update);
		Assert.Equal("You're on the latest iMirror.", result.Message);
		Assert.Empty(harness.View.Calls);
	}

	[Fact]
	public async Task ManualCheck_ShowsNotice_WhenUpdateIsNewer()
	{
		var harness = new Harness { CheckResult = CreateUpdate() };
		UpdateController controller = harness.CreateController();

		UpdateCheckResult result = await controller.CheckForUpdatesAsync(manual: true);

		Assert.False(result.Failed);
		Assert.Equal("iMirror 9.9.9 is available.", result.Message);
		Assert.Contains("ShowUpdateAvailable:9.9.9", harness.View.Calls);
		Assert.Equal("9.9.9", controller.PendingUpdate?.LatestVersion);
	}

	[Fact]
	public async Task AutomaticCheck_DoesNotShowNoticeDirectly()
	{
		var harness = new Harness { CheckResult = CreateUpdate() };
		UpdateController controller = harness.CreateController();

		UpdateCheckResult result = await controller.CheckForUpdatesAsync(manual: false);

		Assert.Equal("9.9.9", result.Update?.LatestVersion);
		Assert.Empty(harness.View.Calls);
	}

	[Fact]
	public async Task Install_LaunchesInstallerThenShutsDown()
	{
		var harness = new Harness();
		UpdateController controller = harness.CreateController();
		UpdateInfo update = CreateUpdate();

		bool started = await controller.InstallUpdateAsync(update, progress: null);

		Assert.True(started);
		Assert.Equal(1, harness.DownloadCalls);
		Assert.Equal(new[] { harness.DownloadedSetupPath }, harness.LaunchedSetupPaths);
		Assert.Equal(1, harness.ShutdownCalls);
		// The installer's AppMutex check runs as soon as it starts, so the single-instance
		// mutex must be released before the installer process is launched.
		Assert.Equal(new[] { "release-mutex", "launch" }, harness.HandoffSequence);
		Assert.Equal(
			new[] { "ShowDownloadStarted:9.9.9", "ShowInstallerStarting", "ShowInstallerHandoff" },
			harness.View.Calls);
		Assert.Equal(
			"Update installer started. Closing iMirror so setup can finish and restart it.",
			harness.View.StatusMessages[^1]);
	}

	[Fact]
	public async Task Install_ReportsFailureWithoutShutdown_WhenDownloadFails()
	{
		var harness = new Harness { DownloadException = new InvalidOperationException("SHA-256 mismatch") };
		UpdateController controller = harness.CreateController();

		bool started = await controller.InstallUpdateAsync(CreateUpdate(), progress: null);

		Assert.False(started);
		Assert.Empty(harness.LaunchedSetupPaths);
		Assert.Equal(0, harness.ShutdownCalls);
		Assert.Equal(new[] { "ShowDownloadStarted:9.9.9", "ShowInstallFailed" }, harness.View.Calls);
		Assert.Contains("Update could not be installed: SHA-256 mismatch", harness.View.StatusMessages);
	}

	[Fact]
	public async Task Install_ReportsFailureWithoutShutdown_WhenLaunchFails()
	{
		var harness = new Harness { LaunchException = new InvalidOperationException("installer missing") };
		UpdateController controller = harness.CreateController();

		bool started = await controller.InstallUpdateAsync(CreateUpdate(), progress: null);

		Assert.False(started);
		Assert.Equal(0, harness.ShutdownCalls);
		Assert.Contains("ShowInstallFailed", harness.View.Calls);
	}

	[Fact]
	public async Task Dismiss_RecordsDismissedVersionAndHidesNotice()
	{
		var harness = new Harness { CheckResult = CreateUpdate() };
		UpdateController controller = harness.CreateController();
		await controller.CheckForUpdatesAsync(manual: true);

		controller.DismissUpdateNotice();

		Assert.Equal(new[] { "9.9.9" }, harness.DismissedVersions);
		Assert.Equal("HideNotice", harness.View.Calls[^1]);
	}

	[Fact]
	public void Dismiss_OnlyHidesNotice_WhenNoPendingUpdate()
	{
		var harness = new Harness();
		UpdateController controller = harness.CreateController();

		controller.DismissUpdateNotice();

		Assert.Empty(harness.DismissedVersions);
		Assert.Equal(new[] { "HideNotice" }, harness.View.Calls);
	}

	[Fact]
	public async Task StartupCheck_CleansUpDownloadedInstallers()
	{
		var harness = new Harness { CheckResult = CreateUpdate(isNewer: false) };
		UpdateController controller = harness.CreateController();

		await controller.CheckForUpdatesOnStartupAsync();

		Assert.Equal(1, harness.CleanUpCalls);
	}

	[Fact]
	public async Task StartupCheck_StopsQuietly_WhenCanceledByShutdown()
	{
		var harness = new Harness { CheckResult = CreateUpdate() };
		UpdateController controller = harness.CreateController();

		controller.CancelPendingOperations();
		await controller.CheckForUpdatesOnStartupAsync();

		Assert.Equal(0, harness.CheckCalls);
		Assert.Equal(0, harness.CleanUpCalls);
		Assert.Empty(harness.View.Calls);
	}

	[Fact]
	public async Task Install_ReturnsFalseWithoutRetryUi_WhenCanceledByShutdown()
	{
		var harness = new Harness();
		UpdateController controller = harness.CreateController();

		controller.CancelPendingOperations();
		bool started = await controller.InstallUpdateAsync(CreateUpdate(), progress: null);

		Assert.False(started);
		Assert.Empty(harness.LaunchedSetupPaths);
		Assert.Equal(0, harness.ShutdownCalls);
		Assert.DoesNotContain("ShowInstallFailed", harness.View.Calls);
		Assert.DoesNotContain(
			harness.View.StatusMessages,
			message => message.StartsWith("Update could not be installed", StringComparison.Ordinal));
	}

	[Fact]
	public async Task ManualCheck_PropagatesCancellation_WhenCanceledByShutdown()
	{
		var harness = new Harness { CheckResult = CreateUpdate() };
		UpdateController controller = harness.CreateController();

		controller.CancelPendingOperations();

		await Assert.ThrowsAsync<OperationCanceledException>(
			() => controller.CheckForUpdatesAsync(manual: true));
	}
}
