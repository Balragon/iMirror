using System;
using System.Diagnostics;
using System.IO;

namespace MacMirrorReceiver;

internal static class UpdateLauncher
{
	internal const string InstallerArguments = "/SILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /IMIRROR_LAUNCH=1";

	public static void Launch(string setupPath)
	{
		Process.Start(CreateStartInfo(setupPath));
	}

	internal static ProcessStartInfo CreateStartInfo(string setupPath)
	{
		if (string.IsNullOrWhiteSpace(setupPath) || !File.Exists(setupPath))
		{
			throw new InvalidOperationException("Downloaded update installer was not found.");
		}

		string? workingDirectory = Path.GetDirectoryName(setupPath);
		return new ProcessStartInfo
		{
			FileName = setupPath,
			Arguments = InstallerArguments,
			WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? AppContext.BaseDirectory : workingDirectory,
			UseShellExecute = true
		};
	}
}
