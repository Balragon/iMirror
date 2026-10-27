using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MacMirrorReceiver.Video;

internal enum PipeEnqueueOutcome
{
	Enqueued,
	FlushedAndEnqueued,
	DroppedIncoming
}

internal readonly record struct PipeEnqueueResult(PipeEnqueueOutcome Outcome, int Flushed, int QueuedAfter);

// Bounded FIFO between the receive path and an ffmpeg stdin writer, capped by item count and
// total payload bytes. What happens on overflow is the caller's policy: flush everything queued
// and keep the incoming item, or drop the incoming item and keep the queue. Every item lost
// either way, or removed by Clear, is counted in Dropped.
internal sealed class PipeInputQueue<T> : IDisposable where T : class
{
	private readonly object _gate = new object();
	private readonly Queue<T> _items = new Queue<T>();
	private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
	private readonly int _maxItems;
	private readonly long _maxBytes;
	private readonly Func<T, int> _sizeOf;
	private long _bytes;
	private long _accepted;
	private long _dropped;

	public PipeInputQueue(int maxItems, long maxBytes, Func<T, int> sizeOf)
	{
		_maxItems = maxItems;
		_maxBytes = maxBytes;
		_sizeOf = sizeOf;
	}

	public int Count
	{
		get
		{
			lock (_gate)
			{
				return _items.Count;
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

	public long Accepted => Interlocked.Read(ref _accepted);

	public long Dropped => Interlocked.Read(ref _dropped);

	// flushOnOverflow is only consulted when the queue is full: true flushes the queue and keeps
	// the incoming item, false drops the incoming item.
	public PipeEnqueueResult Enqueue(T item, Func<T, bool> flushOnOverflow)
	{
		int size = _sizeOf(item);
		bool shouldSignal;
		int flushed = 0;
		int queuedAfter;
		lock (_gate)
		{
			if (_items.Count >= _maxItems || _bytes + size > _maxBytes)
			{
				if (!flushOnOverflow(item))
				{
					Interlocked.Increment(ref _dropped);
					return new PipeEnqueueResult(PipeEnqueueOutcome.DroppedIncoming, 0, _items.Count);
				}
				flushed = ClearLocked();
			}

			shouldSignal = _items.Count == 0;
			_items.Enqueue(item);
			_bytes += size;
			Interlocked.Increment(ref _accepted);
			queuedAfter = _items.Count;
		}

		if (shouldSignal)
		{
			_signal.Release();
		}
		PipeEnqueueOutcome outcome = flushed > 0 ? PipeEnqueueOutcome.FlushedAndEnqueued : PipeEnqueueOutcome.Enqueued;
		return new PipeEnqueueResult(outcome, flushed, queuedAfter);
	}

	public bool TryDequeue(out T? item)
	{
		lock (_gate)
		{
			if (_items.Count == 0)
			{
				item = null;
				return false;
			}
			item = _items.Dequeue();
			_bytes = Math.Max(0L, _bytes - _sizeOf(item));
			return true;
		}
	}

	public Task WaitAsync(CancellationToken cancellationToken)
	{
		return _signal.WaitAsync(cancellationToken);
	}

	public int Clear()
	{
		lock (_gate)
		{
			return ClearLocked();
		}
	}

	private int ClearLocked()
	{
		int count = _items.Count;
		_items.Clear();
		_bytes = 0L;
		Interlocked.Add(ref _dropped, count);
		return count;
	}

	public void Dispose()
	{
		_signal.Dispose();
	}
}
