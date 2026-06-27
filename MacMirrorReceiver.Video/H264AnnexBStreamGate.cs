using System;
using System.Collections.Generic;

namespace MacMirrorReceiver.Video;

public sealed class H264AnnexBStreamGate
{
	private sealed class H264NalInfo
	{
		private readonly record struct NalRange(int Start, int End)
		{
			public int Length => End - Start;
		}

		private NalRange? _latestSpsRange;

		private NalRange? _latestPpsRange;

		public bool HasSps { get; private set; }

		public bool HasPps { get; private set; }

		public bool HasIdr { get; private set; }

		public List<int> NalTypes { get; } = new List<int>();


		public static H264NalInfo FromAnnexB(byte[] payload)
		{
			H264NalInfo h264NalInfo = new H264NalInfo();
			int offset = 0;
			int start;
			int length;
			while (TryFindStartCode(payload, offset, out start, out length))
			{
				int num = start + length;
				if (num >= payload.Length)
				{
					break;
				}
				int num2 = payload[num] & 0x1F;
				h264NalInfo.NalTypes.Add(num2);
				switch (num2)
				{
				case 7:
					h264NalInfo.HasSps = true;
					h264NalInfo._latestSpsRange = new NalRange(start, FindNalEnd(payload, num + 1));
					break;
				case 8:
					h264NalInfo.HasPps = true;
					h264NalInfo._latestPpsRange = new NalRange(start, FindNalEnd(payload, num + 1));
					break;
				case 5:
					h264NalInfo.HasIdr = true;
					break;
				}
				offset = num + 1;
			}
			return h264NalInfo;
		}

		public byte[] ExtractLatestSps(byte[] payload)
		{
			return ExtractRange(payload, _latestSpsRange);
		}

		public byte[] ExtractLatestPps(byte[] payload)
		{
			return ExtractRange(payload, _latestPpsRange);
		}

		private static byte[] ExtractRange(byte[] payload, NalRange? maybeRange)
		{
			if (!maybeRange.HasValue)
			{
				return Array.Empty<byte>();
			}
			NalRange range = maybeRange.Value;
			byte[] array = new byte[range.Length];
			Buffer.BlockCopy(payload, range.Start, array, 0, range.Length);
			return array;
		}

		private static bool TryFindStartCode(byte[] data, int offset, out int start, out int length)
		{
			for (int i = Math.Max(0, offset); i <= data.Length - 3; i++)
			{
				if (data[i] == 0 && data[i + 1] == 0)
				{
					if (data[i + 2] == 1)
					{
						start = i;
						length = 3;
						return true;
					}
					if (i <= data.Length - 4 && data[i + 2] == 0 && data[i + 3] == 1)
					{
						start = i;
						length = 4;
						return true;
					}
				}
			}
			start = -1;
			length = 0;
			return false;
		}

		private static int FindNalEnd(byte[] data, int offset)
		{
			if (TryFindStartCode(data, offset, out var start, out var _))
			{
				return start;
			}
			return data.Length;
		}
	}

	private bool _hasSps;

	private bool _hasPps;

	private bool _started;

	private byte[] _pendingSps = Array.Empty<byte>();

	private byte[] _pendingPps = Array.Empty<byte>();

	public bool IsStarted => _started;

	public long DroppedPackets { get; private set; }

	public long ForwardedPackets { get; private set; }

	public string LastDecision { get; private set; } = "waiting for SPS/PPS keyframe";


	public void RequireKeyframe()
	{
		_started = false;
		LastDecision = "decoder input skipped; waiting for next keyframe";
	}

	public byte[]? Process(byte[] payload)
	{
		H264NalInfo h264NalInfo = H264NalInfo.FromAnnexB(payload);
		if (h264NalInfo.HasSps || h264NalInfo.HasPps)
		{
			UpdatePendingParameterSets(payload, h264NalInfo);
			_hasSps |= h264NalInfo.HasSps;
			_hasPps |= h264NalInfo.HasPps;
		}
		if (!_started)
		{
			if (_hasSps && _hasPps && h264NalInfo.HasIdr)
			{
				_started = true;
				ForwardedPackets++;
				LastDecision = ((h264NalInfo.HasSps || h264NalInfo.HasPps) ? "found SPS/PPS keyframe" : "prepended buffered SPS/PPS to keyframe");
				if (!h264NalInfo.HasSps || !h264NalInfo.HasPps)
				{
					return Combine(PendingParameterSets(), payload);
				}
				return payload;
			}
			DroppedPackets++;
			LastDecision = ((h264NalInfo.NalTypes.Count == 0) ? "dropped packet without Annex B NAL units" : ("waiting for SPS/PPS keyframe; saw NAL " + string.Join(",", h264NalInfo.NalTypes)));
			return null;
		}
		ForwardedPackets++;
		LastDecision = ((h264NalInfo.NalTypes.Count == 0) ? "forwarded packet without parsed NAL marker" : ("forwarded NAL " + string.Join(",", h264NalInfo.NalTypes)));
		return payload;
	}

	public void Reset()
	{
		_hasSps = false;
		_hasPps = false;
		_started = false;
		_pendingSps = Array.Empty<byte>();
		_pendingPps = Array.Empty<byte>();
		DroppedPackets = 0L;
		ForwardedPackets = 0L;
		LastDecision = "waiting for SPS/PPS keyframe";
	}

	private void UpdatePendingParameterSets(byte[] payload, H264NalInfo info)
	{
		byte[] latestSps = info.ExtractLatestSps(payload);
		if (latestSps.Length > 0)
		{
			_pendingSps = latestSps;
		}
		byte[] latestPps = info.ExtractLatestPps(payload);
		if (latestPps.Length > 0)
		{
			_pendingPps = latestPps;
		}
	}

	private byte[] PendingParameterSets()
	{
		return Combine(_pendingSps, _pendingPps);
	}

	private static byte[] Combine(byte[] first, byte[] second)
	{
		if (first.Length == 0)
		{
			return second;
		}
		if (second.Length == 0)
		{
			return first;
		}
		byte[] array = new byte[first.Length + second.Length];
		Buffer.BlockCopy(first, 0, array, 0, first.Length);
		Buffer.BlockCopy(second, 0, array, first.Length, second.Length);
		return array;
	}
}
