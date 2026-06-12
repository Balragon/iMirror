using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

namespace MacMirrorReceiver.Video;

public sealed class MpvVideoPresenter : HwndHost
{
	private const int MaxQueuedInputPackets = 2;

	private const long MaxQueuedInputBytes = 4L * 1024L * 1024L;

	private const long WriteStallThresholdMs = 35;

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

	public MpvVideoPresenter(int width, int height, int fps)
	{
		_width = width;
		_height = height;
		_fps = Math.Max(1, fps);
	}

	public long DroppedInputPackets { get; private set; }

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

		string? mpvPath = FindMpv();
		if (mpvPath == null)
		{
			throw new InvalidOperationException("mpv.exe was not found. Put it on PATH or at tools\\mpv\\mpv.exe.");
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
		lock (_inputGate)
		{
			while (_inputQueue.Count >= MaxQueuedInputPackets || _queuedInputBytes + payload.Length > MaxQueuedInputBytes)
			{
				if (_inputQueue.Count == 0)
				{
					break;
				}
				H264InputPacket dropped = _inputQueue.Dequeue();
				_queuedInputBytes = Math.Max(0L, _queuedInputBytes - dropped.Payload.Length);
				DroppedInputPackets++;
			}
			shouldSignal = _inputQueue.Count == 0;
			_inputQueue.Enqueue(new H264InputPacket(payload, sourceTimestampNanos, receivedTick));
			_queuedInputBytes += payload.Length;
			Interlocked.Increment(ref _acceptedInputPackets);
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
					RecordWriteMetrics(ElapsedMilliseconds(startTick, Stopwatch.GetTimestamp()));
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
			StatusChanged?.Invoke($"mpv stdin write stall: {writeMs}ms, queued={QueuedInputPackets}, dropped={DroppedInputPackets:N0}");
		}
	}

	private void StopProcess()
	{
		_cts.Cancel();
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

	private static string? FindMpv()
	{
		string bundled = Path.Combine(AppContext.BaseDirectory, "tools", "mpv", "mpv.exe");
		if (File.Exists(bundled))
		{
			return bundled;
		}
		string devBundled = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools", "mpv", "mpv.exe");
		if (File.Exists(devBundled))
		{
			return Path.GetFullPath(devBundled);
		}
		string programFiles = Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
			"MPV Player",
			"mpv.exe");
		if (File.Exists(programFiles))
		{
			return programFiles;
		}
		string[] paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
		foreach (string path in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
		{
			string candidate = Path.Combine(path.Trim(), "mpv.exe");
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}
		return null;
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
