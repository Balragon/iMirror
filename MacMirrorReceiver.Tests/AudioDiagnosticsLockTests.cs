using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AudioDiagnosticsLockTests
{
	[Fact]
	public async Task ResolveDiagnostics_DoesNotWaitForAudioPipelineStartup()
	{
		// StartLocked holds the host's gate while WASAPI and FFmpeg start. The UI thread renders
		// diagnostics while the AirPlay data thread waits on it to apply the stream config, so the
		// diagnostics read must not take that lock.
		var host = new AudioSessionHost(() => 1, () => 150);
		object gate = typeof(AudioSessionHost)
			.GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!
			.GetValue(host)!;

		using var held = new ManualResetEventSlim();
		using var release = new ManualResetEventSlim();
		var holder = new Thread(() =>
		{
			lock (gate)
			{
				held.Set();
				release.Wait();
			}
		});
		holder.Start();
		try
		{
			Assert.True(held.Wait(TimeSpan.FromSeconds(5)));
			string diagnostics = await Task.Run(host.ResolveDiagnostics).WaitAsync(TimeSpan.FromSeconds(2));
			Assert.StartsWith("audio: not started, received=0", diagnostics);
		}
		finally
		{
			release.Set();
			holder.Join();
		}
	}

	[Fact]
	public void IgnoresAudioFromAnotherGeneration()
	{
		var host = new AudioSessionHost(() => 7, () => 150);
		int diagnostics = 0;
		host.DiagnosticsChanged += () => diagnostics++;

		host.HandleStreamStarted(44100, 2, 480, generation: 3);
		host.HandleFrame(new byte[16], 0, 0, generation: 0);

		Assert.Equal(0, diagnostics);
		Assert.Equal("audio: not started, received=0", host.ResolveDiagnostics());
	}

	[Fact]
	public void ResetForNewConnectionClearsCountersAndStatus()
	{
		var host = new AudioSessionHost(() => 1, () => 150);
		host.ResetForNewConnection();
		Assert.Equal("audio: waiting for stream, received=0", host.ResolveDiagnostics());
		Assert.False(host.FirewallWarningActive);
	}
}
