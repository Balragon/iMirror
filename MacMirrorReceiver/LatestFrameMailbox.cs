using System;
using System.Threading;
using MacMirrorReceiver.Video;

namespace MacMirrorReceiver;

/// <summary>
/// The single-slot hand-off between the decoder threads and the UI thread: the newest decoded
/// frame replaces any frame still waiting, so presentation never falls behind decoding by more
/// than one frame. One slot for software (<see cref="VideoFrame"/>) and one for GPU
/// (<see cref="IHighResolutionD3DFrame"/>) frames; at most one drain is scheduled at a time.
/// </summary>
internal sealed class LatestFrameMailbox
{
	private readonly object _gate = new object();
	private readonly Action _scheduleDrain;
	private VideoFrame? _pendingFrame;
	private IHighResolutionD3DFrame? _pendingD3DFrame;
	private int _drainQueued;

	/// <param name="scheduleDrain">Schedules <see cref="Drain"/> on the presenting thread; called at most once per pending drain.</param>
	public LatestFrameMailbox(Action scheduleDrain)
	{
		_scheduleDrain = scheduleDrain;
	}

	/// <summary>1 while a drain is scheduled and not yet run; the "dispatcherQueued" diagnostic.</summary>
	public int DrainQueued => Volatile.Read(ref _drainQueued);

	public int PendingCount
	{
		get
		{
			lock (_gate)
			{
				int count = _pendingFrame == null ? 0 : 1;
				if (_pendingD3DFrame != null)
				{
					count++;
				}
				return count;
			}
		}
	}

	/// <summary>Stores the frame as the latest. Returns true when an older pending frame was released to make room.</summary>
	public bool Offer(VideoFrame frame)
	{
		bool replaced;
		lock (_gate)
		{
			replaced = _pendingFrame != null;
			_pendingFrame?.Release();
			_pendingFrame = frame;
		}
		ScheduleDrainOnce();
		return replaced;
	}

	/// <summary>Stores the GPU frame as the latest. Returns true when an older pending frame was disposed to make room.</summary>
	public bool Offer(IHighResolutionD3DFrame frame)
	{
		bool replaced;
		lock (_gate)
		{
			replaced = _pendingD3DFrame != null;
			_pendingD3DFrame?.Dispose();
			_pendingD3DFrame = frame;
		}
		ScheduleDrainOnce();
		return replaced;
	}

	/// <summary>
	/// Presents whatever is pending (software first, then GPU), then re-schedules itself once
	/// if a frame arrived while presenting. Runs on the presenting thread.
	/// </summary>
	public void Drain(Action<VideoFrame> present, Action<IHighResolutionD3DFrame> presentD3D)
	{
		VideoFrame? pendingFrame;
		IHighResolutionD3DFrame? pendingD3DFrame;
		lock (_gate)
		{
			pendingFrame = _pendingFrame;
			_pendingFrame = null;
			pendingD3DFrame = _pendingD3DFrame;
			_pendingD3DFrame = null;
		}
		if (pendingFrame != null)
		{
			present(pendingFrame);
		}
		if (pendingD3DFrame != null)
		{
			presentD3D(pendingD3DFrame);
		}
		Interlocked.Exchange(ref _drainQueued, 0);
		lock (_gate)
		{
			if (_pendingFrame == null && _pendingD3DFrame == null)
			{
				return;
			}
		}
		ScheduleDrainOnce();
	}

	public void ReleasePendingSoftwareFrame()
	{
		lock (_gate)
		{
			_pendingFrame?.Release();
			_pendingFrame = null;
		}
	}

	public void ReleasePendingD3DFrame()
	{
		lock (_gate)
		{
			_pendingD3DFrame?.Dispose();
			_pendingD3DFrame = null;
		}
	}

	private void ScheduleDrainOnce()
	{
		if (Interlocked.Exchange(ref _drainQueued, 1) == 0)
		{
			_scheduleDrain();
		}
	}
}
