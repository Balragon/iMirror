using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

if (args.Length < 3 || args.Length > 7)
{
	Console.Error.WriteLine("Usage: RealDeviceAcceptanceReport <highres-iMirror.log> <capture.submitted.h264> <stable-iMirror.log> [p95-target-ms=150] [minimum-minutes=30] [required-reconnects=3] [geometry=2048x1152@30]");
	return 2;
}

string? highResolutionLog = ResolveFile(args[0], "High-resolution log");
string? submittedDump = ResolveFile(args[1], "Submitted H.264 dump");
string? stableLog = ResolveFile(args[2], "Stable regression log");
if (highResolutionLog == null || submittedDump == null || stableLog == null)
{
	return 2;
}
string p95TargetMs = args.Length >= 4 ? args[3] : "150";
string minimumMinutes = args.Length >= 5 ? args[4] : "30";
string requiredReconnects = args.Length >= 6 ? args[5] : "3";
string geometry = args.Length >= 7 ? args[6] : "2048x1152@30";

string repoRoot = FindRepoRoot(AppContext.BaseDirectory);
string latencyProject = Path.Combine(repoRoot, "tools", "LatencyAcceptanceReport", "LatencyAcceptanceReport.csproj");
string highResolutionProject = Path.Combine(repoRoot, "tools", "HighResolutionProbeReport", "HighResolutionProbeReport.csproj");

Console.WriteLine("iMirror real-device acceptance report");
Console.WriteLine("repoRoot=" + repoRoot);
Console.WriteLine("highResolutionLog=" + highResolutionLog);
Console.WriteLine("submittedDump=" + submittedDump);
Console.WriteLine("stableLog=" + stableLog);
Console.WriteLine("p95TargetMs=" + p95TargetMs + ", minimumMinutes=" + minimumMinutes + ", requiredReconnects=" + requiredReconnects + ", geometry=" + geometry);
Console.WriteLine();

DumpMatchResult dumpMatch = CheckSubmittedDumpMatchesHighResolutionLog(highResolutionLog, submittedDump);
Console.WriteLine(dumpMatch.Message);
Console.WriteLine();

CommandResult highResolutionLatency = RunDotnet(
	repoRoot,
	"run", "--project", latencyProject, "-c", "Release", "--",
	highResolutionLog, p95TargetMs, minimumMinutes, requiredReconnects, "false", "true");

CommandResult highResolutionProbe = RunDotnet(
	repoRoot,
	"run", "--project", highResolutionProject, "-c", "Release", "--",
	submittedDump, "600", geometry);

CommandResult stableLatency = RunDotnet(
	repoRoot,
	"run", "--project", latencyProject, "-c", "Release", "--",
	stableLog, p95TargetMs, minimumMinutes, "0", "true", "false");

bool pass = highResolutionLatency.ExitCode == 0
	&& dumpMatch.Pass
	&& highResolutionProbe.ExitCode == 0
	&& stableLatency.ExitCode == 0;

Console.WriteLine();
Console.WriteLine((pass ? "PASS" : "FAIL") + ": real-device acceptance bundle");
Console.WriteLine($"highResolutionLatency={(highResolutionLatency.ExitCode == 0 ? "pass" : "fail")} exit={highResolutionLatency.ExitCode.ToString(CultureInfo.InvariantCulture)}");
Console.WriteLine($"submittedDumpMatchesLog={(dumpMatch.Pass ? "pass" : "fail")}");
Console.WriteLine($"highResolutionProbe={(highResolutionProbe.ExitCode == 0 ? "pass" : "fail")} exit={highResolutionProbe.ExitCode.ToString(CultureInfo.InvariantCulture)}");
Console.WriteLine($"stableRegression={(stableLatency.ExitCode == 0 ? "pass" : "fail")} exit={stableLatency.ExitCode.ToString(CultureInfo.InvariantCulture)}");
Console.WriteLine("Manual visual check still required: moving Mac app windows must not visibly tear or corrupt.");

return pass ? 0 : 1;

static string? ResolveFile(string path, string label)
{
	string fullPath = Path.GetFullPath(path);
	if (!File.Exists(fullPath))
	{
		Console.Error.WriteLine(label + " not found: " + fullPath);
		return null;
	}

	return fullPath;
}

static DumpMatchResult CheckSubmittedDumpMatchesHighResolutionLog(string highResolutionLog, string submittedDump)
{
	string normalizedSubmittedDump = NormalizePath(submittedDump);
	string[] lines = File.ReadLines(highResolutionLog).ToArray();
	string[] loggedSubmittedDumps = lines
		.Select(ExtractSubmittedDumpPath)
		.Where(path => !string.IsNullOrWhiteSpace(path))
		.Select(path => NormalizePath(path!))
		.Distinct(StringComparer.OrdinalIgnoreCase)
		.ToArray();
	SubmittedDumpClose[] closeLines = lines
		.Select(ExtractSubmittedDumpClose)
		.Where(closeLine => closeLine != null)
		.Select(closeLine => closeLine!)
		.ToArray();

	if (loggedSubmittedDumps.Length == 0)
	{
		return new DumpMatchResult(false, "submittedDumpMatchesLog=fail: high-resolution log has no submitted=... H.264 dump line.");
	}

	if (!loggedSubmittedDumps.Any(path => string.Equals(path, normalizedSubmittedDump, StringComparison.OrdinalIgnoreCase)))
	{
		string loggedPaths = string.Join("; ", loggedSubmittedDumps);
		return new DumpMatchResult(
			false,
			"submittedDumpMatchesLog=fail: submitted H.264 dump is not referenced by the high-resolution log. " +
			"argument=" + normalizedSubmittedDump + "; logged=" + loggedPaths);
	}

	long actualBytes = new FileInfo(submittedDump).Length;
	SubmittedDumpClose[] matchingCloses = closeLines
		.Where(closeLine => string.Equals(NormalizePath(closeLine.Path), normalizedSubmittedDump, StringComparison.OrdinalIgnoreCase))
		.ToArray();
	if (matchingCloses.Length == 0)
	{
		return new DumpMatchResult(
			false,
			"submittedDumpMatchesLog=fail: submitted H.264 dump is referenced, but no submitted=<path> bytes=<n> close line was found.");
	}
	if (matchingCloses.Any(closeLine => closeLine.Bytes == actualBytes))
	{
		return new DumpMatchResult(
			true,
			$"submittedDumpMatchesLog=pass: submitted H.264 dump is referenced and byte count matches ({actualBytes.ToString(CultureInfo.InvariantCulture)} bytes).");
	}

	string loggedBytes = string.Join(", ", matchingCloses.Select(closeLine => closeLine.Bytes.ToString(CultureInfo.InvariantCulture)));
	return new DumpMatchResult(
		false,
		"submittedDumpMatchesLog=fail: submitted H.264 dump byte count mismatch. " +
		"actual=" + actualBytes.ToString(CultureInfo.InvariantCulture) + "; logged=" + loggedBytes);
}

static string? ExtractSubmittedDumpPath(string line)
{
	const string marker = "submitted=";
	int start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
	if (start < 0)
	{
		return null;
	}

	string value = line[(start + marker.Length)..].Trim();
	int comma = value.IndexOf(',', StringComparison.Ordinal);
	if (comma >= 0)
	{
		value = value[..comma];
	}
	int bytes = value.IndexOf(" bytes=", StringComparison.OrdinalIgnoreCase);
	if (bytes >= 0)
	{
		value = value[..bytes];
	}

	value = value.Trim().Trim('"');
	if (value.EndsWith(".", StringComparison.Ordinal))
	{
		value = value[..^1];
	}

	return value.Length == 0 ? null : value;
}

static SubmittedDumpClose? ExtractSubmittedDumpClose(string line)
{
	const string marker = "submitted=";
	const string bytesMarker = " bytes=";
	int start = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
	if (start < 0)
	{
		return null;
	}

	int pathStart = start + marker.Length;
	int bytesStart = line.IndexOf(bytesMarker, pathStart, StringComparison.OrdinalIgnoreCase);
	if (bytesStart < 0)
	{
		return null;
	}

	string path = line[pathStart..bytesStart].Trim().Trim('"');
	if (path.Length == 0)
	{
		return null;
	}

	int valueStart = bytesStart + bytesMarker.Length;
	int valueEnd = valueStart;
	while (valueEnd < line.Length && (char.IsDigit(line[valueEnd]) || line[valueEnd] == ','))
	{
		valueEnd++;
	}
	if (valueEnd == valueStart)
	{
		return null;
	}

	string byteText = line[valueStart..valueEnd].Replace(",", string.Empty, StringComparison.Ordinal);
	return long.TryParse(byteText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedBytes)
		? new SubmittedDumpClose(path, parsedBytes)
		: null;
}

static string NormalizePath(string path)
{
	return Path.GetFullPath(path.Trim().Trim('"')).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}

static string FindRepoRoot(string startDirectory)
{
	var directory = new DirectoryInfo(startDirectory);
	while (directory != null)
	{
		if (File.Exists(Path.Combine(directory.FullName, "MacMirrorReceiver.csproj")))
		{
			return directory.FullName;
		}

		directory = directory.Parent;
	}

	throw new InvalidOperationException("Could not locate repo root containing MacMirrorReceiver.csproj.");
}

static CommandResult RunDotnet(string workingDirectory, params string[] arguments)
{
	Console.WriteLine(">>> dotnet " + string.Join(" ", arguments.Select(QuoteIfNeeded)));
	var startInfo = new ProcessStartInfo
	{
		FileName = "dotnet",
		WorkingDirectory = workingDirectory,
		UseShellExecute = false,
		RedirectStandardOutput = true,
		RedirectStandardError = true
	};
	foreach (string argument in arguments)
	{
		startInfo.ArgumentList.Add(argument);
	}

	using var process = new Process { StartInfo = startInfo };
	process.OutputDataReceived += (_, e) =>
	{
		if (e.Data != null)
		{
			Console.WriteLine(e.Data);
		}
	};
	process.ErrorDataReceived += (_, e) =>
	{
		if (e.Data != null)
		{
			Console.Error.WriteLine(e.Data);
		}
	};

	process.Start();
	process.BeginOutputReadLine();
	process.BeginErrorReadLine();
	process.WaitForExit();
	Console.WriteLine("<<< exit " + process.ExitCode.ToString(CultureInfo.InvariantCulture));
	Console.WriteLine();
	return new CommandResult(process.ExitCode);
}

static string QuoteIfNeeded(string value)
{
	return value.Any(char.IsWhiteSpace) ? "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"" : value;
}

internal sealed record CommandResult(int ExitCode);

internal sealed record DumpMatchResult(bool Pass, string Message);

internal sealed record SubmittedDumpClose(string Path, long Bytes);
