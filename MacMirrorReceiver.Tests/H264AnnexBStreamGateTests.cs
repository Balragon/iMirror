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

	private static byte[] SpsPps()
	{
		return new byte[]
		{
			0x00, 0x00, 0x00, 0x01, 0x67, 0x42, 0x00, 0x1F,
			0x00, 0x00, 0x00, 0x01, 0x68, 0xCE, 0x06, 0xE2
		};
	}

	private static byte[] Idr()
	{
		return new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65, 0x88, 0x84, 0x00 };
	}
}
