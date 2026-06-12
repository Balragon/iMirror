using System;
using System.Collections.Generic;
using System.Linq;

namespace MacMirrorReceiver.Video;

public sealed class H264AnnexBStreamGate
{
	private sealed class H264NalInfo
	{
		private readonly record struct NalRange(int Start, int End)
		{
			public int Length => End - Start;
		}

		private readonly List<NalRange> _parameterSetRanges = new List<NalRange>();

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
					h264NalInfo._parameterSetRanges.Add(new NalRange(start, FindNalEnd(payload, num + 1)));
					break;
				case 8:
					h264NalInfo.HasPps = true;
					h264NalInfo._parameterSetRanges.Add(new NalRange(start, FindNalEnd(payload, num + 1)));
					break;
				case 5:
					h264NalInfo.HasIdr = true;
					break;
				}
				offset = num + 1;
			}
			return h264NalInfo;
		}

		public byte[] ExtractParameterSets(byte[] payload)
		{
			if (_parameterSetRanges.Count == 0)
			{
				return Array.Empty<byte>();
			}
			byte[] array = new byte[_parameterSetRanges.Sum((NalRange range) => range.Length)];
			int num = 0;
			foreach (NalRange parameterSetRange in _parameterSetRanges)
			{
				Buffer.BlockCopy(payload, parameterSetRange.Start, array, num, parameterSetRange.Length);
				num += parameterSetRange.Length;
			}
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

	private byte[] _pendingParameterSets = Array.Empty<byte>();

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
			_pendingParameterSets = LatestParameterSets(_pendingParameterSets, payload, h264NalInfo);
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
					return Combine(_pendingParameterSets, payload);
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
		_pendingParameterSets = Array.Empty<byte>();
		DroppedPackets = 0L;
		ForwardedPackets = 0L;
		LastDecision = "waiting for SPS/PPS keyframe";
	}

	private static byte[] LatestParameterSets(byte[] existing, byte[] payload, H264NalInfo info)
	{
		byte[] array = info.ExtractParameterSets(payload);
		if (array.Length == 0)
		{
			return existing;
		}
		return array;
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
