using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using MacMirrorReceiver.Protocol;
using MacMirrorReceiver.Video;

namespace MacMirrorReceiver;

/// <summary>
/// What <see cref="VideoEngineHost"/> needs from the window. Every member is called on the UI
/// thread except <see cref="Post"/>, <see cref="ConnectionGeneration"/> and
/// <see cref="ReceivedVideoPackets"/>, which decoder and watchdog threads use.
/// </summary>
internal interface IVideoEngineView
{
	/// <summary>Queues <paramref name="action"/> on the UI thread without waiting (BeginInvoke).</summary>
	void Post(Action action, DispatcherPriority priority);

	int ConnectionGeneration { get; }

	long ReceivedVideoPackets { get; }

	StreamConfig? CurrentStreamConfig { get; }

	/// <summary>Hosts the GPU presenter's swap-chain view in the video stage in place of the software surface.</summary>
	void AttachGpuPresenter(IHighResolutionD3DPresenter presenter, StreamConfig config);

	/// <summary>Removes the presenter's view from the stage and shows the software surface again.</summary>
	void DetachGpuPresenter(IHighResolutionD3DPresenter presenter);

	void ShowVideoSurface();

	/// <summary>Clears the software surface and shows the idle panel (GPU failed with no stream config to fall back on).</summary>
	void ShowEmptyStateAfterGpuFailure();

	void ReleasePendingFrames();

	void QueueSoftwareFrame(VideoFrame frame);

	void QueueGpuFrame(IHighResolutionD3DFrame frame);

	/// <summary>The new decoder accepts input: forward the packets buffered while there was none.</summary>
	void FlushPendingVideo();

	void RequireH264Keyframe();

	void BeginLatencyWarmup();

	void SetStatus(string message);

	void UpdateDiagnostics();
}

internal interface IVideoDecoderFactory
{
	ISoftwareVideoDecoder CreateSoftware(int width, int height, int fps, int maxRenderWidth, int outputFps);

	(IHighResolutionD3DPresenter Presenter, IHighResolutionD3DDecoder Decoder) CreateGpu(StreamConfig config);
}

internal sealed class DefaultVideoDecoderFactory : IVideoDecoderFactory
{
	public ISoftwareVideoDecoder CreateSoftware(int width, int height, int fps, int maxRenderWidth, int outputFps)
	{
		return new FfmpegDecoder(width, height, fps, maxRenderWidth, outputFps);
	}

	public (IHighResolutionD3DPresenter Presenter, IHighResolutionD3DDecoder Decoder) CreateGpu(StreamConfig config)
	{
		var presenter = new VorticeD3D11SwapChainVideoPresenter();
		try
		{
			return (presenter, new VorticeMediaFoundationD3D11Decoder(config.Width, config.Height, config.Fps, presenter.Device));
		}
		catch
		{
			presenter.Dispose();
			throw;
		}
	}
}

/// <summary>
/// Owns the video decoder lifecycle: choosing the GPU (Media Foundation/D3D11) or software
/// (FFmpeg) engine for a stream config, wiring and disposing the decoders and the GPU presenter,
/// the one-way fallback to software when the GPU engine faults at runtime, and the
/// "no video packets yet" watchdog. Methods run on the UI thread; decoder callbacks are posted
/// back to it through the view, and each one checks that its decoder is still the active one.
/// </summary>
internal sealed class VideoEngineHost
{
	public static readonly TimeSpan DefaultNoVideoWatchdogDelay = TimeSpan.FromSeconds(2.5);

	private readonly IVideoEngineView _view;
	private readonly IVideoDecoderFactory _factory;
	private readonly Func<StreamConfig, bool> _shouldUseGpu;
	private readonly Func<StreamConfig, int> _resolveSoftwareMaxRenderWidth;
	private readonly Func<StreamConfig, int, int> _resolveSoftwareOutputFps;
	private readonly int _gpuMaxFps;
	private readonly int _defaultMaxRenderWidth;
	private readonly bool _dumpH264;
	private readonly TimeSpan _noVideoWatchdogDelay;

	private ISoftwareVideoDecoder? _softwareDecoder;
	private IHighResolutionD3DDecoder? _gpuDecoder;
	private IHighResolutionD3DPresenter? _gpuPresenter;
	private CancellationTokenSource? _watchdogCts;
	// Set when the GPU engine faults at runtime (device-lost/TDR); the session then stays on the
	// software decoder until the next connection (ResetForNewConnection).
	private bool _gpuPathDisabledThisSession;
	private volatile bool _sinkAcceptingInput;
	private long _restarts;

	public VideoEngineHost(
		IVideoEngineView view,
		IVideoDecoderFactory factory,
		Func<StreamConfig, bool> shouldUseGpu,
		Func<StreamConfig, int> resolveSoftwareMaxRenderWidth,
		Func<StreamConfig, int, int> resolveSoftwareOutputFps,
		int gpuMaxFps,
		int defaultMaxRenderWidth,
		bool dumpH264,
		TimeSpan? noVideoWatchdogDelay = null)
	{
		_view = view;
		_factory = factory;
		_shouldUseGpu = shouldUseGpu;
		_resolveSoftwareMaxRenderWidth = resolveSoftwareMaxRenderWidth;
		_resolveSoftwareOutputFps = resolveSoftwareOutputFps;
		_gpuMaxFps = gpuMaxFps;
		_defaultMaxRenderWidth = defaultMaxRenderWidth;
		_dumpH264 = dumpH264;
		_noVideoWatchdogDelay = noVideoWatchdogDelay ?? DefaultNoVideoWatchdogDelay;
		MaxRenderWidth = defaultMaxRenderWidth;
	}

	public ISoftwareVideoDecoder? SoftwareDecoder => Volatile.Read(ref _softwareDecoder);

	public IHighResolutionD3DDecoder? GpuDecoder => Volatile.Read(ref _gpuDecoder);

	public IHighResolutionD3DPresenter? GpuPresenter => _gpuPresenter;

	public bool HasDecoder => SoftwareDecoder != null || GpuDecoder != null;

	/// <summary>False while a decoder is being replaced; video arriving meanwhile is buffered by the window.</summary>
	public bool SinkAcceptingInput => _sinkAcceptingInput;

	public bool GpuPathDisabledThisSession => _gpuPathDisabledThisSession;

	/// <summary>The "decoder:" diagnostics line. The window also writes it for input-queue and gate states.</summary>
	public string Status { get; set; } = "not started";

	public int MaxRenderWidth { get; private set; }

	public int OutputFps { get; private set; }

	public long Restarts => Interlocked.Read(ref _restarts);

	public void CountRestart()
	{
		Interlocked.Increment(ref _restarts);
	}

	/// <summary>
	/// Replaces whatever decoder is running with a fresh one for <paramref name="config"/>: the GPU
	/// engine when the policy asks for it and it has not faulted this session, otherwise (or when
	/// it fails to start) the software decoder.
	/// </summary>
	public void StartFresh(StreamConfig config, string reason)
	{
		StopWatchdog();
		_sinkAcceptingInput = false;
		_softwareDecoder?.Dispose();
		_softwareDecoder = null;
		_gpuDecoder?.Dispose();
		_gpuDecoder = null;
		DisposeGpuPresenter();
		_view.ReleasePendingFrames();
		_view.ShowVideoSurface();
		// Restart the latency warmup grace so decoder/renderer spin-up and reconnect/session-refresh
		// catch-up are excluded from steady-state percentiles instead of inflating the first window.
		_view.BeginLatencyWarmup();
		bool requireGpu = _shouldUseGpu(config) && !_gpuPathDisabledThisSession;
		if (requireGpu && TryStartGpu(config, reason))
		{
			MaxRenderWidth = config.Width;
			OutputFps = Math.Clamp(config.Fps, 1, _gpuMaxFps);
			_sinkAcceptingInput = true;
			_view.FlushPendingVideo();
			AppLog.Write("Media Foundation D3D11 renderer started for " + reason + ".");
			StartWatchdog(config, _view.ConnectionGeneration);
			return;
		}
		if (requireGpu)
		{
			// GPU engine init failed (no/blocked hardware decode, driver issue). Fall through to the
			// software FFmpeg path, which decodes the (native) stream and downscales for present.
			Status = "GPU decode unavailable; falling back to software decoder.";
			AppLog.Write("High-resolution D3D path failed; falling back to FFmpeg software decoder for " + reason + ".");
		}
		// Software (FFmpeg) decode of >1080 streams (e.g. GPU-fault fallback at native res) is heavy;
		// cap the decoded/presented output to keep decode + present load manageable.
		MaxRenderWidth = _resolveSoftwareMaxRenderWidth(config);
		OutputFps = _resolveSoftwareOutputFps(config, MaxRenderWidth);
		// Every callback below checks that the raising decoder is still the active one: a
		// callback from a decoder that StartFresh has already replaced (its Dispose only waits
		// ~1 s for the read/write tasks) must not touch the new decoder's state.
		ISoftwareVideoDecoder decoder = _factory.CreateSoftware(config.Width, config.Height, config.Fps, MaxRenderWidth, OutputFps);
		_softwareDecoder = decoder;
		decoder.DumpH264Enabled = _dumpH264;
		decoder.StatusChanged += message => _view.Post(delegate
		{
			if (!ReferenceEquals(_softwareDecoder, decoder))
			{
				return;
			}
			Status = message;
			AppLog.Write("Decoder status: " + message);
			if (IsDecoderResyncError(message))
			{
				Status = "decoder resync warning: " + ReceiverChromeText.CompactStatus(message);
			}
			else
			{
				_view.SetStatus(message);
			}
			// NOTE: Do NOT restart the decoder on mb_width/height overflow or
			// sps_id-out-of-range. Those are symptoms of mid-stream bitstream corruption,
			// not a genuine SPS change (no SPS NAL actually arrives). Restarting only
			// re-arms the keyframe gate, and the Mac sender does not emit a fresh IDR on
			// demand, so the gate gets stuck dropping P-slices ("saw NAL 1") and the
			// decoder never recovers. Let ffmpeg keep its state and resync on the next IDR.
			_view.UpdateDiagnostics();
		}, DispatcherPriority.Background);
		decoder.FrameDecoded += frame =>
		{
			if (!ReferenceEquals(Volatile.Read(ref _softwareDecoder), decoder))
			{
				frame.Release();
				return;
			}
			_view.QueueSoftwareFrame(frame);
		};
		// Posted, not invoked: this fires from the decoder's read task, which Dispose() waits on
		// from the UI thread, so a blocking Invoke here could deadlock for that wait.
		decoder.DecoderRestarted += () => _view.Post(delegate
		{
			if (!ReferenceEquals(_softwareDecoder, decoder))
			{
				return;
			}
			CountRestart();
			_view.RequireH264Keyframe();
			AppLog.Write("Decoder restarted (hwaccel fallback); requiring fresh SPS/PPS keyframe.");
			_view.UpdateDiagnostics();
		}, DispatcherPriority.Normal);
		decoder.Start();
		Status = "started";
		_sinkAcceptingInput = true;
		_view.FlushPendingVideo();
		AppLog.Write("FFmpeg software decoder started for " + reason + ".");
		StartWatchdog(config, _view.ConnectionGeneration);
	}

	private bool TryStartGpu(StreamConfig config, string reason)
	{
		try
		{
			(IHighResolutionD3DPresenter presenter, IHighResolutionD3DDecoder decoder) = _factory.CreateGpu(config);
			decoder.DumpH264Enabled = _dumpH264;
			_gpuPresenter = presenter;
			_gpuDecoder = decoder;
			// Host the swap-chain child window in the video stage (bypasses WPF D3DImage composition).
			_view.AttachGpuPresenter(presenter, config);
			presenter.StatusChanged += message => _view.Post(delegate
			{
				AppLog.Write("D3D11 presenter status: " + message);
				_view.UpdateDiagnostics();
			}, DispatcherPriority.Background);
			// Each callback checks that this decoder is still the active one. Without that, a
			// queued Faulted from a decoder that StartFresh (or a synchronous present failure)
			// already replaced would dispose the replacement and disable the GPU path for the
			// session, and a late FrameDecoded could hand a texture from the old device to the
			// new presenter.
			decoder.StatusChanged += message => _view.Post(delegate
			{
				if (!ReferenceEquals(_gpuDecoder, decoder))
				{
					return;
				}
				Status = message;
				AppLog.Write("Media Foundation D3D11 decoder status: " + message);
				_view.SetStatus(message);
				_view.UpdateDiagnostics();
			}, DispatcherPriority.Background);
			decoder.Faulted += message => _view.Post(delegate
			{
				if (!ReferenceEquals(_gpuDecoder, decoder))
				{
					AppLog.Write("Ignoring fault from a superseded Media Foundation decoder: " + message);
					return;
				}
				HandleGpuFatal("High-resolution D3D decoder faulted: " + message);
			}, DispatcherPriority.Background);
			decoder.FrameDecoded += frame =>
			{
				if (!ReferenceEquals(Volatile.Read(ref _gpuDecoder), decoder))
				{
					frame.Dispose();
					return;
				}
				_view.QueueGpuFrame(frame);
			};
			decoder.Start();
			Status = "Media Foundation D3D11 decoder started";
			AppLog.Write($"High-resolution D3D path active for {reason}: {config.Width}x{config.Height}@{config.Fps}, gpuBinding=Vortice, d3d11MultithreadProtected={presenter.IsMultithreadProtected}.");
			return true;
		}
		catch (Exception ex)
		{
			Status = "Media Foundation D3D11 path failed: " + ex.Message;
			AppLog.Write("Media Foundation D3D11 path failed; high-resolution fallback is blocked: " + ex);
			_gpuDecoder?.Dispose();
			_gpuDecoder = null;
			DisposeGpuPresenter();
			_view.ReleasePendingFrames();
			return false;
		}
	}

	/// <summary>
	/// The GPU engine failed at runtime (decoder fault or a present that threw): disable it for
	/// the rest of this session and restart on the software decoder so the mirror survives a
	/// device-lost/TDR.
	/// </summary>
	public void HandleGpuFatal(string message)
	{
		AppLog.Write(message);
		Status = message;
		_gpuPathDisabledThisSession = true;
		_gpuDecoder?.Dispose();
		_gpuDecoder = null;
		DisposeGpuPresenter();
		_view.ReleasePendingFrames();
		_view.RequireH264Keyframe();
		StreamConfig? config = _view.CurrentStreamConfig;
		if (config != null)
		{
			_view.SetStatus("GPU decode faulted; switching to software decoder.");
			AppLog.Write("GPU decode faulted; restarting on FFmpeg software decoder.");
			StartFresh(config, "GPU fault fallback");
		}
		else
		{
			_view.SetStatus("GPU decode failed.");
			_view.ShowEmptyStateAfterGpuFailure();
		}
		_view.UpdateDiagnostics();
	}

	/// <summary>
	/// Stops and disposes both engines and the presenter (session end, disconnect). Each step is
	/// guarded so one failing dispose does not leave the others running.
	/// </summary>
	public void Stop()
	{
		CleanupGuards.RunStep("video watchdog stop", StopWatchdog);
		_sinkAcceptingInput = false;
		ISoftwareVideoDecoder? softwareDecoder = _softwareDecoder;
		_softwareDecoder = null;
		CleanupGuards.RunStep("ffmpeg decoder dispose", () => softwareDecoder?.Dispose());
		IHighResolutionD3DDecoder? gpuDecoder = _gpuDecoder;
		_gpuDecoder = null;
		CleanupGuards.RunStep("Media Foundation D3D decoder dispose", () => gpuDecoder?.Dispose());
		CleanupGuards.RunStep("D3D presenter dispose", DisposeGpuPresenter);
		OutputFps = 0;
		MaxRenderWidth = _defaultMaxRenderWidth;
	}

	/// <summary>A new connection: the GPU engine gets another chance and the counters start over.</summary>
	public void ResetForNewConnection()
	{
		StopWatchdog();
		Interlocked.Exchange(ref _restarts, 0L);
		_gpuPathDisabledThisSession = false;
		Status = "waiting for stream config";
		_sinkAcceptingInput = false;
	}

	/// <summary>Safe from any thread (the first video packet stops it from the receive thread).</summary>
	public void StopWatchdog()
	{
		CancellationTokenSource? cts = Interlocked.Exchange(ref _watchdogCts, null);
		if (cts == null)
		{
			return;
		}

		cts.Cancel();
		cts.Dispose();
	}

	private void StartWatchdog(StreamConfig config, int generation)
	{
		StopWatchdog();
		var cts = new CancellationTokenSource();
		_watchdogCts = cts;
		CancellationToken token = cts.Token;
		Task.Run(async delegate
		{
			try
			{
				await Task.Delay(_noVideoWatchdogDelay, token);
				if (token.IsCancellationRequested ||
					generation != _view.ConnectionGeneration ||
					_view.ReceivedVideoPackets > 0)
				{
					return;
				}

				_view.Post(delegate
				{
					if (token.IsCancellationRequested ||
						generation != _view.ConnectionGeneration ||
						_view.ReceivedVideoPackets > 0)
					{
						return;
					}

					Status = $"waiting for video packets ({config.Width}x{config.Height} @ {config.Fps})";
					_view.SetStatus("Connected, waiting for video frames.");
					AppLog.Write("No video packets received " + _noVideoWatchdogDelay.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "s after stream config. Sender sent config/control data, but no H.264 video payload yet.");
					_view.UpdateDiagnostics();
				}, DispatcherPriority.Normal);
			}
			catch (OperationCanceledException)
			{
			}
		});
	}

	private void DisposeGpuPresenter()
	{
		IHighResolutionD3DPresenter? presenter = _gpuPresenter;
		if (presenter == null)
		{
			return;
		}
		_gpuPresenter = null;
		try
		{
			_view.DetachGpuPresenter(presenter);
		}
		catch
		{
		}
		try
		{
			presenter.Dispose();
		}
		catch
		{
		}
	}

	internal static bool IsDecoderResyncError(string message)
	{
		if (!message.Contains("non-existing PPS", StringComparison.OrdinalIgnoreCase)
			&& !message.Contains("Invalid data found", StringComparison.OrdinalIgnoreCase)
			&& !message.Contains("mb_width/height overflow", StringComparison.OrdinalIgnoreCase)
			&& !message.Contains("reference count overflow", StringComparison.OrdinalIgnoreCase)
			&& !(message.Contains("sps_id", StringComparison.OrdinalIgnoreCase) && message.Contains("out of range", StringComparison.OrdinalIgnoreCase)))
		{
			return message.Contains("no frame", StringComparison.OrdinalIgnoreCase);
		}
		return true;
	}
}
