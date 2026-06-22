using System;
using System.Collections.Generic;
using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class CleanupGuardsTests
{
	[Fact]
	public void RunStep_LogsAndContinues_WhenStepThrows()
	{
		var log = new List<string>();
		var ranAfterThrow = false;

		CleanupGuards.RunStep("throwing step", () => throw new InvalidOperationException("boom"), log.Add);
		CleanupGuards.RunStep("next step", () => ranAfterThrow = true, log.Add);

		Assert.True(ranAfterThrow);
		Assert.Contains(log, message => message.Contains("throwing step", StringComparison.Ordinal) &&
			message.Contains("boom", StringComparison.Ordinal));
	}
}
