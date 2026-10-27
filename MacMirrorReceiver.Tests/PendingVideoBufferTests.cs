using System;
using System.Linq;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class PendingVideoBufferTests
{
	[Fact]
	public void DrainReturnsPacketsInArrivalOrderAndEmptiesTheBuffer()
	{
		var buffer = new PendingVideoBuffer(maxPackets: 8, maxBytes: 1024);
		Assert.Equal(0, buffer.Enqueue(Packet(1, 10)));
		Assert.Equal(0, buffer.Enqueue(Packet(2, 20)));
		Assert.Equal(0, buffer.Enqueue(Packet(3, 30)));
		Assert.Equal(3, buffer.Count);
		Assert.Equal(60, buffer.Bytes);

		PendingVideoPayload[] drained = buffer.Drain();

		Assert.Equal(new ulong[] { 1, 2, 3 }, drained.Select(p => p.SourceTimestampNanos));
		Assert.Equal(0, buffer.Count);
		Assert.Equal(0, buffer.Bytes);
		Assert.Empty(buffer.Drain());
	}

	[Fact]
	public void DropsTheOldestPacketsWhenThePacketCapIsExceeded()
	{
		var buffer = new PendingVideoBuffer(maxPackets: 3, maxBytes: 1024);
		for (ulong i = 1; i <= 3; i++)
		{
			Assert.Equal(0, buffer.Enqueue(Packet(i, 10)));
		}

		Assert.Equal(1, buffer.Enqueue(Packet(4, 10)));
		Assert.Equal(1, buffer.Enqueue(Packet(5, 10)));

		Assert.Equal(2, buffer.Dropped);
		Assert.Equal(new ulong[] { 3, 4, 5 }, buffer.Drain().Select(p => p.SourceTimestampNanos));
	}

	[Fact]
	public void DropsAsManyOldPacketsAsNeededWhenTheByteCapIsExceeded()
	{
		var buffer = new PendingVideoBuffer(maxPackets: 100, maxBytes: 100);
		buffer.Enqueue(Packet(1, 40));
		buffer.Enqueue(Packet(2, 40));

		int dropped = buffer.Enqueue(Packet(3, 90)); // 170 bytes queued; must drop 1 and 2 to fit

		Assert.Equal(2, dropped);
		Assert.Equal(90, buffer.Bytes);
		Assert.Equal(new ulong[] { 3 }, buffer.Drain().Select(p => p.SourceTimestampNanos));
	}

	[Fact]
	public void ClearDiscardsPacketsButKeepsTheDroppedCountUntilReset()
	{
		var buffer = new PendingVideoBuffer(maxPackets: 1, maxBytes: 1024);
		buffer.Enqueue(Packet(1, 10));
		buffer.Enqueue(Packet(2, 10));
		Assert.Equal(1, buffer.Dropped);

		buffer.Clear();
		Assert.Equal(0, buffer.Count);
		Assert.Equal(0, buffer.Bytes);
		Assert.Equal(1, buffer.Dropped);

		buffer.ResetDroppedCount();
		Assert.Equal(0, buffer.Dropped);
	}

	[Fact]
	public void RejectsNonPositiveCaps()
	{
		Assert.Throws<ArgumentOutOfRangeException>(() => new PendingVideoBuffer(0, 10));
		Assert.Throws<ArgumentOutOfRangeException>(() => new PendingVideoBuffer(10, 0));
	}

	private static PendingVideoPayload Packet(ulong timestamp, int size) => new PendingVideoPayload(new byte[size], timestamp, (long)timestamp);
}
