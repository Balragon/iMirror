using System;
using System.IO;

namespace MacMirrorReceiver;

internal static class AppLog
{
	private static readonly string LogPath = Path.Combine(
		AppPaths.LogsDirectory,
#if HARDWARE_PROBE
			"iMirror-hwprobe.log");
#else
			"iMirror.log");
#endif

	// One background writer; callers (network receive, decoder, UI render path) no longer
	// open/append/close the file under a global lock per line, and the file rotates at
	// 5 MB x 3 instead of growing for the lifetime of the install.
	private static readonly RollingFileLog Log = CreateLog();

	private static RollingFileLog CreateLog()
	{
		var log = new RollingFileLog(LogPath);
		AppDomain.CurrentDomain.ProcessExit += (_, _) => log.Flush();
		return log;
	}

	public static void Write(string message)
	{
		Log.Write($"[{DateTimeOffset.Now:O}] {message}");
	}

	/// <summary>Waits for queued lines to reach disk; used before shutdown and process handoffs.</summary>
	public static void Flush()
	{
		Log.Flush();
	}
}
