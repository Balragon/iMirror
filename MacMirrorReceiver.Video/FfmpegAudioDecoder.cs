using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MacMirrorReceiver.Video;

public sealed class FfmpegAudioDecoder : IDisposable
{
	private const int MaxQueuedInputFrames = 512;
	private const long MaxQueuedInputBytes = 4L * 1024L * 1024L;
	private const int MaxSeenSequences = 4096;
	private const long WriteStallThresholdMs = 25;

	private readonly int _sampleRate;
	private readonly int _channels;
	private readonly int _samplesPerFrame;
	private readonly int _pcmFrameBytes;
	private readonly CancellationTokenSource _cts = new CancellationTokenSource();
	private readonly object _processGate = new object();
	private readonly object _inputGate = new object();
	private readonly object _timingGate = new object();
	private readonly SemaphoreSlim _inputSignal = new SemaphoreSlim(0);
	private readonly Queue<AacAudioInputFrame> _inputQueue = new Queue<AacAudioInputFrame>();
	private readonly Queue<AudioFrameTiming> _writtenTiming = new Queue<AudioFrameTiming>();
	private readonly HashSet<ushort> _seenSequences = new HashSet<ushort>();
	private readonly Queue<ushort> _seenSequenceOrder = new Queue<ushort>();
	private long _queuedInputBytes;
	private long _acceptedInputFrames;
	private long _writtenInputFrames;
	private long _decodedPcmFrames;
	private long _duplicateInputFrames;
	private long _latestWriteMilliseconds;
	private long _maxWriteMilliseconds;
	private long _writeStalls;
	private long _lastWriteStallStatusTick;
	private uint? _baseRtpTimestamp;
	private ulong _nextDecodeTime;
	private uint _fragmentSequence = 1;
	private Process? _process;
	private Task? _readTask;
	private Task? _writeTask;

	public FfmpegAudioDecoder(int sampleRate, int channels, int samplesPerFrame)
	{
		_sampleRate = sampleRate > 0 ? sampleRate : 44100;
		_channels = channels > 0 ? channels : 2;
		_samplesPerFrame = samplesPerFrame > 0 ? samplesPerFrame : 480;
		_pcmFrameBytes = checked(_samplesPerFrame * _channels * sizeof(short));
	}

	public int SampleRate => _sampleRate;

	public int Channels => _channels;

	public int SamplesPerFrame => _samplesPerFrame;

	public int PcmFrameBytes => _pcmFrameBytes;

	public long AcceptedInputFrames => Interlocked.Read(ref _acceptedInputFrames);

	public long WrittenInputFrames => Interlocked.Read(ref _writtenInputFrames);

	public long DecodedPcmFrames => Interlocked.Read(ref _decodedPcmFrames);

	public long DuplicateInputFrames => Interlocked.Read(ref _duplicateInputFrames);

	public long DroppedInputFrames { get; private set; }

	public long LatestWriteMilliseconds => Interlocked.Read(ref _latestWriteMilliseconds);

	public long MaxWriteMilliseconds => Interlocked.Read(ref _maxWriteMilliseconds);

	public long WriteStalls => Interlocked.Read(ref _writeStalls);

	public int QueuedInputFrames
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

	public event Action<AudioPcmFrame>? PcmFrameDecoded;

	public void Start()
	{
		string? ffmpegPath = FfmpegDecoder.FindFfmpeg();
		if (ffmpegPath == null)
		{
			throw new InvalidOperationException("ffmpeg.exe was not found. Put it on PATH or at tools\\ffmpeg\\bin\\ffmpeg.exe.");
		}

		StartFfmpegProcess(ffmpegPath);
		_writeTask = Task.Run(WriteInputLoopAsync);
	}

	public bool QueueFrame(byte[] frame, uint rtpTimestamp, ushort sequence, long receivedTick)
	{
		if (frame == null || frame.Length == 0 || _cts.IsCancellationRequested || !IsProcessAlive(_process))
		{
			return false;
		}

		bool shouldSignal = false;
		lock (_inputGate)
		{
			if (_seenSequences.Contains(sequence))
			{
				Interlocked.Increment(ref _duplicateInputFrames);
				return false;
			}
			_seenSequences.Add(sequence);
			_seenSequenceOrder.Enqueue(sequence);
			while (_seenSequenceOrder.Count > MaxSeenSequences)
			{
				_seenSequences.Remove(_seenSequenceOrder.Dequeue());
			}

			if (_inputQueue.Count >= MaxQueuedInputFrames || _queuedInputBytes + frame.Length > MaxQueuedInputBytes)
			{
				DroppedInputFrames += _inputQueue.Count;
				_inputQueue.Clear();
				_queuedInputBytes = 0L;
				lock (_timingGate)
				{
					_writtenTiming.Clear();
				}
				this.StatusChanged?.Invoke("Audio decoder input overflow: flushed queued AAC frames to recover low latency.");
			}

			shouldSignal = _inputQueue.Count == 0;
			_inputQueue.Enqueue(new AacAudioInputFrame(frame, rtpTimestamp, sequence, receivedTick));
			_queuedInputBytes += frame.Length;
			Interlocked.Increment(ref _acceptedInputFrames);
		}

		if (shouldSignal)
		{
			_inputSignal.Release();
		}
		return true;
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
		TrySetAboveNormalPriority(process);
		lock (_processGate)
		{
			_process = process;
		}

		this.StatusChanged?.Invoke($"FFmpeg audio decoder started: {ffmpegPath} (AAC-ELD -> s16le {_sampleRate}Hz/{_channels}ch)");
		this.StatusChanged?.Invoke($"FFmpeg audio cmd: {ffmpegPath} {arguments}");
		Task.Run(() => ReadErrorLinesAsync(process));
		_readTask = Task.Run(() => ReadPcmFramesAsync(process));
	}

	private string BuildArguments()
	{
		return "-hide_banner -loglevel error -flags low_delay -probesize 64k -analyzeduration 100000 "
			+ "-f mp4 -i pipe:0 -vn "
			+ $"-f s16le -acodec pcm_s16le -ar {_sampleRate} -ac {_channels} pipe:1";
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
					this.StatusChanged?.Invoke("audio: " + line.Trim());
				}
			}
		}
		catch
		{
		}
	}

	private async Task WriteInputLoopAsync()
	{
		SetCurrentThreadAboveNormal();
		try
		{
			Process? process = _process;
			if (process == null)
			{
				return;
			}

			byte[] init = BuildInitSegment(_sampleRate, _channels, _samplesPerFrame);
			await process.StandardInput.BaseStream.WriteAsync(init, _cts.Token);
			await process.StandardInput.BaseStream.FlushAsync(_cts.Token);

			while (!_cts.IsCancellationRequested)
			{
				await _inputSignal.WaitAsync(_cts.Token);
				while (!_cts.IsCancellationRequested)
				{
					AacAudioInputFrame? packet = null;
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

					process = _process;
					if (!IsProcessAlive(process))
					{
						return;
					}

					try
					{
						ulong sourceTimestampNanos = CalculateAudioSourceTimestampNanos(packet.RtpTimestamp);
						byte[] segment = BuildMediaSegment(packet.Payload, _fragmentSequence++, _nextDecodeTime, _samplesPerFrame);
						_nextDecodeTime += (ulong)_samplesPerFrame;
						long startTick = Stopwatch.GetTimestamp();
						await process!.StandardInput.BaseStream.WriteAsync(segment, _cts.Token);
						await process.StandardInput.BaseStream.FlushAsync(_cts.Token);
						long endTick = Stopwatch.GetTimestamp();
						lock (_timingGate)
						{
							_writtenTiming.Enqueue(new AudioFrameTiming(packet.RtpTimestamp, packet.Sequence, packet.ReceivedTick, sourceTimestampNanos));
						}
						RecordWriteMetrics(ElapsedMilliseconds(startTick, endTick));
					}
					catch (Exception ex) when (ex is IOException || ex is InvalidOperationException || ex is ObjectDisposedException)
					{
						this.StatusChanged?.Invoke("Audio decoder input stopped: " + ex.Message);
						return;
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			this.StatusChanged?.Invoke("Audio decoder input closed: " + ex.Message);
		}
	}

	private async Task ReadPcmFramesAsync(Process process)
	{
		SetCurrentThreadAboveNormal();
		try
		{
			Stream stream = process.StandardOutput.BaseStream;
			while (!_cts.IsCancellationRequested)
			{
				byte[] buffer = ArrayPool<byte>.Shared.Rent(_pcmFrameBytes);
				try
				{
					await ReadExactAsync(stream, buffer, _pcmFrameBytes, _cts.Token);
					AudioFrameTiming timing;
					lock (_timingGate)
					{
						timing = _writtenTiming.Count > 0
							? _writtenTiming.Dequeue()
							: AudioFrameTiming.Empty;
					}

					var frame = new AudioPcmFrame
					{
						Buffer = buffer,
						ByteCount = _pcmFrameBytes,
						SampleRate = _sampleRate,
						Channels = _channels,
						SamplesPerFrame = _samplesPerFrame,
						RtpTimestamp = timing.RtpTimestamp,
						Sequence = timing.Sequence,
						ReceivedTick = timing.ReceivedTick,
						DecodedTick = Stopwatch.GetTimestamp(),
						SourceTimestampNanos = timing.SourceTimestampNanos,
						ReturnBuffer = rented => ArrayPool<byte>.Shared.Return(rented)
					};

					if (this.PcmFrameDecoded is { } handler)
					{
						handler(frame);
					}
					else
					{
						frame.Release();
					}

					if (Interlocked.Increment(ref _decodedPcmFrames) == 1)
					{
						this.StatusChanged?.Invoke("FFmpeg audio decoded first PCM frame.");
					}
				}
				catch
				{
					ArrayPool<byte>.Shared.Return(buffer);
					throw;
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			if (!_cts.IsCancellationRequested)
			{
				this.StatusChanged?.Invoke("Audio decoder stopped: " + ex.Message);
			}
		}
	}

	private ulong CalculateAudioSourceTimestampNanos(uint rtpTimestamp)
	{
		if (!_baseRtpTimestamp.HasValue)
		{
			_baseRtpTimestamp = rtpTimestamp;
			return 0UL;
		}

		uint delta = unchecked(rtpTimestamp - _baseRtpTimestamp.Value);
		return (ulong)Math.Round((double)delta * 1_000_000_000.0 / _sampleRate);
	}

	private void RecordWriteMetrics(long writeMs)
	{
		Interlocked.Increment(ref _writtenInputFrames);
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
			this.StatusChanged?.Invoke($"FFmpeg audio stdin write stall: {writeMs}ms, queued={QueuedInputFrames}, dropped={DroppedInputFrames:N0}");
		}
	}

	private static byte[] BuildInitSegment(int sampleRate, int channels, int samplesPerFrame)
	{
		byte[] asc = BuildEldAudioSpecificConfig(sampleRate, channels, samplesPerFrame);
		byte[] stsd = FullBox("stsd", 0, 0, UInt32(1), Mp4a(asc, sampleRate, channels));
		byte[] stts = FullBox("stts", 0, 0, UInt32(0));
		byte[] stsc = FullBox("stsc", 0, 0, UInt32(0));
		byte[] stsz = FullBox("stsz", 0, 0, UInt32(0), UInt32(0));
		byte[] stco = FullBox("stco", 0, 0, UInt32(0));
		byte[] stbl = Box("stbl", stsd, stts, stsc, stsz, stco);
		byte[] smhd = FullBox("smhd", 0, 0, UInt16(0), UInt16(0));
		byte[] dref = FullBox("dref", 0, 0, UInt32(1), FullBox("url ", 0, 1, Array.Empty<byte>()));
		byte[] dinf = Box("dinf", dref);
		byte[] minf = Box("minf", smhd, dinf, stbl);
		byte[] mdhd = FullBox("mdhd", 0, 0, UInt32(0), UInt32(0), UInt32((uint)sampleRate), UInt32(0), UInt16(0x55C4), UInt16(0));
		byte[] hdlr = FullBox("hdlr", 0, 0, UInt32(0), FourCc("soun"), new byte[12], Encoding.ASCII.GetBytes("SoundHandler\0"));
		byte[] mdia = Box("mdia", mdhd, hdlr, minf);
		byte[] tkhd = FullBox(
			"tkhd",
			0,
			7,
			UInt32(0),
			UInt32(0),
			UInt32(1),
			UInt32(0),
			UInt32(0),
			new byte[8],
			UInt16(0),
			UInt16(0),
			UInt16(0x0100),
			UInt16(0),
			Matrix(),
			UInt32(0),
			UInt32(0));
		byte[] trak = Box("trak", tkhd, mdia);
		byte[] mvhd = FullBox(
			"mvhd",
			0,
			0,
			UInt32(0),
			UInt32(0),
			UInt32((uint)sampleRate),
			UInt32(0),
			UInt32(0x00010000),
			UInt16(0x0100),
			new byte[10],
			Matrix(),
			new byte[24],
			UInt32(2));
		byte[] trex = FullBox("trex", 0, 0, UInt32(1), UInt32(1), UInt32((uint)samplesPerFrame), UInt32(0), UInt32(0));
		byte[] mvex = Box("mvex", trex);
		byte[] moov = Box("moov", mvhd, trak, mvex);
		byte[] ftyp = Box("ftyp", FourCc("isom"), UInt32(0x00000200), FourCc("isom"), FourCc("iso6"), FourCc("mp41"), FourCc("mp42"), FourCc("M4A "));
		return Concat(ftyp, moov);
	}

	private static byte[] BuildMediaSegment(byte[] frame, uint sequenceNumber, ulong decodeTime, int samplesPerFrame)
	{
		byte[] mfhd = FullBox("mfhd", 0, 0, UInt32(sequenceNumber));
		byte[] tfhd = FullBox("tfhd", 0, 0x020008, UInt32(1), UInt32((uint)samplesPerFrame));
		byte[] tfdt = FullBox("tfdt", 1, 0, UInt64(decodeTime));
		byte[] trun = BuildTrun(frame.Length, 0);
		byte[] traf = Box("traf", tfhd, tfdt, trun);
		byte[] moof = Box("moof", mfhd, traf);
		int dataOffset = checked(moof.Length + 8);
		trun = BuildTrun(frame.Length, dataOffset);
		traf = Box("traf", tfhd, tfdt, trun);
		moof = Box("moof", mfhd, traf);
		byte[] mdat = Box("mdat", frame);
		return Concat(moof, mdat);
	}

	private static byte[] BuildTrun(int sampleSize, int dataOffset)
	{
		return FullBox("trun", 0, 0x000201, UInt32(1), Int32(dataOffset), UInt32((uint)sampleSize));
	}

	private static byte[] Mp4a(byte[] asc, int sampleRate, int channels)
	{
		return Box(
			"mp4a",
			new byte[6],
			UInt16(1),
			new byte[8],
			UInt16((ushort)channels),
			UInt16(16),
			UInt16(0),
			UInt16(0),
			UInt32((uint)sampleRate << 16),
			Esds(asc));
	}

	private static byte[] Esds(byte[] asc)
	{
		byte[] decoderSpecificInfo = Descriptor(0x05, asc);
		byte[] decoderConfig = Descriptor(0x04, Concat(new byte[] { 0x40, 0x15, 0x00, 0x00, 0x00 }, UInt32(0), UInt32(0), decoderSpecificInfo));
		byte[] slConfig = Descriptor(0x06, new byte[] { 0x02 });
		byte[] esDescriptor = Descriptor(0x03, Concat(UInt16(0), new byte[] { 0x00 }, decoderConfig, slConfig));
		return FullBox("esds", 0, 0, esDescriptor);
	}

	private static byte[] Descriptor(int tag, byte[] payload)
	{
		if (payload.Length > 0x7F)
		{
			throw new InvalidOperationException("MP4 descriptor payload is unexpectedly large.");
		}
		return Concat(new[] { (byte)tag, (byte)0x80, (byte)0x80, (byte)0x80, (byte)payload.Length }, payload);
	}

	private static byte[] BuildEldAudioSpecificConfig(int sampleRate, int channels, int samplesPerFrame)
	{
		var writer = new BitWriter();
		writer.Write(31, 5);
		writer.Write(39 - 32, 6);
		writer.Write(GetAacFrequencyIndex(sampleRate), 4);
		writer.Write(channels, 4);
		writer.Write(samplesPerFrame == 480 ? 1 : 0, 1);
		writer.Write(0, 1);
		writer.Write(0, 1);
		writer.Write(0, 1);
		writer.Write(0, 1);
		writer.Write(0, 4);
		return writer.ToArray();
	}

	private static int GetAacFrequencyIndex(int sampleRate)
	{
		return sampleRate switch
		{
			96000 => 0,
			88200 => 1,
			64000 => 2,
			48000 => 3,
			44100 => 4,
			32000 => 5,
			24000 => 6,
			22050 => 7,
			16000 => 8,
			12000 => 9,
			11025 => 10,
			8000 => 11,
			7350 => 12,
			_ => throw new InvalidOperationException("Unsupported AAC sample rate: " + sampleRate)
		};
	}

	private static byte[] Box(string type, params byte[][] payloads)
	{
		int size = checked(8 + payloads.Sum(payload => payload.Length));
		byte[] output = new byte[size];
		BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(0, 4), (uint)size);
		Encoding.ASCII.GetBytes(type, output.AsSpan(4, 4));
		int offset = 8;
		foreach (byte[] payload in payloads)
		{
			Buffer.BlockCopy(payload, 0, output, offset, payload.Length);
			offset += payload.Length;
		}
		return output;
	}

	private static byte[] FullBox(string type, byte version, int flags, params byte[][] payloads)
	{
		byte[] header = new byte[4];
		header[0] = version;
		header[1] = (byte)((flags >> 16) & 0xFF);
		header[2] = (byte)((flags >> 8) & 0xFF);
		header[3] = (byte)(flags & 0xFF);
		return Box(type, Concat(new[] { header }.Concat(payloads).ToArray()));
	}

	private static byte[] Concat(params byte[][] arrays)
	{
		int length = checked(arrays.Sum(array => array.Length));
		byte[] output = new byte[length];
		int offset = 0;
		foreach (byte[] array in arrays)
		{
			Buffer.BlockCopy(array, 0, output, offset, array.Length);
			offset += array.Length;
		}
		return output;
	}

	private static byte[] FourCc(string value)
	{
		return Encoding.ASCII.GetBytes(value);
	}

	private static byte[] UInt16(ushort value)
	{
		byte[] output = new byte[2];
		BinaryPrimitives.WriteUInt16BigEndian(output, value);
		return output;
	}

	private static byte[] UInt32(uint value)
	{
		byte[] output = new byte[4];
		BinaryPrimitives.WriteUInt32BigEndian(output, value);
		return output;
	}

	private static byte[] UInt64(ulong value)
	{
		byte[] output = new byte[8];
		BinaryPrimitives.WriteUInt64BigEndian(output, value);
		return output;
	}

	private static byte[] Int32(int value)
	{
		byte[] output = new byte[4];
		BinaryPrimitives.WriteInt32BigEndian(output, value);
		return output;
	}

	private static byte[] Matrix()
	{
		return new byte[]
		{
			0x00, 0x01, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x00,
			0x00, 0x01, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x00,
			0x00, 0x00, 0x00, 0x00,
			0x40, 0x00, 0x00, 0x00
		};
	}

	private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
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
			if (IsProcessAlive(_process))
			{
				_process!.Kill(entireProcessTree: true);
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
		_inputSignal.Dispose();
		_cts.Dispose();
	}

	private sealed class BitWriter
	{
		private readonly List<int> _bits = new List<int>();

		public void Write(int value, int bitCount)
		{
			for (int bit = bitCount - 1; bit >= 0; bit--)
			{
				_bits.Add((value >> bit) & 1);
			}
		}

		public byte[] ToArray()
		{
			List<int> bits = new List<int>(_bits);
			while (bits.Count % 8 != 0)
			{
				bits.Add(0);
			}
			byte[] output = new byte[bits.Count / 8];
			for (int i = 0; i < output.Length; i++)
			{
				int value = 0;
				for (int bit = 0; bit < 8; bit++)
				{
					value = (value << 1) | bits[i * 8 + bit];
				}
				output[i] = (byte)value;
			}
			return output;
		}
	}
}

public sealed class AudioPcmFrame
{
	private bool _released;

	public required byte[] Buffer { get; init; }

	public required int ByteCount { get; init; }

	public required int SampleRate { get; init; }

	public required int Channels { get; init; }

	public required int SamplesPerFrame { get; init; }

	public uint RtpTimestamp { get; init; }

	public ushort Sequence { get; init; }

	public long ReceivedTick { get; init; }

	public long DecodedTick { get; init; }

	public ulong SourceTimestampNanos { get; init; }

	public Action<byte[]>? ReturnBuffer { get; init; }

	public void Release()
	{
		if (_released)
		{
			return;
		}
		_released = true;
		ReturnBuffer?.Invoke(Buffer);
	}
}

internal sealed record AacAudioInputFrame(byte[] Payload, uint RtpTimestamp, ushort Sequence, long ReceivedTick);

internal readonly record struct AudioFrameTiming(uint RtpTimestamp, ushort Sequence, long ReceivedTick, ulong SourceTimestampNanos)
{
	public static readonly AudioFrameTiming Empty = new AudioFrameTiming(0, 0, 0, 0);
}
