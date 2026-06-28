using System;
using System.Threading;
using MacMirrorReceiver.Video;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace MacMirrorReceiver.Audio;

public sealed class WasapiAudioOutput : IDisposable
{
	internal const int WasapiLatencyMilliseconds = 80;
	private const int BufferMilliseconds = 1000;
	private const int LowBufferMilliseconds = 40;
	private const int LowBufferRecoveredMilliseconds = 90;
	private const int TargetBufferMilliseconds = 140;
	private const int HighBufferMilliseconds = 260;
	private const int MinSyncTargetLatencyMilliseconds = 100;
	private const int MaxSyncTargetLatencyMilliseconds = 260;
	private const int SyncDropToleranceMilliseconds = 45;
	private const long RestartRetryIntervalMilliseconds = 500;
	private readonly object _gate = new object();
	private readonly IWasapiOutputDeviceFactory _outputFactory;
	private readonly BufferedWaveProvider _buffer;
	private readonly int _averageBytesPerSecond;
	private IAudioEndpointRestartNotifier? _endpointNotifier;
	private IWasapiOutputDevice? _output;
	private long _submittedFrames;
	private long _submittedBytes;
	private long _droppedFrames;
	private long _syncDroppedFrames;
	private long _bufferClears;
	private long _lowBufferEvents;
	private long _outputRestarts;
	private long _lastStatusTick;
	private long _lastRestartAttemptTick;
	private int _syncTargetLatencyMilliseconds = 180;
	private int _latestEstimatedLatencyMilliseconds;
	private string _restartReason = "audio endpoint changed";
	private bool _lowBufferActive;
	private bool _restartRequested;
	private bool _disposed;

	public WasapiAudioOutput(int sampleRate, int channels)
		: this(
			sampleRate,
			channels,
			new NAudioWasapiOutputDeviceFactory(),
			DefaultAudioEndpointRestartNotifier.TryCreate())
	{
	}

	internal WasapiAudioOutput(
		int sampleRate,
		int channels,
		IWasapiOutputDeviceFactory outputFactory,
		IAudioEndpointRestartNotifier? endpointNotifier)
	{
		_outputFactory = outputFactory ?? throw new ArgumentNullException(nameof(outputFactory));
		_endpointNotifier = endpointNotifier;
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
		if (_endpointNotifier != null)
		{
			_endpointNotifier.RestartRequested += RequestOutputRestart;
		}
		CreateAndStartOutputLocked();
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

	public long OutputRestarts => Interlocked.Read(ref _outputRestarts);

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

			if (_restartRequested && !TryRestartOutputLocked())
			{
				Interlocked.Increment(ref _droppedFrames);
				return;
			}

			if (_output == null)
			{
				Interlocked.Increment(ref _droppedFrames);
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

	private void CreateAndStartOutputLocked()
	{
		IWasapiOutputDevice output = _outputFactory.Create();
		try
		{
			output.PlaybackStopped += HandlePlaybackStopped;
			output.Init(_buffer);
			output.Play();
			_output = output;
		}
		catch
		{
			output.PlaybackStopped -= HandlePlaybackStopped;
			output.Dispose();
			throw;
		}
	}

	private bool TryRestartOutputLocked()
	{
		long now = Environment.TickCount64;
		if (_output == null && now - Interlocked.Read(ref _lastRestartAttemptTick) < RestartRetryIntervalMilliseconds)
		{
			return false;
		}

		Interlocked.Exchange(ref _lastRestartAttemptTick, now);
		string reason = _restartReason;
		DisposeOutputLocked(stop: true);
		_buffer.ClearBuffer();
		_lowBufferActive = false;
		Interlocked.Increment(ref _bufferClears);
		try
		{
			CreateAndStartOutputLocked();
			_restartRequested = false;
			Interlocked.Increment(ref _outputRestarts);
			this.StatusChanged?.Invoke("WASAPI audio output restarted after " + reason + ".");
			return true;
		}
		catch (Exception ex)
		{
			_restartRequested = true;
			_restartReason = reason;
			this.StatusChanged?.Invoke("WASAPI audio output restart failed after " + reason + ": " + ex.Message);
			return false;
		}
	}

	private void RequestOutputRestart(string reason)
	{
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
			_restartRequested = true;
			_restartReason = string.IsNullOrWhiteSpace(reason) ? "audio endpoint changed" : reason;
		}

		this.StatusChanged?.Invoke("WASAPI audio output restart requested: " + _restartReason + ".");
	}

	private void HandlePlaybackStopped(object? sender, StoppedEventArgs e)
	{
		if (e.Exception == null)
		{
			return;
		}

		RequestOutputRestart("playback stopped: " + e.Exception.Message);
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
		IAudioEndpointRestartNotifier? endpointNotifier;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
			_disposed = true;
			endpointNotifier = _endpointNotifier;
			_endpointNotifier = null;
		}

		if (endpointNotifier != null)
		{
			endpointNotifier.RestartRequested -= RequestOutputRestart;
			endpointNotifier.Dispose();
		}

		lock (_gate)
		{
			DisposeOutputLocked(stop: true);
		}
	}

	private void DisposeOutputLocked(bool stop)
	{
		IWasapiOutputDevice? output = _output;
		_output = null;
		if (output == null)
		{
			return;
		}

		output.PlaybackStopped -= HandlePlaybackStopped;
		try
		{
			if (stop)
			{
				output.Stop();
			}
		}
		catch
		{
		}

		try
		{
			output.Dispose();
		}
		catch
		{
		}
	}
}

internal interface IWasapiOutputDevice : IDisposable
{
	event EventHandler<StoppedEventArgs>? PlaybackStopped;

	void Init(IWaveProvider waveProvider);

	void Play();

	void Stop();
}

internal interface IWasapiOutputDeviceFactory
{
	IWasapiOutputDevice Create();
}

internal interface IAudioEndpointRestartNotifier : IDisposable
{
	event Action<string>? RestartRequested;
}

internal sealed class NAudioWasapiOutputDeviceFactory : IWasapiOutputDeviceFactory
{
	public IWasapiOutputDevice Create()
	{
		return new NAudioWasapiOutputDevice(new WasapiOut(AudioClientShareMode.Shared, WasapiAudioOutput.WasapiLatencyMilliseconds));
	}
}

internal sealed class NAudioWasapiOutputDevice : IWasapiOutputDevice
{
	private readonly WasapiOut _output;

	public NAudioWasapiOutputDevice(WasapiOut output)
	{
		_output = output;
	}

	public event EventHandler<StoppedEventArgs>? PlaybackStopped
	{
		add => _output.PlaybackStopped += value;
		remove => _output.PlaybackStopped -= value;
	}

	public void Init(IWaveProvider waveProvider)
	{
		_output.Init(waveProvider);
	}

	public void Play()
	{
		_output.Play();
	}

	public void Stop()
	{
		_output.Stop();
	}

	public void Dispose()
	{
		_output.Dispose();
	}
}

internal sealed class DefaultAudioEndpointRestartNotifier : IAudioEndpointRestartNotifier
{
	private readonly object _gate = new object();
	private readonly MMDeviceEnumerator _enumerator;
	private readonly AudioEndpointNotificationClient _client;
	private string? _currentRenderEndpointId;
	private bool _disposed;

	private DefaultAudioEndpointRestartNotifier()
	{
		_enumerator = new MMDeviceEnumerator();
		_currentRenderEndpointId = TryGetDefaultRenderEndpointId(_enumerator);
		_client = new AudioEndpointNotificationClient(this);
		_enumerator.RegisterEndpointNotificationCallback(_client);
	}

	public event Action<string>? RestartRequested;

	public static IAudioEndpointRestartNotifier? TryCreate()
	{
		try
		{
			return new DefaultAudioEndpointRestartNotifier();
		}
		catch
		{
			return null;
		}
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
			_enumerator.UnregisterEndpointNotificationCallback(_client);
		}
		catch
		{
		}

		_enumerator.Dispose();
	}

	private void HandleDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
	{
		if (flow != DataFlow.Render)
		{
			return;
		}

		string? previous;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			previous = _currentRenderEndpointId;
			_currentRenderEndpointId = string.IsNullOrWhiteSpace(defaultDeviceId) ? null : defaultDeviceId;
		}

		if (!string.Equals(previous, defaultDeviceId, StringComparison.OrdinalIgnoreCase))
		{
			RaiseRestartRequested($"default render endpoint changed ({role})");
		}
	}

	private void HandleDeviceStateChanged(string deviceId, DeviceState newState)
	{
		if (newState == DeviceState.Active || !IsCurrentRenderEndpoint(deviceId))
		{
			return;
		}

		RaiseRestartRequested("render endpoint state changed to " + newState);
	}

	private void HandleDeviceRemoved(string deviceId)
	{
		if (IsCurrentRenderEndpoint(deviceId))
		{
			RaiseRestartRequested("render endpoint removed");
		}
	}

	private void HandleDeviceAdded(string deviceId)
	{
		if (IsCurrentRenderEndpoint(deviceId))
		{
			RaiseRestartRequested("render endpoint added");
		}
	}

	private bool IsCurrentRenderEndpoint(string deviceId)
	{
		lock (_gate)
		{
			return !_disposed &&
				!string.IsNullOrWhiteSpace(deviceId) &&
				!string.IsNullOrWhiteSpace(_currentRenderEndpointId) &&
				string.Equals(_currentRenderEndpointId, deviceId, StringComparison.OrdinalIgnoreCase);
		}
	}

	private void RaiseRestartRequested(string reason)
	{
		RestartRequested?.Invoke(reason);
	}

	private static string? TryGetDefaultRenderEndpointId(MMDeviceEnumerator enumerator)
	{
		try
		{
			return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID;
		}
		catch
		{
			return null;
		}
	}

	private sealed class AudioEndpointNotificationClient : IMMNotificationClient
	{
		private readonly DefaultAudioEndpointRestartNotifier _owner;

		public AudioEndpointNotificationClient(DefaultAudioEndpointRestartNotifier owner)
		{
			_owner = owner;
		}

		public void OnDeviceStateChanged(string deviceId, DeviceState newState)
		{
			_owner.HandleDeviceStateChanged(deviceId, newState);
		}

		public void OnDeviceAdded(string pwstrDeviceId)
		{
			_owner.HandleDeviceAdded(pwstrDeviceId);
		}

		public void OnDeviceRemoved(string deviceId)
		{
			_owner.HandleDeviceRemoved(deviceId);
		}

		public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
		{
			_owner.HandleDefaultDeviceChanged(flow, role, defaultDeviceId);
		}

		public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key)
		{
		}
	}
}
