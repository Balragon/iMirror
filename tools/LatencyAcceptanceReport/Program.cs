using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

if (args.Length < 1)
{
	Console.Error.WriteLine("Usage: LatencyAcceptanceReport <iMirror.log> [p95-target-ms=150] [minimum-minutes=30] [required-reconnects=0] [require-stable-advertise=false] [require-high-resolution-d3d=false]");
	return 2;
}

string logPath = args[0];
long p95TargetMs = args.Length >= 2 && long.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedTarget)
	? parsedTarget
	: 150L;
double minimumMinutes = args.Length >= 3 && double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedMinutes)
	? parsedMinutes
	: 30.0;
int requiredReconnects = args.Length >= 4 && int.TryParse(args[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedReconnects)
	? parsedReconnects
	: 0;
bool requireStableAdvertise = args.Length >= 5 && bool.TryParse(args[4], out bool parsedRequireStableAdvertise)
	&& parsedRequireStableAdvertise;
bool requireHighResolutionD3D = args.Length >= 6 && bool.TryParse(args[5], out bool parsedRequireHighResolutionD3D)
	&& parsedRequireHighResolutionD3D;

if (!File.Exists(logPath))
{
	Console.Error.WriteLine("Log file not found: " + logPath);
	return 2;
}

LogSignals signals = ReadLogSignals(logPath);
List<LatencyWindow> windows = signals.LatencyWindows;
if (windows.Count == 0)
{
	Console.WriteLine("FAIL: no Presentation latency window lines found.");
	return 1;
}

double totalWindowSeconds = windows.Sum(window => window.Seconds);
double timestampSpanSeconds = CalculateTimestampSpanSeconds(windows);
double evidenceSeconds = Math.Max(totalWindowSeconds, timestampSpanSeconds);
LatencyWindow worstP95 = windows.OrderByDescending(window => window.P95Milliseconds).First();
LatencyWindow worstMax = windows.OrderByDescending(window => window.MaxMilliseconds).First();
int longestNonDecreasingMaxStreak = CalculateLongestNonDecreasingMaxStreak(windows);

bool durationPass = evidenceSeconds >= minimumMinutes * 60.0;
bool p95Pass = windows.All(window => window.P95Milliseconds < p95TargetMs);
bool maxTrendPass = longestNonDecreasingMaxStreak < 6;
bool corruptionPass = signals.CorruptionLines.Count == 0;
bool reconnectPass = signals.ReconnectAttempts >= requiredReconnects;
bool stableAdvertisePass = !requireStableAdvertise || signals.StableAdvertiseLines > 0;
bool highResolutionD3DPass = !requireHighResolutionD3D ||
	(signals.HighResolutionD3DPathActiveLines > 0 &&
		signals.HighResolutionD3DMultithreadProtectedLines > 0 &&
		signals.HighResolutionD3DFirstTextureLines > 0 &&
		signals.HighResolutionD3DFailureLines.Count == 0);
bool pass = durationPass && p95Pass && maxTrendPass && corruptionPass && reconnectPass && stableAdvertisePass && highResolutionD3DPass;

Console.WriteLine((pass ? "PASS" : "FAIL") + ": iMirror acceptance report");
Console.WriteLine($"windows={windows.Count:N0}, evidenceDuration={FormatDuration(evidenceSeconds)}, targetDuration={minimumMinutes.ToString("0.###", CultureInfo.InvariantCulture)}min");
Console.WriteLine($"p95Target={p95TargetMs}ms, worstP95={worstP95.P95Milliseconds}ms at {FormatTimestamp(worstP95.Timestamp)}");
Console.WriteLine($"worstMax={worstMax.MaxMilliseconds}ms at {FormatTimestamp(worstMax.Timestamp)}");
Console.WriteLine($"longestNonDecreasingMaxStreak={longestNonDecreasingMaxStreak:N0} window(s)");
Console.WriteLine($"reconnectAttempts={signals.ReconnectAttempts:N0}, requiredReconnects={requiredReconnects:N0}");
Console.WriteLine($"stableAdvertiseLines={signals.StableAdvertiseLines:N0}, experimentalAdvertiseLines={signals.ExperimentalAdvertiseLines:N0}");
Console.WriteLine($"highResolutionD3DPathActiveLines={signals.HighResolutionD3DPathActiveLines:N0}, d3d11MultithreadProtectedLines={signals.HighResolutionD3DMultithreadProtectedLines:N0}, highResolutionD3DFirstTextureLines={signals.HighResolutionD3DFirstTextureLines:N0}, highResolutionD3DFailureLines={signals.HighResolutionD3DFailureLines.Count:N0}, required={requireHighResolutionD3D}");
Console.WriteLine($"corruptionLines={signals.CorruptionLines.Count:N0}");
Console.WriteLine($"duration={(durationPass ? "pass" : "fail")}, p95={(p95Pass ? "pass" : "fail")}, maxTrend={(maxTrendPass ? "pass" : "fail")}, corruption={(corruptionPass ? "pass" : "fail")}, reconnect={(reconnectPass ? "pass" : "fail")}, stableAdvertise={(stableAdvertisePass ? "pass" : "fail")}, highResolutionD3D={(highResolutionD3DPass ? "pass" : "fail")}");
foreach (string corruptionLine in signals.CorruptionLines.Take(5))
{
	Console.WriteLine("corruption: " + corruptionLine);
}
if (signals.CorruptionLines.Count > 5)
{
	Console.WriteLine($"corruption: ... {signals.CorruptionLines.Count - 5:N0} more line(s)");
}
foreach (string failureLine in signals.HighResolutionD3DFailureLines.Take(5))
{
	Console.WriteLine("highResolutionD3D: " + failureLine);
}
if (signals.HighResolutionD3DFailureLines.Count > 5)
{
	Console.WriteLine($"highResolutionD3D: ... {signals.HighResolutionD3DFailureLines.Count - 5:N0} more line(s)");
}

return pass ? 0 : 1;

static LogSignals ReadLogSignals(string logPath)
{
	var windows = new List<LatencyWindow>();
	var pattern = new Regex(
		@"^\[(?<timestamp>[^\]]+)\].*Presentation latency window:\s+\w+\s+(?<seconds>[0-9.]+)s\s+n=(?<samples>[0-9,]+)\s+p50=(?<p50>[0-9]+)ms\s+p95=(?<p95>[0-9]+)ms\s+max=(?<max>[0-9]+)ms",
		RegexOptions.Compiled | RegexOptions.CultureInvariant);
	var corruptionLines = new List<string>();
	int reconnectAttempts = 0;
	int stableAdvertiseLines = 0;
	int experimentalAdvertiseLines = 0;
	int highResolutionD3DPathActiveLines = 0;
	int highResolutionD3DMultithreadProtectedLines = 0;
	int highResolutionD3DFirstTextureLines = 0;
	var highResolutionD3DFailureLines = new List<string>();

	foreach (string line in File.ReadLines(logPath))
	{
		if (line.Contains("Reconnecting to ", StringComparison.OrdinalIgnoreCase))
		{
			reconnectAttempts++;
		}
		if (line.Contains("advertising stable 1920x1080", StringComparison.OrdinalIgnoreCase))
		{
			stableAdvertiseLines++;
		}
		if (line.Contains("experimental quality display advertise", StringComparison.OrdinalIgnoreCase))
		{
			experimentalAdvertiseLines++;
		}
		if (line.Contains("High-resolution D3D path active", StringComparison.OrdinalIgnoreCase))
		{
			highResolutionD3DPathActiveLines++;
			if (line.Contains("d3d11MultithreadProtected=True", StringComparison.OrdinalIgnoreCase))
			{
				highResolutionD3DMultithreadProtectedLines++;
			}
		}
		if (line.Contains("Media Foundation D3D11 decoder produced first NV12 texture", StringComparison.OrdinalIgnoreCase))
		{
			highResolutionD3DFirstTextureLines++;
		}
		if (IsHighResolutionD3DFailureLine(line))
		{
			highResolutionD3DFailureLines.Add(line);
		}
		if (IsCorruptionLine(line))
		{
			corruptionLines.Add(line);
		}

		Match match = pattern.Match(line);
		if (!match.Success)
		{
			continue;
		}

		if (!DateTimeOffset.TryParse(match.Groups["timestamp"].Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset timestamp))
		{
			timestamp = DateTimeOffset.MinValue;
		}

		windows.Add(new LatencyWindow(
			timestamp,
			ParseDouble(match.Groups["seconds"].Value),
			ParseLong(match.Groups["samples"].Value),
			ParseLong(match.Groups["p50"].Value),
			 ParseLong(match.Groups["p95"].Value),
			 ParseLong(match.Groups["max"].Value)));
	}

	return new LogSignals(
		windows,
		corruptionLines,
		reconnectAttempts,
		stableAdvertiseLines,
		experimentalAdvertiseLines,
		highResolutionD3DPathActiveLines,
		highResolutionD3DMultithreadProtectedLines,
		highResolutionD3DFirstTextureLines,
		highResolutionD3DFailureLines);
}

static bool IsCorruptionLine(string line)
{
	return line.Contains("non-existing PPS", StringComparison.OrdinalIgnoreCase) ||
		line.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase) ||
		line.Contains("mb_width/height overflow", StringComparison.OrdinalIgnoreCase) ||
		line.Contains("reference count overflow", StringComparison.OrdinalIgnoreCase) ||
		(line.Contains("sps_id", StringComparison.OrdinalIgnoreCase) && line.Contains("out of range", StringComparison.OrdinalIgnoreCase)) ||
		line.Contains("non-intra slice in an IDR", StringComparison.OrdinalIgnoreCase);
}

static bool IsHighResolutionD3DFailureLine(string line)
{
	return line.Contains("Media Foundation D3D11 path failed", StringComparison.OrdinalIgnoreCase) ||
		line.Contains("Media Foundation D3D11 decoder produced no NV12 texture within", StringComparison.OrdinalIgnoreCase) ||
		line.Contains("Media Foundation produced output sample without matching NV12 D3D11 texture", StringComparison.OrdinalIgnoreCase) ||
		line.Contains("Media Foundation D3D11 decoder stopped:", StringComparison.OrdinalIgnoreCase) ||
		line.Contains("High-resolution D3D decoder faulted:", StringComparison.OrdinalIgnoreCase) ||
		line.Contains("High-resolution D3D stall:", StringComparison.OrdinalIgnoreCase) ||
		line.Contains("High-resolution D3D present failed:", StringComparison.OrdinalIgnoreCase) ||
		line.Contains("High-resolution D3D output geometry changed:", StringComparison.OrdinalIgnoreCase);
}

static long ParseLong(string value)
{
	return long.Parse(value.Replace(",", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture);
}

static double ParseDouble(string value)
{
	return double.Parse(value.Replace(",", string.Empty, StringComparison.Ordinal), CultureInfo.InvariantCulture);
}

static double CalculateTimestampSpanSeconds(IReadOnlyList<LatencyWindow> windows)
{
	DateTimeOffset first = windows.First().Timestamp;
	DateTimeOffset last = windows.Last().Timestamp;
	if (first == DateTimeOffset.MinValue || last == DateTimeOffset.MinValue || last <= first)
	{
		return 0.0;
	}

	return (last - first).TotalSeconds;
}

static int CalculateLongestNonDecreasingMaxStreak(IReadOnlyList<LatencyWindow> windows)
{
	int longest = 1;
	int current = 1;
	for (int i = 1; i < windows.Count; i++)
	{
		if (windows[i].MaxMilliseconds >= windows[i - 1].MaxMilliseconds)
		{
			current++;
			longest = Math.Max(longest, current);
		}
		else
		{
			current = 1;
		}
	}

	return longest;
}

static string FormatTimestamp(DateTimeOffset timestamp)
{
	return timestamp == DateTimeOffset.MinValue
		? "unknown"
		: timestamp.ToString("O", CultureInfo.InvariantCulture);
}

static string FormatDuration(double seconds)
{
	return TimeSpan.FromSeconds(Math.Max(0.0, seconds)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
}

internal sealed record LatencyWindow(
	DateTimeOffset Timestamp,
	double Seconds,
	long Samples,
	long P50Milliseconds,
	long P95Milliseconds,
	long MaxMilliseconds);

internal sealed record LogSignals(
	List<LatencyWindow> LatencyWindows,
	List<string> CorruptionLines,
	int ReconnectAttempts,
	int StableAdvertiseLines,
	int ExperimentalAdvertiseLines,
	int HighResolutionD3DPathActiveLines,
	int HighResolutionD3DMultithreadProtectedLines,
	int HighResolutionD3DFirstTextureLines,
	List<string> HighResolutionD3DFailureLines);
