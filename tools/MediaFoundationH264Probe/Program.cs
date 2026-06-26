using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

string? h264Path = args.Length >= 1 ? Path.GetFullPath(args[0]) : null;
int maxAccessUnits = args.Length >= 2 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedMaxAccessUnits)
	? parsedMaxAccessUnits
	: 300;
if (h264Path != null && !File.Exists(h264Path))
{
	Console.Error.WriteLine("H.264 file not found: " + h264Path);
	return 2;
}

Console.WriteLine("iMirror Media Foundation H.264 probe");
byte[]? h264Data = null;
VideoGeometry geometry = new VideoGeometry(2048, 1152, 30, "fallback");
if (h264Path != null)
{
	h264Data = File.ReadAllBytes(h264Path);
	H264StreamInfo streamInfo = AnalyzeAnnexBFile(h264Path, h264Data);
	if (streamInfo.Width > 0 && streamInfo.Height > 0)
	{
		geometry = new VideoGeometry(streamInfo.Width, streamInfo.Height, 30, "sps");
	}
	Console.WriteLine();
}

if (args.Length >= 3)
{
	if (!TryParseGeometryOverride(args[2], out VideoGeometry overrideGeometry))
	{
		Console.Error.WriteLine("Invalid geometry override. Use WxH or WxH@fps, for example 2048x1152@30.");
		return 2;
	}
	geometry = overrideGeometry;
}

Console.WriteLine($"probeGeometry={geometry.Width}x{geometry.Height}@{geometry.Fps}, source={geometry.Source}");
int result = ProbeMediaFoundationDecoder(h264Data, maxAccessUnits, geometry);
return result;

static bool TryParseGeometryOverride(string text, out VideoGeometry geometry)
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
	if (sizeAndRate.Length == 2 &&
		!int.TryParse(sizeAndRate[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out fps))
	{
		return false;
	}

	if (width <= 0 || height <= 0 || fps <= 0)
	{
		return false;
	}

	geometry = new VideoGeometry(width, height, fps, "override");
	return true;
}

static H264StreamInfo AnalyzeAnnexBFile(string path, byte[] data)
{
	var counts = new SortedDictionary<int, long>();
	long totalNal = 0;
	long idr = 0;
	long slice = 0;
	long sps = 0;
	long pps = 0;
	long sei = 0;
	H264StreamInfo streamInfo = default;

	foreach (NalUnit nal in EnumerateAnnexBNalUnitsWithStartCodes(data))
	{
		int offset = nal.NalOffset;
		int length = nal.EndOffset - nal.NalOffset;
		if (length <= 0)
		{
			continue;
		}

		int nalType = nal.Type;
		counts.TryGetValue(nalType, out long existing);
		counts[nalType] = existing + 1;
		totalNal++;
		switch (nalType)
		{
		case 1:
			slice++;
			break;
		case 5:
			idr++;
			break;
		case 6:
			sei++;
			break;
		case 7:
			sps++;
			if (streamInfo.Width == 0 && TryParseSps(data, nal, out H264StreamInfo parsed))
			{
				streamInfo = parsed;
			}
			break;
		case 8:
			pps++;
			break;
		}
	}

	Console.WriteLine("H.264 file: " + path);
	Console.WriteLine($"bytes={data.Length:N0}, nalUnits={totalNal:N0}, sps={sps:N0}, pps={pps:N0}, idr={idr:N0}, slice={slice:N0}, sei={sei:N0}");
	Console.WriteLine("nalTypes=" + string.Join(", ", counts.Select(pair => pair.Key.ToString(CultureInfo.InvariantCulture) + ":" + pair.Value.ToString("N0", CultureInfo.InvariantCulture))));
	Console.WriteLine($"accessUnits={BuildAccessUnits(data).Count:N0}");
	if (streamInfo.Width > 0 && streamInfo.Height > 0)
	{
		Console.WriteLine($"spsGeometry={streamInfo.Width}x{streamInfo.Height}, profile={streamInfo.ProfileIdc}, level={streamInfo.LevelIdc}");
	}
	else if (sps > 0)
	{
		Console.WriteLine("WARN: SPS found but geometry could not be parsed.");
	}
	if (sps == 0 || pps == 0 || idr == 0)
	{
		Console.WriteLine("WARN: stream is missing SPS/PPS/IDR evidence needed to initialize a fresh decoder.");
	}
	return streamInfo;
}

static int ProbeMediaFoundationDecoder(byte[]? h264Data, int maxAccessUnits, VideoGeometry geometry)
{
	int startupHr = Native.MFStartup(0x00020070, 0);
	Console.WriteLine("MFStartup=" + FormatHResult(startupHr));
	if (startupHr < 0)
	{
		return 1;
	}

	try
	{
		Guid clsid = new Guid("62CE7E72-4C71-4D20-B15D-452831A87D9D");
		Guid iid = new Guid("BF94C121-5B05-4E6F-8000-BA598961414D");
		int createHr = Native.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out IntPtr transformPtr);
		Console.WriteLine("Microsoft H.264 Decoder MFT CoCreateInstance=" + FormatHResult(createHr));
		if (createHr < 0 || transformPtr == IntPtr.Zero)
		{
			return 1;
		}

		try
		{
			var transform = (IMFTransform)Marshal.GetObjectForIUnknown(transformPtr);
			int countsHr = transform.GetStreamCount(out int inputStreams, out int outputStreams);
			Console.WriteLine($"GetStreamCount={FormatHResult(countsHr)}, inputs={inputStreams}, outputs={outputStreams}");

			int inputTypes = CountAvailableTypes(transform, input: true);
			int outputTypes = CountAvailableTypes(transform, input: false);
			Console.WriteLine($"availableInputTypesBeforeSet={inputTypes:N0}, availableOutputTypesBeforeSet={outputTypes:N0}");
			int setInputHr = TrySetH264InputType(transform, width: geometry.Width, height: geometry.Height, fps: geometry.Fps);
			Console.WriteLine($"SetInputType(H264 {geometry.Width}x{geometry.Height}@{geometry.Fps})=" + FormatHResult(setInputHr));
			IntPtr outputType = GetPreferredOutputType(transform, preferredSubtype: VideoSubtypes.NV12, out string outputTypeSelection);
			int setOutputHr = outputType == IntPtr.Zero ? unchecked((int)0xC00D36B9) : transform.SetOutputType(0, outputType, 0);
			Console.WriteLine($"SetOutputType({outputTypeSelection})=" + FormatHResult(setOutputHr) + DescribeMediaType(outputType));
			if (outputType != IntPtr.Zero)
			{
				Marshal.Release(outputType);
			}
			int outputTypesAfterSet = CountAvailableTypes(transform, input: false);
			Console.WriteLine($"availableOutputTypesAfterInputSet={outputTypesAfterSet:N0}");
			if (inputTypes == 0 || outputTypesAfterSet == 0)
			{
				Console.WriteLine("WARN: MFT created, but available type enumeration was empty.");
			}
			if (h264Data != null && setInputHr >= 0 && setOutputHr >= 0)
			{
				DecodeProbeResult decodeResult = FeedSamplesAndDrain(transform, h264Data, maxAccessUnits);
				Console.WriteLine(
					$"decodeProbe: accessUnitsFed={decodeResult.AccessUnitsFed:N0}, processInputOk={decodeResult.ProcessInputOk:N0}, " +
					$"decodedOutputs={decodeResult.DecodedOutputs:N0}, outputBytes={decodeResult.OutputBytes:N0}, " +
					$"dxgiBuffers={decodeResult.DxgiBuffers:N0}, d3d11Textures={decodeResult.D3D11Textures:N0}, " +
					$"notAccepting={decodeResult.NotAccepting:N0}, needMoreInput={decodeResult.NeedMoreInput:N0}, " +
					$"streamChanges={decodeResult.StreamChanges:N0}, failures={decodeResult.Failures:N0}");
			}
			ProbeD3D11DecoderCapability(h264Data, maxAccessUnits, geometry);
		}
		finally
		{
			Marshal.Release(transformPtr);
		}
	}
	finally
	{
		Console.WriteLine("MFShutdown=" + FormatHResult(Native.MFShutdown()));
	}

	return 0;
}

static void ProbeD3D11DecoderCapability(byte[]? h264Data, int maxAccessUnits, VideoGeometry geometry)
{
	Console.WriteLine();
	Console.WriteLine("D3D11 decode capability probe");
	int createTransformHr = CreateH264DecoderTransform(out IMFTransform? transform, out IntPtr transformPtr);
	Console.WriteLine("D3D11Probe.CoCreateInstance=" + FormatHResult(createTransformHr));
	if (createTransformHr < 0 || transform == null || transformPtr == IntPtr.Zero)
	{
		return;
	}

	IntPtr device = IntPtr.Zero;
	IntPtr deviceContext = IntPtr.Zero;
	IntPtr managerPtr = IntPtr.Zero;
	try
	{
		int d3d11Aware = 0;
		int attributesHr = transform.GetAttributes(out IntPtr attributesPtr);
		int awareHr = unchecked((int)0x80004005);
		if (attributesHr >= 0 && attributesPtr != IntPtr.Zero)
		{
			var attributes = (IMFAttributes)Marshal.GetObjectForIUnknown(attributesPtr);
			try
			{
				Guid d3d11AwareKey = MediaFoundationAttributes.D3D11Aware;
				awareHr = attributes.GetUINT32(ref d3d11AwareKey, out d3d11Aware);
			}
			finally
			{
				Marshal.ReleaseComObject(attributes);
				Marshal.Release(attributesPtr);
			}
		}
		Console.WriteLine($"D3D11Probe.GetAttributes={FormatHResult(attributesHr)}, MF_SA_D3D11_AWARE={FormatHResult(awareHr)}/{d3d11Aware}");
		if (awareHr < 0 || d3d11Aware == 0)
		{
			Console.WriteLine("D3D11Probe.result=not-d3d11-aware");
			return;
		}

		int deviceHr = Native.D3D11CreateDevice(
			IntPtr.Zero,
			D3DDriverType.Hardware,
			IntPtr.Zero,
			D3D11CreateDeviceFlags.BgraSupport | D3D11CreateDeviceFlags.VideoSupport,
			IntPtr.Zero,
			0,
			7,
			out device,
			out int featureLevel,
			out deviceContext);
		Console.WriteLine($"D3D11Probe.D3D11CreateDevice={FormatHResult(deviceHr)}, featureLevel=0x{featureLevel:X}");
		if (deviceHr < 0)
		{
			return;
		}

		int managerHr = Native.MFCreateDXGIDeviceManager(out int resetToken, out managerPtr);
		Console.WriteLine($"D3D11Probe.MFCreateDXGIDeviceManager={FormatHResult(managerHr)}, resetToken={resetToken}");
		if (managerHr < 0 || managerPtr == IntPtr.Zero)
		{
			return;
		}

		var manager = (IMFDXGIDeviceManager)Marshal.GetObjectForIUnknown(managerPtr);
		int resetHr;
		try
		{
			resetHr = manager.ResetDevice(device, resetToken);
		}
		finally
		{
			Marshal.ReleaseComObject(manager);
		}
		Console.WriteLine("D3D11Probe.ResetDevice=" + FormatHResult(resetHr));
		if (resetHr < 0)
		{
			return;
		}

		int setManagerHr = transform.ProcessMessage(MftMessage.SetD3DManager, managerPtr);
		Console.WriteLine("D3D11Probe.SetD3DManager=" + FormatHResult(setManagerHr));
		if (setManagerHr < 0)
		{
			return;
		}

		int setInputHr = TrySetH264InputType(transform, width: geometry.Width, height: geometry.Height, fps: geometry.Fps);
		Console.WriteLine($"D3D11Probe.SetInputType(H264 {geometry.Width}x{geometry.Height}@{geometry.Fps})=" + FormatHResult(setInputHr));
		IntPtr outputType = GetPreferredOutputType(transform, preferredSubtype: VideoSubtypes.NV12, out string outputTypeSelection);
		int setOutputHr = outputType == IntPtr.Zero ? unchecked((int)0xC00D36B9) : transform.SetOutputType(0, outputType, 0);
		Console.WriteLine($"D3D11Probe.SetOutputType({outputTypeSelection})=" + FormatHResult(setOutputHr) + DescribeMediaType(outputType));
		if (outputType != IntPtr.Zero)
		{
			Marshal.Release(outputType);
		}
		int streamInfoHr = transform.GetOutputStreamInfo(0, out MftOutputStreamInfo info);
		Console.WriteLine($"D3D11Probe.GetOutputStreamInfo={FormatHResult(streamInfoHr)}, flags=0x{info.Flags:X8}, size={info.Size:N0}, alignment={info.Alignment:N0}");
		if (h264Data != null && setInputHr >= 0 && setOutputHr >= 0)
		{
			DecodeProbeResult decodeResult = FeedSamplesAndDrain(transform, h264Data, maxAccessUnits);
			Console.WriteLine(
				$"D3D11Probe.decodeProbe: accessUnitsFed={decodeResult.AccessUnitsFed:N0}, processInputOk={decodeResult.ProcessInputOk:N0}, " +
				$"decodedOutputs={decodeResult.DecodedOutputs:N0}, outputBytes={decodeResult.OutputBytes:N0}, " +
				$"dxgiBuffers={decodeResult.DxgiBuffers:N0}, d3d11Textures={decodeResult.D3D11Textures:N0}, " +
				$"notAccepting={decodeResult.NotAccepting:N0}, needMoreInput={decodeResult.NeedMoreInput:N0}, " +
				$"streamChanges={decodeResult.StreamChanges:N0}, failures={decodeResult.Failures:N0}");
		}
		Console.WriteLine("D3D11Probe.result=" + (setInputHr >= 0 && setOutputHr >= 0 ? "d3d11-manager-accepted" : "type-negotiation-failed"));
	}
	finally
	{
		if (managerPtr != IntPtr.Zero)
		{
			Marshal.Release(managerPtr);
		}
		if (deviceContext != IntPtr.Zero)
		{
			Marshal.Release(deviceContext);
		}
		if (device != IntPtr.Zero)
		{
			Marshal.Release(device);
		}
		Marshal.Release(transformPtr);
	}
}

static int CreateH264DecoderTransform(out IMFTransform? transform, out IntPtr transformPtr)
{
	Guid clsid = new Guid("62CE7E72-4C71-4D20-B15D-452831A87D9D");
	Guid iid = new Guid("BF94C121-5B05-4E6F-8000-BA598961414D");
	int hr = Native.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out transformPtr);
	transform = hr >= 0 && transformPtr != IntPtr.Zero ? (IMFTransform)Marshal.GetObjectForIUnknown(transformPtr) : null;
	return hr;
}

static DecodeProbeResult FeedSamplesAndDrain(IMFTransform transform, byte[] h264Data, int maxAccessUnits)
{
	List<AccessUnit> accessUnits = BuildAccessUnits(h264Data);
	var result = new DecodeProbeResult();
	transform.ProcessMessage(MftMessage.NotifyBeginStreaming, IntPtr.Zero);
	transform.ProcessMessage(MftMessage.NotifyStartOfStream, IntPtr.Zero);

	long timestamp = 0L;
	long duration = 333_333L;
	foreach (AccessUnit accessUnit in accessUnits.Take(Math.Max(1, maxAccessUnits)))
	{
		byte[] payload = new byte[accessUnit.Length];
		Buffer.BlockCopy(h264Data, accessUnit.Offset, payload, 0, payload.Length);
		IMFSample sample = CreateSample(payload, timestamp, duration);
		try
		{
			int inputHr = transform.ProcessInput(0, sample, 0);
			if (inputHr == HResults.MfENotAccepting)
			{
				result.NotAccepting++;
				DrainOutput(transform, ref result);
				inputHr = transform.ProcessInput(0, sample, 0);
			}
			if (inputHr < 0)
			{
				result.Failures++;
				Console.WriteLine($"ProcessInput failed at AU {result.AccessUnitsFed:N0}: {FormatHResult(inputHr)}");
				break;
			}

			result.ProcessInputOk++;
			result.AccessUnitsFed++;
			DrainOutput(transform, ref result);
			timestamp += duration;
		}
		finally
		{
			Marshal.ReleaseComObject(sample);
		}
	}

	transform.ProcessMessage(MftMessage.NotifyEndOfStream, IntPtr.Zero);
	transform.ProcessMessage(MftMessage.CommandDrain, IntPtr.Zero);
	DrainOutput(transform, ref result);
	return result;
}

static void DrainOutput(IMFTransform transform, ref DecodeProbeResult result)
{
	for (int i = 0; i < 64; i++)
	{
		int infoHr = transform.GetOutputStreamInfo(0, out MftOutputStreamInfo info);
		if (infoHr < 0)
		{
			result.Failures++;
			Console.WriteLine("GetOutputStreamInfo failed: " + FormatHResult(infoHr));
			return;
		}

		IntPtr samplePtr = IntPtr.Zero;
		IMFSample? outputSample = null;
		if ((info.Flags & MftOutputStreamFlags.ProvidesSamples) == 0)
		{
			int bufferSize = Math.Max(info.Size, 2048 * 1152 * 4);
			outputSample = CreateEmptySample(bufferSize);
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
		int outputHr = transform.ProcessOutput(0, 1, bufferPtr, out int status);
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
			result.NeedMoreInput++;
			ReleaseOutputSample(outputSample, buffer.Sample, samplePtr);
			return;
		}
		if (outputHr == HResults.MfETransformStreamChange)
		{
			result.StreamChanges++;
			ReleaseOutputSample(outputSample, buffer.Sample, samplePtr);
			IntPtr outputType = GetPreferredOutputType(transform, preferredSubtype: VideoSubtypes.NV12, out string outputTypeSelection);
			int setOutputHr = outputType == IntPtr.Zero ? unchecked((int)0xC00D36B9) : transform.SetOutputType(0, outputType, 0);
			Console.WriteLine($"ProcessOutput stream change; SetOutputType({outputTypeSelection})=" + FormatHResult(setOutputHr) + DescribeMediaType(outputType));
			if (outputType != IntPtr.Zero)
			{
				Marshal.Release(outputType);
			}
			if (setOutputHr < 0)
			{
				result.Failures++;
				return;
			}
			continue;
		}
		if (outputHr < 0)
		{
			result.Failures++;
			Console.WriteLine("ProcessOutput failed: " + FormatHResult(outputHr));
			ReleaseOutputSample(outputSample, buffer.Sample, samplePtr);
			return;
		}

		IntPtr producedSamplePtr = buffer.Sample;
		IMFSample? producedSample = outputSample;
		bool releaseProducedSample = false;
		if (producedSamplePtr != IntPtr.Zero && (outputSample == null || producedSamplePtr != samplePtr))
		{
			producedSample = (IMFSample)Marshal.GetObjectForIUnknown(producedSamplePtr);
			releaseProducedSample = true;
		}
		if (producedSample != null)
		{
			producedSample.GetTotalLength(out int totalLength);
			result.OutputBytes += Math.Max(0, totalLength);
			result.DecodedOutputs++;
			InspectOutputSample(producedSample, ref result);
		}
		if (releaseProducedSample && producedSample != null)
		{
			Marshal.ReleaseComObject(producedSample);
		}
		else if (outputSample != null)
		{
			Marshal.ReleaseComObject(outputSample);
		}
	}
}

static void ReleaseOutputSample(IMFSample? outputSample, IntPtr bufferSample, IntPtr originalSamplePtr)
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

static void InspectOutputSample(IMFSample sample, ref DecodeProbeResult result)
{
	int bufferCountHr = sample.GetBufferCount(out int bufferCount);
	if (bufferCountHr < 0 || bufferCount <= 0)
	{
		return;
	}

	int bufferHr = sample.GetBufferByIndex(0, out IMFMediaBuffer mediaBuffer);
	if (bufferHr < 0)
	{
		return;
	}

	IntPtr mediaBufferPtr = IntPtr.Zero;
	IntPtr dxgiBufferPtr = IntPtr.Zero;
	IntPtr texturePtr = IntPtr.Zero;
	try
	{
		mediaBufferPtr = Marshal.GetIUnknownForObject(mediaBuffer);
		Guid dxgiBufferIid = Interfaces.IMFDXGIBuffer;
		int dxgiHr = Marshal.QueryInterface(mediaBufferPtr, in dxgiBufferIid, out dxgiBufferPtr);
		if (dxgiHr < 0 || dxgiBufferPtr == IntPtr.Zero)
		{
			return;
		}

		result.DxgiBuffers++;
		var dxgiBuffer = (IMFDXGIBuffer)Marshal.GetObjectForIUnknown(dxgiBufferPtr);
		try
		{
			Guid textureIid = Interfaces.ID3D11Texture2D;
			int resourceHr = dxgiBuffer.GetResource(ref textureIid, out texturePtr);
			if (resourceHr >= 0 && texturePtr != IntPtr.Zero)
			{
				result.D3D11Textures++;
				if (result.D3D11TextureDescriptionsLogged < 5 && TryDescribeD3D11Texture2D(texturePtr, out string textureDescription))
				{
					result.D3D11TextureDescriptionsLogged++;
					Console.WriteLine("Decoded D3D11 texture: " + textureDescription);
				}
			}
		}
		finally
		{
			Marshal.ReleaseComObject(dxgiBuffer);
		}
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

static bool TryDescribeD3D11Texture2D(IntPtr texturePtr, out string description)
{
	description = string.Empty;
	try
	{
		var texture = (ID3D11Texture2D)Marshal.GetObjectForIUnknown(texturePtr);
		try
		{
			texture.GetDesc(out D3D11Texture2DDesc desc);
			description =
				$"format={FormatDxgiFormat(desc.Format)}({desc.Format}) " +
				$"size={desc.Width}x{desc.Height} mipLevels={desc.MipLevels} array={desc.ArraySize} " +
				$"sample={desc.SampleDescription.Count}/{desc.SampleDescription.Quality} " +
				$"usage={desc.Usage} bind=0x{desc.BindFlags:X8} misc=0x{desc.MiscFlags:X8}";
			return true;
		}
		finally
		{
			Marshal.ReleaseComObject(texture);
		}
	}
	catch (Exception ex)
	{
		description = "GetDesc failed: " + ex.GetType().Name + ": " + ex.Message;
		return false;
	}
}

static string FormatDxgiFormat(int format)
{
	return format switch
	{
		87 => "B8G8R8A8_UNorm",
		103 => "NV12",
		104 => "P010",
		105 => "P016",
		_ => "DXGI_FORMAT_" + format.ToString(CultureInfo.InvariantCulture)
	};
}

static IMFSample CreateSample(byte[] payload, long timestamp, long duration)
{
	IMFSample sample = CreateEmptySample(payload.Length);
	sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
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

	sample.SetSampleTime(timestamp);
	sample.SetSampleDuration(duration);
	return sample;
}

static IMFSample CreateEmptySample(int bufferSize)
{
	int hr = Native.MFCreateSample(out IMFSample sample);
	if (hr < 0)
	{
		Marshal.ThrowExceptionForHR(hr);
	}
	hr = Native.MFCreateMemoryBuffer(bufferSize, out IMFMediaBuffer buffer);
	if (hr < 0)
	{
		Marshal.ReleaseComObject(sample);
		Marshal.ThrowExceptionForHR(hr);
	}
	try
	{
		hr = sample.AddBuffer(buffer);
		if (hr < 0)
		{
			Marshal.ThrowExceptionForHR(hr);
		}
	}
	finally
	{
		Marshal.ReleaseComObject(buffer);
	}
	return sample;
}

static int TrySetH264InputType(IMFTransform transform, int width, int height, int fps)
{
	int hr = Native.MFCreateMediaType(out IMFMediaType mediaType);
	if (hr < 0)
	{
		return hr;
	}

	try
	{
		hr = mediaType.SetGUID(MediaTypeAttributeKeys.MajorType, MediaTypes.Video);
		if (hr < 0)
		{
			return hr;
		}
		hr = mediaType.SetGUID(MediaTypeAttributeKeys.Subtype, VideoSubtypes.H264);
		if (hr < 0)
		{
			return hr;
		}
		hr = mediaType.SetUINT64(MediaTypeAttributeKeys.FrameSize, PackRatio(width, height));
		if (hr < 0)
		{
			return hr;
		}
		hr = mediaType.SetUINT64(MediaTypeAttributeKeys.FrameRate, PackRatio(fps, 1));
		if (hr < 0)
		{
			return hr;
		}

		return transform.SetInputType(0, mediaType, 0);
	}
	finally
	{
		Marshal.ReleaseComObject(mediaType);
	}
}

static long PackRatio(int high, int low)
{
	return ((long)high << 32) | (uint)low;
}

static int CountAvailableTypes(IMFTransform transform, bool input)
{
	int count = 0;
	for (int i = 0; i < 128; i++)
	{
		IntPtr mediaType;
		int hr = input
			? transform.GetInputAvailableType(0, i, out mediaType)
			: transform.GetOutputAvailableType(0, i, out mediaType);
		if (hr < 0)
		{
			break;
		}
		if (mediaType != IntPtr.Zero)
		{
			Marshal.Release(mediaType);
		}
		count++;
	}
	return count;
}

static IntPtr GetAvailableType(IMFTransform transform, bool input, int index)
{
	int hr = input
		? transform.GetInputAvailableType(0, index, out IntPtr mediaType)
		: transform.GetOutputAvailableType(0, index, out mediaType);
	return hr < 0 ? IntPtr.Zero : mediaType;
}

static IntPtr GetPreferredOutputType(IMFTransform transform, Guid preferredSubtype, out string selection)
{
	IntPtr fallback = IntPtr.Zero;
	for (int i = 0; i < 128; i++)
	{
		IntPtr mediaType = GetAvailableType(transform, input: false, index: i);
		if (mediaType == IntPtr.Zero)
		{
			break;
		}
		if (fallback == IntPtr.Zero)
		{
			fallback = mediaType;
			if (IsMediaTypeSubtype(mediaType, preferredSubtype))
			{
				selection = "preferred NV12";
				return mediaType;
			}
			continue;
		}

		if (IsMediaTypeSubtype(mediaType, preferredSubtype))
		{
			Marshal.Release(fallback);
			selection = "preferred NV12";
			return mediaType;
		}
		Marshal.Release(mediaType);
	}

	selection = fallback == IntPtr.Zero ? "none" : "first available fallback";
	return fallback;
}

static bool IsMediaTypeSubtype(IntPtr mediaTypePtr, Guid subtype)
{
	if (mediaTypePtr == IntPtr.Zero)
	{
		return false;
	}
	var mediaType = (IMFMediaType)Marshal.GetObjectForIUnknown(mediaTypePtr);
	try
	{
		Guid subtypeKey = MediaTypeAttributeKeys.Subtype;
		return mediaType.GetGUID(ref subtypeKey, out Guid actualSubtype) >= 0 && actualSubtype == subtype;
	}
	finally
	{
		Marshal.ReleaseComObject(mediaType);
	}
}

static string DescribeMediaType(IntPtr mediaTypePtr)
{
	if (mediaTypePtr == IntPtr.Zero)
	{
		return string.Empty;
	}

	var mediaType = (IMFMediaType)Marshal.GetObjectForIUnknown(mediaTypePtr);
	try
	{
		Guid subtypeKey = MediaTypeAttributeKeys.Subtype;
		Guid frameSizeKey = MediaTypeAttributeKeys.FrameSize;
		string subtype = mediaType.GetGUID(ref subtypeKey, out Guid subtypeValue) >= 0
			? subtypeValue.ToString()
			: "unknown";
		string frame = "unknown";
		if (mediaType.GetUINT64(ref frameSizeKey, out long packedFrameSize) >= 0)
		{
			frame = ((int)(packedFrameSize >> 32)).ToString(CultureInfo.InvariantCulture) + "x" + ((int)packedFrameSize).ToString(CultureInfo.InvariantCulture);
		}
		return $" subtype={subtype} frame={frame}";
	}
	finally
	{
		Marshal.ReleaseComObject(mediaType);
	}
}

static List<AccessUnit> BuildAccessUnits(byte[] data)
{
	var nals = EnumerateAnnexBNalUnitsWithStartCodes(data).ToList();
	var units = new List<AccessUnit>();
	int currentStart = -1;
	bool currentHasVcl = false;

	for (int i = 0; i < nals.Count; i++)
	{
		NalUnit nal = nals[i];
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

static IEnumerable<NalUnit> EnumerateAnnexBNalUnitsWithStartCodes(byte[] data)
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

static bool TryReadFirstMbInSlice(byte[] data, NalUnit nal, out uint firstMbInSlice)
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

static bool TryParseSps(byte[] data, NalUnit nal, out H264StreamInfo streamInfo)
{
	streamInfo = default;
	if (nal.Type != 7 || nal.NalOffset + 4 >= nal.EndOffset)
	{
		return false;
	}

	List<byte> rbsp = BuildRbsp(data, nal.NalOffset + 1, nal.EndOffset);
	var reader = new BitReader(rbsp);
	if (!reader.TryReadBits(8, out uint profileIdc) ||
		!reader.TryReadBits(8, out _) ||
		!reader.TryReadBits(8, out uint levelIdc) ||
		!reader.TryReadUnsignedExpGolomb(out _))
	{
		return false;
	}

	uint chromaFormatIdc = 1;
	bool separateColourPlaneFlag = false;
	if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
	{
		if (!reader.TryReadUnsignedExpGolomb(out chromaFormatIdc))
		{
			return false;
		}
		if (chromaFormatIdc == 3)
		{
			if (!reader.TryReadBit(out separateColourPlaneFlag))
			{
				return false;
			}
		}
		if (!reader.TryReadUnsignedExpGolomb(out _) ||
			!reader.TryReadUnsignedExpGolomb(out _) ||
			!reader.TryReadBit(out _))
		{
			return false;
		}
		if (!reader.TryReadBit(out bool seqScalingMatrixPresentFlag))
		{
			return false;
		}
		if (seqScalingMatrixPresentFlag)
		{
			int scalingListCount = chromaFormatIdc != 3 ? 8 : 12;
			for (int i = 0; i < scalingListCount; i++)
			{
				if (!reader.TryReadBit(out bool scalingListPresent) ||
					(scalingListPresent && !SkipScalingList(ref reader, i < 6 ? 16 : 64)))
				{
					return false;
				}
			}
		}
	}

	if (!reader.TryReadUnsignedExpGolomb(out _) ||
		!reader.TryReadUnsignedExpGolomb(out uint picOrderCntType))
	{
		return false;
	}
	if (picOrderCntType == 0)
	{
		if (!reader.TryReadUnsignedExpGolomb(out _))
		{
			return false;
		}
	}
	else if (picOrderCntType == 1)
	{
		if (!reader.TryReadBit(out _) ||
			!reader.TryReadSignedExpGolomb(out _) ||
			!reader.TryReadSignedExpGolomb(out _) ||
			!reader.TryReadUnsignedExpGolomb(out uint cycleCount))
		{
			return false;
		}
		for (uint i = 0; i < cycleCount; i++)
		{
			if (!reader.TryReadSignedExpGolomb(out _))
			{
				return false;
			}
		}
	}

	if (!reader.TryReadUnsignedExpGolomb(out _) ||
		!reader.TryReadBit(out _) ||
		!reader.TryReadUnsignedExpGolomb(out uint picWidthInMbsMinus1) ||
		!reader.TryReadUnsignedExpGolomb(out uint picHeightInMapUnitsMinus1) ||
		!reader.TryReadBit(out bool frameMbsOnlyFlag))
	{
		return false;
	}
	if (!frameMbsOnlyFlag && !reader.TryReadBit(out _))
	{
		return false;
	}
	if (!reader.TryReadBit(out _) ||
		!reader.TryReadBit(out bool frameCroppingFlag))
	{
		return false;
	}

	uint cropLeft = 0;
	uint cropRight = 0;
	uint cropTop = 0;
	uint cropBottom = 0;
	if (frameCroppingFlag)
	{
		if (!reader.TryReadUnsignedExpGolomb(out cropLeft) ||
			!reader.TryReadUnsignedExpGolomb(out cropRight) ||
			!reader.TryReadUnsignedExpGolomb(out cropTop) ||
			!reader.TryReadUnsignedExpGolomb(out cropBottom))
		{
			return false;
		}
	}

	int width = checked((int)((picWidthInMbsMinus1 + 1) * 16));
	int height = checked((int)((2 - (frameMbsOnlyFlag ? 1u : 0u)) * (picHeightInMapUnitsMinus1 + 1) * 16));
	int cropUnitX;
	int cropUnitY;
	if (chromaFormatIdc == 0 || separateColourPlaneFlag)
	{
		cropUnitX = 1;
		cropUnitY = (int)(2 - (frameMbsOnlyFlag ? 1u : 0u));
	}
	else
	{
		int subWidthC = chromaFormatIdc == 3 ? 1 : 2;
		int subHeightC = chromaFormatIdc == 1 ? 2 : 1;
		cropUnitX = subWidthC;
		cropUnitY = subHeightC * (int)(2 - (frameMbsOnlyFlag ? 1u : 0u));
	}

	width -= checked((int)((cropLeft + cropRight) * (uint)cropUnitX));
	height -= checked((int)((cropTop + cropBottom) * (uint)cropUnitY));
	if (width <= 0 || height <= 0)
	{
		return false;
	}

	streamInfo = new H264StreamInfo(width, height, (int)profileIdc, (int)levelIdc);
	return true;
}

static List<byte> BuildRbsp(byte[] data, int start, int end)
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

static bool SkipScalingList(ref BitReader reader, int size)
{
	int lastScale = 8;
	int nextScale = 8;
	for (int j = 0; j < size; j++)
	{
		if (nextScale != 0)
		{
			if (!reader.TryReadSignedExpGolomb(out int deltaScale))
			{
				return false;
			}
			nextScale = (lastScale + deltaScale + 256) % 256;
		}
		lastScale = nextScale == 0 ? lastScale : nextScale;
	}
	return true;
}

static bool TryReadUnsignedExpGolomb(IReadOnlyList<byte> rbsp, int bitOffset, out uint value)
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

static int ReadBit(IReadOnlyList<byte> bytes, int bitOffset)
{
	if (bitOffset < 0 || bitOffset >= bytes.Count * 8)
	{
		return 0;
	}

	return (bytes[bitOffset / 8] >> (7 - (bitOffset % 8))) & 1;
}

static bool TryFindStartCode(byte[] data, int offset, out int start, out int length)
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

static string FormatHResult(int hr)
{
	return "0x" + unchecked((uint)hr).ToString("X8", CultureInfo.InvariantCulture);
}

[ComImport]
[Guid("BF94C121-5B05-4E6F-8000-BA598961414D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFTransform
{
	[PreserveSig]
	int GetStreamLimits(out int inputMinimum, out int inputMaximum, out int outputMinimum, out int outputMaximum);

	[PreserveSig]
	int GetStreamCount(out int inputStreams, out int outputStreams);

	[PreserveSig]
	int GetStreamIDs(int inputIdArraySize, IntPtr inputIds, int outputIdArraySize, IntPtr outputIds);

	[PreserveSig]
	int GetInputStreamInfo(int inputStreamId, IntPtr streamInfo);

	[PreserveSig]
	int GetOutputStreamInfo(int outputStreamId, out MftOutputStreamInfo streamInfo);

	[PreserveSig]
	int GetAttributes(out IntPtr attributes);

	[PreserveSig]
	int GetInputStreamAttributes(int inputStreamId, out IntPtr attributes);

	[PreserveSig]
	int GetOutputStreamAttributes(int outputStreamId, out IntPtr attributes);

	[PreserveSig]
	int DeleteInputStream(int streamId);

	[PreserveSig]
	int AddInputStreams(int streams, IntPtr streamIds);

	[PreserveSig]
	int GetInputAvailableType(int inputStreamId, int typeIndex, out IntPtr type);

	[PreserveSig]
	int GetOutputAvailableType(int outputStreamId, int typeIndex, out IntPtr type);

	[PreserveSig]
	int SetInputType(int inputStreamId, IMFMediaType type, int flags);

	[PreserveSig]
	int SetOutputType(int outputStreamId, IntPtr type, int flags);

	[PreserveSig]
	int GetInputCurrentType(int inputStreamId, out IntPtr type);

	[PreserveSig]
	int GetOutputCurrentType(int outputStreamId, out IntPtr type);

	[PreserveSig]
	int GetInputStatus(int inputStreamId, out int flags);

	[PreserveSig]
	int GetOutputStatus(out int flags);

	[PreserveSig]
	int SetOutputBounds(long lowerBound, long upperBound);

	[PreserveSig]
	int ProcessEvent(int inputStreamId, IntPtr ev);

	[PreserveSig]
	int ProcessMessage(int message, IntPtr param);

	[PreserveSig]
	int ProcessInput(int inputStreamId, IMFSample sample, int flags);

	[PreserveSig]
	int ProcessOutput(int flags, int outputBufferCount, IntPtr outputSamples, out int status);
}

[ComImport]
[Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFMediaType
{
	[PreserveSig]
	int GetItem(ref Guid guidKey, IntPtr value);
	[PreserveSig]
	int GetItemType(ref Guid guidKey, out int type);
	[PreserveSig]
	int CompareItem(ref Guid guidKey, IntPtr value, out bool result);
	[PreserveSig]
	int Compare(IntPtr attributes, int matchType, out bool result);
	[PreserveSig]
	int GetUINT32(ref Guid guidKey, out int value);
	[PreserveSig]
	int GetUINT64(ref Guid guidKey, out long value);
	[PreserveSig]
	int GetDouble(ref Guid guidKey, out double value);
	[PreserveSig]
	int GetGUID(ref Guid guidKey, out Guid value);
	[PreserveSig]
	int GetStringLength(ref Guid guidKey, out int length);
	[PreserveSig]
	int GetString(ref Guid guidKey, IntPtr value, int size, out int length);
	[PreserveSig]
	int GetAllocatedString(ref Guid guidKey, out IntPtr value, out int length);
	[PreserveSig]
	int GetBlobSize(ref Guid guidKey, out int size);
	[PreserveSig]
	int GetBlob(ref Guid guidKey, IntPtr buffer, int bufferSize, out int blobSize);
	[PreserveSig]
	int GetAllocatedBlob(ref Guid guidKey, out IntPtr buffer, out int size);
	[PreserveSig]
	int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr unknown);
	[PreserveSig]
	int SetItem(ref Guid guidKey, IntPtr value);
	[PreserveSig]
	int DeleteItem(ref Guid guidKey);
	[PreserveSig]
	int DeleteAllItems();
	[PreserveSig]
	int SetUINT32(ref Guid guidKey, int value);
	[PreserveSig]
	int SetUINT64(ref Guid guidKey, long value);
	[PreserveSig]
	int SetDouble(ref Guid guidKey, double value);
	[PreserveSig]
	int SetGUID(ref Guid guidKey, Guid value);
}

[ComImport]
[Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFAttributes
{
	[PreserveSig] int GetItem(ref Guid guidKey, IntPtr value);
	[PreserveSig] int GetItemType(ref Guid guidKey, out int type);
	[PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out bool result);
	[PreserveSig] int Compare(IntPtr attributes, int matchType, out bool result);
	[PreserveSig] int GetUINT32(ref Guid guidKey, out int value);
}

[ComImport]
[Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IMFSample
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
interface IMFMediaBuffer
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
interface IMFDXGIDeviceManager
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
interface IMFDXGIBuffer
{
	[PreserveSig] int GetResource(ref Guid riid, out IntPtr resource);
	[PreserveSig] int GetSubresourceIndex(out int subresourceIndex);
	[PreserveSig] int GetUnknown(ref Guid guid, ref Guid riid, out IntPtr unknown);
	[PreserveSig] int SetUnknown(ref Guid guid, IntPtr unknown);
}

[ComImport]
[Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface ID3D11Texture2D
{
	[PreserveSig] void GetDevice(out IntPtr device);
	[PreserveSig] int GetPrivateData(ref Guid guid, ref int dataSize, IntPtr data);
	[PreserveSig] int SetPrivateData(ref Guid guid, int dataSize, IntPtr data);
	[PreserveSig] int SetPrivateDataInterface(ref Guid guid, IntPtr data);
	[PreserveSig] void GetType(out int resourceDimension);
	[PreserveSig] void SetEvictionPriority(int evictionPriority);
	[PreserveSig] int GetEvictionPriority();
	[PreserveSig] void GetDesc(out D3D11Texture2DDesc desc);
}

[StructLayout(LayoutKind.Sequential)]
struct D3D11Texture2DDesc
{
	public int Width;
	public int Height;
	public int MipLevels;
	public int ArraySize;
	public int Format;
	public DxgiSampleDesc SampleDescription;
	public int Usage;
	public int BindFlags;
	public int CpuAccessFlags;
	public int MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
struct DxgiSampleDesc
{
	public int Count;
	public int Quality;
}

[StructLayout(LayoutKind.Sequential)]
struct MftOutputStreamInfo
{
	public int Flags;
	public int Size;
	public int Alignment;
}

[StructLayout(LayoutKind.Sequential)]
struct MftOutputDataBuffer
{
	public int StreamId;
	public IntPtr Sample;
	public int Status;
	public IntPtr Events;
}

static class Native
{
	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFStartup(int version, int flags);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFShutdown();

	[DllImport("ole32.dll", ExactSpelling = true)]
	public static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, int dwClsContext, ref Guid riid, out IntPtr ppv);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateMediaType(out IMFMediaType mediaType);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateSample(out IMFSample sample);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	public static extern int MFCreateDXGIDeviceManager(out int resetToken, out IntPtr manager);

	[DllImport("d3d11.dll", ExactSpelling = true)]
	public static extern int D3D11CreateDevice(
		IntPtr adapter,
		int driverType,
		IntPtr software,
		int flags,
		IntPtr featureLevels,
		int featureLevelsCount,
		int sdkVersion,
		out IntPtr device,
		out int featureLevel,
		out IntPtr immediateContext);
}

static class MediaTypeAttributeKeys
{
	public static readonly Guid MajorType = new Guid("48EBA18E-F8C9-4687-BF11-0A74C9F96A8F");
	public static readonly Guid Subtype = new Guid("F7E34C9A-42E8-4714-B74B-CB29D72C35E5");
	public static readonly Guid FrameSize = new Guid("1652C33D-D6B2-4012-B834-72030849A37D");
	public static readonly Guid FrameRate = new Guid("C459A2E8-3D2C-4E44-B132-FEE5156C7BB0");
}

static class MediaFoundationAttributes
{
	public static readonly Guid D3D11Aware = new Guid("206B4FC8-FCF9-4C51-AFE3-9764369E33A0");
}

static class Interfaces
{
	public static readonly Guid IMFDXGIBuffer = new Guid("E7174CFA-1C9E-48B1-8866-626226BFC258");
	public static readonly Guid ID3D11Texture2D = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
}

static class MediaTypes
{
	public static readonly Guid Video = new Guid("73646976-0000-0010-8000-00AA00389B71");
}

static class VideoSubtypes
{
	public static readonly Guid H264 = new Guid("34363248-0000-0010-8000-00AA00389B71");
	public static readonly Guid NV12 = new Guid("3231564E-0000-0010-8000-00AA00389B71");
}

static class HResults
{
	public const int MfENotAccepting = unchecked((int)0xC00D36B5);
	public const int MfETransformStreamChange = unchecked((int)0xC00D6D61);
	public const int MfETransformNeedMoreInput = unchecked((int)0xC00D6D72);
}

static class MftMessage
{
	public const int CommandDrain = 0x00000001;
	public const int SetD3DManager = 0x00000002;
	public const int NotifyBeginStreaming = 0x10000000;
	public const int NotifyEndOfStream = 0x10000002;
	public const int NotifyStartOfStream = 0x10000003;
}

static class D3DDriverType
{
	public const int Hardware = 1;
}

static class D3D11CreateDeviceFlags
{
	public const int BgraSupport = 0x00000020;
	public const int VideoSupport = 0x00000800;
}

static class MftOutputStreamFlags
{
	public const int ProvidesSamples = 0x00000100;
}

readonly record struct NalUnit(int StartCodeOffset, int NalOffset, int EndOffset, int Type);

readonly record struct AccessUnit(int Offset, int Length);

readonly record struct VideoGeometry(int Width, int Height, int Fps, string Source);

readonly record struct H264StreamInfo(int Width, int Height, int ProfileIdc, int LevelIdc);

ref struct BitReader
{
	private readonly IReadOnlyList<byte> _bytes;
	private int _bitOffset;

	public BitReader(IReadOnlyList<byte> bytes)
	{
		_bytes = bytes;
		_bitOffset = 0;
	}

	public bool TryReadBit(out bool value)
	{
		value = false;
		if (_bitOffset >= _bytes.Count * 8)
		{
			return false;
		}
		value = ReadBitAt(_bitOffset) != 0;
		_bitOffset++;
		return true;
	}

	public bool TryReadBits(int count, out uint value)
	{
		value = 0;
		if (count < 0 || count > 32 || _bitOffset + count > _bytes.Count * 8)
		{
			return false;
		}
		for (int i = 0; i < count; i++)
		{
			value = (value << 1) | (uint)ReadBitAt(_bitOffset++);
		}
		return true;
	}

	public bool TryReadUnsignedExpGolomb(out uint value)
	{
		value = 0;
		int leadingZeroBits = 0;
		while (_bitOffset + leadingZeroBits < _bytes.Count * 8 && ReadBitAt(_bitOffset + leadingZeroBits) == 0)
		{
			leadingZeroBits++;
			if (leadingZeroBits > 31)
			{
				return false;
			}
		}
		if (_bitOffset + leadingZeroBits >= _bytes.Count * 8)
		{
			return false;
		}
		_bitOffset += leadingZeroBits + 1;

		uint suffix = 0;
		if (leadingZeroBits > 0 && !TryReadBits(leadingZeroBits, out suffix))
		{
			return false;
		}
		value = ((1u << leadingZeroBits) - 1u) + suffix;
		return true;
	}

	public bool TryReadSignedExpGolomb(out int value)
	{
		value = 0;
		if (!TryReadUnsignedExpGolomb(out uint unsignedValue))
		{
			return false;
		}
		value = (int)((unsignedValue + 1) / 2);
		if ((unsignedValue & 1) == 0)
		{
			value = -value;
		}
		return true;
	}

	private int ReadBitAt(int bitOffset)
	{
		if (bitOffset < 0 || bitOffset >= _bytes.Count * 8)
		{
			return 0;
		}

		return (_bytes[bitOffset / 8] >> (7 - (bitOffset % 8))) & 1;
	}
}

struct DecodeProbeResult
{
	public int AccessUnitsFed;
	public int ProcessInputOk;
	public int DecodedOutputs;
	public long OutputBytes;
	public int DxgiBuffers;
	public int D3D11Textures;
	public int D3D11TextureDescriptionsLogged;
	public int NotAccepting;
	public int NeedMoreInput;
	public int StreamChanges;
	public int Failures;
}
