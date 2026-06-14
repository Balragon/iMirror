#if HIGH_RESOLUTION_D3D
using System;
using System.Windows;
using System.Windows.Interop;
using SharpDX.Mathematics.Interop;
using D3D9 = SharpDX.Direct3D9;
using D3D11 = SharpDX.Direct3D11;
using DXGI = SharpDX.DXGI;

namespace MacMirrorReceiver.Video;

public sealed class D3D11VideoProcessorD3DImagePresenter : IDisposable
{
	private readonly IntPtr _windowHandle;
	private readonly D3D11.Device _d3d11Device;
	private readonly D3D11.VideoDevice _videoDevice;
	private readonly D3D11.VideoContext _videoContext;
	private readonly D3D9.Direct3DEx _d3d9;
	private readonly D3D9.DeviceEx _d3d9Device;
	private D3D11.Multithread? _multithread;

	private D3D11.VideoProcessorEnumerator? _enumerator;
	private D3D11.VideoProcessor? _processor;
	private D3D11.Texture2D? _outputTexture;
	private D3D11.VideoProcessorOutputView? _outputView;
	private D3D9.Texture? _d3d9Texture;
	private D3D9.Surface? _d3d9Surface;
	private int _width;
	private int _height;
	private int _fps;
	private bool _loggedInvalidInputSubresource;
	private bool _loggedFrontBufferUnavailable;

	public D3DImage ImageSource { get; } = new D3DImage();

	public D3D11.Device Device => _d3d11Device;

	public int Width => _width;

	public int Height => _height;

	public bool IsMultithreadProtected { get; private set; }

	public event Action<string>? StatusChanged;

	public D3D11VideoProcessorD3DImagePresenter(IntPtr windowHandle)
	{
		_windowHandle = windowHandle;
		_d3d11Device = new D3D11.Device(
			SharpDX.Direct3D.DriverType.Hardware,
			D3D11.DeviceCreationFlags.BgraSupport | D3D11.DeviceCreationFlags.VideoSupport);
		EnableD3D11MultithreadProtection();
		_videoDevice = _d3d11Device.QueryInterface<D3D11.VideoDevice>();
		_videoContext = _d3d11Device.ImmediateContext.QueryInterface<D3D11.VideoContext>();

		_d3d9 = new D3D9.Direct3DEx();
		var present = new D3D9.PresentParameters
		{
			Windowed = true,
			SwapEffect = D3D9.SwapEffect.Discard,
			DeviceWindowHandle = _windowHandle,
			PresentationInterval = D3D9.PresentInterval.Immediate
		};
		_d3d9Device = new D3D9.DeviceEx(
			_d3d9,
			0,
			D3D9.DeviceType.Hardware,
			_windowHandle,
			D3D9.CreateFlags.HardwareVertexProcessing | D3D9.CreateFlags.Multithreaded | D3D9.CreateFlags.FpuPreserve,
			present);
		ImageSource.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;
	}

	private void EnableD3D11MultithreadProtection()
	{
		try
		{
			_multithread = _d3d11Device.QueryInterface<D3D11.Multithread>();
			_multithread.SetMultithreadProtected(true);
			IsMultithreadProtected = _multithread.GetMultithreadProtected();
		}
		catch
		{
			_multithread?.Dispose();
			_multithread = null;
			IsMultithreadProtected = false;
		}
	}

	public void PresentNv12Texture(D3D11.Texture2D inputTexture, int subresourceIndex, int width, int height, int fps)
	{
		EnsurePipeline(width, height, fps);
		if (_enumerator == null || _processor == null || _outputView == null)
		{
			return;
		}

		D3D11.Texture2DDescription inputDescription = inputTexture.Description;
		if (subresourceIndex < 0 || subresourceIndex >= inputDescription.ArraySize)
		{
			if (!_loggedInvalidInputSubresource)
			{
				_loggedInvalidInputSubresource = true;
				StatusChanged?.Invoke(
					$"D3D11 presenter rejected input texture subresourceIndex={subresourceIndex}, arraySize={inputDescription.ArraySize}.");
			}
			return;
		}

		var inputViewDesc = new D3D11.VideoProcessorInputViewDescription
		{
			FourCC = 0,
			Dimension = D3D11.VpivDimension.Texture2D,
			Texture2D = new D3D11.Texture2DVpiv { MipSlice = 0, ArraySlice = subresourceIndex }
		};
		_videoDevice.CreateVideoProcessorInputView(inputTexture, _enumerator, inputViewDesc, out D3D11.VideoProcessorInputView inputView);
		using (inputView)
		{
			var rect = new RawRectangle(0, 0, width, height);
			_videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, D3D11.VideoFrameFormat.Progressive);
			_videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, rect);
			_videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, rect);
			_videoContext.VideoProcessorSetOutputTargetRect(_processor, true, rect);

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
			_videoContext.VideoProcessorBlt(_processor, _outputView, 0, streams.Length, streams);
		}

		_d3d11Device.ImmediateContext.Flush();
		if (!ImageSource.IsFrontBufferAvailable)
		{
			if (!_loggedFrontBufferUnavailable)
			{
				_loggedFrontBufferUnavailable = true;
				StatusChanged?.Invoke("D3DImage front buffer is unavailable; skipping dirty rect until WPF restores it.");
			}
			return;
		}

		ImageSource.Lock();
		try
		{
			ImageSource.AddDirtyRect(new Int32Rect(0, 0, width, height));
		}
		finally
		{
			ImageSource.Unlock();
		}
	}

	private void OnFrontBufferAvailableChanged(object? sender, DependencyPropertyChangedEventArgs e)
	{
		if (!ImageSource.IsFrontBufferAvailable)
		{
			_loggedFrontBufferUnavailable = false;
			StatusChanged?.Invoke("D3DImage front buffer became unavailable.");
			return;
		}

		_loggedFrontBufferUnavailable = false;
		if (_d3d9Surface == null || _width <= 0 || _height <= 0)
		{
			StatusChanged?.Invoke("D3DImage front buffer restored before the D3D9 surface was ready.");
			return;
		}

		ImageSource.Lock();
		try
		{
			ImageSource.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3d9Surface.NativePointer);
			ImageSource.AddDirtyRect(new Int32Rect(0, 0, _width, _height));
		}
		finally
		{
			ImageSource.Unlock();
		}
		StatusChanged?.Invoke("D3DImage front buffer restored; D3D9 surface reattached.");
	}

	private void EnsurePipeline(int width, int height, int fps)
	{
		if (_width == width && _height == height && _fps == fps && _processor != null && _outputView != null && _d3d9Surface != null)
		{
			return;
		}

		DisposePipeline();
		_width = width;
		_height = height;
		_fps = fps;

		var content = new D3D11.VideoProcessorContentDescription
		{
			InputFrameFormat = D3D11.VideoFrameFormat.Progressive,
			InputFrameRate = new DXGI.Rational(fps, 1),
			InputWidth = width,
			InputHeight = height,
			OutputFrameRate = new DXGI.Rational(fps, 1),
			OutputWidth = width,
			OutputHeight = height,
			Usage = D3D11.VideoUsage.PlaybackNormal
		};

		_videoDevice.CreateVideoProcessorEnumerator(ref content, out _enumerator);
		ValidateFormatSupport(_enumerator);
		_videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor);

		_outputTexture = new D3D11.Texture2D(_d3d11Device, new D3D11.Texture2DDescription
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

		var outputViewDesc = new D3D11.VideoProcessorOutputViewDescription
		{
			Dimension = D3D11.VpovDimension.Texture2D,
			Texture2D = new D3D11.Texture2DVpov { MipSlice = 0 }
		};
		_videoDevice.CreateVideoProcessorOutputView(_outputTexture, _enumerator, outputViewDesc, out _outputView);
		AttachOutputToD3DImage(width, height);
	}

	private static void ValidateFormatSupport(D3D11.VideoProcessorEnumerator enumerator)
	{
		enumerator.CheckVideoProcessorFormat(DXGI.Format.NV12, out int nv12Flags);
		enumerator.CheckVideoProcessorFormat(DXGI.Format.B8G8R8A8_UNorm, out int bgraFlags);
		bool nv12Input = (nv12Flags & (int)D3D11.VideoProcessorFormatSupport.Input) != 0;
		bool bgraOutput = (bgraFlags & (int)D3D11.VideoProcessorFormatSupport.Output) != 0;
		if (!nv12Input || !bgraOutput)
		{
			throw new InvalidOperationException($"D3D11 video processor format support is insufficient. nv12Input={nv12Input}, bgraOutput={bgraOutput}");
		}
	}

	private void AttachOutputToD3DImage(int width, int height)
	{
		if (_outputTexture == null)
		{
			throw new InvalidOperationException("D3D11 output texture is not initialized.");
		}

		using var dxgiResource = _outputTexture.QueryInterface<DXGI.Resource>();
		IntPtr sharedHandle = dxgiResource.SharedHandle;
		if (sharedHandle == IntPtr.Zero)
		{
			throw new InvalidOperationException("D3D11 output texture did not expose a shared handle.");
		}

		IntPtr d3d9SharedHandle = sharedHandle;
		_d3d9Texture = new D3D9.Texture(
			_d3d9Device,
			width,
			height,
			1,
			D3D9.Usage.RenderTarget,
			D3D9.Format.A8R8G8B8,
			D3D9.Pool.Default,
			ref d3d9SharedHandle);
		_d3d9Surface = _d3d9Texture.GetSurfaceLevel(0);

		if (!ImageSource.IsFrontBufferAvailable)
		{
			StatusChanged?.Invoke("D3DImage front buffer is unavailable while attaching D3D9 surface.");
			return;
		}

		ImageSource.Lock();
		try
		{
			ImageSource.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3d9Surface.NativePointer);
		}
		finally
		{
			ImageSource.Unlock();
		}
	}

	private void DisposePipeline()
	{
		if (ImageSource.IsFrontBufferAvailable)
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
		}

		_d3d9Surface?.Dispose();
		_d3d9Texture?.Dispose();
		_outputView?.Dispose();
		_outputTexture?.Dispose();
		_processor?.Dispose();
		_enumerator?.Dispose();
		_d3d9Surface = null;
		_d3d9Texture = null;
		_outputView = null;
		_outputTexture = null;
		_processor = null;
		_enumerator = null;
	}

	public void Dispose()
	{
		ImageSource.IsFrontBufferAvailableChanged -= OnFrontBufferAvailableChanged;
		DisposePipeline();
		_d3d9Device.Dispose();
		_d3d9.Dispose();
		_videoContext.Dispose();
		_videoDevice.Dispose();
		_multithread?.Dispose();
		_d3d11Device.Dispose();
	}
}
#endif
