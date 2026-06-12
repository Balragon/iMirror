using System;

namespace MacMirrorReceiver.Video;

public sealed class VideoFrame
{
	private bool _released;

	public required int Width { get; init; }

	public required int Height { get; init; }

	public required byte[] Buffer { get; init; }

	public long ReceivedTick { get; init; }

	public long DecodedTick { get; init; }

	public ulong SourceTimestampNanos { get; init; }

	public Action<byte[]>? ReturnBuffer { get; init; }

	public void Release()
	{
		if (_released)
		{
			return;
		}
		_released = true;
		ReturnBuffer?.Invoke(Buffer);
	}
}
