using System;
using System.Collections.Generic;
using System.Diagnostics;
using MacMirrorReceiver.Audio;
using MacMirrorReceiver.Video;
using NAudio.Wave;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class WasapiAudioOutputTests
{
	[Fact]
	public void Submit_RestartsOutput_WhenEndpointNotifierRequestsRestart()
	{
		var factory = new FakeWasapiOutputDeviceFactory();
		var notifier = new ManualAudioEndpointRestartNotifier();

		using var output = new WasapiAudioOutput(48000, 2, factory, notifier);

		output.Submit(CreateFrame());
		notifier.RaiseRestartRequested("render endpoint removed");
		output.Submit(CreateFrame());

		Assert.Equal(2, factory.Devices.Count);
		Assert.Equal(1, factory.Devices[0].StopCalls);
		Assert.Equal(1, factory.Devices[0].DisposeCalls);
		Assert.Equal(1, factory.Devices[1].InitCalls);
		Assert.Equal(1, factory.Devices[1].PlayCalls);
		Assert.Equal(1, output.OutputRestarts);
		Assert.Equal(1, output.BufferClears);
		Assert.Equal(2, output.SubmittedFrames);
	}

	[Fact]
	public void Submit_RestartsOutput_WhenPlaybackStopsWithException()
	{
		var factory = new FakeWasapiOutputDeviceFactory();
		var notifier = new ManualAudioEndpointRestartNotifier();

		using var output = new WasapiAudioOutput(48000, 2, factory, notifier);

		output.Submit(CreateFrame());
		factory.Devices[0].RaisePlaybackStopped(new InvalidOperationException("device invalidated"));
		output.Submit(CreateFrame());

		Assert.Equal(2, factory.Devices.Count);
		Assert.Equal(1, factory.Devices[0].StopCalls);
		Assert.Equal(1, factory.Devices[0].DisposeCalls);
		Assert.Equal(1, factory.Devices[1].InitCalls);
		Assert.Equal(1, factory.Devices[1].PlayCalls);
		Assert.Equal(1, output.OutputRestarts);
		Assert.Equal(2, output.SubmittedFrames);
	}

	private static AudioPcmFrame CreateFrame()
	{
		const int sampleRate = 48000;
		const int channels = 2;
		const int samplesPerFrame = 480;
		byte[] buffer = new byte[samplesPerFrame * channels * sizeof(short)];
		return new AudioPcmFrame
		{
			Buffer = buffer,
			ByteCount = buffer.Length,
			SampleRate = sampleRate,
			Channels = channels,
			SamplesPerFrame = samplesPerFrame,
			ReceivedTick = Stopwatch.GetTimestamp()
		};
	}

	private sealed class FakeWasapiOutputDeviceFactory : IWasapiOutputDeviceFactory
	{
		public List<FakeWasapiOutputDevice> Devices { get; } = new List<FakeWasapiOutputDevice>();

		public IWasapiOutputDevice Create()
		{
			var device = new FakeWasapiOutputDevice();
			Devices.Add(device);
			return device;
		}
	}

	private sealed class FakeWasapiOutputDevice : IWasapiOutputDevice
	{
		public int InitCalls { get; private set; }

		public int PlayCalls { get; private set; }

		public int StopCalls { get; private set; }

		public int DisposeCalls { get; private set; }

		public event EventHandler<StoppedEventArgs>? PlaybackStopped;

		public void Init(IWaveProvider waveProvider)
		{
			InitCalls++;
		}

		public void Play()
		{
			PlayCalls++;
		}

		public void Stop()
		{
			StopCalls++;
		}

		public void Dispose()
		{
			DisposeCalls++;
		}

		public void RaisePlaybackStopped(Exception exception)
		{
			PlaybackStopped?.Invoke(this, new StoppedEventArgs(exception));
		}
	}

	private sealed class ManualAudioEndpointRestartNotifier : IAudioEndpointRestartNotifier
	{
		public event Action<string>? RestartRequested;

		public void RaiseRestartRequested(string reason)
		{
			RestartRequested?.Invoke(reason);
		}

		public void Dispose()
		{
		}
	}
}
