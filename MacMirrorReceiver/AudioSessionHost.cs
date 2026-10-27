using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using MacMirrorReceiver.Audio;
using MacMirrorReceiver.Video;

namespace MacMirrorReceiver;

/// <summary>
/// Owns the AirPlay audio pipeline for the window: the FFmpeg AAC-ELD decoder and the WASAPI
/// output, their start (with backoff) and stop, the "no RTP after SETUP" firewall watchdog, the
/// counters and the diagnostics line. Thread-agnostic: the AirPlay data thread calls
/// <see cref="HandleStreamStarted"/> and <see cref="HandleFrame"/>, the decoder thread delivers
/// PCM, the UI thread stops and reads. Events are raised on whichever thread caused them; the
/// window marshals them to the dispatcher.
///
/// Lock discipline: <c>_gate</c> guards the decoder/output references and the watchdog. It is
/// held while WASAPI and ffmpeg start (seconds on some PCs), so <see cref="ResolveDiagnostics"/>
/// never takes it, and no NAudio/COM lifecycle call other than start/stop happens under it.
/// </summary>
internal sealed class AudioSessionHost : IDisposable
{
	public const string FirewallWarningMessage =
		"Audio is blocked by Windows Firewall. Allow iMirror for Private and Public networks.";

	private static readonly TimeSpan RtpStartupTimeout = TimeSpan.FromSeconds(8.0);

	private readonly Func<int> _currentGeneration;
	private readonly Func<int> _resolveSyncTargetLatencyMilliseconds;
	private readonly object _gate = new object();
	private readonly AudioStartBackoff _startBackoff = new AudioStartBackoff();

	private FfmpegAudioDecoder? _decoder;
	private WasapiAudioOutput? _output;
	private CancellationTokenSource? _rtpWatchdogCts;
	private volatile bool _firewallWarningActive;
	private volatile bool _firewallAllowRuleKnown;
	private volatile string _status = "not started";
	private int _sampleRate = 44100;
	private int _channels = 2;
	private int _samplesPerFrame = 480;
	private long _framesReceived;
	private long _framesQueued;
	private long _pcmFrames;
	private long _pcmBytes;

	/// <param name="currentGeneration">The connection generation that incoming audio must match.</param>
	/// <param name="resolveSyncTargetLatencyMilliseconds">The audio target latency derived from the video latency and the user offset.</param>
	public AudioSessionHost(Func<int> currentGeneration, Func<int> resolveSyncTargetLatencyMilliseconds)
	{
		_currentGeneration = currentGeneration;
		_resolveSyncTargetLatencyMilliseconds = resolveSyncTargetLatencyMilliseconds;
	}

	/// <summary>Counters or status changed; the window refreshes the diagnostics text (throttled).</summary>
	public event Action? DiagnosticsChanged;

	/// <summary>A decoder/output status line, already stored in <see cref="Status"/>; the window logs and refreshes.</summary>
	public event Action<string>? StatusReported;

	/// <summary>No audio RTP arrived after SETUP and no allow rule is known; the window shows <see cref="FirewallWarningMessage"/>.</summary>
	public event Action? FirewallWarningRaised;

	/// <summary>The warning was cleared (RTP arrived, or the allow rule completed); the window refreshes its chrome.</summary>
	public event Action? FirewallWarningCleared;

	public bool FirewallWarningActive => _firewallWarningActive;

	public string Status => _status;

	/// <summary>Called when the user's Allow in Firewall completed, so the watchdog stops warning.</summary>
	public void MarkFirewallAllowRuleKnown()
	{
		_firewallAllowRuleKnown = true;
	}

	public void HandleStreamStarted(int sampleRate, int channels, int samplesPerFrame, int generation)
	{
		if (!IsCurrentGeneration(generation, _currentGeneration()))
		{
			return;
		}

		lock (_gate)
		{
			bool formatChanged = _decoder != null
				&& (_sampleRate != sampleRate || _channels != channels || _samplesPerFrame != samplesPerFrame);
			_sampleRate = sampleRate > 0 ? sampleRate : 44100;
			_channels = channels > 0 ? channels : 2;
			_samplesPerFrame = samplesPerFrame > 0 ? samplesPerFrame : 480;
			if (formatChanged)
			{
				StopLocked();
			}
			// A new SETUP (new session or new format) gets a fresh start attempt regardless
			// of earlier failures in this session.
			_startBackoff.Reset();
			StartLocked("AirPlay audio stream setup");
			if (_decoder != null && _output != null)
			{
				RestartRtpWatchdogLocked(generation);
			}
		}
	}

	public void HandleFrame(byte[] frame, uint rtpTimestamp, ushort sequence, int generation)
	{
		if (!IsCurrentGeneration(generation, _currentGeneration()))
		{
			return;
		}

		ClearFirewallWarning();
		Interlocked.Increment(ref _framesReceived);
		long receivedTick = Stopwatch.GetTimestamp();
		FfmpegAudioDecoder? decoder;
		lock (_gate)
		{
			if (_decoder == null)
			{
				StartLocked("first AirPlay audio frame");
			}
			decoder = _decoder;
		}

		if (decoder == null)
		{
			DiagnosticsChanged?.Invoke();
			return;
		}

		if (decoder.QueueFrame(frame, rtpTimestamp, sequence, receivedTick))
		{
			Interlocked.Increment(ref _framesQueued);
		}
		DiagnosticsChanged?.Invoke();
	}

	/// <summary>Applies a new target latency to the live output (the Settings slider path).</summary>
	public void ApplySyncTargetLatency()
	{
		WasapiAudioOutput? output = Volatile.Read(ref _output);
		output?.SetSyncTargetLatencyMilliseconds(_resolveSyncTargetLatencyMilliseconds());
	}

	public void ClearFirewallWarning(string logMessage = "AirPlay audio RTP reached the decoder; clearing firewall warning.", string? replacementStatus = null)
	{
		bool changed = false;
		lock (_gate)
		{
			CancelRtpWatchdogLocked();
			if (_firewallWarningActive)
			{
				_firewallWarningActive = false;
				changed = true;
			}
			if (replacementStatus != null && _status == FirewallWarningMessage)
			{
				_status = replacementStatus;
				changed = true;
			}
		}

		if (!changed)
		{
			return;
		}

		AppLog.Write(logMessage);
		FirewallWarningCleared?.Invoke();
	}

	/// <summary>Resets counters and status and stops the pipeline for a new connection.</summary>
	public void ResetForNewConnection()
	{
		Interlocked.Exchange(ref _framesReceived, 0L);
		Interlocked.Exchange(ref _framesQueued, 0L);
		Interlocked.Exchange(ref _pcmFrames, 0L);
		Interlocked.Exchange(ref _pcmBytes, 0L);
		_status = "waiting for stream";
		_startBackoff.Reset();
		Stop();
	}

	public void Stop()
	{
		lock (_gate)
		{
			StopLocked();
		}
	}

	public void Dispose()
	{
		Stop();
	}

	/// <summary>
	/// The diagnostics line. Reads without <c>_gate</c>: StartLocked holds it while WASAPI and the
	/// FFmpeg audio process start, and the UI thread must not wait on that (the AirPlay data
	/// thread in turn waits on the UI thread to apply the stream config before the first IDR).
	/// </summary>
	public string ResolveDiagnostics()
	{
		FfmpegAudioDecoder? decoder = Volatile.Read(ref _decoder);
		WasapiAudioOutput? output = Volatile.Read(ref _output);
		bool firewallWarning = _firewallWarningActive;
		string status = _status;

		string firewallHint = firewallWarning
			? " (no audio RTP after SETUP; allow iMirror in Windows Firewall for Private and Public networks)"
			: string.Empty;

		if (decoder == null || output == null)
		{
			return $"audio: {status}{firewallHint}, received={Interlocked.Read(ref _framesReceived):N0}";
		}

		return $"audio: {status}{firewallHint}, received/queued={Interlocked.Read(ref _framesReceived):N0}/{Interlocked.Read(ref _framesQueued):N0}, " +
			$"dedup={decoder.DuplicateInputFrames:N0}, decoded={Interlocked.Read(ref _pcmFrames):N0} ({(double)Interlocked.Read(ref _pcmBytes) / 1024.0:N1} KB), " +
			$"decoderQueue={decoder.QueuedInputFrames:N0}, audioBuffer={output.BufferedMilliseconds:N0}ms, " +
			$"audioLatency={output.LatestEstimatedLatencyMilliseconds}/{output.SyncTargetLatencyMilliseconds}ms, " +
			$"audioDropped={output.DroppedFrames:N0} sync={output.SyncDroppedFrames:N0}, clears={output.BufferClears:N0}, low={output.LowBufferEvents:N0}";
	}

	internal static bool IsCurrentGeneration(int payloadGeneration, int currentGeneration)
	{
		return payloadGeneration != 0 && payloadGeneration == currentGeneration;
	}

	private void RestartRtpWatchdogLocked(int generation)
	{
		CancelRtpWatchdogLocked();
		long frameBaseline = Interlocked.Read(ref _framesReceived);
		_rtpWatchdogCts = new CancellationTokenSource();
		_ = WatchRtpStartupAsync(generation, frameBaseline, _rtpWatchdogCts.Token);
	}

	private void CancelRtpWatchdogLocked()
	{
		CancellationTokenSource? cts = _rtpWatchdogCts;
		_rtpWatchdogCts = null;
		try
		{
			cts?.Cancel();
		}
		catch
		{
		}
	}

	private async Task WatchRtpStartupAsync(int generation, long frameBaseline, CancellationToken token)
	{
		try
		{
			await Task.Delay(RtpStartupTimeout, token);
		}
		catch (OperationCanceledException)
		{
			return;
		}
		catch (ObjectDisposedException)
		{
			return;
		}

		if (!IsCurrentGeneration(generation, _currentGeneration()) ||
			Interlocked.Read(ref _framesReceived) > frameBaseline)
		{
			return;
		}

		if (await ShouldSuppressFirewallWarningAsync(generation, frameBaseline, token))
		{
			return;
		}

		lock (_gate)
		{
			if (token.IsCancellationRequested ||
				Interlocked.Read(ref _framesReceived) > frameBaseline ||
				_firewallWarningActive)
			{
				return;
			}

			_firewallWarningActive = true;
			_status = FirewallWarningMessage;
		}

		AppLog.Write(
			$"AirPlay audio RTP did not arrive within {RtpStartupTimeout.TotalSeconds:N0}s after SETUP; " +
			"Windows Firewall may be blocking the dynamic UDP audio ports. Allow iMirror for Private and Public networks.");
		FirewallWarningRaised?.Invoke();
	}

	private async Task<bool> ShouldSuppressFirewallWarningAsync(int generation, long frameBaseline, CancellationToken token)
	{
		if (!_firewallAllowRuleKnown)
		{
			bool hasAllowRule = await WindowsFirewallRuleInstaller.HasAllowRuleForCurrentExecutableAsync(token);
			if (hasAllowRule)
			{
				_firewallAllowRuleKnown = true;
			}
		}

		if (!_firewallAllowRuleKnown)
		{
			return false;
		}

		if (token.IsCancellationRequested ||
			!IsCurrentGeneration(generation, _currentGeneration()) ||
			Interlocked.Read(ref _framesReceived) > frameBaseline)
		{
			return true;
		}

		AppLog.Write(
			$"AirPlay audio RTP did not arrive within {RtpStartupTimeout.TotalSeconds:N0}s after SETUP, " +
			"but an enabled inbound Windows Firewall allow rule already exists for iMirror; suppressing the firewall warning.");
		return true;
	}

	private void StartLocked(string reason)
	{
		if (_decoder != null)
		{
			return;
		}
		if (!_startBackoff.ShouldAttempt(Environment.TickCount64))
		{
			// A recent start failed; HandleFrame calls here on every frame, so without this the
			// WASAPI client and ffmpeg spawn were retried ~100 times a second.
			return;
		}

		try
		{
			long startTick = Stopwatch.GetTimestamp();
			var output = new WasapiAudioOutput(_sampleRate, _channels);
			output.StatusChanged += HandleStatus;
			_output = output;
			long outputTick = Stopwatch.GetTimestamp();

			var decoder = new FfmpegAudioDecoder(_sampleRate, _channels, _samplesPerFrame);
			decoder.StatusChanged += HandleStatus;
			decoder.PcmFrameDecoded += HandlePcmFrame;
			_decoder = decoder;
			decoder.Start();
			long decoderTick = Stopwatch.GetTimestamp();

			_status = $"started ({_sampleRate}Hz/{_channels}ch/{_samplesPerFrame}spf)";
			_startBackoff.Reset();
			AppLog.Write(
				$"Audio pipeline started for {reason} in {Stopwatch.GetElapsedTime(startTick, decoderTick).TotalMilliseconds:N0}ms " +
				$"(wasapi={Stopwatch.GetElapsedTime(startTick, outputTick).TotalMilliseconds:N0}ms, " +
				$"ffmpeg={Stopwatch.GetElapsedTime(outputTick, decoderTick).TotalMilliseconds:N0}ms).");
			DiagnosticsChanged?.Invoke();
		}
		catch (Exception ex)
		{
			StopLocked();
			TimeSpan retryDelay = _startBackoff.RecordFailure(Environment.TickCount64);
			_status = $"failed: {ex.Message} (retry in {retryDelay.TotalSeconds:N0}s)";
			AppLog.Write($"Audio pipeline failed to start (attempt {_startBackoff.ConsecutiveFailures}, next retry in {retryDelay.TotalSeconds:N0}s): {ex}");
			DiagnosticsChanged?.Invoke();
		}
	}

	private void HandleStatus(string message)
	{
		_status = message;
		StatusReported?.Invoke(message);
	}

	private void HandlePcmFrame(AudioPcmFrame frame)
	{
		try
		{
			WasapiAudioOutput? output;
			lock (_gate)
			{
				output = _output;
			}
			if (output == null)
			{
				return;
			}

			output.SetSyncTargetLatencyMilliseconds(_resolveSyncTargetLatencyMilliseconds());
			output.Submit(frame);
			Interlocked.Increment(ref _pcmFrames);
			Interlocked.Add(ref _pcmBytes, frame.ByteCount);
		}
		finally
		{
			frame.Release();
		}
	}

	private void StopLocked()
	{
		FfmpegAudioDecoder? decoder = _decoder;
		WasapiAudioOutput? output = _output;
		CancelRtpWatchdogLocked();
		_firewallWarningActive = false;
		_decoder = null;
		_output = null;
		try
		{
			decoder?.Dispose();
		}
		catch
		{
		}
		try
		{
			output?.Dispose();
		}
		catch
		{
		}
	}
}
