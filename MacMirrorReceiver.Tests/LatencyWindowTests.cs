using System;
using System.Diagnostics;
using MacMirrorReceiver.Video;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class LatencyWindowTests
{
	private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);

	[Fact]
	public void Record_CompletesAWindowWithPercentilesAfterTheDuration()
	{
		var window = new LatencyWindow(TimeSpan.FromSeconds(1));
		long start = Ticks(10);

		for (int i = 1; i <= 19; i++)
		{
			Assert.Null(window.Record(i, start + Ticks(0.05 * (i - 1))));
		}
		LatencyWindowSnapshot? completed = window.Record(20, start + Ticks(1.0));

		Assert.NotNull(completed);
		Assert.True(completed.Value.Completed);
		Assert.Equal(20, completed.Value.SampleCount);
		Assert.Equal(10, completed.Value.P50Milliseconds);
		Assert.Equal(19, completed.Value.P95Milliseconds);
		Assert.Equal(20, completed.Value.MaxMilliseconds);
		Assert.Equal(0, completed.Value.WarmupSamplesSkipped);
		Assert.InRange(completed.Value.WindowSeconds, 0.99, 1.01);
	}

	[Fact]
	public void BeginWarmup_SkipsSamplesUntilTheGraceElapsesFromTheFirstSample()
	{
		var window = new LatencyWindow(TimeSpan.FromSeconds(1), warmupGrace: TimeSpan.FromSeconds(0.5));
		long start = Ticks(100);
		window.BeginWarmup();

		// The grace clock starts at the first sample, however late it arrives.
		Assert.Null(window.Record(400, start));
		Assert.Null(window.Record(300, start + Ticks(0.4)));
		Assert.Null(window.Record(10, start + Ticks(0.6)));
		Assert.Null(window.Record(20, start + Ticks(1.1)));
		LatencyWindowSnapshot? completed = window.Record(30, start + Ticks(1.6));

		Assert.NotNull(completed);
		Assert.Equal(3, completed.Value.SampleCount);
		Assert.Equal(20, completed.Value.P50Milliseconds);
		Assert.Equal(30, completed.Value.MaxMilliseconds);
		Assert.Equal(2, completed.Value.WarmupSamplesSkipped);
		Assert.Equal(400, completed.Value.WarmupMaxMilliseconds);
	}

	[Fact]
	public void GetCurrentOrLastSnapshot_ReportsInProgressThenLastCompleted()
	{
		var window = new LatencyWindow(TimeSpan.FromSeconds(1));
		long start = Ticks(5);

		Assert.False(window.GetCurrentOrLastSnapshot(start).HasSamples);
		window.Record(50, start);
		LatencyWindowSnapshot inProgress = window.GetCurrentOrLastSnapshot(start + Ticks(0.25));
		Assert.True(inProgress.HasSamples);
		Assert.False(inProgress.Completed);
		Assert.Equal(1, inProgress.SampleCount);

		window.Record(70, start + Ticks(1.0));
		LatencyWindowSnapshot last = window.GetCurrentOrLastSnapshot(start + Ticks(1.5));
		Assert.True(last.Completed);
		Assert.Equal(2, last.SampleCount);
		Assert.Equal(70, last.MaxMilliseconds);
	}

	[Fact]
	public void Record_ClampsNegativeLatenciesToZero()
	{
		var window = new LatencyWindow(TimeSpan.FromSeconds(1));
		long start = Ticks(1);

		window.Record(-25, start);
		LatencyWindowSnapshot? completed = window.Record(-5, start + Ticks(1.0));

		Assert.NotNull(completed);
		Assert.Equal(0, completed.Value.MaxMilliseconds);
	}

	[Fact]
	public void Reset_ClearsSamplesWarmupAndLastSnapshot()
	{
		var window = new LatencyWindow(TimeSpan.FromSeconds(1));
		long start = Ticks(1);
		window.Record(5, start);
		window.Record(6, start + Ticks(1.0));

		window.Reset();

		Assert.False(window.GetCurrentOrLastSnapshot(start + Ticks(2.0)).HasSamples);
	}
}
