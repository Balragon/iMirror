using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MacMirrorReceiver.Video;

// The process plumbing shared by FfmpegDecoder (H.264 -> BGRA) and FfmpegAudioDecoder
// (AAC-ELD -> PCM): both run ffmpeg.exe with stdin/stdout/stderr redirected, feed stdin from a
// PipeInputQueue, read fixed-size output frames from stdout and forward stderr lines as status.
internal static class FfmpegPipe
{
	public static string? FindFfmpeg()
	{
		string text = Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe");
		if (File.Exists(text))
		{
			return text;
		}
		// Only the install/build tree is probed. The working directory is whatever the launcher
		// set (a shortcut's "Start in", a network share), so an ffmpeg.exe planted under its
		// ancestors would otherwise be executed with the receiver's privileges.
		string? localToolsFfmpeg = FindFfmpegInAncestorTools(AppContext.BaseDirectory);
		if (localToolsFfmpeg != null)
		{
			return localToolsFfmpeg;
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
		catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
		{
			AppLog.Write("ffmpeg.exe probe of the WinGet package folder failed: " + ex.Message);
		}
		return null;
	}

	public static string? FindFfmpegInAncestorTools(string startDirectory)
	{
		var directory = new DirectoryInfo(startDirectory);
		for (int depth = 0; directory != null && depth < 8; depth++, directory = directory.Parent)
		{
			string candidate = Path.Combine(directory.FullName, "tools", "ffmpeg", "bin", "ffmpeg.exe");
			if (File.Exists(candidate))
			{
				return candidate;
			}
		}

		return null;
	}

	public static string FindFfmpegOrThrow()
	{
		return FindFfmpeg() ?? throw new InvalidOperationException("ffmpeg.exe was not found. Put it on PATH or at tools\\ffmpeg\\bin\\ffmpeg.exe.");
	}

	public static Process Start(string ffmpegPath, string arguments)
	{
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
		try
		{
			process.PriorityClass = ProcessPriorityClass.AboveNormal;
		}
		catch
		{
		}
		return process;
	}

	public static bool IsAlive(Process? process)
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

	// Closes stdin and kills the process tree, leaving the Process object for the caller to
	// dispose once its reader tasks have drained.
	public static void Kill(Process? process)
	{
		if (process == null)
		{
			return;
		}
		try
		{
			process.StandardInput.Close();
		}
		catch
		{
		}
		try
		{
			if (IsAlive(process))
			{
				process.Kill(entireProcessTree: true);
			}
		}
		catch
		{
		}
	}

	public static void Stop(Process process)
	{
		Kill(process);
		try
		{
			process.Dispose();
		}
		catch
		{
		}
	}

	// Forwards non-blank stderr lines until the stream ends or isCurrent turns false. A reader
	// failure is reported through onReaderStopped unless shutdown was requested.
	public static async Task ReadStderrLinesAsync(Process process, Func<bool> isCurrent, CancellationToken cancellationToken, Action<string> onLine, Action<string> onReaderStopped)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested && isCurrent())
			{
				string? line = await process.StandardError.ReadLineAsync();
				if (line == null)
				{
					break;
				}
				if (!string.IsNullOrWhiteSpace(line))
				{
					onLine(line.Trim());
				}
			}
		}
		catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is InvalidOperationException || ex is OperationCanceledException)
		{
			// Normal at shutdown; during a session it means ffmpeg diagnostics stop arriving.
			if (!cancellationToken.IsCancellationRequested)
			{
				onReaderStopped(ex.Message);
			}
		}
	}

	// Waits for queued input and hands each item to write until cancellation or until write
	// returns false. Cancellation ends the loop quietly; any other exception reaches the caller.
	public static async Task RunInputLoopAsync<T>(PipeInputQueue<T> queue, Func<T, Task<bool>> write, CancellationToken cancellationToken) where T : class
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested)
			{
				await queue.WaitAsync(cancellationToken);
				while (!cancellationToken.IsCancellationRequested && queue.TryDequeue(out T? item))
				{
					if (!await write(item!))
					{
						return;
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	// Reads stdout in frames of exactly frameBytes into pooled buffers. onFrame owns the buffer it
	// is given; if reading or onFrame throws, the buffer goes back to the pool and the exception
	// reaches the caller.
	public static async Task ReadFixedSizeFramesAsync(Stream stream, int frameBytes, Action<byte[]> onFrame, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested)
		{
			byte[] buffer = ArrayPool<byte>.Shared.Rent(frameBytes);
			try
			{
				await ReadExactAsync(stream, buffer, frameBytes, cancellationToken);
				onFrame(buffer);
			}
			catch
			{
				ArrayPool<byte>.Shared.Return(buffer);
				throw;
			}
		}
	}

	public static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
	{
		int read;
		for (int offset = 0; offset < count; offset += read)
		{
			read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), cancellationToken);
			if (read == 0)
			{
				throw new EndOfStreamException();
			}
		}
	}

	public static long ElapsedMilliseconds(long startTick, long endTick)
	{
		if (endTick <= startTick)
		{
			return 0L;
		}
		return Math.Max(0L, (long)Math.Round((double)(endTick - startTick) * 1000.0 / Stopwatch.Frequency));
	}

	public static void SetCurrentThreadAboveNormal()
	{
		try
		{
			Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
		}
		catch
		{
		}
	}
}
