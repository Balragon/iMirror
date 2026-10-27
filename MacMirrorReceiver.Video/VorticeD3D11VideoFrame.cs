using System;
using System.Runtime.InteropServices;
using D3D11 = Vortice.Direct3D11;

namespace MacMirrorReceiver.Video;

public sealed class VorticeD3D11VideoFrame : IHighResolutionD3DFrame
{
	private bool _disposed;

	public required int Width { get; init; }

	public required int Height { get; init; }

	public required int Fps { get; init; }

	public required D3D11.ID3D11Texture2D Texture { get; init; }

	public required int SubresourceIndex { get; init; }

	// An owned (AddRef'd) IUnknown of the decoder's IMFSample. The MFT's sample allocator recycles a
	// texture-array slice when the *sample* is released, so holding only the texture lets the next
	// decode overwrite this slice before it is presented. Released after the texture in Dispose.
	public IntPtr Sample { get; init; }

	public long ReceivedTick { get; init; }

	public long DecodedTick { get; init; }

	public ulong SourceTimestampNanos { get; init; }

	public void Dispose()
	{
		if (_disposed)
		{
			return;
		}
		_disposed = true;
		Texture.Dispose();
		if (Sample != IntPtr.Zero)
		{
			Marshal.Release(Sample);
		}
	}
}
