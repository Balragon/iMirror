using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AudioDiagnosticsLockTests
{
	[Fact]
	public async Task ResolveAudioDiagnostics_DoesNotWaitForAudioPipelineStartup()
	{
		// StartAudioPipelineLocked holds _audioGate while WASAPI and FFmpeg start. The UI thread renders
		// diagnostics while the AirPlay data thread waits on it to apply the stream config, so the
		// diagnostics read must not take that lock.
		var window = (MainWindow)RuntimeHelpers.GetUninitializedObject(typeof(MainWindow));
		object gate = new object();
		typeof(MainWindow)
			.GetField("_audioGate", BindingFlags.Instance | BindingFlags.NonPublic)!
			.SetValue(window, gate);

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
			string diagnostics = await Task.Run(window.ResolveAudioDiagnostics).WaitAsync(TimeSpan.FromSeconds(2));
			Assert.StartsWith("audio: ", diagnostics);
		}
		finally
		{
			release.Set();
			holder.Join();
		}
	}
}
