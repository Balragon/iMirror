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

const double largeGapThresholdSeconds = 30.0;

double totalWindowSeconds = windows.Sum(window => window.Seconds);
double timestampSpanSeconds = CalculateTimestampSpanSeconds(windows);
double evidenceSeconds = Math.Max(totalWindowSeconds, timestampSpanSeconds);
LatencyWindow worstP95 = windows.OrderByDescending(window => window.P95Milliseconds).First();
LatencyWindow worstMax = windows.OrderByDescending(window => window.MaxMilliseconds).First();
int longestNonDecreasingMaxStreak = CalculateLongestNonDecreasingMaxStreak(windows);

// Spike distribution: worst-window numbers alone hide how pervasive spikes are. Surface how
// many windows actually breached the p95 target and how many had a severe max spike, so a
// reviewer can tell an isolated blip from a recurring stutter without re-reading the raw log.
long severeMaxTargetMs = p95TargetMs * 2;
int p95BreachWindows = windows.Count(window => window.P95Milliseconds >= p95TargetMs);
int severeMaxWindows = windows.Count(window => window.MaxMilliseconds >= severeMaxTargetMs);

// Contiguity: the duration gate is satisfied by either summed window seconds or the
// first->last timestamp span, so a hand-selected or spliced clean slice can pass while the
// removed sections hid spikes. Detect large gaps between consecutive window timestamps so a
// non-contiguous capture is flagged instead of silently accepted. (Reported, not gated:
// a kept-alive process across reconnects legitimately produces warmup gaps.)
(double maxGapSeconds, int largeGapCount) = CalculateTimestampGaps(windows, largeGapThresholdSeconds);
bool contiguousEvidence = largeGapCount == 0;

bool durationPass = evidenceSeconds >= minimumMinutes * 60.0;
bool p95Pass = windows.All(window => window.P95Milliseconds < p95TargetMs);
// Magnitude ceiling on the tail. p95Pass gates the 95th percentile but leaves occasional
// single-frame stutters (300ms-1s+) ungated; for a mirroring product those are user-visible.
// This replaced the old non-decreasing-max-streak FAIL gate (now a WARN), which mis-fired on
// benign sub-threshold upward drift. A magnitude gate catches real tail spikes regardless of
// trend shape and ignores low-value drift.
bool severeMaxPass = severeMaxWindows == 0;
bool maxTrendWarn = longestNonDecreasingMaxStreak >= 6;
bool corruptionPass = signals.CorruptionLines.Count == 0;
// Crash gate: any unhandled-exception marker fails the run. Backward
// compatible — a clean log has zero crash lines, so existing acceptance
// captures are unaffected; only a genuinely crashed soak run fails here.
bool crashPass = signals.CrashLines.Count == 0;
bool reconnectPass = signals.ReconnectAttempts >= requiredReconnects;
bool stableAdvertisePass = !requireStableAdvertise || signals.StableAdvertiseLines > 0;
bool highResolutionD3DPass = !requireHighResolutionD3D ||
	(signals.HighResolutionD3DPathActiveLines > 0 &&
		signals.HighResolutionD3DMultithreadProtectedLines > 0 &&
		signals.HighResolutionD3DFirstTextureLines > 0 &&
		signals.HighResolutionD3DFailureLines.Count == 0);
bool pass = durationPass && p95Pass && severeMaxPass && corruptionPass && crashPass && reconnectPass && stableAdvertisePass && highResolutionD3DPass;

Console.WriteLine((pass ? "PASS" : "FAIL") + ": iMirror acceptance report");
Console.WriteLine($"windows={windows.Count:N0}, evidenceDuration={FormatDuration(evidenceSeconds)}, targetDuration={minimumMinutes.ToString("0.###", CultureInfo.InvariantCulture)}min");
Console.WriteLine($"p95Target={p95TargetMs}ms, worstP95={worstP95.P95Milliseconds}ms at {FormatTimestamp(worstP95.Timestamp)}");
Console.WriteLine($"worstMax={worstMax.MaxMilliseconds}ms at {FormatTimestamp(worstMax.Timestamp)}");
Console.WriteLine($"p95BreachWindows={p95BreachWindows:N0} of {windows.Count:N0} (>= {p95TargetMs}ms), severeMaxWindows={severeMaxWindows:N0} (>= {severeMaxTargetMs}ms)");
Console.WriteLine($"maxWindowGap={FormatDuration(maxGapSeconds)}, largeGaps={largeGapCount:N0} (> {largeGapThresholdSeconds:0}s), contiguousEvidence={contiguousEvidence}");
Console.WriteLine($"longestNonDecreasingMaxStreak={longestNonDecreasingMaxStreak:N0} window(s)");
Console.WriteLine($"reconnectAttempts={signals.ReconnectAttempts:N0}, requiredReconnects={requiredReconnects:N0}");
Console.WriteLine($"stableAdvertiseLines={signals.StableAdvertiseLines:N0}, experimentalAdvertiseLines={signals.ExperimentalAdvertiseLines:N0}");
Console.WriteLine($"highResolutionD3DPathActiveLines={signals.HighResolutionD3DPathActiveLines:N0}, d3d11MultithreadProtectedLines={signals.HighResolutionD3DMultithreadProtectedLines:N0}, highResolutionD3DFirstTextureLines={signals.HighResolutionD3DFirstTextureLines:N0}, highResolutionD3DFailureLines={signals.HighResolutionD3DFailureLines.Count:N0}, required={requireHighResolutionD3D}");
Console.WriteLine($"corruptionLines={signals.CorruptionLines.Count:N0}, crashLines={signals.CrashLines.Count:N0}");
Console.WriteLine($"duration={(durationPass ? "pass" : "fail")}, p95={(p95Pass ? "pass" : "fail")}, severeMax={(severeMaxPass ? "pass" : "fail")}, corruption={(corruptionPass ? "pass" : "fail")}, crash={(crashPass ? "pass" : "fail")}, maxTrend={(maxTrendWarn ? "warn" : "pass")}, reconnect={(reconnectPass ? "pass" : "fail")}, stableAdvertise={(stableAdvertisePass ? "pass" : "fail")}, highResolutionD3D={(highResolutionD3DPass ? "pass" : "fail")}");
if (!contiguousEvidence)
{
	Console.WriteLine($"WARN: evidence is not time-contiguous ({largeGapCount:N0} gap(s) over {largeGapThresholdSeconds:0}s, largest {FormatDuration(maxGapSeconds)}). A hand-selected or spliced clean slice can mask spikes; prefer a single continuous capture for product-release acceptance.");
}
if (maxTrendWarn)
{
	Console.WriteLine($"WARN: max latency increased for {longestNonDecreasingMaxStreak:N0} consecutive window(s). Inspect the spike distribution and queue/stall markers before treating this as a product latency failure.");
}
foreach (string corruptionLine in signals.CorruptionLines.Take(5))
{
	Console.WriteLine("corruption: " + corruptionLine);
}
if (signals.CorruptionLines.Count > 5)
{
	Console.WriteLine($"corruption: ... {signals.CorruptionLines.Count - 5:N0} more line(s)");
}
foreach (string crashLine in signals.CrashLines.Take(5))
{
	Console.WriteLine("crash: " + crashLine);
}
if (signals.CrashLines.Count > 5)
{
	Console.WriteLine($"crash: ... {signals.CrashLines.Count - 5:N0} more line(s)");
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
	var crashLines = new List<string>();
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
		if (IsCrashLine(line))
		{
			crashLines.Add(line);
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
		crashLines,
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

static bool IsCrashLine(string line)
{
	// Unhandled-exception markers written by App.cs. Their presence means the
	// session crashed (or a UI-thread exception escaped), which is a hard soak
	// failure regardless of how clean the latency windows look.
	return line.Contains("Unhandled domain exception:", StringComparison.OrdinalIgnoreCase) ||
		line.Contains("Dispatcher exception:", StringComparison.OrdinalIgnoreCase);
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

static (double MaxGapSeconds, int LargeGapCount) CalculateTimestampGaps(IReadOnlyList<LatencyWindow> windows, double largeGapThresholdSeconds)
{
	double maxGap = 0.0;
	int largeGaps = 0;
	for (int i = 1; i < windows.Count; i++)
	{
		DateTimeOffset previous = windows[i - 1].Timestamp;
		DateTimeOffset current = windows[i].Timestamp;
		if (previous == DateTimeOffset.MinValue || current == DateTimeOffset.MinValue || current <= previous)
		{
			continue;
		}

		double gap = (current - previous).TotalSeconds;
		if (gap > maxGap)
		{
			maxGap = gap;
		}
		if (gap > largeGapThresholdSeconds)
		{
			largeGaps++;
		}
	}

	return (maxGap, largeGaps);
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
	List<string> CrashLines,
	int ReconnectAttempts,
	int StableAdvertiseLines,
	int ExperimentalAdvertiseLines,
	int HighResolutionD3DPathActiveLines,
	int HighResolutionD3DMultithreadProtectedLines,
	int HighResolutionD3DFirstTextureLines,
	List<string> HighResolutionD3DFailureLines);
