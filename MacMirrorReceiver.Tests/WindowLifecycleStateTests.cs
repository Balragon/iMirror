using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class WindowLifecycleStateTests
{
	[Fact]
	public void ShouldHideOnClose_IsFalse_AfterExplicitExit()
	{
		var state = new WindowLifecycleState();

		Assert.True(state.ShouldHideOnClose());

		state.MarkExplicitExit();

		Assert.False(state.ShouldHideOnClose());
	}

	[Fact]
	public void ConsumeFirstHideNotification_ReturnsTrueOnlyOnce()
	{
		var state = new WindowLifecycleState();

		Assert.True(state.ConsumeFirstHideNotification());
		Assert.False(state.ConsumeFirstHideNotification());
	}
}
