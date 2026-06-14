using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace MacMirrorReceiver.Video;

internal static class MpvLocator
{
	private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(2.0);

	public static string? StartupRunnableMpvExecutable { get; } = FindMpvExecutable(requireRunnable: true);

	public static bool StartupRunnableMpvAvailable => StartupRunnableMpvExecutable != null;

	public static string? FindMpvExecutable(bool requireRunnable)
	{
		foreach (string candidate in EnumerateCandidatePaths())
		{
			if (!File.Exists(candidate))
			{
				continue;
			}

			string fullPath = Path.GetFullPath(candidate);
			if (!requireRunnable || CanStartMpv(fullPath))
			{
				return fullPath;
			}
		}

		return null;
	}

	private static IEnumerable<string> EnumerateCandidatePaths()
	{
		yield return Path.Combine(AppContext.BaseDirectory, "tools", "mpv", "mpv.exe");
		yield return Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "mpv", "mpv.exe");

		string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
		if (!string.IsNullOrWhiteSpace(programFiles))
		{
			yield return Path.Combine(programFiles, "MPV Player", "mpv.exe");
		}

		string[] paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
		foreach (string path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
		{
			yield return Path.Combine(path.Trim().Trim('"'), "mpv.exe");
		}
	}

	private static bool CanStartMpv(string mpvPath)
	{
		Process? process = null;
		try
		{
			process = new Process
			{
				StartInfo = new ProcessStartInfo
				{
					FileName = mpvPath,
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					CreateNoWindow = true
				}
			};
			process.StartInfo.ArgumentList.Add("--version");
			if (!process.Start())
			{
				return false;
			}

			if (!process.WaitForExit((int)ValidationTimeout.TotalMilliseconds))
			{
				TryKill(process);
				return false;
			}

			return process.ExitCode == 0;
		}
		catch
		{
			TryKill(process);
			return false;
		}
		finally
		{
			process?.Dispose();
		}
	}

	private static void TryKill(Process? process)
	{
		try
		{
			if (process != null && !process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch
		{
		}
	}
}
