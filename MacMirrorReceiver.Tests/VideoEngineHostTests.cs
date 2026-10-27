using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using MacMirrorReceiver;
using MacMirrorReceiver.Protocol;
using MacMirrorReceiver.Video;
using Xunit;

namespace MacMirrorReceiver.Tests;

/// <summary>
/// Drives <see cref="VideoEngineHost"/> with fake decoders. The GPU-fault fallback could not be
/// reproduced on a device (a driver reset did not remove the D3D device), so these are its only
/// direct coverage.
/// </summary>
public sealed class VideoEngineHostTests
{
	private static readonly StreamConfig HighResolution = new StreamConfig { Width = 2560, Height = 1440, Fps = 60 };

	[Fact]
	public void StartFresh_UsesTheGpuEngineWhenThePolicyAllowsIt()
	{
		var harness = new Harness(useGpu: true);

		harness.Host.StartFresh(HighResolution, "stream config");

		FakeGpuDecoder gpu = Assert.Single(harness.Factory.GpuDecoders);
		Assert.Same(gpu, harness.Host.GpuDecoder);
		Assert.True(gpu.Started);
		Assert.Empty(harness.Factory.SoftwareDecoders);
		Assert.Equal(1, harness.View.Attached);
		Assert.True(harness.Host.SinkAcceptingInput);
		Assert.Equal(1, harness.View.Flushes);
		Assert.Equal(2560, harness.Host.MaxRenderWidth);
		Assert.Equal(30, harness.Host.OutputFps); // clamped to the GPU engine's fps cap
		Assert.Equal("Media Foundation D3D11 decoder started", harness.Host.Status);
	}

	[Fact]
	public void GpuFault_FallsBackToSoftwareOnce_AndKeepsTheGpuOffForTheSession()
	{
		var harness = new Harness(useGpu: true);
		harness.View.CurrentStreamConfig = HighResolution;
		harness.Host.StartFresh(HighResolution, "stream config");
		FakeGpuDecoder gpu = harness.Factory.GpuDecoders[0];
		FakePresenter presenter = harness.Factory.Presenters[0];

		gpu.RaiseFaulted("device removed");

		Assert.True(harness.Host.GpuPathDisabledThisSession);
		Assert.True(gpu.Disposed);
		Assert.True(presenter.Disposed);
		Assert.Equal(1, harness.View.Detached);
		Assert.Null(harness.Host.GpuDecoder);
		FakeSoftwareDecoder software = Assert.Single(harness.Factory.SoftwareDecoders);
		Assert.Same(software, harness.Host.SoftwareDecoder);
		Assert.True(software.Started);
		Assert.Contains("GPU decode faulted; switching to software decoder.", harness.View.Statuses);
		Assert.True(harness.View.KeyframeRequests >= 1);

		// A late second fault from the disposed decoder is ignored, and a new stream config in
		// the same session stays on software.
		gpu.RaiseFaulted("device removed again");
		harness.Host.StartFresh(HighResolution, "stream config");

		Assert.Single(harness.Factory.GpuDecoders);
		Assert.Equal(2, harness.Factory.SoftwareDecoders.Count);
		Assert.Null(harness.Host.GpuDecoder);
	}

	[Fact]
	public void FaultFromASupersededGpuDecoder_IsIgnored()
	{
		var harness = new Harness(useGpu: true);
		harness.View.CurrentStreamConfig = HighResolution;
		harness.Host.StartFresh(HighResolution, "stream config");
		FakeGpuDecoder first = harness.Factory.GpuDecoders[0];
		harness.Host.StartFresh(HighResolution, "stream config");
		FakeGpuDecoder second = harness.Factory.GpuDecoders[1];

		first.RaiseFaulted("late fault from the old device");

		Assert.False(harness.Host.GpuPathDisabledThisSession);
		Assert.Same(second, harness.Host.GpuDecoder);
		Assert.False(second.Disposed);
		Assert.Empty(harness.Factory.SoftwareDecoders);
	}

	[Fact]
	public void GpuStartFailure_FallsBackToSoftwareWithoutDisablingTheGpuForTheSession()
	{
		var harness = new Harness(useGpu: true);
		harness.Factory.FailGpu = true;

		harness.Host.StartFresh(HighResolution, "stream config");

		Assert.Null(harness.Host.GpuDecoder);
		Assert.Null(harness.Host.GpuPresenter);
		Assert.Single(harness.Factory.SoftwareDecoders);
		Assert.False(harness.Host.GpuPathDisabledThisSession);
		Assert.Equal("started", harness.Host.Status);
		Assert.Equal(1920, harness.Host.MaxRenderWidth); // software output cap
	}

	[Fact]
	public void ResetForNewConnection_GivesTheGpuEngineAnotherChance()
	{
		var harness = new Harness(useGpu: true);
		harness.View.CurrentStreamConfig = HighResolution;
		harness.Host.StartFresh(HighResolution, "stream config");
		harness.Factory.GpuDecoders[0].RaiseFaulted("device removed");
		Assert.True(harness.Host.GpuPathDisabledThisSession);

		harness.Host.Stop();
		harness.Host.ResetForNewConnection();
		harness.Host.StartFresh(HighResolution, "stream config");

		Assert.False(harness.Host.GpuPathDisabledThisSession);
		Assert.Equal(2, harness.Factory.GpuDecoders.Count);
		Assert.Same(harness.Factory.GpuDecoders[1], harness.Host.GpuDecoder);
		Assert.Equal("Media Foundation D3D11 decoder started", harness.Host.Status);
	}

	[Fact]
	public void HandleGpuFatal_WithoutAStreamConfig_ShowsTheIdleSurface()
	{
		var harness = new Harness(useGpu: true);
		harness.Host.StartFresh(HighResolution, "stream config");
		harness.View.CurrentStreamConfig = null;

		harness.Host.HandleGpuFatal("High-resolution D3D present failed: boom");

		Assert.Empty(harness.Factory.SoftwareDecoders);
		Assert.Equal(1, harness.View.EmptyStateShown);
		Assert.Contains("GPU decode failed.", harness.View.Statuses);
	}

	[Fact]
	public void FramesFromAReplacedSoftwareDecoder_AreReleasedNotPresented()
	{
		var harness = new Harness(useGpu: false);
		harness.Host.StartFresh(HighResolution, "stream config");
		FakeSoftwareDecoder first = harness.Factory.SoftwareDecoders[0];
		harness.Host.StartFresh(HighResolution, "window size changed");
		FakeSoftwareDecoder second = harness.Factory.SoftwareDecoders[1];
		var released = new List<string>();

		first.RaiseFrame(Frame("old", released));
		second.RaiseFrame(Frame("new", released));

		Assert.True(first.Disposed);
		Assert.Equal(new[] { "old" }, released);
		Assert.Single(harness.View.SoftwareFrames);
	}

	[Fact]
	public void FramesFromAReplacedGpuDecoder_AreDisposedNotPresented()
	{
		var harness = new Harness(useGpu: true);
		harness.Host.StartFresh(HighResolution, "stream config");
		FakeGpuDecoder first = harness.Factory.GpuDecoders[0];
		harness.Host.StartFresh(HighResolution, "stream config");
		var stale = new FakeGpuFrame();
		var current = new FakeGpuFrame();

		first.RaiseFrame(stale);
		harness.Factory.GpuDecoders[1].RaiseFrame(current);

		Assert.True(stale.Disposed);
		Assert.False(current.Disposed);
		Assert.Same(current, Assert.Single(harness.View.GpuFrames));
	}

	[Fact]
	public void SoftwareDecoderRestart_CountsAndRequiresAKeyframe_OnlyForTheActiveDecoder()
	{
		var harness = new Harness(useGpu: false);
		harness.Host.StartFresh(HighResolution, "stream config");
		FakeSoftwareDecoder first = harness.Factory.SoftwareDecoders[0];
		harness.Host.StartFresh(HighResolution, "window size changed");
		FakeSoftwareDecoder second = harness.Factory.SoftwareDecoders[1];

		first.RaiseRestarted();
		Assert.Equal(0, harness.Host.Restarts);
		Assert.Equal(0, harness.View.KeyframeRequests);

		second.RaiseRestarted();
		Assert.Equal(1, harness.Host.Restarts);
		Assert.Equal(1, harness.View.KeyframeRequests);

		harness.Host.ResetForNewConnection();
		Assert.Equal(0, harness.Host.Restarts);
	}

	[Fact]
	public void Stop_DisposesBothEnginesAndThePresenter()
	{
		var harness = new Harness(useGpu: true);
		harness.Host.StartFresh(HighResolution, "stream config");
		FakeGpuDecoder gpu = harness.Factory.GpuDecoders[0];
		FakePresenter presenter = harness.Factory.Presenters[0];

		harness.Host.Stop();

		Assert.True(gpu.Disposed);
		Assert.True(presenter.Disposed);
		Assert.Equal(1, harness.View.Detached);
		Assert.False(harness.Host.HasDecoder);
		Assert.False(harness.Host.SinkAcceptingInput);
		Assert.Equal(0, harness.Host.OutputFps);
		Assert.Equal(3840, harness.Host.MaxRenderWidth);
	}

	[Fact]
	public void SoftwareStatus_ShortensResyncErrorsAndShowsOthers()
	{
		var harness = new Harness(useGpu: false);
		harness.Host.StartFresh(HighResolution, "stream config");
		FakeSoftwareDecoder decoder = harness.Factory.SoftwareDecoders[0];

		decoder.RaiseStatus("[h264 @ 0x1] non-existing PPS 0 referenced, decoding anyway with a much longer tail");
		Assert.StartsWith("decoder resync warning: ", harness.Host.Status);
		Assert.DoesNotContain(harness.View.Statuses, s => s.Contains("non-existing PPS"));

		decoder.RaiseStatus("FFmpeg decoder started");
		Assert.Equal("FFmpeg decoder started", harness.Host.Status);
		Assert.Contains("FFmpeg decoder started", harness.View.Statuses);
	}

	[Fact]
	public void Watchdog_ReportsWhenNoVideoArrives_AndStaysQuietOnceItDoes()
	{
		var quiet = new Harness(useGpu: false, watchdogDelay: TimeSpan.FromMilliseconds(30));
		quiet.Host.StartFresh(HighResolution, "stream config");
		Assert.True(SpinWait.SpinUntil(() => quiet.View.Statuses.Contains("Connected, waiting for video frames."), TimeSpan.FromSeconds(5)));
		Assert.StartsWith("waiting for video packets (2560x1440 @ 60)", quiet.Host.Status);

		var flowing = new Harness(useGpu: false, watchdogDelay: TimeSpan.FromMilliseconds(30));
		flowing.View.ReceivedVideoPackets = 1;
		flowing.Host.StartFresh(HighResolution, "stream config");
		Thread.Sleep(300);
		Assert.DoesNotContain("Connected, waiting for video frames.", flowing.View.Statuses);
	}

	private static VideoFrame Frame(string id, List<string> released) => new VideoFrame
	{
		Width = 1,
		Height = 1,
		Buffer = new byte[4],
		ReturnBuffer = _ => released.Add(id)
	};

	private sealed class Harness
	{
		public Harness(bool useGpu, TimeSpan? watchdogDelay = null)
		{
			View = new FakeView();
			Factory = new FakeFactory();
			Host = new VideoEngineHost(
				View,
				Factory,
				_ => useGpu,
				config => Math.Min(config.Width, 1920),
				(config, _) => Math.Min(config.Fps, 60),
				gpuMaxFps: 30,
				defaultMaxRenderWidth: 3840,
				dumpH264: false,
				noVideoWatchdogDelay: watchdogDelay ?? TimeSpan.FromMinutes(5));
		}

		public FakeView View { get; }

		public FakeFactory Factory { get; }

		public VideoEngineHost Host { get; }
	}

	private sealed class FakeView : IVideoEngineView
	{
		private readonly object _gate = new object();
		private readonly List<string> _statuses = new List<string>();

		public int Attached;
		public int Detached;
		public int Flushes;
		public int KeyframeRequests;
		public int EmptyStateShown;
		public readonly List<VideoFrame> SoftwareFrames = new List<VideoFrame>();
		public readonly List<IHighResolutionD3DFrame> GpuFrames = new List<IHighResolutionD3DFrame>();

		public IReadOnlyList<string> Statuses
		{
			get
			{
				lock (_gate)
				{
					return _statuses.ToArray();
				}
			}
		}

		public int ConnectionGeneration => 7;

		public long ReceivedVideoPackets { get; set; }

		public StreamConfig? CurrentStreamConfig { get; set; }

		// Synchronous: tests observe each posted callback as soon as the event is raised.
		public void Post(Action action, DispatcherPriority priority)
		{
			lock (_gate)
			{
				action();
			}
		}

		public void AttachGpuPresenter(IHighResolutionD3DPresenter presenter, StreamConfig config) => Attached++;

		public void DetachGpuPresenter(IHighResolutionD3DPresenter presenter) => Detached++;

		public void ShowVideoSurface()
		{
		}

		public void ShowEmptyStateAfterGpuFailure() => EmptyStateShown++;

		public void ReleasePendingFrames()
		{
		}

		public void QueueSoftwareFrame(VideoFrame frame) => SoftwareFrames.Add(frame);

		public void QueueGpuFrame(IHighResolutionD3DFrame frame) => GpuFrames.Add(frame);

		public void FlushPendingVideo() => Flushes++;

		public void RequireH264Keyframe() => KeyframeRequests++;

		public void BeginLatencyWarmup()
		{
		}

		public void SetStatus(string message)
		{
			lock (_gate)
			{
				_statuses.Add(message);
			}
		}

		public void UpdateDiagnostics()
		{
		}
	}

	private sealed class FakeFactory : IVideoDecoderFactory
	{
		public readonly List<FakeSoftwareDecoder> SoftwareDecoders = new List<FakeSoftwareDecoder>();
		public readonly List<FakeGpuDecoder> GpuDecoders = new List<FakeGpuDecoder>();
		public readonly List<FakePresenter> Presenters = new List<FakePresenter>();
		public bool FailGpu;

		public ISoftwareVideoDecoder CreateSoftware(int width, int height, int fps, int maxRenderWidth, int outputFps)
		{
			var decoder = new FakeSoftwareDecoder();
			SoftwareDecoders.Add(decoder);
			return decoder;
		}

		public (IHighResolutionD3DPresenter Presenter, IHighResolutionD3DDecoder Decoder) CreateGpu(StreamConfig config)
		{
			if (FailGpu)
			{
				throw new InvalidOperationException("no hardware H.264 decoder");
			}
			var presenter = new FakePresenter();
			var decoder = new FakeGpuDecoder();
			Presenters.Add(presenter);
			GpuDecoders.Add(decoder);
			return (presenter, decoder);
		}
	}

	private sealed class FakeSoftwareDecoder : ISoftwareVideoDecoder
	{
		public bool Started { get; private set; }
		public bool Disposed { get; private set; }
		public int OutputWidth => 1920;
		public int OutputHeight => 1080;
		public int OutputFrameBytes => 1920 * 1080 * 4;
		public int QueuedInputPackets => 0;
		public long QueuedInputBytes => 0;
		public long DroppedInputPackets => 0;
		public long AcceptedInputPackets => 0;
		public long WrittenInputPackets => 0;
		public long LatestWriteMilliseconds => 0;
		public long MaxWriteMilliseconds => 0;
		public long WriteStalls => 0;
		public int PipelineDepthFrames => 0;
		public bool DumpH264Enabled { get; set; }
		public event Action<string>? StatusChanged;
		public event Action<VideoFrame>? FrameDecoded;
		public event Action? DecoderRestarted;
		public void Start() => Started = true;
		public bool QueueH264(byte[] payload, ulong sourceTimestampNanos, long receivedTick) => true;
		public int ClearPendingInput() => 0;
		public void Dispose() => Disposed = true;
		public void RaiseStatus(string message) => StatusChanged?.Invoke(message);
		public void RaiseFrame(VideoFrame frame) => FrameDecoded?.Invoke(frame);
		public void RaiseRestarted() => DecoderRestarted?.Invoke();
	}

	private sealed class FakeGpuDecoder : IHighResolutionD3DDecoder
	{
		public bool Started { get; private set; }
		public bool Disposed { get; private set; }
		public int OutputWidth => 2560;
		public int OutputHeight => 1440;
		public int OutputFrameBytes => 0;
		public bool IsFaulted { get; private set; }
		public int QueuedInputPackets => 0;
		public long QueuedInputBytes => 0;
		public long DroppedInputPackets => 0;
		public long AcceptedInputPackets => 0;
		public long WrittenInputPackets => 0;
		public long LatestWriteMilliseconds => 0;
		public long MaxWriteMilliseconds => 0;
		public long WriteStalls => 0;
		public bool DumpH264Enabled { get; set; }
		public event Action<string>? StatusChanged;
		public event Action<IHighResolutionD3DFrame>? FrameDecoded;
		public event Action<string>? Faulted;
		public void Start() => Started = true;
		public bool QueueH264(byte[] payload, ulong sourceTimestampNanos, long receivedTick) => true;
		public int ClearPendingInput() => 0;
		public void Dispose() => Disposed = true;
		public void RaiseStatus(string message) => StatusChanged?.Invoke(message);
		public void RaiseFrame(IHighResolutionD3DFrame frame) => FrameDecoded?.Invoke(frame);

		public void RaiseFaulted(string message)
		{
			IsFaulted = true;
			Faulted?.Invoke(message);
		}
	}

	private sealed class FakePresenter : IHighResolutionD3DPresenter
	{
		public bool Disposed { get; private set; }
		// The fake view never touches the WPF element, so tests do not need an STA thread.
		public FrameworkElement View => null!;
		public bool IsMultithreadProtected => true;
		public event Action<string>? StatusChanged;
		public void PresentFrame(IHighResolutionD3DFrame frame)
		{
		}
		public void Dispose() => Disposed = true;
		public void RaiseStatus(string message) => StatusChanged?.Invoke(message);
	}

	private sealed class FakeGpuFrame : IHighResolutionD3DFrame
	{
		public bool Disposed { get; private set; }
		public int Width => 1;
		public int Height => 1;
		public int Fps => 30;
		public int SubresourceIndex => 0;
		public long ReceivedTick => 0;
		public long DecodedTick => 0;
		public ulong SourceTimestampNanos => 0;
		public void Dispose() => Disposed = true;
	}
}
