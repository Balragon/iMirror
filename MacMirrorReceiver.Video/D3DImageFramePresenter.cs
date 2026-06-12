#if DIRECTX_PROBE
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using SharpDX.Direct3D9;

namespace MacMirrorReceiver.Video;

public sealed class D3DImageFramePresenter : IDisposable
{
	private readonly IntPtr _windowHandle;

	private readonly Direct3DEx _direct3D;

	private readonly DeviceEx _device;

	private Texture? _stagingTexture;

	private Texture? _renderTexture;

	private Surface? _renderSurface;

	private int _width;

	private int _height;

	public D3DImage ImageSource { get; } = new D3DImage();

	public int Width => _width;

	public int Height => _height;

	public D3DImageFramePresenter(IntPtr windowHandle)
	{
		_windowHandle = windowHandle;
		_direct3D = new Direct3DEx();
		var present = new PresentParameters
		{
			Windowed = true,
			SwapEffect = SwapEffect.Discard,
			DeviceWindowHandle = _windowHandle,
			PresentationInterval = PresentInterval.Immediate
		};
		_device = new DeviceEx(
			_direct3D,
			0,
			DeviceType.Hardware,
			_windowHandle,
			CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
			present);
	}

	public void Present(VideoFrame frame)
	{
		EnsureSurface(frame.Width, frame.Height);
		if (_stagingTexture == null || _renderTexture == null)
		{
			return;
		}

		int sourceStride = frame.Width * 4;
		var locked = _stagingTexture.LockRectangle(0, LockFlags.None);
		try
		{
			if (locked.Pitch == sourceStride)
			{
				Marshal.Copy(frame.Buffer, 0, locked.DataPointer, sourceStride * frame.Height);
			}
			else
			{
				for (int y = 0; y < frame.Height; y++)
				{
					Marshal.Copy(
						frame.Buffer,
						y * sourceStride,
						IntPtr.Add(locked.DataPointer, y * locked.Pitch),
						sourceStride);
				}
			}
		}
		finally
		{
			_stagingTexture.UnlockRectangle(0);
		}

		_device.UpdateTexture(_stagingTexture, _renderTexture);
		ImageSource.Lock();
		try
		{
			ImageSource.AddDirtyRect(new Int32Rect(0, 0, frame.Width, frame.Height));
		}
		finally
		{
			ImageSource.Unlock();
		}
	}

	private void EnsureSurface(int width, int height)
	{
		if (_width == width && _height == height && _stagingTexture != null && _renderTexture != null && _renderSurface != null)
		{
			return;
		}

		DisposeSurface();
		_width = width;
		_height = height;
		_stagingTexture = new Texture(_device, width, height, 1, Usage.None, Format.A8R8G8B8, Pool.SystemMemory);
		_renderTexture = new Texture(_device, width, height, 1, Usage.RenderTarget, Format.A8R8G8B8, Pool.Default);
		_renderSurface = _renderTexture.GetSurfaceLevel(0);

		ImageSource.Lock();
		try
		{
			ImageSource.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _renderSurface.NativePointer);
		}
		finally
		{
			ImageSource.Unlock();
		}
	}

	private void DisposeSurface()
	{
		ImageSource.Lock();
		try
		{
			ImageSource.SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
		}
		finally
		{
			ImageSource.Unlock();
		}
		_renderSurface?.Dispose();
		_renderTexture?.Dispose();
		_stagingTexture?.Dispose();
		_renderSurface = null;
		_renderTexture = null;
		_stagingTexture = null;
	}

	public void Dispose()
	{
		DisposeSurface();
		_device.Dispose();
		_direct3D.Dispose();
	}
}
#endif
