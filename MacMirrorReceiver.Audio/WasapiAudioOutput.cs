using System;
using System.Threading;
using MacMirrorReceiver.Video;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MacMirrorReceiver.Audio;

public sealed class WasapiAudioOutput : IDisposable
{
	private const int WasapiLatencyMilliseconds = 80;
	private const int BufferMilliseconds = 1000;
	private const int LowBufferMilliseconds = 40;
	private const int LowBufferRecoveredMilliseconds = 90;
	private const int TargetBufferMilliseconds = 140;
	private const int HighBufferMilliseconds = 260;
	private const int MinSyncTargetLatencyMilliseconds = 100;
	private const int MaxSyncTargetLatencyMilliseconds = 260;
	private const int SyncDropToleranceMilliseconds = 45;
	private readonly object _gate = new object();
	private readonly BufferedWaveProvider _buffer;
	private readonly WasapiOut _output;
	private readonly int _averageBytesPerSecond;
	private long _submittedFrames;
	private long _submittedBytes;
	private long _droppedFrames;
	private long _syncDroppedFrames;
	private long _bufferClears;
	private long _lowBufferEvents;
	private long _lastStatusTick;
	private int _syncTargetLatencyMilliseconds = 180;
	private int _latestEstimatedLatencyMilliseconds;
	private bool _lowBufferActive;
	private bool _disposed;

	public WasapiAudioOutput(int sampleRate, int channels)
	{
		int resolvedSampleRate = sampleRate > 0 ? sampleRate : 44100;
		int resolvedChannels = channels > 0 ? channels : 2;
		var waveFormat = new WaveFormat(resolvedSampleRate, 16, resolvedChannels);
		_averageBytesPerSecond = waveFormat.AverageBytesPerSecond;
		_buffer = new BufferedWaveProvider(waveFormat)
		{
			BufferDuration = TimeSpan.FromMilliseconds(BufferMilliseconds),
			DiscardOnBufferOverflow = false,
			ReadFully = true
		};
		_output = new WasapiOut(AudioClientShareMode.Shared, WasapiLatencyMilliseconds);
		_output.Init(_buffer);
		_output.Play();
		this.StatusChanged?.Invoke($"WASAPI shared audio output started: {resolvedSampleRate}Hz/{resolvedChannels}ch, buffer={BufferMilliseconds}ms.");
	}

	public int BufferedBytes => _buffer.BufferedBytes;

	public int BufferedMilliseconds => BytesToMilliseconds(_buffer.BufferedBytes);

	public long SubmittedFrames => Interlocked.Read(ref _submittedFrames);

	public long SubmittedBytes => Interlocked.Read(ref _submittedBytes);

	public long DroppedFrames => Interlocked.Read(ref _droppedFrames);

	public long SyncDroppedFrames => Interlocked.Read(ref _syncDroppedFrames);

	public long BufferClears => Interlocked.Read(ref _bufferClears);

	public long LowBufferEvents => Interlocked.Read(ref _lowBufferEvents);

	public int SyncTargetLatencyMilliseconds => Volatile.Read(ref _syncTargetLatencyMilliseconds);

	public int LatestEstimatedLatencyMilliseconds => Volatile.Read(ref _latestEstimatedLatencyMilliseconds);

	public event Action<string>? StatusChanged;

	public void SetSyncTargetLatencyMilliseconds(int targetLatencyMilliseconds)
	{
		int clamped = Math.Clamp(targetLatencyMilliseconds, MinSyncTargetLatencyMilliseconds, MaxSyncTargetLatencyMilliseconds);
		Volatile.Write(ref _syncTargetLatencyMilliseconds, clamped);
	}

	public void Submit(AudioPcmFrame frame)
	{
		if (frame.ByteCount <= 0)
		{
			return;
		}

		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			int beforeBytes = _buffer.BufferedBytes;
			int beforeMs = BytesToMilliseconds(beforeBytes);
			int estimatedLatencyMs = EstimateLatencyMilliseconds(frame, beforeMs);
			Volatile.Write(ref _latestEstimatedLatencyMilliseconds, estimatedLatencyMs);
			int syncTargetMs = SyncTargetLatencyMilliseconds;
			if (beforeBytes + frame.ByteCount > _buffer.BufferLength)
			{
				_buffer.ClearBuffer();
				Interlocked.Increment(ref _bufferClears);
				beforeBytes = 0;
				beforeMs = 0;
			}
			else if (estimatedLatencyMs > syncTargetMs + SyncDropToleranceMilliseconds && beforeMs >= LowBufferRecoveredMilliseconds)
			{
				Interlocked.Increment(ref _droppedFrames);
				Interlocked.Increment(ref _syncDroppedFrames);
				LogStatusThrottled(beforeMs);
				return;
			}
			else if (beforeMs >= HighBufferMilliseconds)
			{
				Interlocked.Increment(ref _droppedFrames);
				LogStatusThrottled(beforeMs);
				return;
			}

			if (beforeMs < LowBufferMilliseconds && Interlocked.Read(ref _submittedFrames) > 10)
			{
				if (!_lowBufferActive)
				{
					_lowBufferActive = true;
					Interlocked.Increment(ref _lowBufferEvents);
				}
			}
			else if (beforeMs >= LowBufferRecoveredMilliseconds)
			{
				_lowBufferActive = false;
			}

			_buffer.AddSamples(frame.Buffer, 0, frame.ByteCount);
			Interlocked.Increment(ref _submittedFrames);
			Interlocked.Add(ref _submittedBytes, frame.ByteCount);
			LogStatusThrottled(beforeMs);
		}
	}

	private void LogStatusThrottled(int beforeMilliseconds)
	{
		long now = Environment.TickCount64;
		long previous = Interlocked.Read(ref _lastStatusTick);
		if (now - previous < 5000)
		{
			return;
		}
		if (Interlocked.CompareExchange(ref _lastStatusTick, now, previous) != previous)
		{
			return;
		}

		this.StatusChanged?.Invoke(
			$"WASAPI audio buffer: before={beforeMilliseconds}ms, after={BufferedMilliseconds}ms, " +
			$"frames={SubmittedFrames:N0}, dropped={DroppedFrames:N0}, syncDropped={SyncDroppedFrames:N0}, clears={BufferClears:N0}, " +
			$"lowTransitions={LowBufferEvents:N0}, estimatedLatency={LatestEstimatedLatencyMilliseconds}ms, syncTarget={SyncTargetLatencyMilliseconds}ms, " +
			$"bufferTarget={TargetBufferMilliseconds}-{HighBufferMilliseconds}ms.");
	}

	private int EstimateLatencyMilliseconds(AudioPcmFrame frame, int bufferedMilliseconds)
	{
		long receivedTick = frame.ReceivedTick;
		int receiveToSubmitMs = 0;
		if (receivedTick > 0)
		{
			long now = System.Diagnostics.Stopwatch.GetTimestamp();
			if (now > receivedTick)
			{
				receiveToSubmitMs = (int)Math.Round((now - receivedTick) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
			}
		}
		return Math.Max(0, receiveToSubmitMs) + bufferedMilliseconds + WasapiLatencyMilliseconds;
	}

	private int BytesToMilliseconds(int bytes)
	{
		if (_averageBytesPerSecond <= 0)
		{
			return 0;
		}
		return (int)Math.Round(bytes * 1000.0 / _averageBytesPerSecond);
	}

	public void Dispose()
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
		}

		try
		{
			_output.Stop();
		}
		catch
		{
		}
		_output.Dispose();
	}
}
