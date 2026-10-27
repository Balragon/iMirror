using System;
using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class ThrottledLogGateTests
{
	[Fact]
	public void PassesOncePerInterval()
	{
		var gate = new ThrottledLogGate(TimeSpan.FromSeconds(1));
		Assert.True(gate.TryPass(1000));
		Assert.False(gate.TryPass(1500));
		Assert.False(gate.TryPass(1999));
		Assert.True(gate.TryPass(2000));
		Assert.False(gate.TryPass(2001));
	}

	[Fact]
	public void ResetLetsTheNextCallerThrough()
	{
		var gate = new ThrottledLogGate(TimeSpan.FromSeconds(10));
		Assert.True(gate.TryPass(5000));
		Assert.False(gate.TryPass(6000));
		gate.Reset();
		Assert.True(gate.TryPass(6000));
	}
}
