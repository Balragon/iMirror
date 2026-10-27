using System;
using System.Collections.Generic;
using MacMirrorReceiver;
using MacMirrorReceiver.Video;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class LatestFrameMailboxTests
{
	[Fact]
	public void OfferSchedulesOneDrainAndDrainPresentsTheLatestFrame()
	{
		int scheduled = 0;
		var mailbox = new LatestFrameMailbox(() => scheduled++);
		var released = new List<int>();
		VideoFrame first = Frame(1, released);
		VideoFrame second = Frame(2, released);

		Assert.False(mailbox.Offer(first));
		Assert.True(mailbox.Offer(second));

		Assert.Equal(1, scheduled);
		Assert.Equal(1, mailbox.DrainQueued);
		Assert.Equal(new[] { 1 }, released); // first was replaced and released
		Assert.Equal(1, mailbox.PendingCount);

		var presented = new List<int>();
		mailbox.Drain(f => presented.Add(f.Width), _ => throw new InvalidOperationException());

		Assert.Equal(new[] { 2 }, presented);
		Assert.Equal(0, mailbox.DrainQueued);
		Assert.Equal(0, mailbox.PendingCount);
	}

	[Fact]
	public void DrainReschedulesWhenAFrameArrivesWhilePresenting()
	{
		int scheduled = 0;
		var mailbox = new LatestFrameMailbox(() => scheduled++);
		var released = new List<int>();
		mailbox.Offer(Frame(1, released));
		Assert.Equal(1, scheduled);

		mailbox.Drain(
			_ => mailbox.Offer(Frame(2, released)), // arrives mid-present; the drain flag is still set
			_ => { });

		Assert.Equal(2, scheduled);
		Assert.Equal(1, mailbox.DrainQueued);
		Assert.Equal(1, mailbox.PendingCount);
	}

	[Fact]
	public void D3DFramesHaveTheirOwnSlotAndAreDisposedWhenReplacedOrReleased()
	{
		var mailbox = new LatestFrameMailbox(() => { });
		var first = new FakeD3DFrame();
		var second = new FakeD3DFrame();

		Assert.False(mailbox.Offer(first));
		Assert.True(mailbox.Offer(second));
		Assert.True(first.Disposed);
		Assert.False(second.Disposed);

		mailbox.ReleasePendingD3DFrame();
		Assert.True(second.Disposed);
		Assert.Equal(0, mailbox.PendingCount);
	}

	[Fact]
	public void ReleasePendingSoftwareFrameReturnsItsBuffer()
	{
		var mailbox = new LatestFrameMailbox(() => { });
		var released = new List<int>();
		mailbox.Offer(Frame(7, released));

		mailbox.ReleasePendingSoftwareFrame();

		Assert.Equal(new[] { 7 }, released);
		Assert.Equal(0, mailbox.PendingCount);
	}

	private static VideoFrame Frame(int id, List<int> released) => new VideoFrame
	{
		Width = id,
		Height = 1,
		Buffer = new byte[4],
		ReturnBuffer = _ => released.Add(id)
	};

	private sealed class FakeD3DFrame : IHighResolutionD3DFrame
	{
		public bool Disposed { get; private set; }
		public int Width => 1;
		public int Height => 1;
		public int Fps => 30;
		public int SubresourceIndex => 0;
		public long ReceivedTick => 0;
		public long DecodedTick => 0;
		public ulong SourceTimestampNanos => 0;
		public void Dispose() => Disposed = true;
	}
}
