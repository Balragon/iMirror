using System;
using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AudioStartBackoffTests
{
	[Fact]
	public void ShouldAttempt_AllowsFirstAttemptImmediately()
	{
		var backoff = new AudioStartBackoff();

		Assert.True(backoff.ShouldAttempt(0));
		Assert.True(backoff.ShouldAttempt(long.MinValue + 1));
	}

	[Fact]
	public void RecordFailure_BlocksRetriesUntilTheDelayElapses()
	{
		var backoff = new AudioStartBackoff();

		TimeSpan delay = backoff.RecordFailure(1_000);

		Assert.Equal(AudioStartBackoff.InitialDelay, delay);
		Assert.False(backoff.ShouldAttempt(1_000));
		Assert.False(backoff.ShouldAttempt(1_000 + (long)delay.TotalMilliseconds - 1));
		Assert.True(backoff.ShouldAttempt(1_000 + (long)delay.TotalMilliseconds));
	}

	[Fact]
	public void RecordFailure_DoublesUpToTheMaximum()
	{
		var backoff = new AudioStartBackoff();

		Assert.Equal(TimeSpan.FromSeconds(5), backoff.RecordFailure(0));
		Assert.Equal(TimeSpan.FromSeconds(10), backoff.RecordFailure(0));
		Assert.Equal(TimeSpan.FromSeconds(20), backoff.RecordFailure(0));
		Assert.Equal(TimeSpan.FromSeconds(40), backoff.RecordFailure(0));
		Assert.Equal(TimeSpan.FromSeconds(60), backoff.RecordFailure(0));
		Assert.Equal(TimeSpan.FromSeconds(60), backoff.RecordFailure(0));
		Assert.Equal(6, backoff.ConsecutiveFailures);
	}

	[Fact]
	public void Reset_AllowsAnImmediateAttemptAgain()
	{
		var backoff = new AudioStartBackoff();
		backoff.RecordFailure(5_000);
		backoff.RecordFailure(5_000);

		backoff.Reset();

		Assert.True(backoff.ShouldAttempt(5_000));
		Assert.Equal(0, backoff.ConsecutiveFailures);
		Assert.Equal(AudioStartBackoff.InitialDelay, backoff.RecordFailure(5_000));
	}
}
