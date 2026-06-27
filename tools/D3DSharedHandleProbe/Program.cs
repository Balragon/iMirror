using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using D3D9 = SharpDX.Direct3D9;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;
using VD3D9 = Vortice.Direct3D9;
using VD3D11 = Vortice.Direct3D11;
using VDXGI = Vortice.DXGI;

int width = args.Length >= 1 && int.TryParse(args[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedWidth)
	? parsedWidth
	: 2048;
int height = args.Length >= 2 && int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedHeight)
	? parsedHeight
	: 1152;

Console.WriteLine("iMirror D3D11/D3D9 shared-handle probe");
Console.WriteLine($"size={width}x{height}");

IntPtr windowHandle = Native.GetConsoleWindow();
if (windowHandle == IntPtr.Zero)
{
	windowHandle = Native.GetDesktopWindow();
}
Console.WriteLine("windowHandle=0x" + windowHandle.ToInt64().ToString("X", CultureInfo.InvariantCulture));

if (string.Equals(Environment.GetEnvironmentVariable("IMIRROR_GPU_BINDING"), "vortice", StringComparison.OrdinalIgnoreCase))
{
	return RunVorticeProbe(width, height, windowHandle);
}

using var d3d11Device = new D3D11.Device(
	SharpDX.Direct3D.DriverType.Hardware,
	D3D11.DeviceCreationFlags.BgraSupport | D3D11.DeviceCreationFlags.VideoSupport);
Console.WriteLine("D3D11.Device featureLevel=" + d3d11Device.FeatureLevel);

var textureDesc = new D3D11.Texture2DDescription
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
};

using var d3d11Texture = new D3D11.Texture2D(d3d11Device, textureDesc);
using var dxgiResource = d3d11Texture.QueryInterface<DXGI.Resource>();
IntPtr sharedHandle = dxgiResource.SharedHandle;
Console.WriteLine("D3D11 sharedHandle=0x" + sharedHandle.ToInt64().ToString("X", CultureInfo.InvariantCulture));
if (sharedHandle == IntPtr.Zero)
{
	Console.WriteLine("result=failed-no-shared-handle");
	return 1;
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
Console.WriteLine("D3D9Ex.Device created");

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
Console.WriteLine("D3D9Ex shared texture opened; surface=0x" + d3d9Surface.NativePointer.ToInt64().ToString("X", CultureInfo.InvariantCulture));

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
Console.WriteLine($"D3DImage backBuffer accepted; pixelSize={image.PixelWidth}x{image.PixelHeight}");

image.Lock();
try
{
	image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
}
finally
{
	image.Unlock();
}

Console.WriteLine("result=d3d11-d3d9-d3dimage-shared-handle-opened");
return 0;

static int RunVorticeProbe(int width, int height, IntPtr windowHandle)
{
	Console.WriteLine("gpuBinding=Vortice");
	using var d3d11Device = VD3D11.D3D11.D3D11CreateDevice(
		Vortice.Direct3D.DriverType.Hardware,
		VD3D11.DeviceCreationFlags.BgraSupport | VD3D11.DeviceCreationFlags.VideoSupport);
	Console.WriteLine("D3D11.Device featureLevel=" + d3d11Device.FeatureLevel);

	var textureDesc = new VD3D11.Texture2DDescription
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
	};

	using var d3d11Texture = d3d11Device.CreateTexture2D(textureDesc);
	using var dxgiResource = d3d11Texture.QueryInterface<VDXGI.IDXGIResource>();
	IntPtr sharedHandle = dxgiResource.SharedHandle;
	Console.WriteLine("D3D11 sharedHandle=0x" + sharedHandle.ToInt64().ToString("X", CultureInfo.InvariantCulture));
	if (sharedHandle == IntPtr.Zero)
	{
		Console.WriteLine("result=failed-no-shared-handle");
		return 1;
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
	Console.WriteLine("D3D9Ex.Device created");

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
	Console.WriteLine("D3D9Ex shared texture opened; surface=0x" + d3d9Surface.NativePointer.ToInt64().ToString("X", CultureInfo.InvariantCulture));

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
	Console.WriteLine($"D3DImage backBuffer accepted; pixelSize={image.PixelWidth}x{image.PixelHeight}");

	image.Lock();
	try
	{
		image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
	}
	finally
	{
		image.Unlock();
	}

	Console.WriteLine("result=vortice-d3d11-d3d9-d3dimage-shared-handle-opened");
	return 0;
}

internal static class Native
{
	[DllImport("kernel32.dll")]
	public static extern IntPtr GetConsoleWindow();

	[DllImport("user32.dll")]
	public static extern IntPtr GetDesktopWindow();
}
