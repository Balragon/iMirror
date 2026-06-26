using System;
using System.Diagnostics;
using System.IO;

namespace MacMirrorReceiver;

internal static class UpdateLauncher
{
	public static void Launch(string setupPath)
	{
		if (string.IsNullOrWhiteSpace(setupPath) || !File.Exists(setupPath))
		{
			throw new InvalidOperationException("Downloaded update installer was not found.");
		}

		string? workingDirectory = Path.GetDirectoryName(setupPath);
		Process.Start(new ProcessStartInfo
		{
			FileName = setupPath,
			Arguments = "/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /IMIRROR_LAUNCH=1",
			WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? AppContext.BaseDirectory : workingDirectory,
			UseShellExecute = true
		});
	}
}
