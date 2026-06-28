using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vortice;
using VD3D9 = Vortice.Direct3D9;
using VD3D11 = Vortice.Direct3D11;
using VDXGI = Vortice.DXGI;

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

return RunVorticeProbe(width, height, windowHandle);

static int RunVorticeProbe(int width, int height, IntPtr windowHandle)
{
	Console.WriteLine("gpuBinding=Vortice");
	using var device = VD3D11.D3D11.D3D11CreateDevice(
		Vortice.Direct3D.DriverType.Hardware,
		VD3D11.DeviceCreationFlags.BgraSupport | VD3D11.DeviceCreationFlags.VideoSupport);
	Console.WriteLine("D3D11.Device featureLevel=" + device.FeatureLevel);

	using var videoDevice = device.QueryInterface<VD3D11.ID3D11VideoDevice>();
	using var videoContext = device.ImmediateContext.QueryInterface<VD3D11.ID3D11VideoContext>();
	Console.WriteLine("VideoDevice/VideoContext acquired");

	var content = new VD3D11.VideoProcessorContentDescription
	{
		InputFrameFormat = VD3D11.VideoFrameFormat.Progressive,
		InputFrameRate = new VDXGI.Rational(30u, 1u),
		InputWidth = (uint)width,
		InputHeight = (uint)height,
		OutputFrameRate = new VDXGI.Rational(30u, 1u),
		OutputWidth = (uint)width,
		OutputHeight = (uint)height,
		Usage = VD3D11.VideoUsage.PlaybackNormal
	};

	videoDevice.CreateVideoProcessorEnumerator(ref content, out VD3D11.ID3D11VideoProcessorEnumerator enumerator);
	using (enumerator)
	{
		VD3D11.VideoProcessorCaps caps = enumerator.VideoProcessorCaps;
		Console.WriteLine($"VideoProcessorCaps deviceCaps=0x{(int)caps.DeviceCaps:X8}, featureCaps=0x{(int)caps.FeatureCaps:X8}, maxInputStreams={caps.MaxInputStreams}, rateConversionCaps={caps.RateConversionCapsCount}");

		enumerator.CheckVideoProcessorFormat(VDXGI.Format.NV12, out VD3D11.VideoProcessorFormatSupport nv12Flags);
		enumerator.CheckVideoProcessorFormat(VDXGI.Format.B8G8R8A8_UNorm, out VD3D11.VideoProcessorFormatSupport bgraFlags);
		Console.WriteLine($"CheckFormat NV12 flags=0x{(int)nv12Flags:X8}");
		Console.WriteLine($"CheckFormat B8G8R8A8_UNorm flags=0x{(int)bgraFlags:X8}");

		bool nv12Input = (nv12Flags & VD3D11.VideoProcessorFormatSupport.Input) != 0;
		bool bgraOutput = (bgraFlags & VD3D11.VideoProcessorFormatSupport.Output) != 0;
		Console.WriteLine($"formatSupport nv12Input={nv12Input}, bgraOutput={bgraOutput}");
		if (!nv12Input || !bgraOutput)
		{
			Console.WriteLine("result=unsupported-format-conversion");
			return 1;
		}

		videoDevice.CreateVideoProcessor(enumerator, 0, out VD3D11.ID3D11VideoProcessor processor);
		using (processor)
		{
			using var inputTexture = device.CreateTexture2D(new VD3D11.Texture2DDescription
			{
				Width = (uint)width,
				Height = (uint)height,
				MipLevels = 1u,
				ArraySize = 1u,
				Format = VDXGI.Format.NV12,
				SampleDescription = new VDXGI.SampleDescription(1, 0),
				Usage = VD3D11.ResourceUsage.Default,
				BindFlags = VD3D11.BindFlags.None,
				CPUAccessFlags = VD3D11.CpuAccessFlags.None,
				MiscFlags = VD3D11.ResourceOptionFlags.None
			});
			using var outputTexture = device.CreateTexture2D(new VD3D11.Texture2DDescription
			{
				Width = (uint)width,
				Height = (uint)height,
				MipLevels = 1u,
				ArraySize = 1u,
				Format = VDXGI.Format.B8G8R8A8_UNorm,
				SampleDescription = new VDXGI.SampleDescription(1, 0),
				Usage = VD3D11.ResourceUsage.Default,
				BindFlags = VD3D11.BindFlags.RenderTarget | VD3D11.BindFlags.ShaderResource,
				CPUAccessFlags = VD3D11.CpuAccessFlags.None,
				MiscFlags = VD3D11.ResourceOptionFlags.Shared
			});

			var inputViewDesc = new VD3D11.VideoProcessorInputViewDescription
			{
				FourCC = 0,
				ViewDimension = VD3D11.VideoProcessorInputViewDimension.Texture2D,
				Texture2D = new VD3D11.Texture2DVideoProcessorInputView { MipSlice = 0u, ArraySlice = 0u }
			};
			videoDevice.CreateVideoProcessorInputView(inputTexture, enumerator, inputViewDesc, out VD3D11.ID3D11VideoProcessorInputView inputView);
			using (inputView)
			{
				var outputViewDesc = new VD3D11.VideoProcessorOutputViewDescription
				{
					ViewDimension = VD3D11.VideoProcessorOutputViewDimension.Texture2D,
					Texture2D = new VD3D11.Texture2DVideoProcessorOutputView { MipSlice = 0u }
				};
				videoDevice.CreateVideoProcessorOutputView(outputTexture, enumerator, outputViewDesc, out VD3D11.ID3D11VideoProcessorOutputView outputView);
				using (outputView)
				{
					Console.WriteLine("VideoProcessor/input/output views created");
					var rect = new RawRect(0, 0, width, height);
					videoContext.VideoProcessorSetStreamFrameFormat(processor, 0, VD3D11.VideoFrameFormat.Progressive);
					videoContext.VideoProcessorSetStreamSourceRect(processor, 0, true, rect);
					videoContext.VideoProcessorSetStreamDestRect(processor, 0, true, rect);
					videoContext.VideoProcessorSetOutputTargetRect(processor, true, rect);
					var streams = new[]
					{
						new VD3D11.VideoProcessorStream
						{
							Enable = true,
							OutputIndex = 0,
							InputFrameOrField = 0,
							PastFrames = 0,
							FutureFrames = 0,
							InputSurface = inputView
						}
					};
					videoContext.VideoProcessorBlt(processor, outputView, 0, (uint)streams.Length, streams);
					device.ImmediateContext.Flush();
					Console.WriteLine("VideoProcessorBlt completed");
					AttachVorticeSharedOutputToD3DImage(outputTexture, width, height, windowHandle);
				}
			}
		}
	}

	Console.WriteLine("result=vortice-nv12-to-bgra-video-processor-d3dimage-completed");
	return 0;
}

static void AttachVorticeSharedOutputToD3DImage(VD3D11.ID3D11Texture2D outputTexture, int width, int height, IntPtr windowHandle)
{
	using var dxgiResource = outputTexture.QueryInterface<VDXGI.IDXGIResource>();
	IntPtr sharedHandle = dxgiResource.SharedHandle;
	Console.WriteLine("VideoProcessor output sharedHandle=0x" + sharedHandle.ToInt64().ToString("X", CultureInfo.InvariantCulture));
	if (sharedHandle == IntPtr.Zero)
	{
		throw new InvalidOperationException("Video processor output texture did not expose a shared handle.");
	}

	using var d3d9 = VD3D9.D3D9.Direct3DCreate9Ex();
	var present = new VD3D9.PresentParameters
	{
		Windowed = true,
		SwapEffect = VD3D9.SwapEffect.Discard,
		DeviceWindowHandle = windowHandle,
		PresentationInterval = VD3D9.PresentInterval.Immediate
	};
	using var d3d9Device = d3d9.CreateDeviceEx(
		0,
		VD3D9.DeviceType.Hardware,
		windowHandle,
		VD3D9.CreateFlags.HardwareVertexProcessing | VD3D9.CreateFlags.Multithreaded | VD3D9.CreateFlags.FpuPreserve,
		present);

	IntPtr d3d9SharedHandle = sharedHandle;
	using var d3d9Texture = d3d9Device.CreateTexture(
		(uint)width,
		(uint)height,
		1u,
		VD3D9.Usage.RenderTarget,
		VD3D9.Format.A8R8G8B8,
		VD3D9.Pool.Default,
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
