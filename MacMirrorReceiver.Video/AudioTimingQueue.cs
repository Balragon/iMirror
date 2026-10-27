using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace MacMirrorReceiver.Video;

/// <summary>
/// Pairs the AAC frames written to ffmpeg's stdin with the PCM frames that come back on its
/// stdout, in order. ffmpeg emits one PCM frame per written AAC frame, so a plain FIFO is
/// right until ffmpeg swallows a frame (decode error): from then on every PCM frame would be
/// paired with the previous frame's timing and the latency estimate would be off by one
/// frame forever, or by the whole pause after an audio-only TEARDOWN/SETUP cycle.
///
/// The queue realigns at the one point where the pairing is known: when the writer has been
/// idle longer than <see cref="IdleRealignThreshold"/>, every PCM frame for the written input
/// has long since been read, so anything still queued is stale and is dropped before the
/// next frame is enqueued. A frame that ffmpeg is genuinely still holding across such a gap
/// comes back with <see cref="AudioFrameTiming.Empty"/>, which the consumers treat as
/// "no receive time" rather than as a latency of zero.
/// </summary>
internal sealed class AudioTimingQueue
{
	public static readonly TimeSpan IdleRealignThreshold = TimeSpan.FromMilliseconds(500);
	// Far above ffmpeg's real in-flight depth (a few frames); guards against unbounded growth
	// while the writer is never idle.
	public const int MaxEntries = 256;

	private readonly object _gate = new object();
	private readonly Queue<AudioFrameTiming> _queue = new Queue<AudioFrameTiming>();
	private readonly long _idleRealignTicks;
	private long _lastEnqueueTick = long.MinValue;

	public AudioTimingQueue()
		: this(IdleRealignThreshold)
	{
	}

	internal AudioTimingQueue(TimeSpan idleRealignThreshold)
	{
		_idleRealignTicks = (long)(idleRealignThreshold.TotalSeconds * Stopwatch.Frequency);
	}

	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _queue.Count;
			}
		}
	}

	/// <summary>
	/// Records the timing of a frame just written to ffmpeg. <paramref name="nowTick"/> is the
	/// Stopwatch tick of the write. Returns the number of stale entries dropped (normally 0).
	/// </summary>
	public int Enqueue(AudioFrameTiming timing, long nowTick)
	{
		lock (_gate)
		{
			int dropped = 0;
			if (_queue.Count > 0 && _lastEnqueueTick != long.MinValue && nowTick - _lastEnqueueTick > _idleRealignTicks)
			{
				dropped = _queue.Count;
				_queue.Clear();
			}

			_queue.Enqueue(timing);
			while (_queue.Count > MaxEntries)
			{
				_queue.Dequeue();
				dropped++;
			}

			_lastEnqueueTick = nowTick;
			return dropped;
		}
	}

	/// <summary>Timing for the next PCM frame read from ffmpeg, or Empty when none is pending.</summary>
	public AudioFrameTiming DequeueOrEmpty()
	{
		lock (_gate)
		{
			return _queue.Count > 0 ? _queue.Dequeue() : AudioFrameTiming.Empty;
		}
	}
}
