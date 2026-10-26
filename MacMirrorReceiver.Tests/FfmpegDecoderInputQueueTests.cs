using System.Collections.Generic;
using MacMirrorReceiver.Video;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class FfmpegDecoderInputQueueTests
{
	[Fact]
	public void EnqueueInput_DropsIncomingNonKeyframeAndKeepsQueuedGop_WhenPacketCapReached()
	{
		using FfmpegDecoder decoder = CreateDecoder();
		FillToPacketCap(decoder);

		decoder.EnqueueInput(PFrameSlice(), 0UL, 0L);

		Assert.Equal(FfmpegDecoder.MaxQueuedInputPackets, decoder.QueuedInputPackets);
		Assert.Equal(FfmpegDecoder.MaxQueuedInputPackets, decoder.AcceptedInputPackets);
		Assert.Equal(1, decoder.DroppedInputPackets);
	}

	[Fact]
	public void EnqueueInput_DropsIncomingNonKeyframe_WhenByteCapReached()
	{
		using FfmpegDecoder decoder = CreateDecoder();
		byte[] large = new byte[FfmpegDecoder.MaxQueuedInputBytes];
		decoder.EnqueueInput(large, 0UL, 0L);

		decoder.EnqueueInput(PFrameSlice(), 0UL, 0L);

		Assert.Equal(1, decoder.QueuedInputPackets);
		Assert.Equal(FfmpegDecoder.MaxQueuedInputBytes, decoder.QueuedInputBytes);
		Assert.Equal(1, decoder.DroppedInputPackets);
	}

	[Fact]
	public void EnqueueInput_FlushesQueueAndResumesFromIncomingKeyframe_WhenPacketCapReached()
	{
		using FfmpegDecoder decoder = CreateDecoder();
		var messages = new List<string>();
		decoder.StatusChanged += messages.Add;
		FillToPacketCap(decoder);
		byte[] idr = Idr();

		decoder.EnqueueInput(idr, 0UL, 0L);

		Assert.Equal(1, decoder.QueuedInputPackets);
		Assert.Equal(idr.Length, decoder.QueuedInputBytes);
		Assert.Equal(FfmpegDecoder.MaxQueuedInputPackets + 1, decoder.AcceptedInputPackets);
		Assert.Equal(FfmpegDecoder.MaxQueuedInputPackets, decoder.DroppedInputPackets);
		string message = Assert.Single(messages);
		Assert.Equal($"Decoder input overflow: flushed {FfmpegDecoder.MaxQueuedInputPackets} queued packet(s); resuming from incoming keyframe.", message);
	}

	[Fact]
	public void EnqueueInput_ReportsFirstOverflowDropImmediatelyAndThrottlesTheRest()
	{
		using FfmpegDecoder decoder = CreateDecoder();
		var messages = new List<string>();
		decoder.StatusChanged += messages.Add;
		FillToPacketCap(decoder);

		for (int i = 0; i < 5; i++)
		{
			decoder.EnqueueInput(PFrameSlice(), 0UL, 0L);
		}

		Assert.Equal(5, decoder.DroppedInputPackets);
		string message = Assert.Single(messages);
		Assert.Equal($"Decoder input overflow: dropped 1 incoming non-keyframe(s) since last report and preserved queued GOP (queued={FfmpegDecoder.MaxQueuedInputPackets}).", message);
	}

	private static FfmpegDecoder CreateDecoder()
	{
		return new FfmpegDecoder(1920, 1080, 60, 1920, 60);
	}

	private static void FillToPacketCap(FfmpegDecoder decoder)
	{
		for (int i = 0; i < FfmpegDecoder.MaxQueuedInputPackets; i++)
		{
			decoder.EnqueueInput(PFrameSlice(), 0UL, 0L);
		}
		Assert.Equal(0, decoder.DroppedInputPackets);
	}

	private static byte[] Idr()
	{
		return new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x00 };
	}

	// Non-IDR coded slice (nal_unit_type 1) used as a stand-in for a P-frame.
	private static byte[] PFrameSlice()
	{
		return new byte[] { 0x00, 0x00, 0x00, 0x01, 0x61, 0x9A, 0x12, 0x34 };
	}
}
