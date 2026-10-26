using System;
using System.Diagnostics;
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
	// Never call NAudio/COM lifecycle methods (Init/Play/Stop/Dispose) while holding _gate:
	// WasapiOut.Stop() joins the playback thread, which may be raising PlaybackStopped.
	private readonly object _gate = new object();
	private readonly IWasapiOutputDeviceFactory _outputFactory;
	private readonly BufferedWaveProvider _buffer;
	private readonly int _averageBytesPerSecond;
	private IAudioEndpointRestartNotifier? _endpointNotifier;
	private IWasapiOutputDevice? _output;
	private IWasapiOutputDevice? _startingOutput;
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
	private string? _restartTargetEndpointId;
	private int _restartUnconditional;
	private long _restartRequestedTimestamp;
	private string _lastStartTimings = "";
	private bool _lowBufferActive;
	private bool _restartRequested;
	private bool _restartInProgress;
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
		_output = CreateAndStartOutput();
		_startingOutput = null;
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

		IWasapiOutputDevice? retiredOutput;
		string restartReason;
		bool skippedRestart = false;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}

			// Another Submit is swapping the device outside the lock.
			if (_restartInProgress)
			{
				Interlocked.Increment(ref _droppedFrames);
				return;
			}

			if (!Volatile.Read(ref _restartRequested))
			{
				if (_output == null)
				{
					Interlocked.Increment(ref _droppedFrames);
					return;
				}

				SubmitLocked(frame);
				return;
			}

			long now = Environment.TickCount64;
			if (_output == null && now - Interlocked.Read(ref _lastRestartAttemptTick) < RestartRetryIntervalMilliseconds)
			{
				Interlocked.Increment(ref _droppedFrames);
				return;
			}

			Interlocked.Exchange(ref _lastRestartAttemptTick, now);
			restartReason = Volatile.Read(ref _restartReason);
			Volatile.Write(ref _restartRequested, false);
			string? targetEndpointId = Interlocked.Exchange(ref _restartTargetEndpointId, null);
			bool unconditional = Interlocked.Exchange(ref _restartUnconditional, 0) != 0;
			// Unplugging the default endpoint raises "Unplugged" and then "default changed"; the first
			// restart usually already opened the new default, and each Init costs 2-3s while Windows settles.
			if (!unconditional &&
				targetEndpointId != null &&
				_output != null &&
				string.Equals(_output.EndpointId, targetEndpointId, StringComparison.OrdinalIgnoreCase))
			{
				SubmitLocked(frame);
				skippedRestart = true;
				retiredOutput = null;
			}
			else
			{
				_restartInProgress = true;
				retiredOutput = _output;
				_output = null;
				_buffer.ClearBuffer();
				_lowBufferActive = false;
				Interlocked.Increment(ref _bufferClears);
			}
		}

		if (skippedRestart)
		{
			this.StatusChanged?.Invoke("WASAPI audio output restart skipped after " + restartReason + ": already on that endpoint.");
			return;
		}

		if (!TryRestartOutput(retiredOutput, restartReason))
		{
			Interlocked.Increment(ref _droppedFrames);
			return;
		}

		lock (_gate)
		{
			if (_disposed || _output == null)
			{
				Interlocked.Increment(ref _droppedFrames);
				return;
			}

			SubmitLocked(frame);
		}
	}

	private void SubmitLocked(AudioPcmFrame frame)
	{
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

	private IWasapiOutputDevice CreateAndStartOutput()
	{
		long started = Stopwatch.GetTimestamp();
		IWasapiOutputDevice output = _outputFactory.Create();
		long created = Stopwatch.GetTimestamp();
		Volatile.Write(ref _startingOutput, output);
		try
		{
			output.PlaybackStopped += HandlePlaybackStopped;
			output.Init(_buffer);
			long initialized = Stopwatch.GetTimestamp();
			output.Play();
			long played = Stopwatch.GetTimestamp();
			_lastStartTimings =
				$"create={Stopwatch.GetElapsedTime(started, created).TotalMilliseconds:F0}ms, " +
				$"init={Stopwatch.GetElapsedTime(created, initialized).TotalMilliseconds:F0}ms, " +
				$"play={Stopwatch.GetElapsedTime(initialized, played).TotalMilliseconds:F0}ms";
			return output;
		}
		catch
		{
			output.PlaybackStopped -= HandlePlaybackStopped;
			Volatile.Write(ref _startingOutput, null);
			output.Dispose();
			throw;
		}
	}

	private bool TryRestartOutput(IWasapiOutputDevice? retiredOutput, string reason)
	{
		long requested = Interlocked.Read(ref _restartRequestedTimestamp);
		double waitedMs = requested > 0 ? Stopwatch.GetElapsedTime(requested).TotalMilliseconds : 0;
		StopAndDisposeOutput(retiredOutput);
		IWasapiOutputDevice output;
		try
		{
			output = CreateAndStartOutput();
		}
		catch (Exception ex)
		{
			lock (_gate)
			{
				_restartInProgress = false;
				if (!_disposed)
				{
					Volatile.Write(ref _restartReason, reason);
					Volatile.Write(ref _restartUnconditional, 1);
					Volatile.Write(ref _restartRequested, true);
				}
			}
			this.StatusChanged?.Invoke("WASAPI audio output restart failed after " + reason + ": " + ex.Message);
			return false;
		}

		bool disposed;
		lock (_gate)
		{
			_restartInProgress = false;
			disposed = _disposed;
			if (!disposed)
			{
				_output = output;
			}
			Volatile.Write(ref _startingOutput, null);
		}

		if (disposed)
		{
			StopAndDisposeOutput(output);
			return false;
		}

		Interlocked.Increment(ref _outputRestarts);
		this.StatusChanged?.Invoke(
			"WASAPI audio output restarted after " + reason +
			" (requestToStart=" + waitedMs.ToString("F0") + "ms, " + _lastStartTimings + ").");
		return true;
	}

	// Lock-free: NAudio raises this from the playback thread, which WasapiOut.Stop() joins.
	// targetEndpointId is the new default for a default-device change; null means restart regardless.
	private void RequestOutputRestart(string reason, string? targetEndpointId)
	{
		if (Volatile.Read(ref _disposed))
		{
			return;
		}

		string resolvedReason = string.IsNullOrWhiteSpace(reason) ? "audio endpoint changed" : reason;
		Volatile.Write(ref _restartReason, resolvedReason);
		if (targetEndpointId == null)
		{
			Volatile.Write(ref _restartUnconditional, 1);
		}
		else
		{
			Volatile.Write(ref _restartTargetEndpointId, targetEndpointId);
		}
		Interlocked.Exchange(ref _restartRequestedTimestamp, Stopwatch.GetTimestamp());
		Volatile.Write(ref _restartRequested, true);
		this.StatusChanged?.Invoke("WASAPI audio output restart requested: " + resolvedReason + ".");
	}

	private void HandlePlaybackStopped(object? sender, StoppedEventArgs e)
	{
		if (e.Exception == null)
		{
			return;
		}

		// A device that was already detached can still report in while Stop() joins its playback thread.
		if (!ReferenceEquals(sender, Volatile.Read(ref _output)) && !ReferenceEquals(sender, Volatile.Read(ref _startingOutput)))
		{
			return;
		}

		RequestOutputRestart("playback stopped: " + e.Exception.Message, targetEndpointId: null);
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
		IWasapiOutputDevice? output;
		lock (_gate)
		{
			if (_disposed)
			{
				return;
			}
			Volatile.Write(ref _disposed, true);
			endpointNotifier = _endpointNotifier;
			_endpointNotifier = null;
			output = _output;
			_output = null;
		}

		if (endpointNotifier != null)
		{
			endpointNotifier.RestartRequested -= RequestOutputRestart;
			endpointNotifier.Dispose();
		}

		StopAndDisposeOutput(output);
	}

	private void StopAndDisposeOutput(IWasapiOutputDevice? output)
	{
		if (output == null)
		{
			return;
		}

		output.PlaybackStopped -= HandlePlaybackStopped;
		try
		{
			output.Stop();
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

	string? EndpointId { get; }

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
	// reason, plus the new default endpoint id for a default-device change (null otherwise).
	event Action<string, string?>? RestartRequested;
}

internal sealed class NAudioWasapiOutputDeviceFactory : IWasapiOutputDeviceFactory
{
	public IWasapiOutputDevice Create()
	{
		// Same endpoint WasapiOut(shareMode, latency) picks, resolved here so its id is known.
		MMDevice device;
		using (var enumerator = new MMDeviceEnumerator())
		{
			device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
		}

		try
		{
			var output = new WasapiOut(device, AudioClientShareMode.Shared, true, WasapiAudioOutput.WasapiLatencyMilliseconds);
			return new NAudioWasapiOutputDevice(output, device);
		}
		catch
		{
			device.Dispose();
			throw;
		}
	}
}

internal sealed class NAudioWasapiOutputDevice : IWasapiOutputDevice
{
	private readonly WasapiOut _output;
	private readonly MMDevice _device;

	public NAudioWasapiOutputDevice(WasapiOut output, MMDevice device)
	{
		_output = output;
		_device = device;
		EndpointId = TryGetId(device);
		_output.PlaybackStopped += (_, e) => PlaybackStopped?.Invoke(this, e);
	}

	// Re-raised with this wrapper as sender so WasapiAudioOutput can tell a stale device from the current one.
	public event EventHandler<StoppedEventArgs>? PlaybackStopped;

	public string? EndpointId { get; }

	private static string? TryGetId(MMDevice device)
	{
		try
		{
			return device.ID;
		}
		catch
		{
			return null;
		}
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
		_device.Dispose();
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

	public event Action<string, string?>? RestartRequested;

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
			RaiseRestartRequested($"default render endpoint changed ({role})", string.IsNullOrWhiteSpace(defaultDeviceId) ? null : defaultDeviceId);
		}
	}

	private void HandleDeviceStateChanged(string deviceId, DeviceState newState)
	{
		if (newState == DeviceState.Active || !IsCurrentRenderEndpoint(deviceId))
		{
			return;
		}

		RaiseRestartRequested("render endpoint state changed to " + newState, null);
	}

	private void HandleDeviceRemoved(string deviceId)
	{
		if (IsCurrentRenderEndpoint(deviceId))
		{
			RaiseRestartRequested("render endpoint removed", null);
		}
	}

	private void HandleDeviceAdded(string deviceId)
	{
		if (IsCurrentRenderEndpoint(deviceId))
		{
			RaiseRestartRequested("render endpoint added", null);
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

	private void RaiseRestartRequested(string reason, string? targetEndpointId)
	{
		RestartRequested?.Invoke(reason, targetEndpointId);
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
