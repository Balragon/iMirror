using System;
using System.IO;
using System.Linq;
using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class RollingFileLogTests : IDisposable
{
	private readonly string _directory = Path.Combine(Path.GetTempPath(), "iMirror-tests", Guid.NewGuid().ToString("N"));

	public RollingFileLogTests()
	{
		Directory.CreateDirectory(_directory);
	}

	public void Dispose()
	{
		try { Directory.Delete(_directory, recursive: true); } catch { }
	}

	[Fact]
	public void Write_ThenFlush_PersistsLinesInOrder()
	{
		string path = Path.Combine(_directory, "app.log");
		using var log = new RollingFileLog(path, maxBytes: 1024 * 1024, keepFiles: 2);

		for (int i = 0; i < 100; i++)
		{
			log.Write("line " + i);
		}
		log.Flush();

		string[] lines = File.ReadAllLines(path);
		Assert.Equal(100, lines.Length);
		Assert.Equal("line 0", lines[0]);
		Assert.Equal("line 99", lines[99]);
		Assert.Equal(0, log.DroppedLines);
	}

	[Fact]
	public void RotateIfNeeded_ShiftsArchivesAndDropsTheOldest()
	{
		string path = Path.Combine(_directory, "app.log");
		File.WriteAllText(path, new string('a', 200));
		File.WriteAllText(RollingFileLog.ArchivePath(path, 1), "one");
		File.WriteAllText(RollingFileLog.ArchivePath(path, 2), "two");
		File.WriteAllText(RollingFileLog.ArchivePath(path, 3), "three");

		RollingFileLog.RotateIfNeeded(path, maxBytes: 100, keepFiles: 3);

		Assert.False(File.Exists(path));
		Assert.Equal(new string('a', 200), File.ReadAllText(RollingFileLog.ArchivePath(path, 1)));
		Assert.Equal("one", File.ReadAllText(RollingFileLog.ArchivePath(path, 2)));
		Assert.Equal("two", File.ReadAllText(RollingFileLog.ArchivePath(path, 3)));
		Assert.False(File.Exists(RollingFileLog.ArchivePath(path, 4)));
	}

	[Fact]
	public void RotateIfNeeded_LeavesSmallFilesAlone()
	{
		string path = Path.Combine(_directory, "app.log");
		File.WriteAllText(path, "small");

		RollingFileLog.RotateIfNeeded(path, maxBytes: 100, keepFiles: 3);

		Assert.Equal("small", File.ReadAllText(path));
		Assert.False(File.Exists(RollingFileLog.ArchivePath(path, 1)));
	}

	[Fact]
	public void Write_RotatesWhenTheActiveFilePassesMaxBytes()
	{
		string path = Path.Combine(_directory, "app.log");
		// Floor is 64 KiB; write well past 2x that in distinguishable batches.
		using var log = new RollingFileLog(path, maxBytes: 64 * 1024, keepFiles: 2);
		string payload = new string('x', 1000);

		for (int i = 0; i < 200; i++)
		{
			log.Write(i + " " + payload);
			if (i % 20 == 19)
			{
				log.Flush();
			}
		}
		log.Flush();

		Assert.True(File.Exists(RollingFileLog.ArchivePath(path, 1)), "expected the first archive");
		// ~200 KB were written; with keepFiles=2 the first archive has been discarded, so what
		// remains is the active file plus two archives of >= 64 KiB each.
		long total = Directory.GetFiles(_directory).Sum(f => new FileInfo(f).Length);
		Assert.True(total >= 120_000, $"total bytes on disk {total}");
		Assert.True(new FileInfo(path).Length <= 64 * 1024 + 25 * 1024, "active file should have rotated near the cap");
		Assert.True(Directory.GetFiles(_directory).Length <= 3, "keepFiles=2 means at most active + 2 archives");
	}

	[Fact]
	public void ArchivePath_InsertsTheIndexBeforeTheExtension()
	{
		Assert.Equal(Path.Combine("C:\\logs", "iMirror.2.log"), RollingFileLog.ArchivePath(Path.Combine("C:\\logs", "iMirror.log"), 2));
	}
}
