using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using MacMirrorReceiver;
using MacMirrorReceiver.Protocol;

namespace MacMirrorReceiver.Networking;

/// <summary>
/// Pure helpers over AirPlay mirror video payloads: recognising a clear (unencrypted) H.264
/// payload, converting AVCC length-prefixed NAL units to Annex B, start-code scanning and the
/// short hex descriptions used in logs. No state; moved out of AirPlayProbeService.
/// </summary>
internal static class MirrorH264Payloads
{
	internal const int MaxH264PrefixProbeBytes = 64;
	internal const int MaxH264NalUnitLength = 8 * 1024 * 1024;

	// Necessary condition for TryNormalizeClearH264Payload on a payload of payloadLength bytes, given
	// at least its first min(ClearH264ProbeBytes, payloadLength) bytes: the first NAL unit must pass
	// the same length and header checks at some probed AVCC offset, or follow an Annex B start code.
	// Bytes decrypted with a wrong key almost never pass it.
	internal static bool MayBeClearH264Payload(byte[] prefix, int payloadLength)
	{
		if (payloadLength < 5)
		{
			return false;
		}

		int maxOffset = Math.Min(MaxH264PrefixProbeBytes, Math.Max(0, payloadLength - 5));
		for (int offset = 0; offset <= maxOffset; offset++)
		{
			int nalSize = BinaryPrimitives.ReadInt32BigEndian(prefix.AsSpan(offset, 4));
			int nalOffset = offset + 4;
			if (nalSize > 0 &&
				nalSize <= MaxH264NalUnitLength &&
				nalSize <= payloadLength - nalOffset &&
				IsPlausibleH264NalHeader(prefix[nalOffset]))
			{
				return true;
			}
		}

		int startCodeLength = StartCodeLengthAt(prefix, 0);
		return startCodeLength > 0 && startCodeLength < payloadLength && IsPlausibleH264NalHeader(prefix[startCodeLength]);
	}

	internal static bool TryNormalizeClearH264Payload(byte[] payload, out byte[] annexB, out string format)
	{
		annexB = Array.Empty<byte>();
		format = string.Empty;
		if (payload.Length < 5)
		{
			return false;
		}

		// Try AVCC (length-prefixed) FIRST. Mac mirror video is AVCC, and probing Annex B
		// first mis-fires when an AVCC big-endian length prefix coincides with a start code
		// (NAL size 1 -> 00 00 00 01, size 256..511 -> 00 00 01 xx). That made
		// TryUseAnnexBPayloadAtOffset copy the raw AVCC bytes verbatim, leaving the 4-byte
		// length prefix in the stream and shifting every NAL by a byte, which corrupts slice
		// headers ("non-intra slice in an IDR NAL unit", first_mb_in_slice != 0). AVCC parsing
		// requires the whole payload to resolve exactly (cursor == length), so it is the
		// stricter, safer check and must run before the lenient Annex B probe.
		int maxOffset = Math.Min(MaxH264PrefixProbeBytes, Math.Max(0, payload.Length - 5));
		for (int offset = 0; offset <= maxOffset; offset++)
		{
			if (TryConvertAvccPayloadAtOffset(payload, offset, littleEndianLength: false, out annexB, out int nalCount))
			{
				format = (offset == 0 ? "avcc-be" : "avcc-be@" + offset.ToString(CultureInfo.InvariantCulture)) + "/nal=" + nalCount.ToString(CultureInfo.InvariantCulture);
				return true;
			}
		}

		// Fallback: a genuine Annex B payload (no AVCC length framing resolved above).
		if (TryUseAnnexBPayloadAtOffset(payload, 0, out annexB))
		{
			format = "annexb";
			return true;
		}

		return false;
	}

	internal static bool TryConvertAvccPayloadAtOffset(byte[] payload, int offset, bool littleEndianLength, out byte[] annexB, out int nalCount)
	{
		annexB = Array.Empty<byte>();
		nalCount = 0;
		if (offset < 0 || offset + 5 > payload.Length)
		{
			return false;
		}

		List<byte> output = new List<byte>();
		int cursor = offset;
		while (cursor + 4 <= payload.Length)
		{
			int nalSize = littleEndianLength
				? BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(cursor, 4))
				: BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(cursor, 4));
			int nalOffset = cursor + 4;
			if (nalSize <= 0 || nalSize > MaxH264NalUnitLength || nalSize > payload.Length - nalOffset)
			{
				return false;
			}
			if (!IsPlausibleH264NalHeader(payload[nalOffset]))
			{
				return false;
			}

			output.Add(0);
			output.Add(0);
			output.Add(0);
			output.Add(1);
			for (int i = 0; i < nalSize; i++)
			{
				output.Add(payload[nalOffset + i]);
			}
			cursor = nalOffset + nalSize;
			nalCount++;
		}

		if (nalCount == 0 || cursor != payload.Length)
		{
			return false;
		}

		annexB = output.ToArray();
		return true;
	}

	internal static bool TryUseAnnexBPayloadAtOffset(byte[] payload, int offset, out byte[] annexB)
	{
		annexB = Array.Empty<byte>();
		if (!IsStartCodeAt(payload, offset))
		{
			return false;
		}

		int nalOffset = offset + StartCodeLengthAt(payload, offset);
		if (nalOffset >= payload.Length || !IsPlausibleH264NalHeader(payload[nalOffset]))
		{
			return false;
		}

		annexB = new byte[payload.Length - offset];
		Buffer.BlockCopy(payload, offset, annexB, 0, annexB.Length);
		return true;
	}

	internal static string DescribeH264Probe(byte[] payload)
	{
		if (payload.Length < 5)
		{
			return "short";
		}

		int beLength = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));
		int leLength = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
		int nalTypeAt4 = payload[4] & 0x1F;
		int startCodeOffset = FindStartCode(payload, 0, Math.Min(MaxH264PrefixProbeBytes, payload.Length - 4));
		string startCode = startCodeOffset >= 0 ? startCodeOffset.ToString(CultureInfo.InvariantCulture) : "none";
		return "beLen=" + beLength.ToString(CultureInfo.InvariantCulture) +
			",leLen=" + leLength.ToString(CultureInfo.InvariantCulture) +
			",nal4=" + nalTypeAt4.ToString(CultureInfo.InvariantCulture) +
			",start=" + startCode;
	}

	internal static bool IsPlausibleH264NalHeader(byte header)
	{
		if ((header & 0x80) != 0)
		{
			return false;
		}

		int nalType = header & 0x1F;
		return nalType == 1 ||
			nalType == 5 ||
			nalType == 6 ||
			nalType == 7 ||
			nalType == 8 ||
			nalType == 9 ||
			nalType == 14;
	}

	internal static bool IsStartCodeAt(byte[] payload, int offset)
	{
		return StartCodeLengthAt(payload, offset) > 0;
	}

	internal static int StartCodeLengthAt(byte[] payload, int offset)
	{
		if (offset + 3 <= payload.Length &&
			payload[offset] == 0 &&
			payload[offset + 1] == 0 &&
			payload[offset + 2] == 1)
		{
			return 3;
		}

		if (offset + 4 <= payload.Length &&
			payload[offset] == 0 &&
			payload[offset + 1] == 0 &&
			payload[offset + 2] == 0 &&
			payload[offset + 3] == 1)
		{
			return 4;
		}

		return 0;
	}

	internal static int FindStartCode(byte[] payload, int start, int endInclusive)
	{
		int end = Math.Min(endInclusive, payload.Length - 3);
		for (int i = Math.Max(0, start); i <= end; i++)
		{
			if (IsStartCodeAt(payload, i))
			{
				return i;
			}
		}

		return -1;
	}

	internal static string DescribeHeadBytes(byte[] payload, int count)
	{
		if (payload == null || payload.Length == 0)
		{
			return "(empty)";
		}
		int n = Math.Min(count, payload.Length);
		return BitConverter.ToString(payload, 0, n);
	}

	internal static byte[] CopyOf(byte[] source)
	{
		byte[] copy = new byte[source.Length];
		Buffer.BlockCopy(source, 0, copy, 0, source.Length);
		return copy;
	}
}
