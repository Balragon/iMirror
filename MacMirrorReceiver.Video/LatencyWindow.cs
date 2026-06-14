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
	long MaxMilliseconds);

internal sealed class LatencyWindow
{
	private readonly object _gate = new object();
	private readonly TimeSpan _duration;
	private readonly List<long> _samples = new List<long>();
	private long _windowStartTick;
	private LatencyWindowSnapshot _lastCompleted;

	public LatencyWindow(TimeSpan duration)
	{
		_duration = duration;
	}

	public LatencyWindowSnapshot? Record(long milliseconds, long nowTick)
	{
		lock (_gate)
		{
			if (_windowStartTick == 0)
			{
				_windowStartTick = nowTick;
			}

			_samples.Add(Math.Max(0L, milliseconds));
			double seconds = ElapsedSeconds(_windowStartTick, nowTick);
			if (seconds < _duration.TotalSeconds)
			{
				return null;
			}

			LatencyWindowSnapshot completed = BuildSnapshot(_samples, seconds, completed: true);
			_lastCompleted = completed;
			_samples.Clear();
			_windowStartTick = nowTick;
			return completed;
		}
	}

	public LatencyWindowSnapshot GetCurrentOrLastSnapshot(long nowTick)
	{
		lock (_gate)
		{
			if (_samples.Count > 0 && _windowStartTick != 0)
			{
				return BuildSnapshot(_samples, ElapsedSeconds(_windowStartTick, nowTick), completed: false);
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
			_lastCompleted = default;
		}
	}

	private static LatencyWindowSnapshot BuildSnapshot(IReadOnlyList<long> samples, double seconds, bool completed)
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
			MaxMilliseconds: sorted[^1]);
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
