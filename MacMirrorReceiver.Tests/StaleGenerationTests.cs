using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class StaleGenerationTests
{
	[Theory]
	[InlineData(1, 1, true)]
	[InlineData(1, 2, false)]
	[InlineData(0, 1, false)]
	public void IsCurrentGeneration_RequiresNonZeroMatch(int payloadGeneration, int currentGeneration, bool expected)
	{
		Assert.Equal(expected, MainWindow.IsCurrentGeneration(payloadGeneration, currentGeneration));
	}
}
