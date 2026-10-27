using System;
using System.Diagnostics;
using MacMirrorReceiver.Video;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AudioTimingQueueTests
{
	private static readonly long Tick100ms = Stopwatch.Frequency / 10;

	[Fact]
	public void PairsWrittenFramesWithReturnedPcmInOrder()
	{
		var queue = new AudioTimingQueue(TimeSpan.FromMilliseconds(500));
		long now = Stopwatch.GetTimestamp();
		Assert.Equal(0, queue.Enqueue(Timing(1), now));
		Assert.Equal(0, queue.Enqueue(Timing(2), now + Tick100ms / 10));
		Assert.Equal(0, queue.Enqueue(Timing(3), now + Tick100ms / 5));

		Assert.Equal(1, queue.DequeueOrEmpty().Sequence);
		Assert.Equal(2, queue.DequeueOrEmpty().Sequence);
		Assert.Equal(3, queue.DequeueOrEmpty().Sequence);
		Assert.Equal(AudioFrameTiming.Empty, queue.DequeueOrEmpty());
	}

	[Fact]
	public void DropsStaleEntriesWhenTheWriterResumesAfterAnIdleGap()
	{
		var queue = new AudioTimingQueue(TimeSpan.FromMilliseconds(500));
		long now = Stopwatch.GetTimestamp();
		queue.Enqueue(Timing(1), now);
		queue.Enqueue(Timing(2), now + Tick100ms / 10);
		Assert.Equal(1, queue.DequeueOrEmpty().Sequence); // ffmpeg returned frame 1 and swallowed frame 2

		int dropped = queue.Enqueue(Timing(3), now + 6 * Tick100ms); // 600 ms later, audio resumes

		Assert.Equal(1, dropped);
		Assert.Equal(3, queue.DequeueOrEmpty().Sequence);
	}

	[Fact]
	public void KeepsPendingEntriesAcrossAShortGap()
	{
		var queue = new AudioTimingQueue(TimeSpan.FromMilliseconds(500));
		long now = Stopwatch.GetTimestamp();
		queue.Enqueue(Timing(1), now);

		int dropped = queue.Enqueue(Timing(2), now + 4 * Tick100ms);

		Assert.Equal(0, dropped);
		Assert.Equal(1, queue.DequeueOrEmpty().Sequence);
		Assert.Equal(2, queue.DequeueOrEmpty().Sequence);
	}

	[Fact]
	public void CapsGrowthWhileTheWriterIsNeverIdle()
	{
		var queue = new AudioTimingQueue(TimeSpan.FromMilliseconds(500));
		long now = Stopwatch.GetTimestamp();
		int dropped = 0;
		for (int i = 0; i < AudioTimingQueue.MaxEntries + 10; i++)
		{
			dropped += queue.Enqueue(Timing((ushort)i), now + i);
		}

		Assert.Equal(10, dropped);
		Assert.Equal(AudioTimingQueue.MaxEntries, queue.Count);
		Assert.Equal(10, queue.DequeueOrEmpty().Sequence);
	}

	private static AudioFrameTiming Timing(ushort sequence) => new AudioFrameTiming(sequence * 480u, sequence, 1000 + sequence, 0);
}
