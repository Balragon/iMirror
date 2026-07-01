using System;
using System.IO;
using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class UpdateLauncherTests
{
	[Fact]
	public void CreateStartInfo_UsesSilentCloseAndRelaunchArguments()
	{
		string tempDirectory = Path.Combine(Path.GetTempPath(), "iMirror-update-launcher-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempDirectory);
		string setupPath = Path.Combine(tempDirectory, "iMirror-0.7.6-setup.exe");
		File.WriteAllText(setupPath, "fake setup");

		try
		{
			var startInfo = UpdateLauncher.CreateStartInfo(setupPath);

			Assert.Equal(setupPath, startInfo.FileName);
			Assert.Equal(tempDirectory, startInfo.WorkingDirectory);
			Assert.True(startInfo.UseShellExecute);
			Assert.Contains("/SILENT", startInfo.Arguments, StringComparison.Ordinal);
			Assert.Contains("/SUPPRESSMSGBOXES", startInfo.Arguments, StringComparison.Ordinal);
			Assert.Contains("/CLOSEAPPLICATIONS", startInfo.Arguments, StringComparison.Ordinal);
			Assert.Contains("/RESTARTAPPLICATIONS", startInfo.Arguments, StringComparison.Ordinal);
			Assert.Contains("/IMIRROR_LAUNCH=1", startInfo.Arguments, StringComparison.Ordinal);
		}
		finally
		{
			try
			{
				Directory.Delete(tempDirectory, recursive: true);
			}
			catch
			{
			}
		}
	}

	[Fact]
	public void CreateStartInfo_RejectsMissingInstaller()
	{
		Assert.Throws<InvalidOperationException>(() =>
			UpdateLauncher.CreateStartInfo(Path.Combine(Path.GetTempPath(), "missing-iMirror-setup.exe")));
	}
}
