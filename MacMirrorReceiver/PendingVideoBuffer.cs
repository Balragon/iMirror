using System;
using System.Collections.Generic;
using System.Threading;

namespace MacMirrorReceiver;

internal readonly record struct PendingVideoPayload(byte[] Payload, ulong SourceTimestampNanos, long ReceivedTick);

/// <summary>
/// Holds the H.264 packets that arrive before the decoder or render sink is ready (the initial
/// SPS/PPS/IDR burst while FFmpeg, Media Foundation or the WPF surface spins up) so they can be
/// replayed in order once it is. Bounded by packet count and by bytes; when either cap is
/// exceeded the oldest packets are dropped and counted.
/// </summary>
internal sealed class PendingVideoBuffer
{
	private readonly object _gate = new object();
	private readonly Queue<PendingVideoPayload> _queue = new Queue<PendingVideoPayload>();
	private readonly int _maxPackets;
	private readonly long _maxBytes;
	private long _bytes;
	private long _dropped;

	public PendingVideoBuffer(int maxPackets, long maxBytes)
	{
		if (maxPackets <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxPackets));
		}
		if (maxBytes <= 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxBytes));
		}
		_maxPackets = maxPackets;
		_maxBytes = maxBytes;
	}

	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _queue.Count;
			}
		}
	}

	public long Bytes
	{
		get
		{
			lock (_gate)
			{
				return _bytes;
			}
		}
	}

	/// <summary>Packets dropped since the last <see cref="ResetDroppedCount"/>.</summary>
	public long Dropped => Interlocked.Read(ref _dropped);

	/// <summary>Queues a packet. Returns how many older packets this call dropped to stay within the caps.</summary>
	public int Enqueue(PendingVideoPayload packet)
	{
		int droppedNow = 0;
		lock (_gate)
		{
			_queue.Enqueue(packet);
			_bytes += packet.Payload.Length;
			while (_queue.Count > _maxPackets || _bytes > _maxBytes)
			{
				PendingVideoPayload dropped = _queue.Dequeue();
				_bytes = Math.Max(0L, _bytes - dropped.Payload.Length);
				droppedNow++;
			}
		}

		if (droppedNow > 0)
		{
			Interlocked.Add(ref _dropped, droppedNow);
		}
		return droppedNow;
	}

	/// <summary>Removes and returns every queued packet in arrival order (empty when none).</summary>
	public PendingVideoPayload[] Drain()
	{
		lock (_gate)
		{
			if (_queue.Count == 0)
			{
				return Array.Empty<PendingVideoPayload>();
			}

			PendingVideoPayload[] pending = _queue.ToArray();
			_queue.Clear();
			_bytes = 0L;
			return pending;
		}
	}

	public void Clear()
	{
		lock (_gate)
		{
			_queue.Clear();
			_bytes = 0L;
		}
	}

	public void ResetDroppedCount()
	{
		Interlocked.Exchange(ref _dropped, 0L);
	}
}
