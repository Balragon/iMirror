using System;
using System.IO;

namespace MacMirrorReceiver;

internal static class AppLog
{
	private static readonly object Gate = new object();

	private static readonly string LogPath = Path.Combine(
		AppPaths.LogsDirectory,
#if HARDWARE_PROBE
			"iMirror-hwprobe.log");
#else
			"iMirror.log");
#endif

	public static void Write(string message)
	{
		lock (Gate)
		{
			File.AppendAllText(LogPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
		}
	}
}
