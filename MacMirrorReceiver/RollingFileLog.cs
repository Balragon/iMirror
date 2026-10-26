using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace MacMirrorReceiver;

/// <summary>
/// Append-only text log with size-based rotation and a single background writer thread.
/// <para>
/// Callers never block on disk: <see cref="Write"/> enqueues and returns. The writer thread
/// drains the queue, appends in batches, and rotates <c>name.log</c> → <c>name.1.log</c> →
/// … → <c>name.N.log</c> when the active file passes <see cref="MaxBytes"/>. Lines are
/// dropped only when the queue backs up past <see cref="MaxQueuedLines"/> (a stalled disk),
/// and the drop count is recorded in the log once the disk catches up.
/// </para>
/// </summary>
internal sealed class RollingFileLog : IDisposable
{
	public const long DefaultMaxBytes = 5L * 1024L * 1024L;
	public const int DefaultKeepFiles = 3;
	private const int MaxQueuedLines = 20_000;
	private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(3);

	private readonly string _path;
	private readonly BlockingCollection<string> _queue = new BlockingCollection<string>(new ConcurrentQueue<string>());
	private readonly Thread _writer;
	private readonly object _flushGate = new object();
	private long _droppedLines;
	private volatile bool _disposed;

	public RollingFileLog(string path, long maxBytes = DefaultMaxBytes, int keepFiles = DefaultKeepFiles)
	{
		_path = path ?? throw new ArgumentNullException(nameof(path));
		MaxBytes = Math.Max(64 * 1024, maxBytes);
		KeepFiles = Math.Max(0, keepFiles);
		_writer = new Thread(WriterLoop)
		{
			IsBackground = true,
			Name = "iMirror log writer"
		};
		_writer.Start();
	}

	public long MaxBytes { get; }

	public int KeepFiles { get; }

	public string FilePath => _path;

	public long DroppedLines => Interlocked.Read(ref _droppedLines);

	public void Write(string line)
	{
		if (_disposed)
		{
			return;
		}

		if (_queue.Count >= MaxQueuedLines)
		{
			Interlocked.Increment(ref _droppedLines);
			return;
		}

		try
		{
			_queue.Add(line);
		}
		catch (InvalidOperationException)
		{
			// Completed during shutdown.
		}
	}

	/// <summary>Blocks until everything queued before the call has been written (or the timeout passes).</summary>
	public void Flush()
	{
		lock (_flushGate)
		{
			using var done = new ManualResetEventSlim(false);
			_flushRequests.Enqueue(done);
			try
			{
				_queue.Add(FlushMarker);
			}
			catch (InvalidOperationException)
			{
				return;
			}
			done.Wait(FlushTimeout);
		}
	}

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		_queue.CompleteAdding();
		_writer.Join(FlushTimeout);
	}

	private const string FlushMarker = "\0flush\0";
	private readonly ConcurrentQueue<ManualResetEventSlim> _flushRequests = new ConcurrentQueue<ManualResetEventSlim>();

	private void WriterLoop()
	{
		var batch = new StringBuilder();
		foreach (string first in _queue.GetConsumingEnumerable())
		{
			batch.Clear();
			bool flushRequested = Append(batch, first);
			while (_queue.TryTake(out string? next))
			{
				flushRequested |= Append(batch, next);
			}

			long dropped = Interlocked.Exchange(ref _droppedLines, 0);
			if (dropped > 0)
			{
				batch.Append('[').Append(DateTimeOffset.Now.ToString("O")).Append("] AppLog dropped ")
					.Append(dropped).Append(" line(s) while the disk was slow.").Append(Environment.NewLine);
			}

			if (batch.Length > 0)
			{
				AppendToFile(batch.ToString());
			}

			if (flushRequested)
			{
				while (_flushRequests.TryDequeue(out ManualResetEventSlim? waiter))
				{
					try { waiter.Set(); } catch (ObjectDisposedException) { }
				}
			}
		}
	}

	private static bool Append(StringBuilder batch, string line)
	{
		if (ReferenceEquals(line, FlushMarker))
		{
			return true;
		}
		batch.Append(line);
		if (!line.EndsWith('\n'))
		{
			batch.Append(Environment.NewLine);
		}
		return false;
	}

	private void AppendToFile(string text)
	{
		try
		{
			string? directory = System.IO.Path.GetDirectoryName(_path);
			if (!string.IsNullOrWhiteSpace(directory))
			{
				Directory.CreateDirectory(directory);
			}

			RotateIfNeeded(_path, MaxBytes, KeepFiles);
			File.AppendAllText(_path, text);
		}
		catch
		{
			// Logging must never take the app down.
		}
	}

	/// <summary>
	/// When <paramref name="path"/> is at least <paramref name="maxBytes"/>, shifts
	/// <c>name.(k).ext</c> to <c>name.(k+1).ext</c> for k = keep-1..1, moves the active file to
	/// <c>name.1.ext</c>, and deletes anything beyond <paramref name="keepFiles"/>.
	/// </summary>
	internal static void RotateIfNeeded(string path, long maxBytes, int keepFiles)
	{
		var info = new FileInfo(path);
		if (!info.Exists || info.Length < maxBytes)
		{
			return;
		}

		if (keepFiles <= 0)
		{
			File.Delete(path);
			return;
		}

		string oldest = ArchivePath(path, keepFiles);
		if (File.Exists(oldest))
		{
			File.Delete(oldest);
		}
		for (int k = keepFiles - 1; k >= 1; k--)
		{
			string from = ArchivePath(path, k);
			if (File.Exists(from))
			{
				File.Move(from, ArchivePath(path, k + 1), overwrite: true);
			}
		}
		File.Move(path, ArchivePath(path, 1), overwrite: true);
	}

	internal static string ArchivePath(string path, int index)
	{
		string? directory = System.IO.Path.GetDirectoryName(path);
		string name = System.IO.Path.GetFileNameWithoutExtension(path);
		string extension = System.IO.Path.GetExtension(path);
		string archive = name + "." + index + extension;
		return string.IsNullOrEmpty(directory) ? archive : System.IO.Path.Combine(directory, archive);
	}
}
