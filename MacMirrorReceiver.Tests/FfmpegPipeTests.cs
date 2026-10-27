using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MacMirrorReceiver.Video;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class FfmpegPipeTests
{
	[Fact]
	public void InputQueue_DequeuesInOrderAndTracksBytes()
	{
		using var queue = CreateQueue(maxItems: 4, maxBytes: 100);

		queue.Enqueue(new byte[10], NeverConsulted);
		queue.Enqueue(new byte[20], NeverConsulted);

		Assert.Equal(2, queue.Count);
		Assert.Equal(30, queue.Bytes);
		Assert.True(queue.TryDequeue(out byte[]? first));
		Assert.Equal(10, first!.Length);
		Assert.Equal(20, queue.Bytes);
		Assert.True(queue.TryDequeue(out byte[]? second));
		Assert.Equal(20, second!.Length);
		Assert.False(queue.TryDequeue(out _));
		Assert.Equal(0, queue.Bytes);
		Assert.Equal(2, queue.Accepted);
		Assert.Equal(0, queue.Dropped);
	}

	[Fact]
	public void InputQueue_FlushPolicyClearsQueueAndKeepsIncoming_WhenItemCapReached()
	{
		using var queue = CreateQueue(maxItems: 3, maxBytes: 100);
		for (int i = 0; i < 3; i++)
		{
			queue.Enqueue(new byte[5], NeverConsulted);
		}
		byte[] incoming = new byte[7];

		PipeEnqueueResult result = queue.Enqueue(incoming, _ => true);

		Assert.Equal(PipeEnqueueOutcome.FlushedAndEnqueued, result.Outcome);
		Assert.Equal(3, result.Flushed);
		Assert.Equal(1, result.QueuedAfter);
		Assert.Equal(7, queue.Bytes);
		Assert.Equal(4, queue.Accepted);
		Assert.Equal(3, queue.Dropped);
		Assert.True(queue.TryDequeue(out byte[]? kept));
		Assert.Same(incoming, kept);
	}

	[Fact]
	public void InputQueue_DropPolicyKeepsQueueAndDropsIncoming_WhenByteCapReached()
	{
		using var queue = CreateQueue(maxItems: 10, maxBytes: 50);
		queue.Enqueue(new byte[40], NeverConsulted);

		PipeEnqueueResult result = queue.Enqueue(new byte[11], _ => false);

		Assert.Equal(PipeEnqueueOutcome.DroppedIncoming, result.Outcome);
		Assert.Equal(0, result.Flushed);
		Assert.Equal(1, result.QueuedAfter);
		Assert.Equal(1, queue.Count);
		Assert.Equal(40, queue.Bytes);
		Assert.Equal(1, queue.Accepted);
		Assert.Equal(1, queue.Dropped);
	}

	[Fact]
	public void InputQueue_ClearCountsRemovedItemsAsDropped()
	{
		using var queue = CreateQueue(maxItems: 10, maxBytes: 100);
		queue.Enqueue(new byte[1], NeverConsulted);
		queue.Enqueue(new byte[2], NeverConsulted);

		Assert.Equal(2, queue.Clear());

		Assert.Equal(0, queue.Count);
		Assert.Equal(0, queue.Bytes);
		Assert.Equal(2, queue.Dropped);
		Assert.Equal(0, queue.Clear());
	}

	[Fact]
	public async Task RunInputLoop_WritesQueuedItemsInOrderAndStopsWhenWriteFails()
	{
		using var queue = CreateQueue(maxItems: 10, maxBytes: 100);
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
		var written = new List<int>();
		Task loop = FfmpegPipe.RunInputLoopAsync(queue, item =>
		{
			written.Add(item.Length);
			return Task.FromResult(item.Length != 3);
		}, cts.Token);

		queue.Enqueue(new byte[1], NeverConsulted);
		queue.Enqueue(new byte[2], NeverConsulted);
		queue.Enqueue(new byte[3], NeverConsulted);
		queue.Enqueue(new byte[4], NeverConsulted);

		await loop.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.Equal(new[] { 1, 2, 3 }, written);
		Assert.Equal(1, queue.Count);
	}

	[Fact]
	public async Task RunInputLoop_EndsQuietlyOnCancellation()
	{
		using var queue = CreateQueue(maxItems: 10, maxBytes: 100);
		using var cts = new CancellationTokenSource();
		Task loop = FfmpegPipe.RunInputLoopAsync(queue, _ => Task.FromResult(true), cts.Token);

		cts.Cancel();

		await loop.WaitAsync(TimeSpan.FromSeconds(5));
		Assert.True(loop.IsCompletedSuccessfully);
	}

	[Fact]
	public async Task ReadFixedSizeFrames_DeliversWholeFramesThenThrowsOnPartialFrame()
	{
		byte[] data = Enumerable.Range(0, 3 * 4 + 2).Select(i => (byte)i).ToArray();
		using var stream = new TrickleStream(data, chunkSize: 3);
		var frames = new List<byte[]>();

		await Assert.ThrowsAsync<EndOfStreamException>(() =>
			FfmpegPipe.ReadFixedSizeFramesAsync(stream, 4, buffer => frames.Add(buffer[..4]), CancellationToken.None));

		Assert.Equal(3, frames.Count);
		Assert.Equal(new byte[] { 0, 1, 2, 3 }, frames[0]);
		Assert.Equal(new byte[] { 4, 5, 6, 7 }, frames[1]);
		Assert.Equal(new byte[] { 8, 9, 10, 11 }, frames[2]);
	}

	[Fact]
	public void WriteMetrics_TracksLatestMaxAndCount()
	{
		var metrics = new PipeWriteMetrics(stallThresholdMs: 35);

		Assert.False(metrics.Record(5));
		Assert.False(metrics.Record(12));
		Assert.False(metrics.Record(3));

		Assert.Equal(3, metrics.Written);
		Assert.Equal(3, metrics.LatestMilliseconds);
		Assert.Equal(12, metrics.MaxMilliseconds);
		Assert.Equal(0, metrics.Stalls);
	}

	[Fact]
	public void WriteMetrics_ReportsFirstStallAndThrottlesTheRest()
	{
		var metrics = new PipeWriteMetrics(stallThresholdMs: 35);

		Assert.True(metrics.Record(35));
		Assert.False(metrics.Record(80));
		Assert.False(metrics.Record(40));

		Assert.Equal(3, metrics.Stalls);
		Assert.Equal(80, metrics.MaxMilliseconds);
	}

	[Fact]
	public void ElapsedMilliseconds_IsZeroForReversedTicks()
	{
		Assert.Equal(0, FfmpegPipe.ElapsedMilliseconds(100, 50));
		Assert.Equal(1000, FfmpegPipe.ElapsedMilliseconds(0, System.Diagnostics.Stopwatch.Frequency));
	}

	private static PipeInputQueue<byte[]> CreateQueue(int maxItems, long maxBytes)
	{
		return new PipeInputQueue<byte[]>(maxItems, maxBytes, item => item.Length);
	}

	private static bool NeverConsulted(byte[] item)
	{
		throw new InvalidOperationException("The overflow policy must only run when the queue is full.");
	}

	// Returns at most chunkSize bytes per read, like a pipe delivering ffmpeg output in pieces.
	private sealed class TrickleStream : MemoryStream
	{
		private readonly int _chunkSize;

		public TrickleStream(byte[] data, int chunkSize)
			: base(data)
		{
			_chunkSize = chunkSize;
		}

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
		{
			return base.ReadAsync(buffer[..Math.Min(buffer.Length, _chunkSize)], cancellationToken);
		}
	}
}
