#if HIGH_RESOLUTION_D3D
using System;
using D3D11 = SharpDX.Direct3D11;

namespace MacMirrorReceiver.Video;

public sealed class D3D11VideoFrame : IDisposable
{
	private bool _disposed;

	public required int Width { get; init; }

	public required int Height { get; init; }

	public required int Fps { get; init; }

	public required D3D11.Texture2D Texture { get; init; }

	public required int SubresourceIndex { get; init; }

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
	}
}
#endif
