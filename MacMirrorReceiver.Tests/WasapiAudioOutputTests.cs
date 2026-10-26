using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
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

	[Fact]
	public void Submit_DoesNotDeadlock_WhenPlaybackThreadFaultsWhileRestartStopsOutput()
	{
		var factory = new FakeWasapiOutputDeviceFactory();
		var notifier = new ManualAudioEndpointRestartNotifier();

		using var output = new WasapiAudioOutput(48000, 2, factory, notifier);

		output.Submit(CreateFrame());
		notifier.RaiseRestartRequested("render endpoint removed");
		factory.Devices[0].ArmPlaybackThreadFaultOnStop(new InvalidOperationException("device invalidated"));
		output.Submit(CreateFrame());
		factory.Devices[0].WaitForPlaybackThread();
		output.Submit(CreateFrame());

		Assert.False(factory.Devices[0].StopJoinTimedOut, "Stop() waited on a playback thread that was blocked on the output lock.");
		Assert.Equal(2, factory.Devices.Count);
		Assert.Equal(1, factory.Devices[0].StopCalls);
		Assert.Equal(1, factory.Devices[0].DisposeCalls);
		Assert.Equal(1, output.OutputRestarts);
		Assert.Equal(3, output.SubmittedFrames);
	}

	[Fact]
	public void Dispose_DoesNotDeadlock_WhenPlaybackThreadFaultsWhileStoppingOutput()
	{
		var factory = new FakeWasapiOutputDeviceFactory();
		var notifier = new ManualAudioEndpointRestartNotifier();
		var output = new WasapiAudioOutput(48000, 2, factory, notifier);

		output.Submit(CreateFrame());
		factory.Devices[0].ArmPlaybackThreadFaultOnStop(new InvalidOperationException("device invalidated"));
		output.Dispose();
		factory.Devices[0].WaitForPlaybackThread();

		Assert.False(factory.Devices[0].StopJoinTimedOut, "Stop() waited on a playback thread that was blocked on the output lock.");
		Assert.Single(factory.Devices);
		Assert.Equal(1, factory.Devices[0].StopCalls);
		Assert.Equal(1, factory.Devices[0].DisposeCalls);
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
		private static readonly TimeSpan StopJoinTimeout = TimeSpan.FromSeconds(2);
		private EventHandler<StoppedEventArgs>? _faultHandler;
		private Exception? _fault;
		private Thread? _playbackThread;

		public bool StopJoinTimedOut { get; private set; }

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
			EventHandler<StoppedEventArgs>? handler = _faultHandler;
			Exception? fault = _fault;
			_faultHandler = null;
			if (handler == null || fault == null)
			{
				return;
			}

			// Like NAudio's WasapiOut.Stop(): the playback thread raises PlaybackStopped
			// on its way out and Stop() joins it.
			_playbackThread = new Thread(() => handler(this, new StoppedEventArgs(fault))) { IsBackground = true };
			_playbackThread.Start();
			if (!_playbackThread.Join(StopJoinTimeout))
			{
				StopJoinTimedOut = true;
			}
		}

		public void Dispose()
		{
			DisposeCalls++;
		}

		public void RaisePlaybackStopped(Exception exception)
		{
			PlaybackStopped?.Invoke(this, new StoppedEventArgs(exception));
		}

		// Models a device fault whose PlaybackStopped raise is already in flight on the
		// playback thread (handler captured now) when the receiver calls Stop().
		public void ArmPlaybackThreadFaultOnStop(Exception exception)
		{
			_faultHandler = PlaybackStopped;
			_fault = exception;
		}

		public void WaitForPlaybackThread()
		{
			_playbackThread?.Join();
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
