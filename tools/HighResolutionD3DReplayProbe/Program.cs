using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using MacMirrorReceiver.Video;

internal static class Program
{
	[STAThread]
	public static int Main(string[] args)
	{
		if (args.Length < 1)
		{
			Console.Error.WriteLine("Usage: HighResolutionD3DReplayProbe <capture.submitted.h264> [max-access-units=600] [geometry=2048x1152@30]");
			return 2;
		}

		string capturePath = Path.GetFullPath(args[0]);
		if (!File.Exists(capturePath))
		{
			Console.Error.WriteLine("H.264 capture not found: " + capturePath);
			return 2;
		}

		int maxAccessUnits = args.Length >= 2 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMax)
			? parsedMax
			: 600;
		if (!TryParseGeometry(args.Length >= 3 ? args[2] : "2048x1152@30", out VideoGeometry geometry))
		{
			Console.Error.WriteLine("Invalid geometry. Use WxH or WxH@fps, for example 2048x1152@30.");
			return 2;
		}

		byte[] data = File.ReadAllBytes(capturePath);
		List<AccessUnit> accessUnits = BuildAccessUnits(data);
		int unitsToFeed = Math.Min(Math.Max(1, maxAccessUnits), accessUnits.Count);
		Console.WriteLine("iMirror high-resolution D3D product replay probe");
		Console.WriteLine("capture=" + capturePath);
		Console.WriteLine($"bytes={data.Length:N0}, accessUnits={accessUnits.Count:N0}, feed={unitsToFeed:N0}");
		Console.WriteLine($"geometry={geometry.Width}x{geometry.Height}@{geometry.Fps}");

		IntPtr windowHandle = Native.GetConsoleWindow();
		if (windowHandle == IntPtr.Zero)
		{
			windowHandle = Native.GetDesktopWindow();
		}

		long decodedFrames = 0;
		long presentedFrames = 0;
		long firstDecodedTick = 0;
		using var decodedFrameQueue = new BlockingCollection<D3D11VideoFrame>();
		using var presenter = new D3D11VideoProcessorD3DImagePresenter(windowHandle);
		Console.WriteLine("d3d11MultithreadProtected=" + presenter.IsMultithreadProtected);
		using var decoder = new MediaFoundationD3D11Decoder(geometry.Width, geometry.Height, geometry.Fps, presenter.Device);
		decoder.StatusChanged += message => Console.WriteLine("status: " + message);
		decoder.FrameDecoded += frame =>
		{
			if (Interlocked.Increment(ref decodedFrames) == 1)
			{
				firstDecodedTick = Stopwatch.GetTimestamp();
			}
			if (!decodedFrameQueue.IsAddingCompleted)
			{
				decodedFrameQueue.Add(frame);
				return;
			}
			frame.Dispose();
		};

		void DrainDecodedFrames()
		{
			while (decodedFrameQueue.TryTake(out D3D11VideoFrame? frame))
			{
				try
				{
					presenter.PresentNv12Texture(frame.Texture, frame.SubresourceIndex, frame.Width, frame.Height, frame.Fps);
					Interlocked.Increment(ref presentedFrames);
				}
				finally
				{
					frame.Dispose();
				}
			}
		}

		void DisposeQueuedFrames()
		{
			while (decodedFrameQueue.TryTake(out D3D11VideoFrame? frame))
			{
				frame.Dispose();
			}
		}

		long startTick = Stopwatch.GetTimestamp();
		try
		{
			decoder.Start();
			for (int i = 0; i < unitsToFeed; i++)
			{
				AccessUnit accessUnit = accessUnits[i];
				var payload = new byte[accessUnit.Length];
				Buffer.BlockCopy(data, accessUnit.Offset, payload, 0, payload.Length);
				if (!decoder.QueueH264(payload, 0, Stopwatch.GetTimestamp()))
				{
					Console.WriteLine($"QueueH264 returned false at accessUnit={i + 1:N0}");
					break;
				}
			}

			WaitForReplayToSettle(
				decoder,
				() => Interlocked.Read(ref decodedFrames),
				() => Interlocked.Read(ref presentedFrames),
				DrainDecodedFrames,
				TimeSpan.FromSeconds(13.0));
		}
		finally
		{
			decodedFrameQueue.CompleteAdding();
			DisposeQueuedFrames();
		}
		long elapsedMs = ElapsedMilliseconds(startTick, Stopwatch.GetTimestamp());
		long firstDecodedMs = firstDecodedTick == 0 ? -1 : ElapsedMilliseconds(startTick, firstDecodedTick);
		Console.WriteLine("== Summary ==");
		Console.WriteLine($"queuedRemaining={decoder.QueuedInputPackets:N0}, accepted={decoder.AcceptedInputPackets:N0}, submitted={decoder.WrittenInputPackets:N0}, dropped={decoder.DroppedInputPackets:N0}");
		Console.WriteLine($"decodedFrames={decodedFrames:N0}, presentedFrames={presentedFrames:N0}, firstDecodedMs={firstDecodedMs}, elapsedMs={elapsedMs:N0}");
		bool pass = decodedFrames > 0 && presentedFrames > 0;
		Console.WriteLine((pass ? "PASS" : "FAIL") + ": product MF/D3D11 decode-to-D3DImage replay");
		return pass ? 0 : 1;
	}

	private static void WaitForReplayToSettle(
		MediaFoundationD3D11Decoder decoder,
		Func<long> decodedFrames,
		Func<long> presentedFrames,
		Action drainDecodedFrames,
		TimeSpan timeout)
	{
		long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
		while (Environment.TickCount64 < deadline)
		{
			drainDecodedFrames();
			if (decoder.QueuedInputPackets == 0 && decodedFrames() > 0 && presentedFrames() > 0)
			{
				Thread.Sleep(500);
				drainDecodedFrames();
				return;
			}
			Thread.Sleep(25);
		}
		drainDecodedFrames();
	}

	private static bool TryParseGeometry(string text, out VideoGeometry geometry)
	{
		geometry = default;
		string[] sizeAndRate = text.Split('@', 2, StringSplitOptions.TrimEntries);
		string[] size = sizeAndRate[0].Split('x', 2, StringSplitOptions.TrimEntries);
		if (size.Length != 2 ||
			!int.TryParse(size[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int width) ||
			!int.TryParse(size[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
		{
			return false;
		}

		int fps = 30;
		if (sizeAndRate.Length == 2 && !int.TryParse(sizeAndRate[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out fps))
		{
			return false;
		}
		if (width <= 0 || height <= 0 || fps <= 0)
		{
			return false;
		}

		geometry = new VideoGeometry(width, height, fps);
		return true;
	}

	private static List<AccessUnit> BuildAccessUnits(byte[] data)
	{
		var nals = EnumerateAnnexBNalUnitsWithStartCodes(data).ToList();
		var units = new List<AccessUnit>();
		int currentStart = -1;
		bool currentHasVcl = false;

		foreach (NalUnit nal in nals)
		{
			bool isVcl = nal.Type is 1 or 5;
			bool startsNewAccessUnit = isVcl && currentHasVcl && TryReadFirstMbInSlice(data, nal, out uint firstMbInSlice) && firstMbInSlice == 0;
			if (startsNewAccessUnit && currentStart >= 0)
			{
				units.Add(new AccessUnit(currentStart, nal.StartCodeOffset - currentStart));
				currentStart = nal.StartCodeOffset;
				currentHasVcl = false;
			}
			if (currentStart < 0)
			{
				currentStart = nal.StartCodeOffset;
			}
			if (isVcl)
			{
				currentHasVcl = true;
			}
		}

		if (currentStart >= 0)
		{
			units.Add(new AccessUnit(currentStart, data.Length - currentStart));
		}

		return units.Where(unit => unit.Length > 0).ToList();
	}

	private static IEnumerable<NalUnit> EnumerateAnnexBNalUnitsWithStartCodes(byte[] data)
	{
		int searchOffset = 0;
		while (TryFindStartCode(data, searchOffset, out int start, out int startCodeLength))
		{
			int nalOffset = start + startCodeLength;
			int nextSearch = Math.Max(nalOffset, start + 1);
			int nextStart = TryFindStartCode(data, nextSearch, out int foundNextStart, out _)
				? foundNextStart
				: data.Length;
			if (nalOffset < nextStart)
			{
				yield return new NalUnit(start, nalOffset, nextStart, data[nalOffset] & 0x1F);
			}
			searchOffset = nextStart;
		}
	}

	private static bool TryReadFirstMbInSlice(byte[] data, NalUnit nal, out uint firstMbInSlice)
	{
		firstMbInSlice = 0;
		int payloadOffset = nal.NalOffset + 1;
		int payloadLength = Math.Max(0, nal.EndOffset - payloadOffset);
		if (payloadLength == 0)
		{
			return false;
		}

		List<byte> rbsp = BuildRbsp(data, payloadOffset, nal.EndOffset);
		return TryReadUnsignedExpGolomb(rbsp, 0, out firstMbInSlice);
	}

	private static List<byte> BuildRbsp(byte[] data, int start, int end)
	{
		var rbsp = new List<byte>(Math.Max(0, end - start));
		int zeroCount = 0;
		for (int i = start; i < end; i++)
		{
			byte value = data[i];
			if (zeroCount >= 2 && value == 0x03)
			{
				zeroCount = 0;
				continue;
			}
			rbsp.Add(value);
			zeroCount = value == 0 ? zeroCount + 1 : 0;
		}
		return rbsp;
	}

	private static bool TryReadUnsignedExpGolomb(IReadOnlyList<byte> rbsp, int bitOffset, out uint value)
	{
		value = 0;
		int leadingZeroBits = 0;
		while (ReadBit(rbsp, bitOffset + leadingZeroBits) == 0)
		{
			leadingZeroBits++;
			if (leadingZeroBits > 31 || bitOffset + leadingZeroBits >= rbsp.Count * 8)
			{
				return false;
			}
		}

		uint suffix = 0;
		for (int i = 0; i < leadingZeroBits; i++)
		{
			suffix = (suffix << 1) | (uint)ReadBit(rbsp, bitOffset + leadingZeroBits + 1 + i);
		}
		value = ((1u << leadingZeroBits) - 1u) + suffix;
		return true;
	}

	private static int ReadBit(IReadOnlyList<byte> bytes, int bitOffset)
	{
		if (bitOffset < 0 || bitOffset >= bytes.Count * 8)
		{
			return 0;
		}
		return (bytes[bitOffset / 8] >> (7 - (bitOffset % 8))) & 1;
	}

	private static bool TryFindStartCode(byte[] data, int offset, out int start, out int length)
	{
		for (int i = Math.Max(0, offset); i + 3 < data.Length; i++)
		{
			if (data[i] != 0 || data[i + 1] != 0)
			{
				continue;
			}
			if (data[i + 2] == 1)
			{
				start = i;
				length = 3;
				return true;
			}
			if (i + 4 < data.Length && data[i + 2] == 0 && data[i + 3] == 1)
			{
				start = i;
				length = 4;
				return true;
			}
		}

		start = -1;
		length = 0;
		return false;
	}

	private static long ElapsedMilliseconds(long startTick, long endTick)
	{
		if (endTick <= startTick)
		{
			return 0L;
		}
		return Math.Max(0L, (long)Math.Round((double)(endTick - startTick) * 1000.0 / Stopwatch.Frequency));
	}

	private readonly record struct VideoGeometry(int Width, int Height, int Fps);

	private readonly record struct NalUnit(int StartCodeOffset, int NalOffset, int EndOffset, int Type);

	private readonly record struct AccessUnit(int Offset, int Length);

	private static class Native
	{
		[DllImport("kernel32.dll")]
		public static extern IntPtr GetConsoleWindow();

		[DllImport("user32.dll")]
		public static extern IntPtr GetDesktopWindow();
	}
}
