using System;
using System.Threading;

namespace MacMirrorReceiver;

/// <summary>
/// Lets at most one caller through per interval, from any thread, without a lock. Used for the
/// per-second render stats, the 10-second video health line and the stale-frame drop lines so
/// a hot path never logs on every frame.
/// </summary>
internal sealed class ThrottledLogGate
{
	// Far in the past, so a fresh or reset gate lets its first caller through whatever the clock reads.
	private const long NeverPassed = long.MinValue / 2;

	private readonly long _intervalMilliseconds;
	private long _lastPassTick = NeverPassed;

	public ThrottledLogGate(TimeSpan interval)
	{
		_intervalMilliseconds = (long)interval.TotalMilliseconds;
	}

	/// <summary>True for the first caller once <c>interval</c> has elapsed since the last pass (or ever).</summary>
	public bool TryPass()
	{
		return TryPass(Environment.TickCount64);
	}

	internal bool TryPass(long nowMilliseconds)
	{
		long previous = Interlocked.Read(ref _lastPassTick);
		if (nowMilliseconds - previous < _intervalMilliseconds)
		{
			return false;
		}
		return Interlocked.CompareExchange(ref _lastPassTick, nowMilliseconds, previous) == previous;
	}

	/// <summary>Lets the next caller through regardless of the interval.</summary>
	public void Reset()
	{
		Interlocked.Exchange(ref _lastPassTick, NeverPassed);
	}
}
