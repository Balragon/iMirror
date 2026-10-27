using System;

namespace MacMirrorReceiver;

/// <summary>
/// Rate-limits audio pipeline start attempts after a failure. Without it, a session whose
/// audio cannot start (no default render device under RDP, missing ffmpeg.exe, unsupported
/// format) re-created the WASAPI client and tried to spawn ffmpeg on every incoming audio
/// frame, about a hundred times per second, each with a full stack trace in the log.
/// </summary>
internal sealed class AudioStartBackoff
{
	public static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(5);
	public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(60);

	private int _consecutiveFailures;
	private long _nextAttemptTick = long.MinValue;

	public int ConsecutiveFailures => _consecutiveFailures;

	/// <summary>True when a start may be attempted at <paramref name="nowTick"/> (ms clock).</summary>
	public bool ShouldAttempt(long nowTick)
	{
		return nowTick >= _nextAttemptTick;
	}

	/// <summary>Records a failed start and returns the delay before the next attempt.</summary>
	public TimeSpan RecordFailure(long nowTick)
	{
		_consecutiveFailures++;
		double multiplier = Math.Pow(2, Math.Min(_consecutiveFailures - 1, 10));
		TimeSpan delay = TimeSpan.FromMilliseconds(Math.Min(InitialDelay.TotalMilliseconds * multiplier, MaxDelay.TotalMilliseconds));
		_nextAttemptTick = nowTick + (long)delay.TotalMilliseconds;
		return delay;
	}

	/// <summary>Clears the backoff after a successful start or when a new session begins.</summary>
	public void Reset()
	{
		_consecutiveFailures = 0;
		_nextAttemptTick = long.MinValue;
	}
}
