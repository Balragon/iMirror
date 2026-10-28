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
/// Readers over the dictionaries AirPlayBinaryPlist.Read returns for SETUP and TEARDOWN bodies:
/// typed field access with the integer widths senders actually use, the stream list, the RTSP
/// target session id, and the teardown classification. No state; moved out of AirPlayProbeService.
/// </summary>
internal static class AirPlaySetupPlist
{
	internal static bool TryReadData(Dictionary<string, object?> dictionary, string key, out byte[] data)
	{
		if (dictionary.TryGetValue(key, out object? value) && value is byte[] bytes)
		{
			data = bytes;
			return true;
		}

		data = Array.Empty<byte>();
		return false;
	}

	internal static bool TryReadStreamConnectionId(Dictionary<string, object?> request, out ulong streamConnectionId)
	{
		streamConnectionId = 0;
		if (!request.TryGetValue("streams", out object? streamsObject) || streamsObject is not List<object?> streams)
		{
			return false;
		}

		foreach (Dictionary<string, object?> stream in streams.OfType<Dictionary<string, object?>>())
		{
			if (TryReadUInt64(stream, "streamConnectionID", out streamConnectionId))
			{
				return true;
			}
		}

		return false;
	}

	internal static bool ContainsMirrorVideoStream(Dictionary<string, object?> request)
	{
		if (!request.TryGetValue("streams", out object? streamsObject) || streamsObject is not List<object?> streams)
		{
			return false;
		}

		foreach (Dictionary<string, object?> stream in streams.OfType<Dictionary<string, object?>>())
		{
			if (TryReadInt32(stream, "type", out int streamType) && streamType == 110)
			{
				return true;
			}
		}

		return false;
	}

	internal static bool TryReadRtspTargetSessionId(string target, out ulong sessionId)
	{
		sessionId = 0;
		int slash = target.LastIndexOf('/');
		if (slash < 0 || slash + 1 >= target.Length)
		{
			return false;
		}

		return ulong.TryParse(target.AsSpan(slash + 1), out sessionId);
	}

	internal static bool TryReadInt32(Dictionary<string, object?> dictionary, string key, out int value)
	{
		value = 0;
		if (!dictionary.TryGetValue(key, out object? rawValue))
		{
			return false;
		}

		switch (rawValue)
		{
			case byte number:
				value = number;
				return true;
			case short number:
				value = number;
				return true;
			case int number:
				value = number;
				return true;
			case long number when number >= int.MinValue && number <= int.MaxValue:
				value = (int)number;
				return true;
			case ulong number when number <= int.MaxValue:
				value = (int)number;
				return true;
			default:
				return false;
		}
	}

	internal static bool TryReadUInt64(Dictionary<string, object?> dictionary, string key, out ulong value)
	{
		value = 0;
		if (!dictionary.TryGetValue(key, out object? rawValue))
		{
			return false;
		}

		switch (rawValue)
		{
			case byte number:
				value = number;
				return true;
			case short number when number >= 0:
				value = (ulong)number;
				return true;
			case int number when number >= 0:
				value = (ulong)number;
				return true;
			case long number when number >= 0:
				value = (ulong)number;
				return true;
			case long number:
				value = unchecked((ulong)number);
				return true;
			case ulong number:
				value = number;
				return true;
			default:
				return false;
		}
	}

	/// <summary>
	/// True when a TEARDOWN ends the mirror stream: no body (the whole session), or a <c>streams</c>
	/// list that includes the type-110 video stream. A TEARDOWN listing only audio (type 96) is not.
	/// </summary>
	internal static bool IsMirrorTeardown(byte[] body)
	{
		if (body.Length == 0 ||
			AirPlayBinaryPlist.Read(body) is not Dictionary<string, object?> request ||
			!request.ContainsKey("streams"))
		{
			return true;
		}

		bool mirror = ContainsMirrorVideoStream(request);
		if (!mirror)
		{
			AppLog.Write("AirPlay TEARDOWN covers only non-video streams; mirror session kept.");
		}
		return mirror;
	}

	// Compact recursive description of a parsed plist (for audio-discovery logging). Byte arrays are
	// shown as length + a short hex prefix so keys like ekey/eiv are visible without huge dumps.
	internal static string DescribePlistObject(object? value, int depth)
	{
		if (depth > 6)
		{
			return "...";
		}
		switch (value)
		{
			case null:
				return "null";
			case byte[] bytes:
				return $"bytes[{bytes.Length}]{(bytes.Length > 0 ? " " + BitConverter.ToString(bytes, 0, Math.Min(16, bytes.Length)) : string.Empty)}";
			case Dictionary<string, object?> dict:
				return "{" + string.Join(", ", dict.Select(kv => kv.Key + "=" + DescribePlistObject(kv.Value, depth + 1))) + "}";
			case System.Collections.IEnumerable enumerable when value is not string:
				return "[" + string.Join(", ", enumerable.Cast<object?>().Select(item => DescribePlistObject(item, depth + 1))) + "]";
			default:
				return value.ToString() ?? "?";
		}
	}
}
