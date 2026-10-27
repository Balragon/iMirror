using System;
using System.Runtime.InteropServices;
using MacMirrorReceiver.Video;
using Xunit;
using D3D11 = Vortice.Direct3D11;

namespace MacMirrorReceiver.Tests;

public sealed class VorticeD3D11VideoFrameTests
{
	[Fact]
	public void Dispose_ReleasesSampleReferenceExactlyOnce_EvenWhenDisposedTwice()
	{
		// A managed object's COM callable wrapper stands in for the IMFSample: its IUnknown
		// reference count is observable through AddRef/Release return values.
		object sampleStandIn = new object();
		IntPtr sample = Marshal.GetIUnknownForObject(sampleStandIn);
		Marshal.AddRef(sample);
		Marshal.AddRef(sample); // the reference the frame owns
		VorticeD3D11VideoFrame frame = CreateFrame(sample);

		frame.Dispose();
		frame.Dispose();

		// Two references left (the test's own). One fewer means a double release, one more a leak.
		Assert.Equal(1, Marshal.Release(sample));
		Assert.Equal(0, Marshal.Release(sample));
		GC.KeepAlive(sampleStandIn);
	}

	[Fact]
	public void Dispose_WithoutSample_DoesNotThrow()
	{
		VorticeD3D11VideoFrame frame = CreateFrame(IntPtr.Zero);

		frame.Dispose();
		frame.Dispose();
	}

	private static VorticeD3D11VideoFrame CreateFrame(IntPtr sample)
	{
		return new VorticeD3D11VideoFrame
		{
			Width = 2560,
			Height = 1440,
			Fps = 30,
			Texture = new D3D11.ID3D11Texture2D(IntPtr.Zero),
			SubresourceIndex = 0,
			Sample = sample
		};
	}
}
