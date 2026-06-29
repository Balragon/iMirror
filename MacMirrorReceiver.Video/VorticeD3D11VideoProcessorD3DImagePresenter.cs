#if HIGH_RESOLUTION_D3D
using System;
using System.Windows;
using System.Windows.Interop;
using Vortice;
using D3D9 = Vortice.Direct3D9;
using D3D11 = Vortice.Direct3D11;
using DXGI = Vortice.DXGI;

namespace MacMirrorReceiver.Video;

public sealed class VorticeD3D11VideoProcessorD3DImagePresenter : IDisposable
{
	private readonly IntPtr _windowHandle;
	private readonly D3D11.ID3D11Device _d3d11Device;
	private readonly D3D11.ID3D11DeviceContext _d3d11Context;
	private readonly D3D11.ID3D11VideoDevice _videoDevice;
	private readonly D3D11.ID3D11VideoContext _videoContext;
	private readonly D3D9.IDirect3D9Ex _d3d9;
	private readonly D3D9.IDirect3DDevice9Ex _d3d9Device;
	private D3D11.ID3D11Multithread? _multithread;

	private D3D11.ID3D11VideoProcessorEnumerator? _enumerator;
	private D3D11.ID3D11VideoProcessor? _processor;
	private D3D11.ID3D11Texture2D? _outputTexture;
	private D3D11.ID3D11VideoProcessorOutputView? _outputView;
	private D3D9.IDirect3DTexture9? _d3d9Texture;
	private D3D9.IDirect3DSurface9? _d3d9Surface;
	// Present surface size is decoupled from the decode size: WPF compositing a D3DImage scales
	// poorly with surface area (a 2560x1440 D3DImage composited at only ~7fps), so the GPU video
	// processor downscales the decoded native frame to <= PresentMaxWidth for a lighter WPF surface
	// while decode stays native (Point A unaffected - the Mac still sends native).
	private const int PresentMaxWidth = 1920;

	private int _inputWidth;
	private int _inputHeight;
	private int _outputWidth;
	private int _outputHeight;
	private int _fps;
	private bool _loggedInvalidInputSubresource;
	private bool _loggedFrontBufferUnavailable;

	public D3DImage ImageSource { get; } = new D3DImage();

	public D3D11.ID3D11Device Device => _d3d11Device;

	public int Width => _inputWidth;

	public int Height => _inputHeight;

	public bool IsMultithreadProtected { get; private set; }

	public event Action<string>? StatusChanged;

	public void PresentFrame(IHighResolutionD3DFrame frame)
	{
		if (frame is not VorticeD3D11VideoFrame d3dFrame)
		{
			throw new ArgumentException("Vortice D3DImage presenter requires a Vortice D3D11 video frame.", nameof(frame));
		}

		PresentNv12Texture(d3dFrame.Texture, d3dFrame.SubresourceIndex, d3dFrame.Width, d3dFrame.Height, d3dFrame.Fps);
	}

	public VorticeD3D11VideoProcessorD3DImagePresenter(IntPtr windowHandle)
	{
		_windowHandle = windowHandle;
		_d3d11Device = D3D11.D3D11.D3D11CreateDevice(
			Vortice.Direct3D.DriverType.Hardware,
			D3D11.DeviceCreationFlags.BgraSupport | D3D11.DeviceCreationFlags.VideoSupport);
		EnableD3D11MultithreadProtection();
		_videoDevice = _d3d11Device.QueryInterface<D3D11.ID3D11VideoDevice>();
		_d3d11Context = _d3d11Device.ImmediateContext;
		_videoContext = _d3d11Context.QueryInterface<D3D11.ID3D11VideoContext>();

		_d3d9 = D3D9.D3D9.Direct3DCreate9Ex();
		var present = new D3D9.PresentParameters
		{
			Windowed = true,
			SwapEffect = D3D9.SwapEffect.Discard,
			DeviceWindowHandle = _windowHandle,
			PresentationInterval = D3D9.PresentInterval.Immediate
		};
		_d3d9Device = _d3d9.CreateDeviceEx(
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
			_multithread = _d3d11Device.QueryInterface<D3D11.ID3D11Multithread>();
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

	public void PresentNv12Texture(D3D11.ID3D11Texture2D inputTexture, int subresourceIndex, int width, int height, int fps)
	{
		(int outputWidth, int outputHeight) = ComputePresentSize(width, height);
		EnsurePipeline(width, height, outputWidth, outputHeight, fps);
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
			ViewDimension = D3D11.VideoProcessorInputViewDimension.Texture2D,
			Texture2D = new D3D11.Texture2DVideoProcessorInputView { MipSlice = 0u, ArraySlice = (uint)subresourceIndex }
		};
		_videoDevice.CreateVideoProcessorInputView(inputTexture, _enumerator, inputViewDesc, out D3D11.ID3D11VideoProcessorInputView inputView);
		using (inputView)
		{
			var sourceRect = new RawRect(0, 0, width, height);
			var outputRect = new RawRect(0, 0, outputWidth, outputHeight);
			_videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, D3D11.VideoFrameFormat.Progressive);
			_videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, sourceRect);
			_videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, outputRect);
			_videoContext.VideoProcessorSetOutputTargetRect(_processor, true, outputRect);

			var streams = new[]
			{
				new D3D11.VideoProcessorStream
				{
					Enable = true,
					OutputIndex = 0,
					InputFrameOrField = 0,
					PastFrames = 0,
					FutureFrames = 0,
					InputSurface = inputView
				}
			};
			_videoContext.VideoProcessorBlt(_processor, _outputView, 0, (uint)streams.Length, streams);
		}

		_d3d11Context.Flush();
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
			ImageSource.AddDirtyRect(new Int32Rect(0, 0, outputWidth, outputHeight));
		}
		finally
		{
			ImageSource.Unlock();
		}
	}

	private static (int Width, int Height) ComputePresentSize(int inputWidth, int inputHeight)
	{
		if (inputWidth <= 0 || inputHeight <= 0 || inputWidth <= PresentMaxWidth)
		{
			return (inputWidth, inputHeight);
		}

		int outputWidth = PresentMaxWidth;
		int outputHeight = (int)Math.Round((double)inputHeight * PresentMaxWidth / inputWidth);
		outputHeight = Math.Max(2, outputHeight / 2 * 2);
		return (outputWidth, outputHeight);
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
		if (_d3d9Surface == null || _outputWidth <= 0 || _outputHeight <= 0)
		{
			StatusChanged?.Invoke("D3DImage front buffer restored before the D3D9 surface was ready.");
			return;
		}

		ImageSource.Lock();
		try
		{
			ImageSource.SetBackBuffer(D3DResourceType.IDirect3DSurface9, _d3d9Surface.NativePointer);
			ImageSource.AddDirtyRect(new Int32Rect(0, 0, _outputWidth, _outputHeight));
		}
		finally
		{
			ImageSource.Unlock();
		}
		StatusChanged?.Invoke("D3DImage front buffer restored; D3D9 surface reattached.");
	}

	private void EnsurePipeline(int inputWidth, int inputHeight, int outputWidth, int outputHeight, int fps)
	{
		if (_inputWidth == inputWidth && _inputHeight == inputHeight
			&& _outputWidth == outputWidth && _outputHeight == outputHeight
			&& _fps == fps && _processor != null && _outputView != null && _d3d9Surface != null)
		{
			return;
		}

		DisposePipeline();
		_inputWidth = inputWidth;
		_inputHeight = inputHeight;
		_outputWidth = outputWidth;
		_outputHeight = outputHeight;
		_fps = fps;

		var content = new D3D11.VideoProcessorContentDescription
		{
			InputFrameFormat = D3D11.VideoFrameFormat.Progressive,
			InputFrameRate = new DXGI.Rational((uint)fps, 1u),
			InputWidth = (uint)inputWidth,
			InputHeight = (uint)inputHeight,
			OutputFrameRate = new DXGI.Rational((uint)fps, 1u),
			OutputWidth = (uint)outputWidth,
			OutputHeight = (uint)outputHeight,
			Usage = D3D11.VideoUsage.PlaybackNormal
		};

		_videoDevice.CreateVideoProcessorEnumerator(ref content, out _enumerator);
		ValidateFormatSupport(_enumerator);
		_videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor);
		D3D11VideoProcessorColorSpace.Apply(_videoContext, _processor, StatusChanged);

		_outputTexture = _d3d11Device.CreateTexture2D(new D3D11.Texture2DDescription
		{
			Width = (uint)outputWidth,
			Height = (uint)outputHeight,
			MipLevels = 1u,
			ArraySize = 1u,
			Format = DXGI.Format.B8G8R8A8_UNorm,
			SampleDescription = new DXGI.SampleDescription(1, 0),
			Usage = D3D11.ResourceUsage.Default,
			BindFlags = D3D11.BindFlags.RenderTarget | D3D11.BindFlags.ShaderResource,
			CPUAccessFlags = D3D11.CpuAccessFlags.None,
			MiscFlags = D3D11.ResourceOptionFlags.Shared
		});

		var outputViewDesc = new D3D11.VideoProcessorOutputViewDescription
		{
			ViewDimension = D3D11.VideoProcessorOutputViewDimension.Texture2D,
			Texture2D = new D3D11.Texture2DVideoProcessorOutputView { MipSlice = 0u }
		};
		_videoDevice.CreateVideoProcessorOutputView(_outputTexture, _enumerator, outputViewDesc, out _outputView);
		AttachOutputToD3DImage(outputWidth, outputHeight);
	}

	private static void ValidateFormatSupport(D3D11.ID3D11VideoProcessorEnumerator enumerator)
	{
		enumerator.CheckVideoProcessorFormat(DXGI.Format.NV12, out D3D11.VideoProcessorFormatSupport nv12Flags);
		enumerator.CheckVideoProcessorFormat(DXGI.Format.B8G8R8A8_UNorm, out D3D11.VideoProcessorFormatSupport bgraFlags);
		bool nv12Input = (nv12Flags & D3D11.VideoProcessorFormatSupport.Input) != 0;
		bool bgraOutput = (bgraFlags & D3D11.VideoProcessorFormatSupport.Output) != 0;
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

		using var dxgiResource = _outputTexture.QueryInterface<DXGI.IDXGIResource>();
		IntPtr sharedHandle = dxgiResource.SharedHandle;
		if (sharedHandle == IntPtr.Zero)
		{
			throw new InvalidOperationException("D3D11 output texture did not expose a shared handle.");
		}

		IntPtr d3d9SharedHandle = sharedHandle;
		_d3d9Texture = _d3d9Device.CreateTexture(
			(uint)width,
			(uint)height,
			1u,
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
		_d3d11Context.Dispose();
		_d3d11Device.Dispose();
	}
}
#endif
