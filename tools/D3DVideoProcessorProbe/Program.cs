using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SharpDX.Mathematics.Interop;
using D3D9 = SharpDX.Direct3D9;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;

int width = args.Length >= 1 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedWidth)
	? parsedWidth
	: 2048;
int height = args.Length >= 2 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedHeight)
	? parsedHeight
	: 1152;

Console.WriteLine("iMirror D3D11 video processor probe");
Console.WriteLine($"size={width}x{height}");

IntPtr windowHandle = Native.GetConsoleWindow();
if (windowHandle == IntPtr.Zero)
{
	windowHandle = Native.GetDesktopWindow();
}
Console.WriteLine("windowHandle=0x" + windowHandle.ToInt64().ToString("X", CultureInfo.InvariantCulture));

using var device = new D3D11.Device(
	SharpDX.Direct3D.DriverType.Hardware,
	D3D11.DeviceCreationFlags.BgraSupport | D3D11.DeviceCreationFlags.VideoSupport);
Console.WriteLine("D3D11.Device featureLevel=" + device.FeatureLevel);

using var videoDevice = device.QueryInterface<D3D11.VideoDevice>();
using var videoContext = device.ImmediateContext.QueryInterface<D3D11.VideoContext>();
Console.WriteLine("VideoDevice/VideoContext acquired");

var content = new D3D11.VideoProcessorContentDescription
{
	InputFrameFormat = D3D11.VideoFrameFormat.Progressive,
	InputFrameRate = new DXGI.Rational(30, 1),
	InputWidth = width,
	InputHeight = height,
	OutputFrameRate = new DXGI.Rational(30, 1),
	OutputWidth = width,
	OutputHeight = height,
	Usage = D3D11.VideoUsage.PlaybackNormal
};

videoDevice.CreateVideoProcessorEnumerator(ref content, out D3D11.VideoProcessorEnumerator enumerator);
using (enumerator)
{
	D3D11.VideoProcessorCaps caps = enumerator.VideoProcessorCaps;
	Console.WriteLine($"VideoProcessorCaps deviceCaps=0x{(int)caps.DeviceCaps:X8}, featureCaps=0x{(int)caps.FeatureCaps:X8}, maxInputStreams={caps.MaxInputStreams}, rateConversionCaps={caps.RateConversionCapsCount}");

	int nv12Flags;
	enumerator.CheckVideoProcessorFormat(DXGI.Format.NV12, out nv12Flags);
	int bgraFlags;
	enumerator.CheckVideoProcessorFormat(DXGI.Format.B8G8R8A8_UNorm, out bgraFlags);
	Console.WriteLine($"CheckFormat NV12 flags=0x{nv12Flags:X8}");
	Console.WriteLine($"CheckFormat B8G8R8A8_UNorm flags=0x{bgraFlags:X8}");

	bool nv12Input = (nv12Flags & (int)D3D11.VideoProcessorFormatSupport.Input) != 0;
	bool bgraOutput = (bgraFlags & (int)D3D11.VideoProcessorFormatSupport.Output) != 0;
	Console.WriteLine($"formatSupport nv12Input={nv12Input}, bgraOutput={bgraOutput}");
	if (!nv12Input || !bgraOutput)
	{
		Console.WriteLine("result=unsupported-format-conversion");
		return 1;
	}

	videoDevice.CreateVideoProcessor(enumerator, 0, out D3D11.VideoProcessor processor);
	using (processor)
	{
		using var inputTexture = new D3D11.Texture2D(device, new D3D11.Texture2DDescription
		{
			Width = width,
			Height = height,
			MipLevels = 1,
			ArraySize = 1,
			Format = DXGI.Format.NV12,
			SampleDescription = new DXGI.SampleDescription(1, 0),
			Usage = D3D11.ResourceUsage.Default,
			BindFlags = D3D11.BindFlags.None,
			CpuAccessFlags = D3D11.CpuAccessFlags.None,
			OptionFlags = D3D11.ResourceOptionFlags.None
		});
		using var outputTexture = new D3D11.Texture2D(device, new D3D11.Texture2DDescription
		{
			Width = width,
			Height = height,
			MipLevels = 1,
			ArraySize = 1,
			Format = DXGI.Format.B8G8R8A8_UNorm,
			SampleDescription = new DXGI.SampleDescription(1, 0),
			Usage = D3D11.ResourceUsage.Default,
			BindFlags = D3D11.BindFlags.RenderTarget | D3D11.BindFlags.ShaderResource,
			CpuAccessFlags = D3D11.CpuAccessFlags.None,
			OptionFlags = D3D11.ResourceOptionFlags.Shared
		});

		var inputViewDesc = new D3D11.VideoProcessorInputViewDescription
		{
			FourCC = 0,
			Dimension = D3D11.VpivDimension.Texture2D,
			Texture2D = new D3D11.Texture2DVpiv { MipSlice = 0, ArraySlice = 0 }
		};
		videoDevice.CreateVideoProcessorInputView(inputTexture, enumerator, inputViewDesc, out D3D11.VideoProcessorInputView inputView);
		using (inputView)
		{
			var outputViewDesc = new D3D11.VideoProcessorOutputViewDescription
			{
				Dimension = D3D11.VpovDimension.Texture2D,
				Texture2D = new D3D11.Texture2DVpov { MipSlice = 0 }
			};
			videoDevice.CreateVideoProcessorOutputView(outputTexture, enumerator, outputViewDesc, out D3D11.VideoProcessorOutputView outputView);
			using (outputView)
			{
				Console.WriteLine("VideoProcessor/input/output views created");
				var rect = new RawRectangle(0, 0, width, height);
				videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, D3D11.VideoFrameFormat.Progressive);
				videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, rect);
				videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, rect);
				videoContext.VideoProcessorSetOutputTargetRect(processor, true, rect);
				var streams = new[]
				{
					new D3D11.VideoProcessorStream
					{
						Enable = true,
						OutputIndex = 0,
						InputFrameOrField = 0,
						PastFrames = 0,
						FutureFrames = 0,
						PInputSurface = inputView
					}
				};
				videoContext.VideoProcessorBlt(processor, outputView, 0, streams.Length, streams);
				device.ImmediateContext.Flush();
				Console.WriteLine("VideoProcessorBlt completed");
				AttachSharedOutputToD3DImage(outputTexture, width, height, windowHandle);
			}
		}
	}
}

Console.WriteLine("result=nv12-to-bgra-video-processor-d3dimage-completed");
return 0;

static void AttachSharedOutputToD3DImage(D3D11.Texture2D outputTexture, int width, int height, IntPtr windowHandle)
{
	using var dxgiResource = outputTexture.QueryInterface<DXGI.Resource>();
	IntPtr sharedHandle = dxgiResource.SharedHandle;
	Console.WriteLine("VideoProcessor output sharedHandle=0x" + sharedHandle.ToInt64().ToString("X", CultureInfo.InvariantCulture));
	if (sharedHandle == IntPtr.Zero)
	{
		throw new InvalidOperationException("Video processor output texture did not expose a shared handle.");
	}

	using var d3d9 = new D3D9.Direct3DEx();
	var present = new D3D9.PresentParameters
	{
		Windowed = true,
		SwapEffect = D3D9.SwapEffect.Discard,
		DeviceWindowHandle = windowHandle,
		PresentationInterval = D3D9.PresentInterval.Immediate
	};
	using var d3d9Device = new D3D9.DeviceEx(
		d3d9,
		0,
		D3D9.DeviceType.Hardware,
		windowHandle,
		D3D9.CreateFlags.HardwareVertexProcessing | D3D9.CreateFlags.Multithreaded | D3D9.CreateFlags.FpuPreserve,
		present);

	IntPtr d3d9SharedHandle = sharedHandle;
	using var d3d9Texture = new D3D9.Texture(
		d3d9Device,
		width,
		height,
		1,
		D3D9.Usage.RenderTarget,
		D3D9.Format.A8R8G8B8,
		D3D9.Pool.Default,
		ref d3d9SharedHandle);
	using var d3d9Surface = d3d9Texture.GetSurfaceLevel(0);
	Console.WriteLine("D3D9Ex opened video processor output; surface=0x" + d3d9Surface.NativePointer.ToInt64().ToString("X", CultureInfo.InvariantCulture));

	var image = new D3DImage();
	image.Lock();
	try
	{
		image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, d3d9Surface.NativePointer);
		image.AddDirtyRect(new Int32Rect(0, 0, width, height));
	}
	finally
	{
		image.Unlock();
	}
	Console.WriteLine($"D3DImage accepted video processor output; pixelSize={image.PixelWidth}x{image.PixelHeight}");

	image.Lock();
	try
	{
		image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
	}
	finally
	{
		image.Unlock();
	}
}

internal static class Native
{
	[DllImport("kernel32.dll")]
	public static extern IntPtr GetConsoleWindow();

	[DllImport("user32.dll")]
	public static extern IntPtr GetDesktopWindow();
}
