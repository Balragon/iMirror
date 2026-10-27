using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MacMirrorReceiver.Video;

public sealed class FfmpegDecoder : ISoftwareVideoDecoder
{
	internal const int MaxQueuedInputPackets = 1024;

	internal const long MaxQueuedInputBytes = 64L * 1024L * 1024L;

	private const long WriteStallThresholdMs = 35;

	private const int OutputBytesPerPixel = 4;

	private const string OutputPixelFormat = "bgra";

	// Written after every packet. FFmpeg's raw H.264 parser only closes an access unit when it
	// sees the start of the next one, so without this each frame waits for the following packet
	// (one frame at 60fps, seconds for sparse updates). An AUD marks the boundary immediately.
	private static readonly byte[] AccessUnitDelimiter = { 0x00, 0x00, 0x00, 0x01, 0x09, 0xF0 };

	private readonly int _inputWidth;

	private readonly int _inputHeight;

	private readonly int _inputFps;

	private readonly int _outputWidth;

	private readonly int _outputHeight;

	private readonly int _targetOutputFps;

	private readonly int _frameSize;

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	private readonly object _processGate = new object();

	private readonly PipeInputQueue<H264InputPacket> _inputQueue = new PipeInputQueue<H264InputPacket>(MaxQueuedInputPackets, MaxQueuedInputBytes, packet => packet.Payload.Length);

	private readonly PipeWriteMetrics _writeMetrics = new PipeWriteMetrics(WriteStallThresholdMs);

	private readonly object _overflowStatusGate = new object();

	private long _overflowDropsSinceStatus;

	private long _lastOverflowStatusTick;

	private long _latestInputReceivedTick;

	private long _latestInputSourceTimestampNanos;

	// True end-to-end latency instrumentation. Stamping each decoded frame with the LATEST written
	// packet's receivedTick understates latency badly when frames buffer inside FFmpeg (the output
	// frame is older than the most recent input). Instead, FIFO the receivedTick of each written
	// packet and dequeue one per decoded frame, so a frame carries the receivedTick of the packet
	// that actually produced it. The queue depth (= written but not yet emitted) is the FFmpeg
	// pipeline buffering, which localizes hidden latency.
	private readonly object _fifoGate = new object();

	private readonly Queue<long> _writtenReceivedTicks = new Queue<long>();

	private long _lastReadReceivedTick;

	private int _pipelineDepthFrames;

	private Process? _process;

	private Task? _readTask;

	private Task? _writeTask;

	private long _decodedOutputFrames;

	// Optional debug capture of the H.264 Annex B elementary stream, enabled by setting the
	// IMIRROR_DUMP_H264 environment variable (to a file path, or "1"/"true" for a default
	// timestamped file). When enabled, two files are written so decrypt vs. feeding problems
	// can be separated (the streams are raw video and contain no key material):
	//   *.submitted.h264 - the complete post-gate stream handed to QueueH264, BEFORE any
	//       backpressure drop. Use this to answer "is the decrypted/converted stream valid?"
	//   *.written.h264   - the exact bytes written to the FFmpeg process stdin (post-drop).
	//       Use this to answer "did in-app feeding/backpressure corrupt FFmpeg's input?"
	private const long MaxDumpBytes = 512L * 1024L * 1024L;

	private static int s_dumpInstanceCounter;

	private DumpFile? _submittedDump;

	private DumpFile? _writtenDump;

	private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(12.0);

	// Default decoder path: use software first. The AirPlay H.264 bytes captured from the
	// in-app pipe decode cleanly offline, but the Windows hwaccel paths can still produce
	// zero frames over the live pipe. Set IMIRROR_PREFER_HARDWARE=1 to try D3D11VA/DXVA2
	// first for performance testing.
	// Diagnostic override: set IMIRROR_FORCE_SOFTWARE=1 to skip the hardware candidates and
	// decode in software only. Useful to confirm whether a "zero decoded frames" symptom is
	// caused by the hwaccel path rather than by the H.264 stream itself.
	private readonly DecoderCandidate[] _decoderCandidates = BuildDecoderCandidates();

	private static DecoderCandidate[] BuildDecoderCandidates()
	{
		string? force = Environment.GetEnvironmentVariable("IMIRROR_FORCE_SOFTWARE");
		if (!string.IsNullOrWhiteSpace(force) && force != "0" && !string.Equals(force, "false", StringComparison.OrdinalIgnoreCase))
		{
			return new[] { new DecoderCandidate("software", string.Empty) };
		}

		string? preferHardware = Environment.GetEnvironmentVariable("IMIRROR_PREFER_HARDWARE");
		if (!string.IsNullOrWhiteSpace(preferHardware) && preferHardware != "0" && !string.Equals(preferHardware, "false", StringComparison.OrdinalIgnoreCase))
		{
			return new[]
			{
				new DecoderCandidate("d3d11va", "-hwaccel d3d11va "),
				new DecoderCandidate("dxva2", "-hwaccel dxva2 "),
				new DecoderCandidate("software", string.Empty)
			};
		}

		return new[]
		{
			new DecoderCandidate("software", string.Empty),
			new DecoderCandidate("d3d11va", "-hwaccel d3d11va "),
			new DecoderCandidate("dxva2", "-hwaccel dxva2 ")
		};
	}

	private int _decoderCandidateIndex;

	public long DroppedInputPackets => _inputQueue.Dropped;

	public long AcceptedInputPackets => _inputQueue.Accepted;

	public long WrittenInputPackets => _writeMetrics.Written;

	public long LatestWriteMilliseconds => _writeMetrics.LatestMilliseconds;

	public long MaxWriteMilliseconds => _writeMetrics.MaxMilliseconds;

	public long WriteStalls => _writeMetrics.Stalls;

	public int PipelineDepthFrames => Volatile.Read(ref _pipelineDepthFrames);

	public int OutputWidth => _outputWidth;

	public int OutputHeight => _outputHeight;

	public int OutputFrameBytes => _frameSize;

	public int QueuedInputPackets => _inputQueue.Count;

	public long QueuedInputBytes => _inputQueue.Bytes;

	public event Action<string>? StatusChanged;

	public event Action<VideoFrame>? FrameDecoded;

	// Raised when the decoder internally restarts its FFmpeg process (e.g. hardware-accel
	// fallback d3d11va -> dxva2 -> software). The new process has no H.264 extradata, so
	// the upstream Annex B gate must re-establish SPS/PPS and wait for the next keyframe.
	public event Action? DecoderRestarted;

	public bool DumpH264Enabled { get; set; }

	public FfmpegDecoder(int width, int height, int inputFps, int maxOutputWidth, int targetOutputFps)
	{
		_inputWidth = width;
		_inputHeight = height;
		_inputFps = Math.Max(1, inputFps);
		_targetOutputFps = targetOutputFps;
		if (width > maxOutputWidth)
		{
			_outputWidth = maxOutputWidth;
			_outputHeight = Math.Max(2, (int)Math.Round((double)height * ((double)maxOutputWidth / (double)width)));
			_outputHeight = _outputHeight / 2 * 2;
		}
		else
		{
			_outputWidth = width;
			_outputHeight = height;
		}
		_frameSize = checked(_outputWidth * _outputHeight * OutputBytesPerPixel);
	}

	public void Start()
	{
		string ffmpegPath = FfmpegPipe.FindFfmpegOrThrow();
		TryOpenH264Dump();
		StartFfmpegProcess(ffmpegPath);
		_writeTask = Task.Run(WriteInputLoopAsync);
	}

	private void TryOpenH264Dump()
	{
		string? setting = Environment.GetEnvironmentVariable("IMIRROR_DUMP_H264");
		if (!DumpH264Enabled && string.IsNullOrWhiteSpace(setting))
		{
			return;
		}

		try
		{
			string basePath;
			if (string.IsNullOrWhiteSpace(setting) || setting == "1" || string.Equals(setting, "true", StringComparison.OrdinalIgnoreCase))
			{
				string fileName = "imirror-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".h264";
				basePath = Path.Combine(MacMirrorReceiver.AppPaths.DumpsDirectory, fileName);
			}
			else
			{
				basePath = setting;
				string? dir = Path.GetDirectoryName(basePath);
				if (!string.IsNullOrEmpty(dir))
				{
					Directory.CreateDirectory(dir);
				}
			}

			// Each decoder instance gets its own pair of files. With a fixed name, a decoder
			// restart (new stream config, render-width change, reconnection) would re-Create
			// the same paths and truncate the previous session's capture to 0 bytes.
			int instance = Interlocked.Increment(ref s_dumpInstanceCounter);
			string submittedPath = AppendSuffix(basePath, $"d{instance:00}.submitted");
			string writtenPath = AppendSuffix(basePath, $"d{instance:00}.written");
			_submittedDump = new DumpFile(submittedPath, MaxDumpBytes);
			_writtenDump = new DumpFile(writtenPath, MaxDumpBytes);
			_submittedDump.WriteFailed += message => this.StatusChanged?.Invoke("H264 submitted dump write failed; dump stopped: " + message);
			_writtenDump.WriteFailed += message => this.StatusChanged?.Invoke("H264 written dump write failed; dump stopped: " + message);
			this.StatusChanged?.Invoke($"H264 dump enabled (max {MaxDumpBytes / (1024 * 1024)}MB each): submitted={submittedPath}, written={writtenPath}.");
		}
		catch (Exception ex)
		{
			this.StatusChanged?.Invoke("H264 dump could not be opened: " + ex.Message);
		}
	}

	private static string AppendSuffix(string path, string suffix)
	{
		string dir = Path.GetDirectoryName(path) ?? string.Empty;
		string name = Path.GetFileNameWithoutExtension(path);
		string ext = Path.GetExtension(path);
		if (string.IsNullOrEmpty(ext))
		{
			ext = ".h264";
		}
		return Path.Combine(dir, name + "." + suffix + ext);
	}

	// Single-writer-safe capped append target for a raw H.264 elementary stream dump.
	private sealed class DumpFile : IDisposable
	{
		private readonly object _gate = new object();
		private readonly long _maxBytes;
		private readonly string _path;
		private FileStream? _stream;
		private long _bytes;
		private bool _capLogged;

		public event Action<string>? WriteFailed;

		public DumpFile(string path, long maxBytes)
		{
			_path = path;
			_maxBytes = maxBytes;
			_stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
		}

		public bool Write(byte[] payload)
		{
			lock (_gate)
			{
				if (_stream == null)
				{
					return false;
				}
				if (_bytes + payload.Length > _maxBytes)
				{
					bool firstCap = !_capLogged;
					_capLogged = true;
					return firstCap;
				}
				try
				{
					_stream.Write(payload, 0, payload.Length);
					_stream.Flush();
					_bytes += payload.Length;
				}
				catch (Exception ex)
				{
					// Stop dumping after the first write failure instead of failing silently on every
					// packet. Any exception is caught: this runs on the receive path and a diagnostic
					// file must never take the session down.
					_stream.Dispose();
					_stream = null;
					this.WriteFailed?.Invoke(ex.Message);
				}
				return false;
			}
		}

		public void Dispose()
		{
			lock (_gate)
			{
				try
				{
					_stream?.Dispose();
				}
				catch
				{
				}
				_stream = null;
			}
		}
	}

	private void StartFfmpegProcess(string ffmpegPath)
	{
		string arguments = BuildArguments();
		Process process = FfmpegPipe.Start(ffmpegPath, arguments);
		lock (_processGate)
		{
			_process = process;
		}
		this.StatusChanged?.Invoke($"FFmpeg started{GetDecoderLabel()}: {ffmpegPath} ({_inputWidth}x{_inputHeight} -> {_outputWidth}x{_outputHeight} @ {_targetOutputFps}fps)");
		this.StatusChanged?.Invoke($"FFmpeg cmd: {ffmpegPath} {arguments}");
		Task.Run(() => FfmpegPipe.ReadStderrLinesAsync(
			process,
			() => ReferenceEquals(_process, process),
			_cts.Token,
			OnStderrLine,
			message => this.StatusChanged?.Invoke("FFmpeg stderr reader stopped: " + message)));
		_readTask = Task.Run(() => ReadFramesAsync(process));
		Task.Run(() => WatchFirstFrameTimeoutAsync(process));
	}

	private string BuildArguments()
	{
		string videoFilter = BuildVideoFilterArgument();
		// probesize/analyzeduration must be large enough for avformat_find_stream_info to read a
		// full SPS and establish the real dimensions before the decoder is set up. With the
		// previous "-probesize 32 -analyzeduration 0" the hardware decoders (d3d11va/dxva2) could
		// be initialised without valid geometry and silently produce zero frames over a pipe,
		// even though the exact same bytes decode in software with default probing. 1MB / 200ms
		// is a cap (find_stream_info returns as soon as it has enough on a live stream), so this
		// keeps latency low while letting the decoder initialise reliably.
		// Do NOT add -fflags nobuffer: AVFMT_FLAG_NOBUFFER discards the packets consumed during
		// stream analysis instead of replaying them to the decoder. A mirror stream has a single
		// IDR at the start, so analysis eats the SPS/PPS/IDR and the decoder only ever sees
		// undecodable mid-GOP P-frames. Verified by per-flag A/B on captured streams: nobuffer
		// alone reproduces frames=0 / exit 69 (-max_error_rate exceeded); all other flags here
		// were individually harmless (109/109 frames).
		// Decode threading: software decode of high-motion 1080p60 (e.g. sports video) cannot keep
		// up on a single core, so the input queue backs up ~5s (measured: decoderQueue ~250-300
		// packets, true receive->render ~6s) even though the machine has idle cores. The AirPlay
		// mirror stream encodes one slice per frame, so SLICE threading gives no parallelism and
		// kept FFmpeg pinned to ~1 core. FRAME threading decodes consecutive frames across cores
		// and does parallelize this stream. It adds ~(threads) frames of pipeline delay (~50ms),
		// which is negligible against the multi-second queue backlog it removes on 1080p-class
		// streams. Smaller portrait/phone streams do not need that much parallelism; using fewer
		// frame threads keeps the software fallback below its latency tier without reintroducing
		// the old 1080p backlog.
		// NOTE (measured 2026-10-27, FFmpeg 8.1.1): libavcodec disables frame threading whenever
		// AV_CODEC_FLAG_LOW_DELAY is set, so with "-flags low_delay" the decoder actually runs on
		// one thread (cpu/real 1.0, same fps as -threads 1). It still decodes 1080p desktop
		// content at ~400 fps, so the flag stays; the thread settings below are effectively inert.
		// Output "-threads 1": the rawvideo encoder otherwise runs frame-threaded and holds three
		// frames before emitting the first, which is ~50ms at 60fps and seconds when the Mac sends
		// sparse updates (typing, cursor blink) — long enough for every such frame to be dropped as
		// stale before presentation.
		int decoderThreads = ResolveDecoderThreadCount(_outputWidth, _outputHeight, _targetOutputFps);
		return "-hide_banner -loglevel error -flags low_delay -probesize 1M -analyzeduration 200000 -avioflags direct -max_delay 0 "
			+ GetDecoderInputArgument()
			+ $"-threads {decoderThreads} -thread_type frame -f h264 -i pipe:0 "
			+ videoFilter
			+ $"-fps_mode passthrough -threads 1 -f rawvideo -pix_fmt {OutputPixelFormat} pipe:1";
	}

	internal static int ResolveDecoderThreadCount(int outputWidth, int outputHeight, int targetOutputFps)
	{
		long outputPixels = (long)Math.Max(1, outputWidth) * Math.Max(1, outputHeight);
		int fps = Math.Max(1, targetOutputFps);
		if (outputPixels <= 1_000_000L && fps <= 60)
		{
			return 2;
		}

		return 4;
	}

	private string BuildVideoFilterArgument()
	{
		var filters = new List<string>();
		if (_targetOutputFps > 0 && _targetOutputFps < _inputFps)
		{
			filters.Add($"fps={_targetOutputFps}");
		}
		if (_outputWidth != _inputWidth || _outputHeight != _inputHeight)
		{
			filters.Add($"scale={_outputWidth}:{_outputHeight}:flags=fast_bilinear");
		}
		return filters.Count == 0 ? string.Empty : "-vf " + string.Join(",", filters) + " ";
	}

	private string GetDecoderInputArgument()
	{
		return _decoderCandidates[_decoderCandidateIndex].InputArguments;
	}

	private string GetDecoderLabel()
	{
		return $" [decoder:{_decoderCandidates[_decoderCandidateIndex].Label}]";
	}

	private void OnStderrLine(string line)
	{
		// The trailing AccessUnitDelimiter has no picture after it when stdin closes.
		if (!line.Contains("missing picture in access unit with size 6", StringComparison.Ordinal))
		{
			this.StatusChanged?.Invoke(line);
		}
	}

	public bool QueueH264(byte[] payload, ulong sourceTimestampNanos, long receivedTick)
	{
		if (!FfmpegPipe.IsAlive(_process) || _cts.IsCancellationRequested)
		{
			return false;
		}
		if (_submittedDump != null && _submittedDump.Write(payload))
		{
			this.StatusChanged?.Invoke($"H264 submitted dump reached {MaxDumpBytes / (1024 * 1024)}MB cap; further packets are not captured.");
		}
		EnqueueInput(payload, sourceTimestampNanos, receivedTick);
		// A backpressure drop is counted in DroppedInputPackets, not reported as unavailable input:
		// false makes MainWindow log "input unavailable" once per dropped packet.
		return true;
	}

	// The overflow policy, split from QueueH264 so tests can drive it without an FFmpeg process.
	internal void EnqueueInput(byte[] payload, ulong sourceTimestampNanos, long receivedTick)
	{
		// On overflow keep the queued GOP and drop only the incoming non-IDR packet. FFmpeg conceals
		// the missing frame by repeating the previous reference, so the damage stays in the screen
		// regions that frame changed and heals as they change again. Flushing the queue and waiting
		// for the next keyframe instead loses video until reconnect: Mac senders send one IDR per
		// connection and none on demand. An incoming IDR can start over, so it flushes the queue.
		PipeEnqueueResult result = _inputQueue.Enqueue(new H264InputPacket(payload, sourceTimestampNanos, receivedTick), static packet => ContainsIdrNal(packet.Payload));
		if (result.Flushed > 0)
		{
			this.StatusChanged?.Invoke($"Decoder input overflow: flushed {result.Flushed} queued packet(s); resuming from incoming keyframe.");
		}

		long overflowDropsToReport = 0;
		lock (_overflowStatusGate)
		{
			if (result.Outcome == PipeEnqueueOutcome.DroppedIncoming)
			{
				_overflowDropsSinceStatus++;
			}
			// One status line per second at most; the first drop of an overflow reports immediately.
			if (_overflowDropsSinceStatus > 0)
			{
				long now = Environment.TickCount64;
				if (now - _lastOverflowStatusTick >= 1000)
				{
					overflowDropsToReport = _overflowDropsSinceStatus;
					_overflowDropsSinceStatus = 0;
					_lastOverflowStatusTick = now;
				}
			}
		}
		if (overflowDropsToReport > 0)
		{
			this.StatusChanged?.Invoke($"Decoder input overflow: dropped {overflowDropsToReport:N0} incoming non-keyframe(s) since last report and preserved queued GOP (queued={result.QueuedAfter}).");
		}
	}

	private static bool ContainsIdrNal(byte[] payload)
	{
		for (int i = 0; i + 3 < payload.Length; i++)
		{
			if (payload[i] != 0 || payload[i + 1] != 0)
			{
				continue;
			}
			int nalOffset;
			if (payload[i + 2] == 1)
			{
				nalOffset = i + 3;
			}
			else if (payload[i + 2] == 0 && i + 4 < payload.Length && payload[i + 3] == 1)
			{
				nalOffset = i + 4;
			}
			else
			{
				continue;
			}
			if (nalOffset < payload.Length && (payload[nalOffset] & 0x1F) == 5)
			{
				return true;
			}
			i = nalOffset - 1;
		}
		return false;
	}

	public int ClearPendingInput()
	{
		int count = _inputQueue.Clear();
		lock (_fifoGate)
		{
			// In-flight frames belong to the (about to be replaced) process; drop their ticks
			// so the new process's frames are not stamped with stale receivedTicks.
			_writtenReceivedTicks.Clear();
			Volatile.Write(ref _pipelineDepthFrames, 0);
		}
		return count;
	}

	private async Task WriteInputLoopAsync()
	{
		FfmpegPipe.SetCurrentThreadAboveNormal();
		try
		{
			await FfmpegPipe.RunInputLoopAsync(_inputQueue, WritePacketAsync, _cts.Token);
		}
		catch (Exception ex)
		{
			this.StatusChanged?.Invoke("Decoder input closed: " + ex.Message);
		}
	}

	// A packet for a dead process is skipped, not fatal: a hwaccel fallback may be replacing it.
	private async Task<bool> WritePacketAsync(H264InputPacket packet)
	{
		Process? process = _process;
		try
		{
			if (FfmpegPipe.IsAlive(process))
			{
				Interlocked.Exchange(ref _latestInputReceivedTick, packet.ReceivedTick);
				Interlocked.Exchange(ref _latestInputSourceTimestampNanos, unchecked((long)packet.SourceTimestampNanos));
				long startTick = Stopwatch.GetTimestamp();
				await process!.StandardInput.BaseStream.WriteAsync(packet.Payload, _cts.Token);
				await process.StandardInput.BaseStream.WriteAsync(AccessUnitDelimiter, _cts.Token);
				long endTick = Stopwatch.GetTimestamp();
				lock (_fifoGate)
				{
					_writtenReceivedTicks.Enqueue(packet.ReceivedTick);
				}
				if (_writtenDump != null && _writtenDump.Write(packet.Payload))
				{
					this.StatusChanged?.Invoke($"H264 written dump reached {MaxDumpBytes / (1024 * 1024)}MB cap; further packets are not captured.");
				}
				long writeMs = FfmpegPipe.ElapsedMilliseconds(startTick, endTick);
				if (_writeMetrics.Record(writeMs))
				{
					this.StatusChanged?.Invoke($"FFmpeg stdin write stall: {writeMs}ms, queued={QueuedInputPackets}, dropped={DroppedInputPackets:N0}");
				}
			}
		}
		catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is ObjectDisposedException)
		{
			this.StatusChanged?.Invoke("Decoder input retrying: " + ex.Message);
		}
		return true;
	}

	private async Task ReadFramesAsync(Process process)
	{
		FfmpegPipe.SetCurrentThreadAboveNormal();
		try
		{
			await FfmpegPipe.ReadFixedSizeFramesAsync(process.StandardOutput.BaseStream, _frameSize, OnFrameRead, _cts.Token);
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex2)
		{
			if (TryFallbackDecoder(process, ex2.Message))
			{
				return;
			}
			this.StatusChanged?.Invoke("Decoder stopped: " + ex2.Message);
		}
	}

	private void OnFrameRead(byte[] buffer)
	{
		long frameReceivedTick;
		lock (_fifoGate)
		{
			if (_writtenReceivedTicks.Count > 0)
			{
				frameReceivedTick = _writtenReceivedTicks.Dequeue();
				_lastReadReceivedTick = frameReceivedTick;
			}
			else
			{
				frameReceivedTick = _lastReadReceivedTick;
			}
			Volatile.Write(ref _pipelineDepthFrames, _writtenReceivedTicks.Count);
		}
		var frame = new VideoFrame
		{
			Width = _outputWidth,
			Height = _outputHeight,
			Buffer = buffer,
			ReceivedTick = frameReceivedTick,
			DecodedTick = Stopwatch.GetTimestamp(),
			SourceTimestampNanos = unchecked((ulong)Interlocked.Read(ref _latestInputSourceTimestampNanos)),
			ReturnBuffer = rented => ArrayPool<byte>.Shared.Return(rented)
		};
		if (FrameDecoded is { } handler)
		{
			handler(frame);
		}
		else
		{
			frame.Release();
		}
		if (Interlocked.Increment(ref _decodedOutputFrames) == 1)
		{
			this.StatusChanged?.Invoke("FFmpeg decoded first frame.");
		}
	}

	private async Task WatchFirstFrameTimeoutAsync(Process process)
	{
		try
		{
			bool loggedNoKeyframe = false;
			while (true)
			{
				await Task.Delay(FirstFrameTimeout, _cts.Token);
				if (_cts.IsCancellationRequested || Interlocked.Read(ref _decodedOutputFrames) > 0)
				{
					return;
				}
				// A first-frame timeout with zero bytes written to FFmpeg means the upstream Annex B
				// gate has not forwarded a keyframe yet, so the decoder has been fed nothing. AirPlay
				// mirror streams emit a single IDR at session start (no periodic keyframes, and there
				// is no receiver-side keyframe request), so a missed keyframe never self-heals. Tearing
				// the process down and cascading through the hwaccel candidates cannot help here -- every
				// fresh process would also sit idle -- and it only resets the gate and emits misleading
				// "Output file does not contain any stream" errors. Keep waiting and report the real
				// cause instead of churning decoders.
				if (_writeMetrics.Written == 0)
				{
					if (!loggedNoKeyframe)
					{
						loggedNoKeyframe = true;
						this.StatusChanged?.Invoke($"No decoded frame within {FirstFrameTimeout.TotalSeconds:N0}s and no input written: upstream has not forwarded a keyframe; awaiting keyframe (no decoder fallback).");
					}
					continue;
				}
				// Input was fed but produced no decoded frame: a genuine decoder problem worth a fallback.
				TryFallbackDecoder(process, $"no decoded frame within {FirstFrameTimeout.TotalSeconds:N0}s");
				return;
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private bool TryFallbackDecoder(Process failedProcess, string reason)
	{
		lock (_processGate)
		{
			if (_cts.IsCancellationRequested)
			{
				// Dispose() killed the process; the resulting EndOfStream is not a decoder
				// failure, and starting a replacement ffmpeg.exe here would leak it.
				return false;
			}
			if (!ReferenceEquals(_process, failedProcess))
			{
				return true;
			}
			if (Interlocked.Read(ref _decodedOutputFrames) > 0)
			{
				return false;
			}
			if (_decoderCandidateIndex >= _decoderCandidates.Length - 1)
			{
				return false;
			}
			string failedLabel = _decoderCandidates[_decoderCandidateIndex].Label;
			_decoderCandidateIndex++;
			string nextLabel = _decoderCandidates[_decoderCandidateIndex].Label;
			string ffmpegPath = failedProcess.StartInfo.FileName;
			this.StatusChanged?.Invoke($"Decoder {failedLabel} failed ({reason}); falling back to {nextLabel}.");
			FfmpegPipe.Stop(failedProcess);
			// Drop packets queued for the dead process. They are mid-stream P-frames with no
			// SPS/PPS/IDR; feeding them to the fresh process produces only decode errors until
			// the next keyframe. The dead process blocks writes (IsAlive == false), so
			// clearing here before StartFfmpegProcess closes the window cleanly.
			int discarded = ClearPendingInput();
			if (discarded > 0)
			{
				this.StatusChanged?.Invoke($"Decoder fallback discarded {discarded} stale queued packet(s).");
			}
			StartFfmpegProcess(ffmpegPath);
		}

		// The freshly started process has no H.264 extradata. Notify outside the lock so the
		// upstream gate re-prepends SPS/PPS and waits for the next keyframe before forwarding.
		this.DecoderRestarted?.Invoke();
		return true;
	}

	public void Dispose()
	{
		_cts.Cancel();
		FfmpegPipe.Kill(_process);
		try
		{
			_readTask?.Wait(TimeSpan.FromSeconds(1.0));
			_writeTask?.Wait(TimeSpan.FromSeconds(1.0));
		}
		catch
		{
		}
		// Kill whatever process is current now: a hwaccel fallback racing this Dispose could
		// have replaced _process after the Kill above, and a bare Dispose() leaves it running.
		Process? remaining;
		lock (_processGate)
		{
			remaining = _process;
			_process = null;
		}
		if (remaining != null)
		{
			FfmpegPipe.Stop(remaining);
		}
		_submittedDump?.Dispose();
		_writtenDump?.Dispose();
		_inputQueue.Dispose();
		_cts.Dispose();
	}
}

internal sealed record H264InputPacket(byte[] Payload, ulong SourceTimestampNanos, long ReceivedTick);

internal sealed record DecoderCandidate(string Label, string InputArguments);
