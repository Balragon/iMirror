using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MacMirrorReceiver.Video;

internal readonly record struct LatencyWindowSnapshot(
	bool HasSamples,
	bool Completed,
	int SampleCount,
	double WindowSeconds,
	long P50Milliseconds,
	long P95Milliseconds,
	long MaxMilliseconds,
	int WarmupSamplesSkipped,
	long WarmupMaxMilliseconds);

internal sealed class LatencyWindow
{
	private readonly object _gate = new object();
	private readonly TimeSpan _duration;
	private readonly TimeSpan _warmupGrace;
	private readonly List<long> _samples = new List<long>();
	private long _windowStartTick;
	private bool _warmupArmed;
	private long _warmupUntilTick;
	private int _warmupSamplesSkipped;
	private long _warmupMaxMs;
	private LatencyWindowSnapshot _lastCompleted;

	public LatencyWindow(TimeSpan duration, TimeSpan? warmupGrace = null)
	{
		_duration = duration;
		_warmupGrace = warmupGrace ?? TimeSpan.FromSeconds(1.5);
	}

	// Arm a warmup grace period after a (re)start or session refresh. Samples recorded during the
	// grace are excluded from the steady-state percentiles, because decoder/renderer spin-up,
	// decryptor backlog, and reconnect catch-up otherwise land in the first window and inflate its
	// p95/max. The grace clock starts on the FIRST sample after arming, not on wall-clock time
	// here: first-frame can be delayed many seconds (slow IDR/keyframe wait, FFmpeg probe), and a
	// wall-clock grace would expire before any frame arrives and isolate nothing. The skipped
	// samples are not hidden: their count and worst latency are carried into the first completed
	// window (WarmupSamplesSkipped / WarmupMaxMilliseconds).
	public void BeginWarmup()
	{
		lock (_gate)
		{
			_warmupArmed = true;
			_warmupUntilTick = 0L;
			_samples.Clear();
			_windowStartTick = 0L;
			_warmupSamplesSkipped = 0;
			_warmupMaxMs = 0L;
		}
	}

	public LatencyWindowSnapshot? Record(long milliseconds, long nowTick)
	{
		long clamped = Math.Max(0L, milliseconds);
		lock (_gate)
		{
			if (_warmupArmed)
			{
				if (_warmupUntilTick == 0L)
				{
					// First sample after arming: start the grace clock from actual frame flow.
					_warmupUntilTick = nowTick + (long)(_warmupGrace.TotalSeconds * Stopwatch.Frequency);
				}
				if (nowTick < _warmupUntilTick)
				{
					_warmupSamplesSkipped++;
					_warmupMaxMs = Math.Max(_warmupMaxMs, clamped);
					return null;
				}
				_warmupArmed = false;
				_warmupUntilTick = 0L;
			}

			if (_windowStartTick == 0)
			{
				_windowStartTick = nowTick;
			}

			_samples.Add(clamped);
			double seconds = ElapsedSeconds(_windowStartTick, nowTick);
			if (seconds < _duration.TotalSeconds)
			{
				return null;
			}

			LatencyWindowSnapshot completed = BuildSnapshot(_samples, seconds, completed: true, _warmupSamplesSkipped, _warmupMaxMs);
			_lastCompleted = completed;
			_samples.Clear();
			_windowStartTick = nowTick;
			_warmupSamplesSkipped = 0;
			_warmupMaxMs = 0L;
			return completed;
		}
	}

	public LatencyWindowSnapshot GetCurrentOrLastSnapshot(long nowTick)
	{
		lock (_gate)
		{
			if (_samples.Count > 0 && _windowStartTick != 0)
			{
				return BuildSnapshot(_samples, ElapsedSeconds(_windowStartTick, nowTick), completed: false, _warmupSamplesSkipped, _warmupMaxMs);
			}

			return _lastCompleted;
		}
	}

	public void Reset()
	{
		lock (_gate)
		{
			_samples.Clear();
			_windowStartTick = 0L;
			_warmupArmed = false;
			_warmupUntilTick = 0L;
			_warmupSamplesSkipped = 0;
			_warmupMaxMs = 0L;
			_lastCompleted = default;
		}
	}

	private static LatencyWindowSnapshot BuildSnapshot(IReadOnlyList<long> samples, double seconds, bool completed, int warmupSamplesSkipped, long warmupMaxMs)
	{
		if (samples.Count == 0)
		{
			return default;
		}

		var sorted = new List<long>(samples);
		sorted.Sort();
		return new LatencyWindowSnapshot(
			HasSamples: true,
			Completed: completed,
			SampleCount: sorted.Count,
			WindowSeconds: Math.Max(0.001, seconds),
			P50Milliseconds: Percentile(sorted, 50.0),
			P95Milliseconds: Percentile(sorted, 95.0),
			MaxMilliseconds: sorted[^1],
			WarmupSamplesSkipped: warmupSamplesSkipped,
			WarmupMaxMilliseconds: warmupMaxMs);
	}

	private static long Percentile(IReadOnlyList<long> sortedValues, double percentile)
	{
		if (sortedValues.Count == 0)
		{
			return 0L;
		}

		int index = (int)Math.Ceiling(sortedValues.Count * percentile / 100.0) - 1;
		return sortedValues[Math.Clamp(index, 0, sortedValues.Count - 1)];
	}

	private static double ElapsedSeconds(long startTick, long endTick)
	{
		if (endTick <= startTick)
		{
			return 0.0;
		}

		return (endTick - startTick) / (double)Stopwatch.Frequency;
	}
}
