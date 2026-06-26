using System;
using System.IO;

namespace MacMirrorReceiver;

// Central provider for runtime-writable paths.
//
// The install directory (AppContext.BaseDirectory) is treated as READ-ONLY:
// once iMirror is installed under Program Files, writing next to the exe
// fails. All artifacts the app produces at runtime — logs, diagnostics,
// crash/media dumps, persisted settings — therefore live under
// %LOCALAPPDATA%\iMirror so they keep working regardless of install location.
//
// Read-only lookups (bundled ffmpeg.exe, ThirdParty\playfair, assembly
// probing) intentionally stay on AppContext.BaseDirectory and are NOT served
// here — reading from the install directory is fine; only writing is not.
internal static class AppPaths
{
	private const string AppFolderName = "iMirror";

	// Resolved once. If LocalApplicationData is unavailable (rare — e.g. a
	// stripped-down service account) we fall back to the per-user temp
	// directory, NEVER to the install directory, which would reintroduce the
	// read-only write failure this class exists to prevent.
	private static readonly string Root = ResolveRoot();

	public static string LogsDirectory { get; } = EnsureDirectory(Path.Combine(Root, "Logs"));

	public static string DiagnosticsDirectory { get; } = EnsureDirectory(Path.Combine(Root, "Diagnostics"));

	public static string DumpsDirectory { get; } = EnsureDirectory(Path.Combine(Root, "Dumps"));

	public static string ConfigDirectory { get; } = EnsureDirectory(Path.Combine(Root, "Config"));

	public static string UpdateDownloadsDirectory { get; } = EnsureDirectory(Path.Combine(Root, "Updates"));

	private static string ResolveRoot()
	{
		string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
		if (string.IsNullOrWhiteSpace(localAppData))
		{
			return Path.Combine(Path.GetTempPath(), AppFolderName + "-data");
		}

		return Path.Combine(localAppData, AppFolderName);
	}

	private static string EnsureDirectory(string path)
	{
		try
		{
			Directory.CreateDirectory(path);
		}
		catch
		{
			// Creation can fail under exotic ACLs; the caller's subsequent write
			// will surface the real error. We still return the intended path so
			// behavior stays consistent and the path is diagnosable in logs.
		}

		return path;
	}
}
