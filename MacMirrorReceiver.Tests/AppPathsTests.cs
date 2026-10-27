using System;
using System.IO;
using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AppPathsTests
{
	[Fact]
	public void TheTestRunUsesItsOwnDataDirectory()
	{
		string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

		Assert.True(AppPaths.IsDataDirectoryOverridden);
		Assert.StartsWith(TestDataDirectory.Path, AppPaths.LogsDirectory, StringComparison.OrdinalIgnoreCase);
		Assert.StartsWith(TestDataDirectory.Path, AppPaths.ConfigDirectory, StringComparison.OrdinalIgnoreCase);
		Assert.StartsWith(TestDataDirectory.Path, ReceiverSettings.SettingsPath, StringComparison.OrdinalIgnoreCase);
		Assert.False(AppPaths.LogsDirectory.StartsWith(Path.Combine(localAppData, "iMirror"), StringComparison.OrdinalIgnoreCase));
	}

	[Fact]
	public void AppLogWritesIntoTheTestDataDirectory()
	{
		string marker = "AppPathsTests marker " + Guid.NewGuid().ToString("N");

		AppLog.Write(marker);
		AppLog.Flush();

		string logPath = Path.Combine(AppPaths.LogsDirectory, "iMirror.log");
		using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		using var reader = new StreamReader(stream);
		Assert.Contains(marker, reader.ReadToEnd());
	}
}
