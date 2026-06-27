using System.Linq;
using MacMirrorReceiver.Video;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class H264AnnexBStreamGateTests
{
	[Fact]
	public void Process_BuffersParameterSetsAndPrependsThemToNextIdr()
	{
		var gate = new H264AnnexBStreamGate();
		byte[] parameterSets = SpsPps();
		byte[] idr = Idr();

		Assert.Null(gate.Process(parameterSets));
		byte[]? forwarded = gate.Process(idr);

		Assert.NotNull(forwarded);
		Assert.True(gate.IsStarted);
		Assert.Equal("prepended buffered SPS/PPS to keyframe", gate.LastDecision);
		Assert.Equal(parameterSets.Concat(idr).ToArray(), forwarded);
	}

	[Fact]
	public void Process_ForwardsInlineParameterSetsWithIdr()
	{
		var gate = new H264AnnexBStreamGate();
		byte[] keyframe = SpsPps().Concat(Idr()).ToArray();

		byte[]? forwarded = gate.Process(keyframe);

		Assert.NotNull(forwarded);
		Assert.True(gate.IsStarted);
		Assert.Equal("found SPS/PPS keyframe", gate.LastDecision);
		Assert.Equal(keyframe, forwarded);
	}

	[Fact]
	public void Process_PrependsSpsAndPpsWhenParameterSetsArriveInSeparatePackets()
	{
		var gate = new H264AnnexBStreamGate();
		byte[] sps = Sps();
		byte[] pps = Pps();
		byte[] idr = Idr();

		Assert.Null(gate.Process(sps));
		Assert.Null(gate.Process(pps));
		byte[]? forwarded = gate.Process(idr);

		Assert.NotNull(forwarded);
		Assert.True(gate.IsStarted);
		Assert.Equal("prepended buffered SPS/PPS to keyframe", gate.LastDecision);
		Assert.Equal(sps.Concat(pps).Concat(idr).ToArray(), forwarded);
	}

	[Fact]
	public void Process_DropsPacketWithoutAnnexBNalUnits()
	{
		var gate = new H264AnnexBStreamGate();

		byte[]? forwarded = gate.Process(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD });

		Assert.Null(forwarded);
		Assert.False(gate.IsStarted);
		Assert.Equal(1, gate.DroppedPackets);
		Assert.Equal("dropped packet without Annex B NAL units", gate.LastDecision);
	}

	[Fact]
	public void Process_DropsEmptyPayload()
	{
		var gate = new H264AnnexBStreamGate();

		byte[]? forwarded = gate.Process(new byte[0]);

		Assert.Null(forwarded);
		Assert.False(gate.IsStarted);
		Assert.Equal(1, gate.DroppedPackets);
		Assert.Equal("dropped packet without Annex B NAL units", gate.LastDecision);
	}

	[Fact]
	public void Process_StartsOnKeyframeWithMixedThreeAndFourByteStartCodes()
	{
		var gate = new H264AnnexBStreamGate();
		byte[] keyframe = MixedStartCodeKeyframe();

		byte[]? forwarded = gate.Process(keyframe);

		Assert.NotNull(forwarded);
		Assert.True(gate.IsStarted);
		Assert.Equal("found SPS/PPS keyframe", gate.LastDecision);
		Assert.Equal(keyframe, forwarded);
	}

	[Fact]
	public void RequireKeyframe_DropsNonKeyframesUntilNextIdr()
	{
		var gate = new H264AnnexBStreamGate();
		Assert.NotNull(gate.Process(SpsPps().Concat(Idr()).ToArray()));

		gate.RequireKeyframe();
		Assert.False(gate.IsStarted);

		byte[]? droppedSlice = gate.Process(PFrameSlice());
		Assert.Null(droppedSlice);
		Assert.False(gate.IsStarted);

		byte[] idr = Idr();
		byte[]? recovered = gate.Process(idr);

		Assert.NotNull(recovered);
		Assert.True(gate.IsStarted);
		// Buffered SPS/PPS from the first keyframe are re-prepended to the recovery IDR.
		Assert.Equal(SpsPps().Concat(idr).ToArray(), recovered);
	}

	[Fact]
	public void Process_ForwardsNonKeyframeSlicesOnceStarted()
	{
		var gate = new H264AnnexBStreamGate();
		Assert.NotNull(gate.Process(SpsPps().Concat(Idr()).ToArray()));

		byte[] slice = PFrameSlice();
		byte[]? forwarded = gate.Process(slice);

		Assert.NotNull(forwarded);
		Assert.Equal(slice, forwarded);
		Assert.Equal("forwarded NAL 1", gate.LastDecision);
		Assert.Equal(2, gate.ForwardedPackets);
	}

	[Fact]
	public void Process_KeepsForwardingAcrossRepeatedIdrKeyframes()
	{
		var gate = new H264AnnexBStreamGate();
		byte[] keyframe = SpsPps().Concat(Idr()).ToArray();

		Assert.NotNull(gate.Process(keyframe));
		Assert.NotNull(gate.Process(keyframe));

		Assert.True(gate.IsStarted);
		Assert.Equal(0, gate.DroppedPackets);
		Assert.Equal(2, gate.ForwardedPackets);
	}

	[Fact]
	public void Reset_ClearsStartedStateAndCounters()
	{
		var gate = new H264AnnexBStreamGate();
		gate.Process(SpsPps().Concat(Idr()).ToArray());

		gate.Reset();

		Assert.False(gate.IsStarted);
		Assert.Equal(0, gate.DroppedPackets);
		Assert.Equal(0, gate.ForwardedPackets);
		Assert.Equal("waiting for SPS/PPS keyframe", gate.LastDecision);
	}

	private static byte[] SpsPps()
	{
		return Sps().Concat(Pps()).ToArray();
	}

	private static byte[] Sps()
	{
		return new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x1F };
	}

	private static byte[] Pps()
	{
		return new byte[] { 0x00, 0x00, 0x00, 0x01, 0x68, 0xCE, 0x06, 0xE2 };
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

	// Keyframe whose NAL units use a mix of 4-byte (SPS) and 3-byte (PPS, IDR) start codes.
	private static byte[] MixedStartCodeKeyframe()
	{
		return new byte[]
		{
			0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x1F,
			0x00, 0x00, 0x01, 0x68, 0xCE, 0x06, 0xE2,
			0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x00
		};
	}
}
