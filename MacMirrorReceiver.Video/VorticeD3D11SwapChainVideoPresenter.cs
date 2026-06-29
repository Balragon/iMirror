#if HIGH_RESOLUTION_D3D
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Vortice;
using D3D11 = Vortice.Direct3D11;
using DXGI = Vortice.DXGI;

namespace MacMirrorReceiver.Video;

// Presents GPU-decoded NV12 textures via a DXGI flip-model swap chain on a child HWND hosted in WPF
// (HwndHost), bypassing WPF's D3DImage composition. Measured: D3DImage composition capped the
// present at ~10fps regardless of surface size; a swap chain flips directly to the window at the
// display refresh rate with minimal latency. The video processor converts NV12 -> BGRA and scales
// the decoded native frame to the window directly into the swap chain back buffer.
public sealed class VorticeD3D11SwapChainVideoPresenter : HwndHost, IHighResolutionD3DPresenter
{
	private const int WS_CHILD = 0x40000000;
	private const int WS_VISIBLE = 0x10000000;
	private const int WS_CLIPSIBLINGS = 0x04000000;
	private const int WS_CLIPCHILDREN = 0x02000000;

	private readonly object _gate = new object();
	private readonly D3D11.ID3D11Device _d3d11Device;
	private readonly D3D11.ID3D11DeviceContext _d3d11Context;
	private readonly D3D11.ID3D11VideoDevice _videoDevice;
	private readonly D3D11.ID3D11VideoContext _videoContext;
	private D3D11.ID3D11Multithread? _multithread;

	private IntPtr _childWindow;
	private DXGI.IDXGISwapChain1? _swapChain;
	private D3D11.ID3D11VideoProcessorEnumerator? _enumerator;
	private D3D11.ID3D11VideoProcessor? _processor;
	private int _inputWidth;
	private int _inputHeight;
	private int _swapWidth;
	private int _swapHeight;
	private int _fps;
	private bool _loggedInvalidInputSubresource;
	private bool _disposed;

	public VorticeD3D11SwapChainVideoPresenter()
	{
		_d3d11Device = D3D11.D3D11.D3D11CreateDevice(
			Vortice.Direct3D.DriverType.Hardware,
			D3D11.DeviceCreationFlags.BgraSupport | D3D11.DeviceCreationFlags.VideoSupport);
		EnableMultithreadProtection();
		_videoDevice = _d3d11Device.QueryInterface<D3D11.ID3D11VideoDevice>();
		_d3d11Context = _d3d11Device.ImmediateContext;
		_videoContext = _d3d11Context.QueryInterface<D3D11.ID3D11VideoContext>();
	}

	public D3D11.ID3D11Device Device => _d3d11Device;

	public FrameworkElement View => this;

	public bool IsMultithreadProtected { get; private set; }

	public event Action<string>? StatusChanged;

	public void PresentFrame(IHighResolutionD3DFrame frame)
	{
		if (frame is not VorticeD3D11VideoFrame d3dFrame)
		{
			throw new ArgumentException("Vortice presenter requires a Vortice D3D11 video frame.", nameof(frame));
		}

		PresentNv12Texture(d3dFrame.Texture, d3dFrame.SubresourceIndex, d3dFrame.Width, d3dFrame.Height, d3dFrame.Fps);
	}

	private void EnableMultithreadProtection()
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
		lock (_gate)
		{
			if (_disposed || _childWindow == IntPtr.Zero)
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
						$"D3D11 swap-chain presenter rejected input texture subresourceIndex={subresourceIndex}, arraySize={inputDescription.ArraySize}.");
				}
				return;
			}

			GetClientSize(_childWindow, out int targetWidth, out int targetHeight);
			if (targetWidth <= 0 || targetHeight <= 0)
			{
				return;
			}

			EnsureSwapChain(targetWidth, targetHeight);
			EnsureVideoProcessor(width, height, targetWidth, targetHeight, fps);
			if (_swapChain == null || _processor == null || _enumerator == null)
			{
				return;
			}

			using D3D11.ID3D11Texture2D backBuffer = _swapChain.GetBuffer<D3D11.ID3D11Texture2D>(0);
			_videoDevice.CreateVideoProcessorOutputView(
				backBuffer,
				_enumerator,
				new D3D11.VideoProcessorOutputViewDescription
				{
					ViewDimension = D3D11.VideoProcessorOutputViewDimension.Texture2D,
					Texture2D = new D3D11.Texture2DVideoProcessorOutputView { MipSlice = 0u }
				},
				out D3D11.ID3D11VideoProcessorOutputView outputView);
			using (outputView)
			{
				_videoDevice.CreateVideoProcessorInputView(
					inputTexture,
					_enumerator,
					new D3D11.VideoProcessorInputViewDescription
					{
						FourCC = 0,
						ViewDimension = D3D11.VideoProcessorInputViewDimension.Texture2D,
						Texture2D = new D3D11.Texture2DVideoProcessorInputView { MipSlice = 0u, ArraySlice = (uint)subresourceIndex }
					},
					out D3D11.ID3D11VideoProcessorInputView inputView);
				using (inputView)
				{
					var sourceRect = new RawRect(0, 0, width, height);
					// Stretch-fill the child window (clean, no per-frame clear, no resize artifacts).
					// Aspect ratio is preserved by WPF sizing the host window to the source aspect; the
					// letterbox/pillarbox bars are the (black) WPF background around the host window.
					var destRect = new RawRect(0, 0, _swapWidth, _swapHeight);
					_videoContext.VideoProcessorSetStreamFrameFormat(_processor, 0, D3D11.VideoFrameFormat.Progressive);
					_videoContext.VideoProcessorSetStreamSourceRect(_processor, 0, true, sourceRect);
					_videoContext.VideoProcessorSetStreamDestRect(_processor, 0, true, destRect);
					_videoContext.VideoProcessorSetOutputTargetRect(_processor, true, destRect);

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
					_videoContext.VideoProcessorBlt(_processor, outputView, 0, (uint)streams.Length, streams);
				}
			}

			// Present(1) flips at the display refresh; flip-model + discard gives minimal latency.
			_swapChain.Present(1, DXGI.PresentFlags.None);
		}
	}

	private void EnsureSwapChain(int width, int height)
	{
		if (_swapChain != null && _swapWidth == width && _swapHeight == height)
		{
			return;
		}

		if (_swapChain == null)
		{
			var desc = new DXGI.SwapChainDescription1
			{
				Width = (uint)width,
				Height = (uint)height,
				Format = DXGI.Format.B8G8R8A8_UNorm,
				Stereo = false,
				SampleDescription = new DXGI.SampleDescription(1, 0),
				BufferUsage = DXGI.Usage.RenderTargetOutput,
				BufferCount = 2u,
				Scaling = DXGI.Scaling.Stretch,
				SwapEffect = DXGI.SwapEffect.FlipDiscard,
				AlphaMode = DXGI.AlphaMode.Ignore,
				Flags = DXGI.SwapChainFlags.None
			};
			using var dxgiDevice = _d3d11Device.QueryInterface<DXGI.IDXGIDevice>();
			using DXGI.IDXGIAdapter adapter = dxgiDevice.GetAdapter();
			using var factory = adapter.GetParent<DXGI.IDXGIFactory2>();
			_swapChain = factory.CreateSwapChainForHwnd(_d3d11Device, _childWindow, desc);
			_swapWidth = width;
			_swapHeight = height;
			StatusChanged?.Invoke($"D3D11 swap-chain created on child window: {width}x{height}, flip-discard, multithreadProtected={IsMultithreadProtected}.");
			return;
		}

		// Resize: the video-processor output target depends on the swap size, so drop it too.
		DisposeVideoProcessor();
		_swapChain.ResizeBuffers(2u, (uint)width, (uint)height, DXGI.Format.B8G8R8A8_UNorm, DXGI.SwapChainFlags.None);
		_swapWidth = width;
		_swapHeight = height;
	}

	private void EnsureVideoProcessor(int inputWidth, int inputHeight, int outputWidth, int outputHeight, int fps)
	{
		if (_processor != null && _enumerator != null
			&& _inputWidth == inputWidth && _inputHeight == inputHeight
			&& _swapWidth == outputWidth && _swapHeight == outputHeight && _fps == fps)
		{
			return;
		}

		DisposeVideoProcessor();
		_inputWidth = inputWidth;
		_inputHeight = inputHeight;
		_fps = Math.Max(1, fps);

		var content = new D3D11.VideoProcessorContentDescription
		{
			InputFrameFormat = D3D11.VideoFrameFormat.Progressive,
			InputFrameRate = new DXGI.Rational((uint)_fps, 1u),
			InputWidth = (uint)inputWidth,
			InputHeight = (uint)inputHeight,
			OutputFrameRate = new DXGI.Rational((uint)_fps, 1u),
			OutputWidth = (uint)outputWidth,
			OutputHeight = (uint)outputHeight,
			Usage = D3D11.VideoUsage.PlaybackNormal
		};
		_videoDevice.CreateVideoProcessorEnumerator(ref content, out _enumerator);
		ValidateFormatSupport(_enumerator);
		_videoDevice.CreateVideoProcessor(_enumerator, 0, out _processor);
		D3D11VideoProcessorColorSpace.Apply(_videoContext, _processor, StatusChanged);
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

	protected override HandleRef BuildWindowCore(HandleRef hwndParent)
	{
		_childWindow = CreateWindowEx(
			0,
			"static",
			string.Empty,
			WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN,
			0,
			0,
			Math.Max(1, (int)Math.Round(ActualWidth)),
			Math.Max(1, (int)Math.Round(ActualHeight)),
			hwndParent.Handle,
			IntPtr.Zero,
			IntPtr.Zero,
			IntPtr.Zero);
		if (_childWindow == IntPtr.Zero)
		{
			throw new InvalidOperationException("Could not create D3D11 swap-chain host window.");
		}
		return new HandleRef(this, _childWindow);
	}

	protected override void DestroyWindowCore(HandleRef hwnd)
	{
		lock (_gate)
		{
			DisposeVideoProcessor();
			_swapChain?.Dispose();
			_swapChain = null;
			_swapWidth = 0;
			_swapHeight = 0;
		}
		if (hwnd.Handle != IntPtr.Zero)
		{
			DestroyWindow(hwnd.Handle);
		}
		_childWindow = IntPtr.Zero;
	}

	protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
	{
		base.OnRenderSizeChanged(sizeInfo);
		if (_childWindow != IntPtr.Zero)
		{
			MoveWindow(
				_childWindow,
				0,
				0,
				Math.Max(1, (int)Math.Round(ActualWidth)),
				Math.Max(1, (int)Math.Round(ActualHeight)),
				true);
		}
	}

	private void DisposeVideoProcessor()
	{
		_processor?.Dispose();
		_enumerator?.Dispose();
		_processor = null;
		_enumerator = null;
		_inputWidth = 0;
		_inputHeight = 0;
	}

	protected override void Dispose(bool disposing)
	{
		if (disposing)
		{
			lock (_gate)
			{
				_disposed = true;
				DisposeVideoProcessor();
				_swapChain?.Dispose();
				_swapChain = null;
				_videoContext.Dispose();
				_videoDevice.Dispose();
				_multithread?.Dispose();
				_d3d11Context.Dispose();
				_d3d11Device.Dispose();
			}
		}
		base.Dispose(disposing);
	}

	private static void GetClientSize(IntPtr hwnd, out int width, out int height)
	{
		if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out RECT rect))
		{
			width = 0;
			height = 0;
			return;
		}
		width = Math.Max(0, rect.Right - rect.Left);
		height = Math.Max(0, rect.Bottom - rect.Top);
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct RECT
	{
		public int Left;
		public int Top;
		public int Right;
		public int Bottom;
	}

	[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr CreateWindowEx(
		int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
		int x, int y, int nWidth, int nHeight,
		IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool DestroyWindow(IntPtr hwnd);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);

	[DllImport("user32.dll", SetLastError = true)]
	private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
}
#endif
