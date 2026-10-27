using System;
using System.IO;
using System.Runtime.CompilerServices;
using MacMirrorReceiver;

namespace MacMirrorReceiver.Tests;

/// <summary>
/// Points the app's data directory at a per-run temp folder before any test touches
/// <c>AppPaths</c>, so the suite never reads the developer's settings from
/// %LOCALAPPDATA%\iMirror\Config or writes test traffic into their real iMirror.log.
/// </summary>
internal static class TestDataDirectory
{
	public static string Path { get; private set; } = string.Empty;

#pragma warning disable CA2255 // A module initializer is the point: it must run before AppPaths is first read.
	[ModuleInitializer]
#pragma warning restore CA2255
	internal static void Isolate()
	{
		Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iMirror-tests", "data-" + Environment.ProcessId);
		Directory.CreateDirectory(Path);
		Environment.SetEnvironmentVariable(AppPaths.DataDirectoryEnvironmentVariableName, Path);
	}
}
