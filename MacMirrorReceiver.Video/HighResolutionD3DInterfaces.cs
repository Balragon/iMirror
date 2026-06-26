#if HIGH_RESOLUTION_D3D
using System;
using System.Windows;

namespace MacMirrorReceiver.Video;

public interface IHighResolutionD3DFrame : IDisposable
{
	int Width { get; }

	int Height { get; }

	int Fps { get; }

	int SubresourceIndex { get; }

	long ReceivedTick { get; }

	long DecodedTick { get; }

	ulong SourceTimestampNanos { get; }
}

public interface IHighResolutionD3DDecoder : IDisposable
{
	int OutputWidth { get; }

	int OutputHeight { get; }

	int OutputFrameBytes { get; }

	bool IsFaulted { get; }

	int QueuedInputPackets { get; }

	long QueuedInputBytes { get; }

	long DroppedInputPackets { get; }

	long AcceptedInputPackets { get; }

	long WrittenInputPackets { get; }

	long LatestWriteMilliseconds { get; }

	long MaxWriteMilliseconds { get; }

	long WriteStalls { get; }

	bool DumpH264Enabled { get; set; }

	event Action<string>? StatusChanged;

	event Action<IHighResolutionD3DFrame>? FrameDecoded;

	event Action<string>? Faulted;

	void Start();

	bool QueueH264(byte[] payload, ulong sourceTimestampNanos, long receivedTick);

	int ClearPendingInput();
}

public interface IHighResolutionD3DPresenter : IDisposable
{
	FrameworkElement View { get; }

	bool IsMultithreadProtected { get; }

	event Action<string>? StatusChanged;

	void PresentFrame(IHighResolutionD3DFrame frame);
}
#endif
