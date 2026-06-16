using System;
using System.Runtime.InteropServices;

namespace MacMirrorReceiver.Video;

internal static class HighResolutionPipelineProbe
{
	public const string EnvironmentVariableName = "IMIRROR_HR_V2_PROBE";

	private const int S_OK = 0;
	private const int MFVersion = 0x00020070;
	private const int D3D11SdkVersion = 7;
	private const int D3DDriverTypeHardware = 1;
	private const int D3D11CreateDeviceBgraSupport = 0x20;
	private const int D3D11CreateDeviceVideoSupport = 0x800;
	private const int ClsctxInprocServer = 0x1;
	private const int MftMessageSetD3DManager = 0x2;

	private static readonly Guid ClsidCmsH264DecoderMft = new Guid("62CE7E72-4C71-4D20-B15D-452831A87D9D");
	private static readonly Guid IidIMFTransform = new Guid("BF94C121-5B05-4E6F-8000-BA598961414D");
	private static readonly Guid MfSaD3D11Aware = new Guid("206B4FC8-FCF9-4C51-AFE3-9764369E33A0");

	public static void RunIfEnabled()
	{
		string? value = Environment.GetEnvironmentVariable(EnvironmentVariableName);
		if (string.IsNullOrWhiteSpace(value) ||
			value == "0" ||
			string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		Run();
	}

	private static bool? _cachedHardwareDecodeAvailable;
	private static readonly object _detectGate = new object();

	// Whether this machine can hardware-decode H.264 to D3D11 textures (MF H.264 MFT is
	// MF_SA_D3D11_AWARE and a D3D11 hardware video device can be created). Cached; used to decide
	// whether to advertise native resolution and route to the GPU engine by default.
	public static bool IsHardwareDecodeAvailable
	{
		get
		{
			if (ReceiverSettings.Load().Effective.VideoEngine == ReceiverVideoEngineSetting.Software)
			{
				return false;
			}

			lock (_detectGate)
			{
				_cachedHardwareDecodeAvailable ??= DetectHardwareDecode();
				return _cachedHardwareDecodeAvailable.Value;
			}
		}
	}

	private static bool DetectHardwareDecode()
	{
		bool mfStarted = false;
		try
		{
			if (MFStartup(MFVersion, 0) < 0)
			{
				return false;
			}
			mfStarted = true;

			if (TryCreateD3D11Device(D3D11CreateDeviceBgraSupport | D3D11CreateDeviceVideoSupport, out _) != S_OK)
			{
				return false;
			}

			Guid clsid = ClsidCmsH264DecoderMft;
			Guid iid = IidIMFTransform;
			if (CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out IntPtr decoder) != S_OK || decoder == IntPtr.Zero)
			{
				return false;
			}

			try
			{
				var transform = (IMFTransform)Marshal.GetObjectForIUnknown(decoder);
				try
				{
					if (transform.GetAttributes(out IntPtr attributesPtr) < 0 || attributesPtr == IntPtr.Zero)
					{
						return false;
					}
					var attributes = (IMFAttributes)Marshal.GetObjectForIUnknown(attributesPtr);
					try
					{
						Guid key = MfSaD3D11Aware;
						return attributes.GetUINT32(ref key, out int aware) >= 0 && aware != 0;
					}
					finally
					{
						Marshal.ReleaseComObject(attributes);
						Marshal.Release(attributesPtr);
					}
				}
				finally
				{
					Marshal.ReleaseComObject(transform);
				}
			}
			finally
			{
				Marshal.Release(decoder);
			}
		}
		catch
		{
			return false;
		}
		finally
		{
			if (mfStarted)
			{
				MFShutdown();
			}
		}
	}

	private static void Run()
	{
		AppLog.Write("High-resolution v2 probe started.");
		ProbeMediaFoundation();
		ProbeD3D11();
		AppLog.Write("High-resolution v2 probe finished.");
	}

	private static void ProbeMediaFoundation()
	{
		int startupHr = MFStartup(MFVersion, 0);
		AppLog.Write($"HR v2 probe: MFStartup hr={FormatHResult(startupHr)}.");
		if (startupHr < 0)
		{
			return;
		}

		try
		{
			IntPtr decoder = IntPtr.Zero;
			Guid decoderClsid = ClsidCmsH264DecoderMft;
			Guid transformIid = IidIMFTransform;
			int createHr = CoCreateInstance(
				ref decoderClsid,
				IntPtr.Zero,
				ClsctxInprocServer,
				ref transformIid,
				out decoder);
			AppLog.Write(createHr == S_OK && decoder != IntPtr.Zero
				? "HR v2 probe: Microsoft H.264 Decoder MFT can be created in-process."
				: $"HR v2 probe: Microsoft H.264 Decoder MFT create failed hr={FormatHResult(createHr)}.");
			if (decoder != IntPtr.Zero)
			{
				ProbeMediaFoundationD3D11Manager(decoder);
				Marshal.Release(decoder);
			}
		}
		finally
		{
			int shutdownHr = MFShutdown();
			if (shutdownHr < 0)
			{
				AppLog.Write($"HR v2 probe: MFShutdown hr={FormatHResult(shutdownHr)}.");
			}
		}
	}

	private static void ProbeMediaFoundationD3D11Manager(IntPtr decoder)
	{
		var transform = (IMFTransform)Marshal.GetObjectForIUnknown(decoder);
		IntPtr device = IntPtr.Zero;
		IntPtr context = IntPtr.Zero;
		IntPtr manager = IntPtr.Zero;
		try
		{
			int attributesHr = transform.GetAttributes(out IntPtr attributesPtr);
			int awareHr = unchecked((int)0x80004005);
			int d3d11Aware = 0;
			if (attributesHr >= 0 && attributesPtr != IntPtr.Zero)
			{
				var attributes = (IMFAttributes)Marshal.GetObjectForIUnknown(attributesPtr);
				try
				{
					Guid key = MfSaD3D11Aware;
					awareHr = attributes.GetUINT32(ref key, out d3d11Aware);
				}
				finally
				{
					Marshal.ReleaseComObject(attributes);
					Marshal.Release(attributesPtr);
				}
			}

			AppLog.Write($"HR v2 probe: Microsoft H.264 Decoder MF_SA_D3D11_AWARE hr={FormatHResult(awareHr)}, value={d3d11Aware}.");
			if (awareHr < 0 || d3d11Aware == 0)
			{
				return;
			}

			int deviceHr = D3D11CreateDevice(
				IntPtr.Zero,
				D3DDriverTypeHardware,
				IntPtr.Zero,
				D3D11CreateDeviceBgraSupport | D3D11CreateDeviceVideoSupport,
				IntPtr.Zero,
				0,
				D3D11SdkVersion,
				out device,
				out int featureLevel,
				out context);
			AppLog.Write($"HR v2 probe: D3D11 hardware video device for MFT hr={FormatHResult(deviceHr)}, featureLevel=0x{featureLevel:X}.");
			if (deviceHr < 0)
			{
				return;
			}

			int managerHr = MFCreateDXGIDeviceManager(out int resetToken, out manager);
			AppLog.Write($"HR v2 probe: MFCreateDXGIDeviceManager hr={FormatHResult(managerHr)}.");
			if (managerHr < 0 || manager == IntPtr.Zero)
			{
				return;
			}

			var dxgiManager = (IMFDXGIDeviceManager)Marshal.GetObjectForIUnknown(manager);
			int resetHr;
			try
			{
				resetHr = dxgiManager.ResetDevice(device, resetToken);
			}
			finally
			{
				Marshal.ReleaseComObject(dxgiManager);
			}
			AppLog.Write($"HR v2 probe: IMFDXGIDeviceManager.ResetDevice hr={FormatHResult(resetHr)}.");
			if (resetHr < 0)
			{
				return;
			}

			int setManagerHr = transform.ProcessMessage(MftMessageSetD3DManager, manager);
			AppLog.Write($"HR v2 probe: H.264 Decoder accepted D3D11 manager hr={FormatHResult(setManagerHr)}.");
		}
		finally
		{
			if (manager != IntPtr.Zero)
			{
				Marshal.Release(manager);
			}
			if (context != IntPtr.Zero)
			{
				Marshal.Release(context);
			}
			if (device != IntPtr.Zero)
			{
				Marshal.Release(device);
			}
			Marshal.ReleaseComObject(transform);
		}
	}

	private static void ProbeD3D11()
	{
		int videoHr = TryCreateD3D11Device(D3D11CreateDeviceBgraSupport | D3D11CreateDeviceVideoSupport, out int videoFeatureLevel);
		if (videoHr == S_OK)
		{
			AppLog.Write($"HR v2 probe: D3D11 hardware video device created, featureLevel=0x{videoFeatureLevel:X}.");
			return;
		}

		AppLog.Write($"HR v2 probe: D3D11 hardware video device create failed hr={FormatHResult(videoHr)}.");
		int basicHr = TryCreateD3D11Device(D3D11CreateDeviceBgraSupport, out int basicFeatureLevel);
		AppLog.Write(basicHr == S_OK
			? $"HR v2 probe: D3D11 hardware device created without video-support flag, featureLevel=0x{basicFeatureLevel:X}."
			: $"HR v2 probe: D3D11 hardware device create failed hr={FormatHResult(basicHr)}.");
	}

	private static int TryCreateD3D11Device(int flags, out int featureLevel)
	{
		IntPtr device = IntPtr.Zero;
		IntPtr context = IntPtr.Zero;
		int hr = D3D11CreateDevice(
			IntPtr.Zero,
			D3DDriverTypeHardware,
			IntPtr.Zero,
			flags,
			IntPtr.Zero,
			0,
			D3D11SdkVersion,
			out device,
			out featureLevel,
			out context);

		if (context != IntPtr.Zero)
		{
			Marshal.Release(context);
		}
		if (device != IntPtr.Zero)
		{
			Marshal.Release(device);
		}
		return hr;
	}

	private static string FormatHResult(int hr)
	{
		return "0x" + unchecked((uint)hr).ToString("X8");
	}

	[DllImport("mfplat.dll", ExactSpelling = true)]
	private static extern int MFStartup(int version, int flags);

	[DllImport("mfplat.dll", ExactSpelling = true)]
	private static extern int MFShutdown();

	[DllImport("mfplat.dll", ExactSpelling = true)]
	private static extern int MFCreateDXGIDeviceManager(out int resetToken, out IntPtr manager);

	[DllImport("ole32.dll", ExactSpelling = true)]
	private static extern int CoCreateInstance(
		ref Guid rclsid,
		IntPtr pUnkOuter,
		int dwClsContext,
		ref Guid riid,
		out IntPtr ppv);

	[DllImport("d3d11.dll", ExactSpelling = true)]
	private static extern int D3D11CreateDevice(
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

	[ComImport]
	[Guid("BF94C121-5B05-4E6F-8000-BA598961414D")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMFTransform
	{
		[PreserveSig] int GetStreamLimits(out int inputMinimum, out int inputMaximum, out int outputMinimum, out int outputMaximum);
		[PreserveSig] int GetStreamCount(out int inputStreams, out int outputStreams);
		[PreserveSig] int GetStreamIDs(int inputIdArraySize, IntPtr inputIds, int outputIdArraySize, IntPtr outputIds);
		[PreserveSig] int GetInputStreamInfo(int inputStreamId, IntPtr streamInfo);
		[PreserveSig] int GetOutputStreamInfo(int outputStreamId, IntPtr streamInfo);
		[PreserveSig] int GetAttributes(out IntPtr attributes);
		[PreserveSig] int GetInputStreamAttributes(int inputStreamId, out IntPtr attributes);
		[PreserveSig] int GetOutputStreamAttributes(int outputStreamId, out IntPtr attributes);
		[PreserveSig] int DeleteInputStream(int streamId);
		[PreserveSig] int AddInputStreams(int streams, IntPtr streamIds);
		[PreserveSig] int GetInputAvailableType(int inputStreamId, int typeIndex, out IntPtr type);
		[PreserveSig] int GetOutputAvailableType(int outputStreamId, int typeIndex, out IntPtr type);
		[PreserveSig] int SetInputType(int inputStreamId, IntPtr type, int flags);
		[PreserveSig] int SetOutputType(int outputStreamId, IntPtr type, int flags);
		[PreserveSig] int GetInputCurrentType(int inputStreamId, out IntPtr type);
		[PreserveSig] int GetOutputCurrentType(int outputStreamId, out IntPtr type);
		[PreserveSig] int GetInputStatus(int inputStreamId, out int flags);
		[PreserveSig] int GetOutputStatus(out int flags);
		[PreserveSig] int SetOutputBounds(long lowerBound, long upperBound);
		[PreserveSig] int ProcessEvent(int inputStreamId, IntPtr ev);
		[PreserveSig] int ProcessMessage(int message, IntPtr param);
		[PreserveSig] int ProcessInput(int inputStreamId, IntPtr sample, int flags);
		[PreserveSig] int ProcessOutput(int flags, int outputBufferCount, IntPtr outputSamples, out int status);
	}

	[ComImport]
	[Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMFAttributes
	{
		[PreserveSig] int GetItem(ref Guid guidKey, IntPtr value);
		[PreserveSig] int GetItemType(ref Guid guidKey, out int type);
		[PreserveSig] int CompareItem(ref Guid guidKey, IntPtr value, out bool result);
		[PreserveSig] int Compare(IntPtr attributes, int matchType, out bool result);
		[PreserveSig] int GetUINT32(ref Guid guidKey, out int value);
	}

	[ComImport]
	[Guid("EB533D5D-2DB6-40F8-97A9-494692014F07")]
	[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
	private interface IMFDXGIDeviceManager
	{
		[PreserveSig] int CloseDeviceHandle(IntPtr deviceHandle);
		[PreserveSig] int GetVideoService(IntPtr deviceHandle, ref Guid riid, out IntPtr service);
		[PreserveSig] int LockDevice(IntPtr deviceHandle, ref Guid riid, out IntPtr device, bool block);
		[PreserveSig] int OpenDeviceHandle(out IntPtr deviceHandle);
		[PreserveSig] int ResetDevice(IntPtr device, int resetToken);
		[PreserveSig] int TestDevice(IntPtr deviceHandle);
		[PreserveSig] int UnlockDevice(IntPtr deviceHandle, bool saveState);
	}
}
