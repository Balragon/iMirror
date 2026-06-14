using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace MacMirrorReceiver.Video;

public sealed class MpvVideoPresenter : HwndHost
{
	private const int MaxQueuedInputPackets = 240;

	private const long MaxQueuedInputBytes = 32L * 1024L * 1024L;

	private const long WriteStallThresholdMs = 35;

	private const long DiagnosticsIntervalMs = 10_000;

	private const int WS_CHILD = 0x40000000;

	private const int WS_VISIBLE = 0x10000000;

	private const int WS_CLIPSIBLINGS = 0x04000000;

	private const int WS_CLIPCHILDREN = 0x02000000;

	private readonly int _width;

	private readonly int _height;

	private readonly int _fps;

	private readonly CancellationTokenSource _cts = new CancellationTokenSource();

	private readonly object _inputGate = new object();

	private readonly SemaphoreSlim _inputSignal = new SemaphoreSlim(0);

	private readonly Queue<H264InputPacket> _inputQueue = new Queue<H264InputPacket>();

	private readonly object _diagnosticsGate = new object();

	private readonly List<long> _diagnosticsIntervalReceiveToWriteMilliseconds = new List<long>();

	private readonly List<long> _diagnosticsAllReceiveToWriteMilliseconds = new List<long>();

	private long _queuedInputBytes;

	private Process? _process;

	private Task? _writeTask;

	private Task? _errorTask;

	private IntPtr _childWindow;

	private long _acceptedInputPackets;

	private long _writtenInputPackets;

	private long _latestWriteMilliseconds;

	private long _maxWriteMilliseconds;

	private long _writeStalls;

	private long _lastWriteStallStatusTick;

	private long _lastWriteStallDroppedInputPackets;

	private long _droppedInputPackets;

	private long _diagnosticsStartTick;

	private long _diagnosticsIntervalStartTick;

	private long _diagnosticsLastAcceptedInputPackets;

	private long _diagnosticsLastWrittenInputPackets;

	private long _diagnosticsLastDroppedInputPackets;

	private long _diagnosticsIntervalWriteStalls;

	private long _diagnosticsIntervalMaxWriteStallMilliseconds;

	private bool _diagnosticsSummaryWritten;

	private static readonly bool DiagnosticsEnabled = string.Equals(
		Environment.GetEnvironmentVariable("IMIRROR_DIAG"),
		"1",
		StringComparison.OrdinalIgnoreCase);

	public MpvVideoPresenter(int width, int height, int fps)
	{
		_width = width;
		_height = height;
		_fps = Math.Max(1, fps);
	}

	public long DroppedInputPackets => Interlocked.Read(ref _droppedInputPackets);

	public long AcceptedInputPackets => Interlocked.Read(ref _acceptedInputPackets);

	public long WrittenInputPackets => Interlocked.Read(ref _writtenInputPackets);

	public long LatestWriteMilliseconds => Interlocked.Read(ref _latestWriteMilliseconds);

	public long MaxWriteMilliseconds => Interlocked.Read(ref _maxWriteMilliseconds);

	public long WriteStalls => Interlocked.Read(ref _writeStalls);

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

	public event Action? InputQueueOverflowed;

	public bool CanAcceptInput
	{
		get
		{
			Process? process = _process;
			return process != null && !process.HasExited && !_cts.IsCancellationRequested;
		}
	}

	public void Start()
	{
		if (_childWindow == IntPtr.Zero)
		{
			throw new InvalidOperationException("mpv host window is not ready.");
		}

		string? mpvPath = MpvLocator.StartupRunnableMpvExecutable;
		if (mpvPath == null)
		{
			throw new InvalidOperationException("mpv.exe was not found or could not be started. Put it on PATH or at tools\\mpv\\mpv.exe.");
		}

		string[] arguments = BuildArguments(_childWindow, _fps);
		var process = new Process
		{
			StartInfo = new ProcessStartInfo
			{
				FileName = mpvPath,
				UseShellExecute = false,
				RedirectStandardInput = true,
				RedirectStandardError = true,
				CreateNoWindow = true
			},
			EnableRaisingEvents = true
		};
		foreach (string argument in arguments)
		{
			process.StartInfo.ArgumentList.Add(argument);
		}

		process.Start();
		_process = process;
		TrySetAboveNormalPriority(process);
		StatusChanged?.Invoke($"mpv started [renderer:mpv/d3d11]: {mpvPath} ({_width}x{_height} @ {_fps}fps)");
		StatusChanged?.Invoke("mpv cmd: " + mpvPath + " " + string.Join(" ", arguments));
		StartDiagnosticsIfEnabled();
		_writeTask = Task.Run(WriteInputLoopAsync);
		_errorTask = Task.Run(() => ReadErrorLinesAsync(process));
	}

	public bool QueueH264(byte[] payload, ulong sourceTimestampNanos, long receivedTick)
	{
		if (_process == null || _process.HasExited || _cts.IsCancellationRequested)
		{
			return false;
		}

		bool shouldSignal;
		bool overflowed = false;
		long droppedOnOverflow = 0L;
		lock (_inputGate)
		{
			if (_inputQueue.Count >= MaxQueuedInputPackets || _queuedInputBytes + payload.Length > MaxQueuedInputBytes)
			{
				droppedOnOverflow = _inputQueue.Count + 1L;
				_inputQueue.Clear();
				_queuedInputBytes = 0L;
				Interlocked.Add(ref _droppedInputPackets, droppedOnOverflow);
				overflowed = true;
			}
			if (!overflowed)
			{
				shouldSignal = _inputQueue.Count == 0;
				_inputQueue.Enqueue(new H264InputPacket(payload, sourceTimestampNanos, receivedTick));
				_queuedInputBytes += payload.Length;
				Interlocked.Increment(ref _acceptedInputPackets);
			}
			else
			{
				while (_inputSignal.Wait(0))
				{
				}
				shouldSignal = false;
			}
		}
		if (overflowed)
		{
			StatusChanged?.Invoke($"mpv input queue overflowed; dropped {droppedOnOverflow:N0} compressed packets and waiting for next keyframe.");
			InputQueueOverflowed?.Invoke();
			return false;
		}
		if (shouldSignal)
		{
			_inputSignal.Release();
		}
		return true;
	}

	protected override HandleRef BuildWindowCore(HandleRef hwndParent)
	{
		_childWindow = CreateWindowEx(
			0,
			"static",
			string.Empty,
			WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
			0,
			0,
			Math.Max(1, (int)Math.Round(ActualWidth)),
			Math.Max(1, (int)Math.Round(ActualHeight)),
			hwndParent.Handle,
			IntPtr.Zero,
			IntPtr.Zero,
			IntPtr.Zero);
		if (_childWindow == IntPtr.Zero)
		{
			throw new InvalidOperationException("Could not create mpv host window.");
		}
		return new HandleRef(this, _childWindow);
	}

	protected override void DestroyWindowCore(HandleRef hwnd)
	{
		StopProcess();
		if (hwnd.Handle != IntPtr.Zero)
		{
			DestroyWindow(hwnd.Handle);
		}
		_childWindow = IntPtr.Zero;
	}

	protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
	{
		base.OnRenderSizeChanged(sizeInfo);
		if (_childWindow != IntPtr.Zero)
		{
			MoveWindow(
				_childWindow,
				0,
				0,
				Math.Max(1, (int)Math.Round(ActualWidth)),
				Math.Max(1, (int)Math.Round(ActualHeight)),
				true);
		}
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			StopProcess();
			_inputSignal.Dispose();
			_cts.Dispose();
		}
		base.Dispose(disposing);
	}

	private static string[] BuildArguments(IntPtr hostWindow, int fps)
	{
		int containerFps = Math.Clamp(fps, 1, 240);
		return new[]
		{
			"--no-config",
			"--no-terminal",
			"--msg-level=all=warn",
			"--profile=low-latency",
			"--force-window=yes",
			"--wid=" + hostWindow.ToInt64(),
			"--vo=gpu",
			"--gpu-api=d3d11",
			"--hwdec=d3d11va",
			"--vd-lavc-threads=1",
			"--container-fps-override=" + containerFps.ToString(System.Globalization.CultureInfo.InvariantCulture),
			"--framedrop=decoder+vo",
			"--vd-lavc-framedrop=nonref",
			"--cache=no",
			"--cache-pause=no",
			"--demuxer-readahead-secs=0",
			"--demuxer-max-bytes=1MiB",
			"--demuxer-max-back-bytes=0",
			"--demuxer-lavf-probe-info=nostreams",
			"--demuxer-lavf-probesize=32",
			"--demuxer-lavf-analyzeduration=0",
			"--demuxer-lavf-o=fflags=+nobuffer",
			"--video-sync=display-desync",
			"--untimed",
			"--demuxer-lavf-format=h264",
			"-"
		};
	}

	private async Task WriteInputLoopAsync()
	{
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
					if (process == null || process.HasExited)
					{
						continue;
					}
					long startTick = Stopwatch.GetTimestamp();
					await process.StandardInput.BaseStream.WriteAsync(packet.Payload, _cts.Token);
					long endTick = Stopwatch.GetTimestamp();
					RecordWriteMetrics(packet, ElapsedMilliseconds(startTick, endTick), endTick);
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			StatusChanged?.Invoke("mpv input stopped: " + ex.Message);
		}
	}

	private async Task ReadErrorLinesAsync(Process process)
	{
		try
		{
			while (!_cts.IsCancellationRequested && ReferenceEquals(_process, process))
			{
				string? line = await process.StandardError.ReadLineAsync();
				if (line == null)
				{
					break;
				}
				if (!string.IsNullOrWhiteSpace(line))
				{
					StatusChanged?.Invoke("mpv: " + line.Trim());
				}
			}
		}
		catch
		{
		}
	}

	private void RecordWriteMetrics(H264InputPacket packet, long writeMs, long writeDoneTick)
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

		RecordDiagnostics(packet, writeMs, writeDoneTick);

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
			long droppedTotal = DroppedInputPackets;
			long previousDropped = Interlocked.Exchange(ref _lastWriteStallDroppedInputPackets, droppedTotal);
			long droppedDelta = Math.Max(0L, droppedTotal - previousDropped);
			StatusChanged?.Invoke($"mpv stdin write stall: {writeMs}ms, queued={QueuedInputPackets}, dropped_total={droppedTotal:N0} (+{droppedDelta:N0} since last)");
		}
	}

	private void StopProcess()
	{
		_cts.Cancel();
		string? diagnosticsSummary = BuildDiagnosticsSummary();
		if (diagnosticsSummary != null)
		{
			StatusChanged?.Invoke(diagnosticsSummary);
		}
		Process? process = _process;
		_process = null;
		try
		{
			process?.StandardInput.Close();
		}
		catch
		{
		}
		try
		{
			if (process != null && !process.HasExited)
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch
		{
		}
		try
		{
			_writeTask?.Wait(TimeSpan.FromSeconds(1.0));
			_errorTask?.Wait(TimeSpan.FromSeconds(1.0));
		}
		catch
		{
		}
		try
		{
			process?.Dispose();
		}
		catch
		{
		}
	}

	private void StartDiagnosticsIfEnabled()
	{
		if (!DiagnosticsEnabled)
		{
			return;
		}

		long now = Stopwatch.GetTimestamp();
		lock (_diagnosticsGate)
		{
			_diagnosticsStartTick = now;
			_diagnosticsIntervalStartTick = now;
			_diagnosticsLastAcceptedInputPackets = AcceptedInputPackets;
			_diagnosticsLastWrittenInputPackets = WrittenInputPackets;
			_diagnosticsLastDroppedInputPackets = DroppedInputPackets;
		}
		StatusChanged?.Invoke("mpv diag enabled: interval=10s, latency=receive->stdin-write-complete.");
	}

	private void RecordDiagnostics(H264InputPacket packet, long writeMs, long writeDoneTick)
	{
		if (!DiagnosticsEnabled)
		{
			return;
		}

		string? intervalStatus = null;
		long receiveToWriteMs = ElapsedMilliseconds(packet.ReceivedTick, writeDoneTick);
		lock (_diagnosticsGate)
		{
			if (_diagnosticsStartTick == 0)
			{
				_diagnosticsStartTick = writeDoneTick;
				_diagnosticsIntervalStartTick = writeDoneTick;
			}

			_diagnosticsIntervalReceiveToWriteMilliseconds.Add(receiveToWriteMs);
			_diagnosticsAllReceiveToWriteMilliseconds.Add(receiveToWriteMs);
			if (writeMs >= WriteStallThresholdMs)
			{
				_diagnosticsIntervalWriteStalls++;
				_diagnosticsIntervalMaxWriteStallMilliseconds = Math.Max(_diagnosticsIntervalMaxWriteStallMilliseconds, writeMs);
			}

			long intervalMs = ElapsedMilliseconds(_diagnosticsIntervalStartTick, writeDoneTick);
			if (intervalMs >= DiagnosticsIntervalMs)
			{
				intervalStatus = BuildDiagnosticsIntervalStatusLocked(intervalMs, writeDoneTick);
			}
		}

		if (intervalStatus != null)
		{
			StatusChanged?.Invoke(intervalStatus);
		}
	}

	private string BuildDiagnosticsIntervalStatusLocked(long intervalMs, long nowTick)
	{
		long acceptedTotal = AcceptedInputPackets;
		long writtenTotal = WrittenInputPackets;
		long droppedTotal = DroppedInputPackets;
		long acceptedDelta = Math.Max(0L, acceptedTotal - _diagnosticsLastAcceptedInputPackets);
		long writtenDelta = Math.Max(0L, writtenTotal - _diagnosticsLastWrittenInputPackets);
		long droppedDelta = Math.Max(0L, droppedTotal - _diagnosticsLastDroppedInputPackets);
		long intervalStalls = _diagnosticsIntervalWriteStalls;
		long intervalMaxStallMs = _diagnosticsIntervalMaxWriteStallMilliseconds;
		long latencyP50 = Percentile(_diagnosticsIntervalReceiveToWriteMilliseconds, 50.0);
		long latencyP95 = Percentile(_diagnosticsIntervalReceiveToWriteMilliseconds, 95.0);
		long latencyMax = MaxOrZero(_diagnosticsIntervalReceiveToWriteMilliseconds);
		double seconds = Math.Max(0.001, intervalMs / 1000.0);

		_diagnosticsLastAcceptedInputPackets = acceptedTotal;
		_diagnosticsLastWrittenInputPackets = writtenTotal;
		_diagnosticsLastDroppedInputPackets = droppedTotal;
		_diagnosticsIntervalStartTick = nowTick;
		_diagnosticsIntervalWriteStalls = 0L;
		_diagnosticsIntervalMaxWriteStallMilliseconds = 0L;
		_diagnosticsIntervalReceiveToWriteMilliseconds.Clear();

		return "mpv diag interval: " +
			$"accepted_to_mpv={FormatRate(acceptedDelta, seconds)}/s, " +
			$"stdin_write={FormatRate(writtenDelta, seconds)}/s, " +
			$"dropped_total={droppedTotal:N0} (+{droppedDelta:N0}), " +
			$"stdin_stalls={intervalStalls:N0} max={intervalMaxStallMs}ms, " +
			$"receive->stdin_write_complete p50={latencyP50}ms p95={latencyP95}ms max={latencyMax}ms, " +
			$"queue={QueuedInputPackets:N0} packets/{(double)QueuedInputBytes / 1024.0:N1}KB";
	}

	private string? BuildDiagnosticsSummary()
	{
		if (!DiagnosticsEnabled)
		{
			return null;
		}

		lock (_diagnosticsGate)
		{
			if (_diagnosticsSummaryWritten || _diagnosticsStartTick == 0)
			{
				return null;
			}

			_diagnosticsSummaryWritten = true;
			long now = Stopwatch.GetTimestamp();
			long durationMs = ElapsedMilliseconds(_diagnosticsStartTick, now);
			long acceptedTotal = AcceptedInputPackets;
			long writtenTotal = WrittenInputPackets;
			long droppedTotal = DroppedInputPackets;
			long stallsTotal = WriteStalls;
			long latencyP95 = Percentile(_diagnosticsAllReceiveToWriteMilliseconds, 95.0);
			long latencyMax = MaxOrZero(_diagnosticsAllReceiveToWriteMilliseconds);
			double dropRate = acceptedTotal == 0 ? 0.0 : (double)droppedTotal * 100.0 / acceptedTotal;

			return "mpv diag summary: " +
				$"duration={FormatDuration(durationMs)}, " +
				$"accepted_to_mpv={acceptedTotal:N0}, stdin_written={writtenTotal:N0}, " +
				$"dropped_total={droppedTotal:N0} ({dropRate.ToString("0.###", CultureInfo.InvariantCulture)}%), " +
				$"stdin_stalls={stallsTotal:N0}, " +
				$"receive->stdin_write_complete p95={latencyP95}ms max={latencyMax}ms";
		}
	}

	private static long Percentile(List<long> values, double percentile)
	{
		if (values.Count == 0)
		{
			return 0L;
		}

		List<long> sorted = new List<long>(values);
		sorted.Sort();
		int index = (int)Math.Ceiling(percentile / 100.0 * sorted.Count) - 1;
		index = Math.Clamp(index, 0, sorted.Count - 1);
		return sorted[index];
	}

	private static long MaxOrZero(List<long> values)
	{
		long max = 0L;
		foreach (long value in values)
		{
			if (value > max)
			{
				max = value;
			}
		}
		return max;
	}

	private static string FormatRate(long count, double seconds)
	{
		return (count / seconds).ToString("0.0", CultureInfo.InvariantCulture);
	}

	private static string FormatDuration(long milliseconds)
	{
		TimeSpan duration = TimeSpan.FromMilliseconds(milliseconds);
		return duration.TotalHours >= 1.0
			? duration.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
			: duration.ToString(@"m\:ss", CultureInfo.InvariantCulture);
	}

	private static long ElapsedMilliseconds(long startTick, long endTick)
	{
		if (endTick <= startTick)
		{
			return 0L;
		}
		return Math.Max(0L, (long)Math.Round((double)(endTick - startTick) * 1000.0 / Stopwatch.Frequency));
	}

	private static void TrySetAboveNormalPriority(Process process)
	{
		try
		{
			process.PriorityClass = ProcessPriorityClass.AboveNormal;
		}
		catch
		{
		}
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateWindowEx(
		int dwExStyle,
		string lpClassName,
		string lpWindowName,
		int dwStyle,
		int x,
		int y,
		int nWidth,
		int nHeight,
		IntPtr hWndParent,
		IntPtr hMenu,
		IntPtr hInstance,
		IntPtr lpParam);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool DestroyWindow(IntPtr hwnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
}
