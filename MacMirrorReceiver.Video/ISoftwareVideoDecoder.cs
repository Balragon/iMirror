using System;

namespace MacMirrorReceiver.Video;

/// <summary>
/// The software (FFmpeg) video decoder as the receiver's engine host sees it. Mirrors
/// <see cref="IHighResolutionD3DDecoder"/> for the CPU path so the decoder lifecycle can be
/// driven, and its GPU-fault fallback tested, without spawning ffmpeg.
/// </summary>
public interface ISoftwareVideoDecoder : IDisposable
{
	int OutputWidth { get; }

	int OutputHeight { get; }

	int OutputFrameBytes { get; }

	int QueuedInputPackets { get; }

	long QueuedInputBytes { get; }

	long DroppedInputPackets { get; }

	long AcceptedInputPackets { get; }

	long WrittenInputPackets { get; }

	long LatestWriteMilliseconds { get; }

	long MaxWriteMilliseconds { get; }

	long WriteStalls { get; }

	int PipelineDepthFrames { get; }

	bool DumpH264Enabled { get; set; }

	event Action<string>? StatusChanged;

	event Action<VideoFrame>? FrameDecoded;

	event Action? DecoderRestarted;

	void Start();

	bool QueueH264(byte[] payload, ulong sourceTimestampNanos, long receivedTick);

	int ClearPendingInput();
}
