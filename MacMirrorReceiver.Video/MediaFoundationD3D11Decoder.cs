#if HIGH_RESOLUTION_D3D
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using D3D11 = SharpDX.Direct3D11;

namespace MacMirrorReceiver.Video;

public sealed class MediaFoundationD3D11Decoder : IDisposable
{
	private const int MaxQueuedInputPackets = 1024;
	private const long MaxQueuedInputBytes = 64L * 1024L * 1024L;
	private const long WriteStallThresholdMs = 35;
	private const long MaxDumpBytes = 512L * 1024L * 1024L;
	private const int MfVersion = 0x00020070;
	private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(12.0);
	private static readonly TimeSpan DecodeStallThreshold = TimeSpan.FromSeconds(5.0);
	private static int s_dumpInstanceCounter;

	private readonly int _width;
	private readonly int _height;
	private readonly int _fps;
	private readonly D3D11.Device _device;
	private readonly CancellationTokenSource _cts = new CancellationTokenSource();
	private readonly object _inputGate = new object();
	private readonly Queue<H264InputPacket> _inputQueue = new Queue<H264InputPacket>();
	private readonly SemaphoreSlim _inputSignal = new SemaphoreSlim(0);

	private long _queuedInputBytes;
	private Task? _decodeTask;
	private IntPtr _transformPtr;
	private MfTransform? _transform;
	private IntPtr _dxgiManagerPtr;
	private bool _mfStarted;
	private bool _streamingStarted;
	private long _timestamp;

	private long _acceptedInputPackets;
	private long _writtenInputPackets;
	private long _latestWriteMilliseconds;
	private long _maxWriteMilliseconds;
	private long _writeStalls;
	private long _lastWriteStallStatusTick;
	private long _decodedOutputFrames;
	private long _outputSamplesWithoutD3D11Texture;
	private long _lastNonD3D11OutputStatusTick;
	private int _faulted;
	private bool _loggedFirstTextureDetails;
	private bool _loggedInvalidTextureSubresource;
	private bool _loggedSubresourceQueryFailure;
	private DumpFile? _receivedDump;
	private DumpFile? _submittedDump;

	public long DroppedInputPackets { get; private set; }

	public long AcceptedInputPackets => Interlocked.Read(ref _acceptedInputPackets);

	public long WrittenInputPackets => Interlocked.Read(ref _writtenInputPackets);

	public long LatestWriteMilliseconds => Interlocked.Read(ref _latestWriteMilliseconds);

	public long MaxWriteMilliseconds => Interlocked.Read(ref _maxWriteMilliseconds);

	public long WriteStalls => Interlocked.Read(ref _writeStalls);

	public int OutputWidth => _width;

	public int OutputHeight => _height;

	public int OutputFrameBytes => 0;

	public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

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

	public event Action<D3D11VideoFrame>? FrameDecoded;

	public event Action? InputQueueOverflowed;

	public event Action<string>? Faulted;

	public MediaFoundationD3D11Decoder(int width, int height, int fps, D3D11.Device device)
	{
		_width = width;
		_height = height;
		_fps = Math.Max(1, fps);
		_device = device;
	}

	public void Start()
	{
		TryOpenH264Dump();
		InitializeMediaFoundation();
		_decodeTask = Task.Run(DecodeLoopAsync);
		Task.Run(WatchFirstFrameTimeoutAsync);
		Task.Run(WatchDecodeLivenessAsync);
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
				string fileName = "imirror-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".h264";
				basePath = System.IO.Path.Combine(AppContext.BaseDirectory, fileName);
			}
			else
			{
				basePath = setting;
				string? dir = System.IO.Path.GetDirectoryName(basePath);
				if (!string.IsNullOrEmpty(dir))
				{
					System.IO.Directory.CreateDirectory(dir);
				}
			}

			int instance = Interlocked.Increment(ref s_dumpInstanceCounter);
			string receivedPath = AppendSuffix(basePath, $"d{instance:00}.received");
			string submittedPath = AppendSuffix(basePath, $"d{instance:00}.submitted");
			DumpFile? receivedDump = null;
			DumpFile? submittedDump = null;
			try
			{
				receivedDump = new DumpFile(receivedPath, MaxDumpBytes);
				submittedDump = new DumpFile(submittedPath, MaxDumpBytes);
				_receivedDump = receivedDump;
				_submittedDump = submittedDump;
				receivedDump = null;
				submittedDump = null;
			}
			finally
			{
				receivedDump?.Dispose();
				submittedDump?.Dispose();
			}
			StatusChanged?.Invoke($"H264 dump enabled for Media Foundation D3D11 path (max {MaxDumpBytes / (1024 * 1024)}MB): received={receivedPath}, submitted={submittedPath}.");
		}
		catch (Exception ex)
		{
			StatusChanged?.Invoke("H264 dump could not be opened for Media Foundation D3D11 path: " + ex.Message);
		}
	}

	private static string AppendSuffix(string path, string suffix)
	{
		string dir = System.IO.Path.GetDirectoryName(path) ?? string.Empty;
		string name = System.IO.Path.GetFileNameWithoutExtension(path);
		string ext = System.IO.Path.GetExtension(path);
		if (string.IsNullOrEmpty(ext))
		{
			ext = ".h264";
		}
		return System.IO.Path.Combine(dir, name + "." + suffix + ext);
	}

	private void InitializeMediaFoundation()
	{
		ThrowIfFailed(Native.MFStartup(MfVersion, 0), "MFStartup");
		_mfStarted = true;

		ThrowIfFailed(CreateH264DecoderTransform(out _transform, out _transformPtr), "CoCreateInstance(Microsoft H.264 Decoder MFT)");
		if (_transform == null)
		{
			throw new InvalidOperationException("Microsoft H.264 Decoder MFT was not created.");
		}

		ThrowIfFailed(Native.MFCreateDXGIDeviceManager(out int resetToken, out _dxgiManagerPtr), "MFCreateDXGIDeviceManager");
		var manager = (MfDxgiDeviceManager)Marshal.GetObjectForIUnknown(_dxgiManagerPtr);
		try
		{
			ThrowIfFailed(manager.ResetDevice(_device.NativePointer, resetToken), "IMFDXGIDeviceManager.ResetDevice");
		}
		finally
		{
			Marshal.ReleaseComObject(manager);
		}

		ThrowIfFailed(_transform.ProcessMessage(MftMessage.SetD3DManager, _dxgiManagerPtr), "MFT_MESSAGE_SET_D3D_MANAGER");
		ThrowIfFailed(SetH264InputType(_transform, _width, _height, _fps), "SetInputType(H264)");
		ConfigureOutputType("initial");

		_transform.ProcessMessage(MftMessage.NotifyBeginStreaming, IntPtr.Zero);
		_transform.ProcessMessage(MftMessage.NotifyStartOfStream, IntPtr.Zero);
		_streamingStarted = true;
		StatusChanged?.Invoke($"Media Foundation D3D11 decoder started: {_width}x{_height}@{_fps}, output=NV12 texture");
	}

	public bool QueueH264(byte[] payload, ulong sourceTimestampNanos, long receivedTick)
	{
		if (_cts.IsCancellationRequested || _transform == null || IsFaulted)
		{
			return false;
		}
		if (_receivedDump != null && _receivedDump.Write(payload))
		{
			StatusChanged?.Invoke($"H264 received dump reached {MaxDumpBytes / (1024 * 1024)}MB cap; further packets are not captured.");
		}

		bool shouldSignal = false;
		int flushed = 0;
		bool droppedIncoming = false;
		lock (_inputGate)
		{
			if (_inputQueue.Count >= MaxQueuedInputPackets || _queuedInputBytes + payload.Length > MaxQueuedInputBytes)
			{
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
			StatusChanged?.Invoke(droppedIncoming
				? $"Media Foundation decoder input overflow: flushed {flushed} queued packet(s); waiting for next keyframe."
				: $"Media Foundation decoder input overflow: flushed {flushed} queued packet(s); resuming from incoming keyframe.");
			if (droppedIncoming)
			{
				InputQueueOverflowed?.Invoke();
			}
		}

		if (shouldSignal)
		{
			_inputSignal.Release();
		}
		return !droppedIncoming;
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

	private async Task DecodeLoopAsync()
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
							_queuedInputBytes = Math.Max(0L, _queuedInputBytes - packet.Value.Payload.Length);
						}
					}
					if (packet == null)
					{
						break;
					}

					long startTick = Stopwatch.GetTimestamp();
					ProcessPacket(packet.Value);
					RecordWriteMetrics(ElapsedMilliseconds(startTick, Stopwatch.GetTimestamp()));
				}
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			Fault("Media Foundation D3D11 decoder stopped: " + ex.Message);
		}
	}

	private void ProcessPacket(H264InputPacket packet)
	{
		if (_transform == null || IsFaulted)
		{
			return;
		}

		long duration = 10_000_000L / _fps;
		using MfCom<IMFSample> sample = CreateSample(packet.Payload, _timestamp, duration);
		int hr = _transform.ProcessInput(0, sample.Value, 0);
		if (hr == HResults.MfENotAccepting)
		{
			DrainOutput(packet);
			hr = _transform.ProcessInput(0, sample.Value, 0);
		}
		ThrowIfFailed(hr, "ProcessInput");
		if (_submittedDump != null && _submittedDump.Write(packet.Payload))
		{
			StatusChanged?.Invoke($"H264 submitted dump reached {MaxDumpBytes / (1024 * 1024)}MB cap; further packets are not captured.");
		}
		Interlocked.Increment(ref _writtenInputPackets);
		_timestamp += duration;
		DrainOutput(packet);
	}

	private void DrainOutput(H264InputPacket packet)
	{
		if (_transform == null)
		{
			return;
		}

		for (int i = 0; i < 64; i++)
		{
			ThrowIfFailed(_transform.GetOutputStreamInfo(0, out MftOutputStreamInfo info), "GetOutputStreamInfo");
			IntPtr samplePtr = IntPtr.Zero;
			IMFSample? outputSample = null;
			if ((info.Flags & MftOutputStreamFlags.ProvidesSamples) == 0)
			{
				outputSample = CreateEmptySample(Math.Max(info.Size, _width * _height * 4)).Value;
				samplePtr = Marshal.GetIUnknownForObject(outputSample);
			}

			var buffer = new MftOutputDataBuffer
			{
				StreamId = 0,
				Sample = samplePtr,
				Status = 0,
				Events = IntPtr.Zero
			};
			IntPtr bufferPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<MftOutputDataBuffer>());
			Marshal.StructureToPtr(buffer, bufferPtr, false);
			int outputHr = _transform.ProcessOutput(0, 1, bufferPtr, out _);
			buffer = Marshal.PtrToStructure<MftOutputDataBuffer>(bufferPtr);
			Marshal.FreeCoTaskMem(bufferPtr);
			if (buffer.Events != IntPtr.Zero)
			{
				Marshal.Release(buffer.Events);
			}
			if (samplePtr != IntPtr.Zero)
			{
				Marshal.Release(samplePtr);
			}

			if (outputHr == HResults.MfETransformNeedMoreInput)
			{
				ReleaseOutputSample(outputSample, buffer.Sample, samplePtr);
				return;
			}
			if (outputHr == HResults.MfETransformStreamChange)
			{
				ReleaseOutputSample(outputSample, buffer.Sample, samplePtr);
				ConfigureOutputType("stream change");
				continue;
			}
			ThrowIfFailed(outputHr, "ProcessOutput");

			IMFSample? producedSample = outputSample;
			bool releaseProducedSample = false;
			if (buffer.Sample != IntPtr.Zero && (outputSample == null || buffer.Sample != samplePtr))
			{
				producedSample = (IMFSample)Marshal.GetObjectForIUnknown(buffer.Sample);
				releaseProducedSample = true;
			}

			try
			{
				if (producedSample != null && TryCreateD3D11Frame(producedSample, packet, out D3D11VideoFrame? frame) && frame != null)
				{
					D3D11VideoFrame decodedFrame = frame;
					if (FrameDecoded is { } handler)
					{
						handler(decodedFrame);
					}
					else
					{
						decodedFrame.Dispose();
					}
					if (Interlocked.Increment(ref _decodedOutputFrames) == 1)
					{
						StatusChanged?.Invoke("Media Foundation D3D11 decoder produced first NV12 texture.");
					}
				}
				else if (producedSample != null)
				{
					RecordNonD3D11OutputSample();
				}
			}
			finally
			{
				if (releaseProducedSample && producedSample != null)
				{
					Marshal.ReleaseComObject(producedSample);
					if (buffer.Sample != IntPtr.Zero)
					{
						Marshal.Release(buffer.Sample);
					}
				}
				else if (outputSample != null)
				{
					Marshal.ReleaseComObject(outputSample);
				}
			}
		}
	}

	private void ConfigureOutputType(string reason)
	{
		if (_transform == null)
		{
			return;
		}

		IntPtr outputType = GetPreferredOutputType(_transform, VideoSubtypes.NV12);
		try
		{
			ThrowIfFailed(_transform.SetOutputType(0, outputType, 0), "SetOutputType(NV12)");
			ValidateCurrentOutputType(reason);
			StatusChanged?.Invoke($"Media Foundation D3D11 decoder output type set: {reason}, NV12 texture.");
		}
		finally
		{
			Marshal.Release(outputType);
		}
	}

	private void ValidateCurrentOutputType(string reason)
	{
		if (_transform == null)
		{
			return;
		}

		IntPtr currentTypePtr = IntPtr.Zero;
		MfMediaType? currentType = null;
		try
		{
			ThrowIfFailed(_transform.GetOutputCurrentType(0, out currentTypePtr), "GetOutputCurrentType");
			currentType = (MfMediaType)Marshal.GetObjectForIUnknown(currentTypePtr);

			Guid subtypeKey = MediaTypeAttributeKeys.Subtype;
			Guid frameSizeKey = MediaTypeAttributeKeys.FrameSize;
			ThrowIfFailed(currentType.GetGUID(ref subtypeKey, out Guid actualSubtype), "GetOutputCurrentType(MF_MT_SUBTYPE)");
			ThrowIfFailed(currentType.GetUINT64(ref frameSizeKey, out long packedFrameSize), "GetOutputCurrentType(MF_MT_FRAME_SIZE)");

			int actualWidth = (int)(packedFrameSize >> 32);
			int actualHeight = (int)(packedFrameSize & 0xFFFFFFFFL);
			if (actualSubtype == VideoSubtypes.NV12 && actualWidth == _width && actualHeight == _height)
			{
				return;
			}

			string message =
				$"High-resolution D3D output geometry changed: expected {_width}x{_height} NV12, " +
				$"got {actualWidth}x{actualHeight} {FormatVideoSubtype(actualSubtype)} during {reason}.";
			Fault(message);
			throw new InvalidOperationException(message);
		}
		finally
		{
			if (currentType != null)
			{
				Marshal.ReleaseComObject(currentType);
			}
			if (currentTypePtr != IntPtr.Zero)
			{
				Marshal.Release(currentTypePtr);
			}
		}
	}

	private static void ReleaseOutputSample(IMFSample? outputSample, IntPtr bufferSample, IntPtr originalSamplePtr)
	{
		if (bufferSample != IntPtr.Zero && bufferSample != originalSamplePtr)
		{
			Marshal.Release(bufferSample);
		}
		if (outputSample != null)
		{
			Marshal.ReleaseComObject(outputSample);
		}
	}

	private bool TryCreateD3D11Frame(IMFSample sample, H264InputPacket packet, out D3D11VideoFrame? frame)
	{
		frame = null;
		int bufferHr = sample.GetBufferByIndex(0, out IMFMediaBuffer mediaBuffer);
		if (bufferHr < 0)
		{
			return false;
		}

		IntPtr mediaBufferPtr = IntPtr.Zero;
		IntPtr dxgiBufferPtr = IntPtr.Zero;
		IntPtr texturePtr = IntPtr.Zero;
		int subresourceIndex = 0;
		try
		{
			mediaBufferPtr = Marshal.GetIUnknownForObject(mediaBuffer);
			Guid dxgiBufferIid = Interfaces.IMFDXGIBuffer;
			int dxgiHr = Marshal.QueryInterface(mediaBufferPtr, ref dxgiBufferIid, out dxgiBufferPtr);
			if (dxgiHr < 0 || dxgiBufferPtr == IntPtr.Zero)
			{
				return false;
			}

			var dxgiBuffer = (IMFDXGIBuffer)Marshal.GetObjectForIUnknown(dxgiBufferPtr);
			try
			{
				Guid textureIid = Interfaces.ID3D11Texture2D;
				int resourceHr = dxgiBuffer.GetResource(ref textureIid, out texturePtr);
				if (resourceHr < 0 || texturePtr == IntPtr.Zero)
				{
					return false;
				}
				int subresourceHr = dxgiBuffer.GetSubresourceIndex(out subresourceIndex);
				if (subresourceHr < 0)
				{
					if (!_loggedSubresourceQueryFailure)
					{
						_loggedSubresourceQueryFailure = true;
						StatusChanged?.Invoke("Media Foundation D3D11 texture subresource query failed: " + FormatHResult(subresourceHr));
					}
					return false;
				}
			}
			finally
			{
				Marshal.ReleaseComObject(dxgiBuffer);
			}

			var texture = new D3D11.Texture2D(texturePtr);
			D3D11.Texture2DDescription desc = texture.Description;
			if (desc.Format != SharpDX.DXGI.Format.NV12 || desc.Width != _width || desc.Height != _height)
			{
				string message =
					$"High-resolution D3D output geometry changed: expected {_width}x{_height} NV12, " +
					$"got {desc.Width}x{desc.Height} {desc.Format} from decoder texture.";
				texture.Dispose();
				texturePtr = IntPtr.Zero;
				Fault(message);
				throw new InvalidOperationException(message);
			}
			if (subresourceIndex < 0 || subresourceIndex >= desc.ArraySize)
			{
				if (!_loggedInvalidTextureSubresource)
				{
					_loggedInvalidTextureSubresource = true;
					StatusChanged?.Invoke(
						$"Media Foundation D3D11 decoder rejected NV12 texture subresourceIndex={subresourceIndex}, arraySize={desc.ArraySize}.");
				}
				texture.Dispose();
				texturePtr = IntPtr.Zero;
				return false;
			}
			if (!_loggedFirstTextureDetails)
			{
				_loggedFirstTextureDetails = true;
				StatusChanged?.Invoke(
					$"Media Foundation D3D11 first texture: format={desc.Format} size={desc.Width}x{desc.Height} " +
					$"arraySize={desc.ArraySize} subresourceIndex={subresourceIndex}.");
			}

			frame = new D3D11VideoFrame
			{
				Width = _width,
				Height = _height,
				Fps = _fps,
				Texture = texture,
				SubresourceIndex = subresourceIndex,
				ReceivedTick = packet.ReceivedTick,
				DecodedTick = Stopwatch.GetTimestamp(),
				SourceTimestampNanos = packet.SourceTimestampNanos
			};
			texturePtr = IntPtr.Zero;
			return true;
		}
		finally
		{
			if (texturePtr != IntPtr.Zero)
			{
				Marshal.Release(texturePtr);
			}
			if (dxgiBufferPtr != IntPtr.Zero)
			{
				Marshal.Release(dxgiBufferPtr);
			}
			if (mediaBufferPtr != IntPtr.Zero)
			{
				Marshal.Release(mediaBufferPtr);
			}
			Marshal.ReleaseComObject(mediaBuffer);
		}
	}

	private void RecordWriteMetrics(long writeMs)
	{
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
			StatusChanged?.Invoke($"Media Foundation ProcessInput stall: {writeMs}ms, queued={QueuedInputPackets}, dropped={DroppedInputPackets:N0}");
		}
	}

	private void RecordNonD3D11OutputSample()
	{
		long samples = Interlocked.Increment(ref _outputSamplesWithoutD3D11Texture);
		long now = Environment.TickCount64;
		long previous = Interlocked.Read(ref _lastNonD3D11OutputStatusTick);
		if (samples == 1 || now - previous >= 1000)
		{
			Interlocked.Exchange(ref _lastNonD3D11OutputStatusTick, now);
			StatusChanged?.Invoke($"Media Foundation produced output sample without matching NV12 D3D11 texture; count={samples:N0}.");
		}
	}

	private async Task WatchFirstFrameTimeoutAsync()
	{
		try
		{
			await Task.Delay(FirstFrameTimeout, _cts.Token);
			if (!_cts.IsCancellationRequested && Interlocked.Read(ref _decodedOutputFrames) == 0)
			{
				Fault(
					$"Media Foundation D3D11 decoder produced no NV12 texture within {FirstFrameTimeout.TotalSeconds:N0}s; " +
					$"queued={QueuedInputPackets}, accepted={AcceptedInputPackets:N0}, submitted={WrittenInputPackets:N0}, " +
					$"nonD3D11Outputs={Interlocked.Read(ref _outputSamplesWithoutD3D11Texture):N0}.");
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private async Task WatchDecodeLivenessAsync()
	{
		long lastAccepted = AcceptedInputPackets;
		long lastDecoded = Interlocked.Read(ref _decodedOutputFrames);
		long flatSinceTick = 0L;

		try
		{
			while (!_cts.IsCancellationRequested && !IsFaulted)
			{
				await Task.Delay(TimeSpan.FromSeconds(1.0), _cts.Token);
				if (_cts.IsCancellationRequested || IsFaulted)
				{
					return;
				}

				long accepted = AcceptedInputPackets;
				long decoded = Interlocked.Read(ref _decodedOutputFrames);
				if (decoded == 0)
				{
					lastAccepted = accepted;
					lastDecoded = decoded;
					flatSinceTick = 0L;
					continue;
				}

				bool inputAdvancing = accepted > lastAccepted;
				bool decodedFlat = decoded == lastDecoded;
				if (inputAdvancing && decodedFlat)
				{
					long now = Stopwatch.GetTimestamp();
					if (flatSinceTick == 0L)
					{
						flatSinceTick = now;
					}
					if (TimeSpan.FromSeconds((double)(now - flatSinceTick) / Stopwatch.Frequency) >= DecodeStallThreshold)
					{
						Fault(
							$"High-resolution D3D stall: accepted={accepted:N0}, submitted={WrittenInputPackets:N0}, " +
							$"decoded={decoded:N0}, queued={QueuedInputPackets:N0}, dropped={DroppedInputPackets:N0}.");
						return;
					}
				}
				else
				{
					flatSinceTick = 0L;
				}

				lastAccepted = accepted;
				lastDecoded = decoded;
			}
		}
		catch (OperationCanceledException)
		{
		}
	}

	private void Fault(string message)
	{
		if (Interlocked.Exchange(ref _faulted, 1) != 0)
		{
			return;
		}

		StatusChanged?.Invoke(message);
		Faulted?.Invoke(message);
	}

	private static MfCom<IMFSample> CreateSample(byte[] payload, long timestamp, long duration)
	{
		MfCom<IMFSample> sample = CreateEmptySample(payload.Length);
		sample.Value.ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
		try
		{
			buffer.Lock(out IntPtr data, out _, out _);
			try
			{
				Marshal.Copy(payload, 0, data, payload.Length);
			}
			finally
			{
				buffer.Unlock();
			}
			buffer.SetCurrentLength(payload.Length);
		}
		finally
		{
			Marshal.ReleaseComObject(buffer);
		}

		sample.Value.SetSampleTime(timestamp);
		sample.Value.SetSampleDuration(duration);
		return sample;
	}

	private static MfCom<IMFSample> CreateEmptySample(int bufferSize)
	{
		ThrowIfFailed(Native.MFCreateSample(out IMFSample sample), "MFCreateSample");
		ThrowIfFailed(Native.MFCreateMemoryBuffer(bufferSize, out IMFMediaBuffer buffer), "MFCreateMemoryBuffer");
		try
		{
			ThrowIfFailed(sample.AddBuffer(buffer), "IMFSample.AddBuffer");
		}
		finally
		{
			Marshal.ReleaseComObject(buffer);
		}
		return new MfCom<IMFSample>(sample);
	}

	private static int CreateH264DecoderTransform(out MfTransform? transform, out IntPtr transformPtr)
	{
		Guid clsid = new Guid("62CE7E72-4C71-4D20-B15D-452831A87D9D");
		Guid iid = new Guid("BF94C121-5B05-4E6F-8000-BA598961414D");
		int hr = Native.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out transformPtr);
		transform = hr >= 0 && transformPtr != IntPtr.Zero ? (MfTransform)Marshal.GetObjectForIUnknown(transformPtr) : null;
		return hr;
	}

	private static int SetH264InputType(MfTransform transform, int width, int height, int fps)
	{
		int hr = Native.MFCreateMediaType(out MfMediaType mediaType);
		if (hr < 0)
		{
			return hr;
		}

		try
		{
			Guid majorTypeKey = MediaTypeAttributeKeys.MajorType;
			Guid subtypeKey = MediaTypeAttributeKeys.Subtype;
			Guid frameSizeKey = MediaTypeAttributeKeys.FrameSize;
			Guid frameRateKey = MediaTypeAttributeKeys.FrameRate;
			hr = mediaType.SetGUID(ref majorTypeKey, MediaTypes.Video);
			if (hr < 0) return hr;
			hr = mediaType.SetGUID(ref subtypeKey, VideoSubtypes.H264);
			if (hr < 0) return hr;
			hr = mediaType.SetUINT64(ref frameSizeKey, PackRatio(width, height));
			if (hr < 0) return hr;
			hr = mediaType.SetUINT64(ref frameRateKey, PackRatio(fps, 1));
			if (hr < 0) return hr;
			return transform.SetInputType(0, mediaType, 0);
		}
		finally
		{
			Marshal.ReleaseComObject(mediaType);
		}
	}

	private static IntPtr GetPreferredOutputType(MfTransform transform, Guid preferredSubtype)
	{
		IntPtr fallback = IntPtr.Zero;
		for (int index = 0; ; index++)
		{
			int hr = transform.GetOutputAvailableType(0, index, out IntPtr type);
			if (hr < 0)
			{
				break;
			}
			if (IsMediaTypeSubtype(type, preferredSubtype))
			{
				if (fallback != IntPtr.Zero)
				{
					Marshal.Release(fallback);
				}
				return type;
			}
			if (fallback == IntPtr.Zero)
			{
				fallback = type;
			}
			else
			{
				Marshal.Release(type);
			}
		}
		if (fallback == IntPtr.Zero)
		{
			throw new InvalidOperationException("The H.264 decoder did not expose an output media type.");
		}
		return fallback;
	}

	private static bool IsMediaTypeSubtype(IntPtr mediaTypePtr, Guid subtype)
	{
		var mediaType = (MfMediaType)Marshal.GetObjectForIUnknown(mediaTypePtr);
		try
		{
			Guid subtypeKey = MediaTypeAttributeKeys.Subtype;
			return mediaType.GetGUID(ref subtypeKey, out Guid actual) >= 0 && actual == subtype;
		}
		finally
		{
			Marshal.ReleaseComObject(mediaType);
		}
	}

	private static long PackRatio(int high, int low)
	{
		return ((long)high << 32) | (uint)low;
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

	private static long ElapsedMilliseconds(long startTick, long endTick)
	{
		if (endTick <= startTick)
		{
			return 0L;
		}
		return Math.Max(0L, (long)Math.Round((double)(endTick - startTick) * 1000.0 / Stopwatch.Frequency));
	}

	private static void ThrowIfFailed(int hr, string operation)
	{
		if (hr < 0)
		{
			throw new InvalidOperationException(operation + " failed: " + FormatHResult(hr));
		}
	}

	private static string FormatHResult(int hr)
	{
		return "0x" + unchecked((uint)hr).ToString("X8", CultureInfo.InvariantCulture);
	}

	private static string FormatVideoSubtype(Guid subtype)
	{
		if (subtype == VideoSubtypes.NV12)
		{
			return "NV12";
		}
		if (subtype == VideoSubtypes.H264)
		{
			return "H264";
		}
		return subtype.ToString("D");
	}

	private sealed class DumpFile : IDisposable
	{
		private readonly object _gate = new object();
		private readonly long _maxBytes;
		private System.IO.FileStream? _stream;
		private long _bytes;
		private bool _capLogged;

		public DumpFile(string path, long maxBytes)
		{
			Path = path;
			_maxBytes = maxBytes;
			_stream = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read);
		}

		public string Path { get; }

		public long BytesWritten
		{
			get
			{
				lock (_gate)
				{
					return _bytes;
				}
			}
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

	private void CloseDumpFile(ref DumpFile? dump, string fieldName)
	{
		DumpFile? current = dump;
		dump = null;
		if (current == null)
		{
			return;
		}

		string path = current.Path;
		long bytes = current.BytesWritten;
		current.Dispose();
		StatusChanged?.Invoke($"H264 {fieldName} dump closed: {fieldName}={path} bytes={bytes}.");
	}

	public void Dispose()
	{
		_cts.Cancel();
		try
		{
			_decodeTask?.Wait(TimeSpan.FromSeconds(1.0));
		}
		catch
		{
		}
		ClearPendingInput();
		if (_streamingStarted && _transform != null)
		{
			_transform.ProcessMessage(MftMessage.NotifyEndOfStream, IntPtr.Zero);
		}
		if (_transform != null)
		{
			Marshal.ReleaseComObject(_transform);
			_transform = null;
		}
		if (_transformPtr != IntPtr.Zero)
		{
			Marshal.Release(_transformPtr);
			_transformPtr = IntPtr.Zero;
		}
		if (_dxgiManagerPtr != IntPtr.Zero)
		{
			Marshal.Release(_dxgiManagerPtr);
			_dxgiManagerPtr = IntPtr.Zero;
		}
		if (_mfStarted)
		{
			Native.MFShutdown();
			_mfStarted = false;
		}
		CloseDumpFile(ref _receivedDump, "received");
		CloseDumpFile(ref _submittedDump, "submitted");
		_inputSignal.Dispose();
		_cts.Dispose();
	}

	private readonly record struct H264InputPacket(byte[] Payload, ulong SourceTimestampNanos, long ReceivedTick);

	private readonly struct MfCom<T> : IDisposable where T : class
	{
		public MfCom(T value)
		{
			Value = value;
		}

		public T Value { get; }

		public void Dispose()
		{
			Marshal.ReleaseComObject(Value);
		}
	}
}

[ComImport]
[Guid("BF94C121-5B05-4E6F-8000-BA598961414D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface MfTransform
{
	[PreserveSig] int GetStreamLimits(out int inputMinimum, out int inputMaximum, out int outputMinimum, out int outputMaximum);
	[PreserveSig] int GetStreamCount(out int inputStreams, out int outputStreams);
	[PreserveSig] int GetStreamIDs(int inputIdArraySize, IntPtr inputIds, int outputIdArraySize, IntPtr outputIds);
	[PreserveSig] int GetInputStreamInfo(int inputStreamId, IntPtr streamInfo);
	[PreserveSig] int GetOutputStreamInfo(int outputStreamId, out MftOutputStreamInfo streamInfo);
	[PreserveSig] int GetAttributes(out IntPtr attributes);
	[PreserveSig] int GetInputStreamAttributes(int inputStreamId, out IntPtr attributes);
	[PreserveSig] int GetOutputStreamAttributes(int outputStreamId, out IntPtr attributes);
	[PreserveSig] int DeleteInputStream(int streamId);
	[PreserveSig] int AddInputStreams(int streams, IntPtr streamIds);
	[PreserveSig] int GetInputAvailableType(int inputStreamId, int typeIndex, out IntPtr type);
	[PreserveSig] int GetOutputAvailableType(int outputStreamId, int typeIndex, out IntPtr type);
	[PreserveSig] int SetInputType(int inputStreamId, MfMediaType type, int flags);
	[PreserveSig] int SetOutputType(int outputStreamId, IntPtr type, int flags);
	[PreserveSig] int GetInputCurrentType(int inputStreamId, out IntPtr type);
	[PreserveSig] int GetOutputCurrentType(int outputStreamId, out IntPtr type);
	[PreserveSig] int GetInputStatus(int inputStreamId, out int flags);
	[PreserveSig] int GetOutputStatus(out int flags);
	[PreserveSig] int SetOutputBounds(long lowerBound, long upperBound);
	[PreserveSig] int ProcessEvent(int inputStreamId, IntPtr ev);
	[PreserveSig] int ProcessMessage(int message, IntPtr param);
	[PreserveSig] int ProcessInput(int inputStreamId, IMFSample sample, int flags);
	[PreserveSig] int ProcessOutput(int flags, int outputBufferCount, IntPtr outputSamples, out int status);
}

[ComImport]
[Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface MfMediaType
{
	[PreserveSig] int GetItem(ref Guid guidKey, IntPtr value);
	[PreserveSig] int GetItemType(ref Guid guidKey, out int type);
	[PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out bool result);
	[PreserveSig] int Compare(IntPtr attributes, int matchType, out bool result);
	[PreserveSig] int GetUINT32(ref Guid guidKey, out int value);
	[PreserveSig] int GetUINT64(ref Guid guidKey, out long value);
	[PreserveSig] int GetDouble(ref Guid guidKey, out double value);
	[PreserveSig] int GetGUID(ref Guid guidKey, out Guid value);
	[PreserveSig] int GetStringLength(ref Guid guidKey, out int length);
	[PreserveSig] int GetString(ref Guid guidKey, IntPtr value, int size, out int length);
	[PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr value, out int length);
	[PreserveSig] int GetBlobSize(ref Guid guidKey, out int size);
	[PreserveSig] int GetBlob(ref Guid guidKey, IntPtr buffer, int bufferSize, out int blobSize);
	[PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr buffer, out int size);
	[PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr unknown);
	[PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
	[PreserveSig] int DeleteItem(ref Guid guidKey);
	[PreserveSig] int DeleteAllItems();
	[PreserveSig] int SetUINT32(ref Guid guidKey, int value);
	[PreserveSig] int SetUINT64(ref Guid guidKey, long value);
	[PreserveSig] int SetDouble(ref Guid guidKey, double value);
	[PreserveSig] int SetGUID(ref Guid guidKey, Guid value);
}

[ComImport]
[Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
	[PreserveSig] int GetItem(ref Guid guidKey, IntPtr value);
	[PreserveSig] int GetItemType(ref Guid guidKey, out int type);
	[PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out bool result);
	[PreserveSig] int Compare(IntPtr attributes, int matchType, out bool result);
	[PreserveSig] int GetUINT32(ref Guid guidKey, out int value);
	[PreserveSig] int GetUINT64(ref Guid guidKey, out long value);
	[PreserveSig] int GetDouble(ref Guid guidKey, out double value);
	[PreserveSig] int GetGUID(ref Guid guidKey, out Guid value);
	[PreserveSig] int GetStringLength(ref Guid guidKey, out int length);
	[PreserveSig] int GetString(ref Guid guidKey, IntPtr value, int size, out int length);
	[PreserveSig] int GetAllocatedString(ref Guid guidKey, out IntPtr value, out int length);
	[PreserveSig] int GetBlobSize(ref Guid guidKey, out int size);
	[PreserveSig] int GetBlob(ref Guid guidKey, IntPtr buffer, int bufferSize, out int blobSize);
	[PreserveSig] int GetAllocatedBlob(ref Guid guidKey, out IntPtr buffer, out int size);
	[PreserveSig] int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr unknown);
	[PreserveSig] int SetItem(ref Guid guidKey, IntPtr value);
	[PreserveSig] int DeleteItem(ref Guid guidKey);
	[PreserveSig] int DeleteAllItems();
	[PreserveSig] int SetUINT32(ref Guid guidKey, int value);
	[PreserveSig] int SetUINT64(ref Guid guidKey, long value);
	[PreserveSig] int SetDouble(ref Guid guidKey, double value);
	[PreserveSig] int SetGUID(ref Guid guidKey, Guid value);
	[PreserveSig] int SetString(ref Guid guidKey, string value);
	[PreserveSig] int SetBlob(ref Guid guidKey, IntPtr buffer, int size);
	[PreserveSig] int SetUnknown(ref Guid guidKey, IntPtr unknown);
	[PreserveSig] int LockStore();
	[PreserveSig] int UnlockStore();
	[PreserveSig] int GetCount(out int items);
	[PreserveSig] int GetItemByIndex(int index, out Guid guidKey, IntPtr value);
	[PreserveSig] int CopyAllItems(IntPtr destination);
	[PreserveSig] int GetSampleFlags(out int flags);
	[PreserveSig] int SetSampleFlags(int flags);
	[PreserveSig] int GetSampleTime(out long sampleTime);
	[PreserveSig] int SetSampleTime(long sampleTime);
	[PreserveSig] int GetSampleDuration(out long sampleDuration);
	[PreserveSig] int SetSampleDuration(long sampleDuration);
	[PreserveSig] int GetBufferCount(out int bufferCount);
	[PreserveSig] int GetBufferByIndex(int index, out IMFMediaBuffer buffer);
	[PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
	[PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
	[PreserveSig] int RemoveBufferByIndex(int index);
	[PreserveSig] int RemoveAllBuffers();
	[PreserveSig] int GetTotalLength(out int totalLength);
	[PreserveSig] int CopyToBuffer(IMFMediaBuffer buffer);
}

[ComImport]
[Guid("045FA593-8799-42B8-BC8D-8968C6453507")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
	[PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
	[PreserveSig] int Unlock();
	[PreserveSig] int GetCurrentLength(out int currentLength);
	[PreserveSig] int SetCurrentLength(int currentLength);
	[PreserveSig] int GetMaxLength(out int maxLength);
}

[ComImport]
[Guid("EB533D5D-2DB6-40F8-97A9-494692014F07")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface MfDxgiDeviceManager
{
	[PreserveSig] int CloseDeviceHandle(IntPtr deviceHandle);
	[PreserveSig] int GetVideoService(IntPtr deviceHandle, ref Guid riid, out IntPtr service);
	[PreserveSig] int LockDevice(IntPtr deviceHandle, ref Guid riid, out IntPtr device, bool block);
	[PreserveSig] int OpenDeviceHandle(out IntPtr deviceHandle);
	[PreserveSig] int ResetDevice(IntPtr device, int resetToken);
	[PreserveSig] int TestDevice(IntPtr deviceHandle);
	[PreserveSig] int UnlockDevice(IntPtr deviceHandle, bool saveState);
}

[ComImport]
[Guid("E7174CFA-1C9E-48B1-8866-626226BFC258")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFDXGIBuffer
{
	[PreserveSig] int GetResource(ref Guid riid, out IntPtr resource);
	[PreserveSig] int GetSubresourceIndex(out int subresourceIndex);
	[PreserveSig] int GetUnknown(ref Guid guid, ref Guid riid, out IntPtr unknown);
	[PreserveSig] int SetUnknown(ref Guid guid, IntPtr unknown);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
	public int Flags;
	public int Size;
	public int Alignment;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputDataBuffer
{
	public int StreamId;
	public IntPtr Sample;
	public int Status;
	public IntPtr Events;
}

internal static class Native
{
	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFStartup(int version, int flags);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFShutdown();

	[DllImport("ole32.dll", ExactSpelling = true)]
	public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext, ref Guid riid, out IntPtr ppv);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateMediaType(out MfMediaType mediaType);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateSample(out IMFSample sample);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateDXGIDeviceManager(out int resetToken, out IntPtr manager);
}

internal static class MediaTypeAttributeKeys
{
	public static readonly Guid MajorType = new Guid("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
	public static readonly Guid Subtype = new Guid("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
	public static readonly Guid FrameSize = new Guid("1652C33D-D6B2-4012-B834-72030849A37D");
	public static readonly Guid FrameRate = new Guid("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
}

internal static class Interfaces
{
	public static readonly Guid IMFDXGIBuffer = new Guid("E7174CFA-1C9E-48B1-8866-626226BFC258");
	public static readonly Guid ID3D11Texture2D = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
}

internal static class MediaTypes
{
	public static readonly Guid Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
}

internal static class VideoSubtypes
{
	public static readonly Guid H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
	public static readonly Guid NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
}

internal static class HResults
{
	public const int MfENotAccepting = unchecked((int)0xC00D36B5);
	public const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
	public const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
}

internal static class MftMessage
{
	public const int CommandDrain = 0x00000001;
	public const int SetD3DManager = 0x00000002;
	public const int NotifyBeginStreaming = 0x10000000;
	public const int NotifyEndOfStream = 0x10000002;
	public const int NotifyStartOfStream = 0x10000003;
}

internal static class MftOutputStreamFlags
{
	public const int ProvidesSamples = 0x00000100;
}
#endif
