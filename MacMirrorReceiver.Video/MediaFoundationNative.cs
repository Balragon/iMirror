#if HIGH_RESOLUTION_D3D
using System;
using System.Runtime.InteropServices;

namespace MacMirrorReceiver.Video;

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

[ComImport]
[Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface MfAttributes
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
