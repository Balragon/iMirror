using System;
using System.Threading;

namespace MacMirrorReceiver.Video;

// Counters for writes into a decoder's ffmpeg stdin: count, latest and maximum duration, and
// stalls (writes at or above the threshold), with stall reports limited to one per second.
internal sealed class PipeWriteMetrics
{
	private readonly long _stallThresholdMs;
	private long _written;
	private long _latestMilliseconds;
	private long _maxMilliseconds;
	private long _stalls;
	private long _lastStallReportTick;

	public PipeWriteMetrics(long stallThresholdMs)
	{
		_stallThresholdMs = stallThresholdMs;
	}

	public long Written => Interlocked.Read(ref _written);

	public long LatestMilliseconds => Interlocked.Read(ref _latestMilliseconds);

	public long MaxMilliseconds => Interlocked.Read(ref _maxMilliseconds);

	public long Stalls => Interlocked.Read(ref _stalls);

	// Records one completed write. Returns true when it was a stall that should be reported now.
	public bool Record(long writeMs)
	{
		Interlocked.Increment(ref _written);
		Interlocked.Exchange(ref _latestMilliseconds, writeMs);
		long previousMax;
		do
		{
			previousMax = Interlocked.Read(ref _maxMilliseconds);
			if (writeMs <= previousMax)
			{
				break;
			}
		}
		while (Interlocked.CompareExchange(ref _maxMilliseconds, writeMs, previousMax) != previousMax);

		if (writeMs < _stallThresholdMs)
		{
			return false;
		}

		Interlocked.Increment(ref _stalls);
		long now = Environment.TickCount64;
		long previous = Interlocked.Read(ref _lastStallReportTick);
		if (now - previous < 1000)
		{
			return false;
		}
		Interlocked.Exchange(ref _lastStallReportTick, now);
		return true;
	}
}
