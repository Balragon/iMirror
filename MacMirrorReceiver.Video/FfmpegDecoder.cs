using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MacMirrorReceiver.Video;

public sealed class FfmpegDecoder : IDisposable
{
	private const int MaxQueuedInputPackets = 256;

	private const long MaxQueuedInputBytes = 24L * 1024L * 1024L;

	private const long WriteStallThresholdMs = 35;

	private const int OutputBytesPerPixel = 4;

	private const string OutputPixelFormat = "bgra";

	private readonly int _inputWidth;

	private readonly int _inputHeight;

	private readonly int _inputFps;

	private readonly int _outputWidth;

	private readonly int _outputHeight;

	private readonly int _targetOutputFps;

	private readonly int _frameSize;

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	private readonly object _processGate = new object();

	private readonly object _inputGate = new object();

	private readonly SemaphoreSlim _inputSignal = new SemaphoreSlim(0);

	private readonly Queue<H264InputPacket> _inputQueue = new Queue<H264InputPacket>();

	private long _queuedInputBytes;

	private long _latestInputReceivedTick;

	private long _latestInputSourceTimestampNanos;

	private Process? _process;

	private Task? _readTask;

	private Task? _writeTask;

	private long _decodedOutputFrames;

	private long _acceptedInputPackets;

	private long _writtenInputPackets;

	private long _latestWriteMilliseconds;

	private long _maxWriteMilliseconds;

	private long _writeStalls;

	private long _lastWriteStallStatusTick;

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

	public long DroppedInputPackets { get; private set; }

	public long AcceptedInputPackets => Interlocked.Read(ref _acceptedInputPackets);

	public long WrittenInputPackets => Interlocked.Read(ref _writtenInputPackets);

	public long LatestWriteMilliseconds => Interlocked.Read(ref _latestWriteMilliseconds);

	public long MaxWriteMilliseconds => Interlocked.Read(ref _maxWriteMilliseconds);

	public long WriteStalls => Interlocked.Read(ref _writeStalls);

	public int OutputWidth => _outputWidth;

	public int OutputHeight => _outputHeight;

	public int OutputFrameBytes => _frameSize;

	public int QueuedInputPackets
	{
		get
		{
			lock (_inputGate)
			{
				return _inputQueue.Count;
			}
		}
	}

	public long QueuedInputBytes
	{
		get
		{
			lock (_inputGate)
			{
				return _queuedInputBytes;
			}
		}
	}

	public event Action<string>? StatusChanged;

	public event Action<VideoFrame>? FrameDecoded;

	// Raised when the decoder internally restarts its FFmpeg process (e.g. hardware-accel
	// fallback d3d11va -> dxva2 -> software). The new process has no H.264 extradata, so
	// the upstream Annex B gate must re-establish SPS/PPS and wait for the next keyframe.
	public event Action? DecoderRestarted;

	// Raised when the input queue overflowed and was flushed without an IDR to resume from.
	// The upstream gate should call H264AnnexBStreamGate.RequireKeyframe() so feeding resumes
	// cleanly at the next keyframe instead of with mid-GOP P-frames.
	public event Action? InputQueueOverflowed;

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
		string? text = FindFfmpeg();
		if (text == null)
		{
			throw new InvalidOperationException("ffmpeg.exe was not found. Put it on PATH or at tools\\ffmpeg\\bin\\ffmpeg.exe.");
		}
		TryOpenH264Dump();
		StartFfmpegProcess(text);
		_writeTask = Task.Run(WriteInputLoopAsync);
	}

	private void TryOpenH264Dump()
	{
		string? setting = Environment.GetEnvironmentVariable("IMIRROR_DUMP_H264");
		if (string.IsNullOrWhiteSpace(setting))
		{
			return;
		}

		try
		{
			string basePath;
			if (setting == "1" || string.Equals(setting, "true", StringComparison.OrdinalIgnoreCase))
			{
				string fileName = "imirror-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".h264";
				basePath = Path.Combine(AppContext.BaseDirectory, fileName);
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
				catch
				{
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
		var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = ffmpegPath,
				Arguments = arguments,
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			},
			EnableRaisingEvents = true
		};
		process.Start();
		TrySetRealtimePriority(process);
		lock (_processGate)
		{
			_process = process;
		}
		this.StatusChanged?.Invoke($"FFmpeg started{GetDecoderLabel()}: {ffmpegPath} ({_inputWidth}x{_inputHeight} -> {_outputWidth}x{_outputHeight} @ {_targetOutputFps}fps)");
		this.StatusChanged?.Invoke($"FFmpeg cmd: {ffmpegPath} {arguments}");
		Task.Run(() => ReadErrorLinesAsync(process));
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
		return "-hide_banner -loglevel error -flags low_delay -probesize 1M -analyzeduration 200000 -avioflags direct -max_delay 0 "
			+ GetDecoderInputArgument()
			+ "-threads 1 -thread_type slice -f h264 -i pipe:0 "
			+ videoFilter
			+ $"-fps_mode passthrough -f rawvideo -pix_fmt {OutputPixelFormat} pipe:1";
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

	private async Task ReadErrorLinesAsync(Process process)
	{
		try
		{
			while (!_cts.IsCancellationRequested && ReferenceEquals(_process, process))
			{
				string? text = await process.StandardError.ReadLineAsync();
				if (text != null)
				{
					if (!string.IsNullOrWhiteSpace(text))
					{
						this.StatusChanged?.Invoke(text.Trim());
					}
					continue;
				}
				break;
			}
		}
		catch
		{
		}
	}

	public bool QueueH264(byte[] payload, ulong sourceTimestampNanos, long receivedTick)
	{
		if (!IsProcessAlive(_process) || _cts.IsCancellationRequested)
		{
			return false;
		}
		if (_submittedDump != null && _submittedDump.Write(payload))
		{
			this.StatusChanged?.Invoke($"H264 submitted dump reached {MaxDumpBytes / (1024 * 1024)}MB cap; further packets are not captured.");
		}
		bool shouldSignal = false;
		int flushed = 0;
		bool droppedIncoming = false;
		lock (_inputGate)
		{
			if (_inputQueue.Count >= MaxQueuedInputPackets || _queuedInputBytes + payload.Length > MaxQueuedInputBytes)
			{
				// Dropping individual H.264 packets corrupts the GOP until the next IDR (every
				// later P-frame references the dropped one), which shows up as decoder
				// "Invalid data" bursts. Recover in GOP units instead: flush the whole queue,
				// then either resume from the incoming payload if it is itself an IDR sync
				// point, or drop it too and let the upstream gate hold input until the next
				// keyframe (InputQueueOverflowed -> H264AnnexBStreamGate.RequireKeyframe).
				flushed = _inputQueue.Count;
				_inputQueue.Clear();
				_queuedInputBytes = 0L;
				DroppedInputPackets += flushed;
				if (!ContainsIdrNal(payload))
				{
					droppedIncoming = true;
					DroppedInputPackets++;
				}
			}

			if (!droppedIncoming)
			{
				shouldSignal = _inputQueue.Count == 0;
				_inputQueue.Enqueue(new H264InputPacket(payload, sourceTimestampNanos, receivedTick));
				_queuedInputBytes += payload.Length;
				Interlocked.Increment(ref _acceptedInputPackets);
			}
		}
		if (flushed > 0)
		{
			this.StatusChanged?.Invoke(droppedIncoming
				? $"Decoder input overflow: flushed {flushed} queued packet(s); waiting for next keyframe."
				: $"Decoder input overflow: flushed {flushed} queued packet(s); resuming from incoming keyframe.");
			if (droppedIncoming)
			{
				this.InputQueueOverflowed?.Invoke();
			}
		}
		if (shouldSignal)
		{
			_inputSignal.Release();
		}
		return true;
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
		lock (_inputGate)
		{
			int count = _inputQueue.Count;
			_inputQueue.Clear();
			_queuedInputBytes = 0L;
			DroppedInputPackets += count;
			return count;
		}
	}

	private async Task WriteInputLoopAsync()
	{
		SetCurrentThreadAboveNormal();
		try
		{
			while (!_cts.IsCancellationRequested)
			{
				await _inputSignal.WaitAsync(_cts.Token);
				while (!_cts.IsCancellationRequested)
				{
					H264InputPacket? packet = null;
					lock (_inputGate)
					{
						if (_inputQueue.Count > 0)
						{
							packet = _inputQueue.Dequeue();
							_queuedInputBytes = Math.Max(0L, _queuedInputBytes - packet.Payload.Length);
						}
					}
					if (packet == null)
					{
						break;
					}
					Process? process = _process;
					try
					{
						if (process != null && IsProcessAlive(process))
						{
							Interlocked.Exchange(ref _latestInputReceivedTick, packet.ReceivedTick);
							Interlocked.Exchange(ref _latestInputSourceTimestampNanos, unchecked((long)packet.SourceTimestampNanos));
							long startTick = Stopwatch.GetTimestamp();
							await process.StandardInput.BaseStream.WriteAsync(packet.Payload, _cts.Token);
							long endTick = Stopwatch.GetTimestamp();
							if (_writtenDump != null && _writtenDump.Write(packet.Payload))
							{
								this.StatusChanged?.Invoke($"H264 written dump reached {MaxDumpBytes / (1024 * 1024)}MB cap; further packets are not captured.");
							}
							RecordWriteMetrics(ElapsedMilliseconds(startTick, endTick));
						}
					}
					catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is ObjectDisposedException)
					{
						this.StatusChanged?.Invoke("Decoder input retrying: " + ex.Message);
					}
					}
				}
			}
			catch (OperationCanceledException)
			{
			}
		catch (Exception ex2)
		{
			this.StatusChanged?.Invoke("Decoder input closed: " + ex2.Message);
		}
	}

	private async Task ReadFramesAsync(Process process)
	{
		SetCurrentThreadAboveNormal();
		try
		{
			Stream stream = process.StandardOutput.BaseStream;
			while (!_cts.IsCancellationRequested)
			{
				byte[] buffer = ArrayPool<byte>.Shared.Rent(_frameSize);
				try
				{
					await ReadExactAsync(stream, buffer, _frameSize, _cts.Token);
					var frame = new VideoFrame
					{
						Width = _outputWidth,
						Height = _outputHeight,
						Buffer = buffer,
						ReceivedTick = Interlocked.Read(ref _latestInputReceivedTick),
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
				}
				catch
				{
					ArrayPool<byte>.Shared.Return(buffer);
					throw;
				}
				if (Interlocked.Increment(ref _decodedOutputFrames) == 1)
				{
					this.StatusChanged?.Invoke("FFmpeg decoded first frame.");
				}
			}
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

	private void RecordWriteMetrics(long writeMs)
	{
		Interlocked.Increment(ref _writtenInputPackets);
		Interlocked.Exchange(ref _latestWriteMilliseconds, writeMs);
		long previousMax;
		do
		{
			previousMax = Interlocked.Read(ref _maxWriteMilliseconds);
			if (writeMs <= previousMax)
			{
				break;
			}
		}
		while (Interlocked.CompareExchange(ref _maxWriteMilliseconds, writeMs, previousMax) != previousMax);

		if (writeMs < WriteStallThresholdMs)
		{
			return;
		}

		Interlocked.Increment(ref _writeStalls);
		long now = Environment.TickCount64;
		long previous = Interlocked.Read(ref _lastWriteStallStatusTick);
		if (now - previous >= 1000)
		{
			Interlocked.Exchange(ref _lastWriteStallStatusTick, now);
			this.StatusChanged?.Invoke($"FFmpeg stdin write stall: {writeMs}ms, queued={QueuedInputPackets}, dropped={DroppedInputPackets:N0}");
		}
	}

	private static long ElapsedMilliseconds(long startTick, long endTick)
	{
		if (endTick <= startTick)
		{
			return 0L;
		}
		return Math.Max(0L, (long)Math.Round((double)(endTick - startTick) * 1000.0 / Stopwatch.Frequency));
	}

	private static void SetCurrentThreadAboveNormal()
	{
		try
		{
			Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
		}
		catch
		{
		}
	}

	private async Task WatchFirstFrameTimeoutAsync(Process process)
	{
		try
		{
			await Task.Delay(FirstFrameTimeout, _cts.Token);
			if (_cts.IsCancellationRequested || Interlocked.Read(ref _decodedOutputFrames) > 0)
			{
				return;
			}
			TryFallbackDecoder(process, $"no decoded frame within {FirstFrameTimeout.TotalSeconds:N0}s");
		}
		catch (OperationCanceledException)
		{
		}
	}

	private bool TryFallbackDecoder(Process failedProcess, string reason)
	{
		lock (_processGate)
		{
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
			StopProcess(failedProcess);
			// Drop packets queued for the dead process. They are mid-stream P-frames with no
			// SPS/PPS/IDR; feeding them to the fresh process produces only decode errors until
			// the next keyframe. The dead process blocks writes (IsProcessAlive == false), so
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

	private static Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
	{
		return ReadExactAsync(stream, buffer, buffer.Length, cancellationToken);
	}

	private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
	{
		int num;
		for (int offset = 0; offset < count; offset += num)
		{
			num = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken);
			if (num == 0)
			{
				throw new EndOfStreamException();
			}
		}
	}

	private static string? FindFfmpeg()
	{
		string text = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe");
		if (File.Exists(text))
		{
			return text;
		}
		string path2 = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "ffmpeg", "bin", "ffmpeg.exe");
		if (File.Exists(path2))
		{
			return Path.GetFullPath(path2);
		}
		string[] array = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
		foreach (string text2 in array)
		{
			if (!string.IsNullOrWhiteSpace(text2))
			{
				string text3 = Path.Combine(text2.Trim(), "ffmpeg.exe");
				if (File.Exists(text3))
				{
					return text3;
				}
			}
		}
		try
		{
			string path3 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
			if (Directory.Exists(path3))
			{
				string? text4 = Directory.EnumerateFiles(path3, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault((string path) => path.Contains("Gyan.FFmpeg", StringComparison.OrdinalIgnoreCase));
				if (text4 != null)
				{
					return text4;
				}
			}
		}
		catch
		{
		}
		return null;
	}

	private static void TrySetRealtimePriority(Process process)
	{
		try
		{
			process.PriorityClass = ProcessPriorityClass.AboveNormal;
		}
		catch
		{
		}
	}

	private static void StopProcess(Process process)
	{
		try
		{
			process.StandardInput.Close();
		}
		catch
		{
		}
		try
		{
			if (IsProcessAlive(process))
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch
		{
		}
		try
		{
			process.Dispose();
		}
		catch
		{
		}
	}

	public void Dispose()
	{
		_cts.Cancel();
		try
		{
			_process?.StandardInput.Close();
		}
		catch
		{
		}
		try
		{
			Process? process = _process;
			if (IsProcessAlive(process))
			{
				process!.Kill(entireProcessTree: true);
			}
		}
		catch
		{
		}
		try
		{
			_readTask?.Wait(TimeSpan.FromSeconds(1.0));
			_writeTask?.Wait(TimeSpan.FromSeconds(1.0));
		}
		catch
		{
		}
		_process?.Dispose();
		_submittedDump?.Dispose();
		_writtenDump?.Dispose();
		_inputSignal.Dispose();
		_cts.Dispose();
	}

	private static bool IsProcessAlive(Process? process)
	{
		if (process == null)
		{
			return false;
		}

		try
		{
			return !process.HasExited;
		}
		catch (InvalidOperationException)
		{
			return false;
		}
	}
}

internal sealed record H264InputPacket(byte[] Payload, ulong SourceTimestampNanos, long ReceivedTick);

internal sealed record DecoderCandidate(string Label, string InputArguments);
