using System.Buffers.Binary;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length < 1)
{
	Console.Error.WriteLine("Usage: AirPlayDiagnosticProbe <diagnostic-json> [iMirror.dll]");
	return 2;
}

string diagnosticPath = Path.GetFullPath(args[0]);
string assemblyPath = args.Length >= 2
	? Path.GetFullPath(args[1])
	: Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "bin", "Release", "net8.0-windows", "iMirror.dll"));

using JsonDocument document = JsonDocument.Parse(File.ReadAllText(diagnosticPath));
JsonElement root = document.RootElement;
byte[] keyMessage = FromHex(GetRequiredString(root, "fpSetupKeyMessageHex"));
byte[] encryptedEkey = FromHex(GetRequiredString(root, "encryptedEkeyHex"));
byte[] encryptedPayload = FromHex(GetRequiredString(root, "encryptedPayloadHex"));
byte[]? eiv = TryGetString(root, "eivHex") is string eivHex ? FromHex(eivHex) : null;
string streamId = GetRequiredString(root, "streamConnectionId");
string? rtspId = TryGetString(root, "rtspTargetSessionId");
string? snapshotKey = TryGetString(root, "fairPlayAesKeyHex");

Assembly assembly = Assembly.LoadFrom(assemblyPath);
Type playFairType = assembly.GetType("MacMirrorReceiver.Networking.AirPlayPlayFair", throwOnError: true)!;
MethodInfo decryptMethod = playFairType.GetMethod("DecryptAesKey", BindingFlags.Public | BindingFlags.Static)!;
byte[] fairPlayKey = (byte[])decryptMethod.Invoke(null, new object[] { keyMessage, encryptedEkey })!;

Console.WriteLine("Diagnostic: " + diagnosticPath);
Console.WriteLine("Assembly:   " + assemblyPath);
Console.WriteLine("Stream ID:  " + streamId);
if (!string.IsNullOrWhiteSpace(rtspId))
{
	Console.WriteLine("RTSP ID:    " + rtspId);
}
Console.WriteLine("Snapshot FairPlay key: " + (snapshotKey ?? "(none)"));
Console.WriteLine("Current  FairPlay key: " + Convert.ToHexString(fairPlayKey));
Console.WriteLine();

List<Candidate> candidates = new();
foreach ((string idName, string idText) in EnumerateIds(streamId, rtspId))
{
	foreach ((string keyName, byte[] keyVariant) in EnumerateKeyVariants(fairPlayKey))
	{
		byte[] key = Derive("AirPlayStreamKey", idText, keyVariant);
		byte[] iv = Derive("AirPlayStreamIV", idText, keyVariant);
		AddCandidates(candidates, idName + "/uxplay-direct" + keyName, key, iv, encryptedPayload.Length);
		if (eiv is { Length: >= 16 })
		{
			AddCandidates(candidates, idName + "/uxplay-direct-eiv" + keyName, key, eiv.Take(16).ToArray(), encryptedPayload.Length);
		}
	}
}

int tested = 0;
List<string> firstReports = new();
foreach (Candidate candidate in candidates)
{
	tested++;
	byte[] clear = AesCtrDecrypt(encryptedPayload, candidate.Key, candidate.Iv, candidate.SkipBytes, candidate.PayloadOffset);
	if (TryNormalizeH264(clear, out string format))
	{
		Console.WriteLine("MATCH " + candidate.Name + " " + format);
		Console.WriteLine("Clear first bytes: " + Convert.ToHexString(clear.Take(Math.Min(32, clear.Length)).ToArray()));
		return 0;
	}

	if (firstReports.Count < 16)
	{
		firstReports.Add(candidate.Name + "=" + Convert.ToHexString(clear.Take(Math.Min(8, clear.Length)).ToArray()) + "/" + DescribeProbe(clear));
	}
}

Console.WriteLine("No H.264 candidate matched.");
Console.WriteLine("Tested: " + tested.ToString(CultureInfo.InvariantCulture));
Console.WriteLine("First reports:");
foreach (string report in firstReports)
{
	Console.WriteLine("  " + report);
}
return 1;

static void AddCandidates(List<Candidate> candidates, string name, byte[] key, byte[] iv, int payloadLength)
{
	for (int skip = 0; skip < 16; skip++)
	{
		string skipName = skip == 0 ? name : name + "-skip" + skip.ToString(CultureInfo.InvariantCulture);
		candidates.Add(new Candidate(skipName, key, iv, skip, PayloadOffset: 0));
		if (skip != 0)
		{
			continue;
		}

		int maxOffset = Math.Min(64, Math.Max(0, payloadLength - 5));
		for (int offset = 1; offset <= maxOffset; offset++)
		{
			candidates.Add(new Candidate(name + "-payload-offset" + offset.ToString(CultureInfo.InvariantCulture), key, iv, SkipBytes: 0, PayloadOffset: offset));
		}
	}
}

static IEnumerable<(string Name, string Text)> EnumerateIds(string streamId, string? rtspId)
{
	yield return ("stream", streamId);
	if (ulong.TryParse(streamId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong streamValue))
	{
		string signed = unchecked((long)streamValue).ToString(CultureInfo.InvariantCulture);
		if (!string.Equals(signed, streamId, StringComparison.Ordinal))
		{
			yield return ("stream-signed", signed);
		}
	}

	if (!string.IsNullOrWhiteSpace(rtspId) && !string.Equals(rtspId, streamId, StringComparison.Ordinal))
	{
		yield return ("rtsp", rtspId);
		if (ulong.TryParse(rtspId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong rtspValue))
		{
			string signed = unchecked((long)rtspValue).ToString(CultureInfo.InvariantCulture);
			if (!string.Equals(signed, rtspId, StringComparison.Ordinal))
			{
				yield return ("rtsp-signed", signed);
			}
		}
	}
}

static IEnumerable<(string Name, byte[] Key)> EnumerateKeyVariants(byte[] fairPlayKey)
{
	byte[] key = fairPlayKey.Take(16).ToArray();
	yield return (string.Empty, key);
	yield return ("-reverse", key.Reverse().ToArray());
	yield return ("-word-byteswap", ReverseWordBytes(key));
	yield return ("-word-order", ReverseWordOrder(key));
	yield return ("-word-order-byteswap", ReverseWordBytes(ReverseWordOrder(key)));
}

static byte[] ReverseWordBytes(byte[] source)
{
	byte[] output = source.ToArray();
	for (int offset = 0; offset + 3 < output.Length; offset += 4)
	{
		Array.Reverse(output, offset, 4);
	}
	return output;
}

static byte[] ReverseWordOrder(byte[] source)
{
	byte[] output = new byte[source.Length];
	for (int offset = 0; offset + 3 < source.Length; offset += 4)
	{
		Buffer.BlockCopy(source, offset, output, source.Length - offset - 4, 4);
	}
	return output;
}

static byte[] Derive(string label, string streamId, byte[] fairPlayKey)
{
	byte[] labelBytes = Encoding.ASCII.GetBytes(label);
	byte[] idBytes = Encoding.ASCII.GetBytes(streamId);
	byte[] input = new byte[labelBytes.Length + idBytes.Length + 16];
	Buffer.BlockCopy(labelBytes, 0, input, 0, labelBytes.Length);
	Buffer.BlockCopy(idBytes, 0, input, labelBytes.Length, idBytes.Length);
	Buffer.BlockCopy(fairPlayKey, 0, input, labelBytes.Length + idBytes.Length, 16);
	return SHA512.HashData(input).Take(16).ToArray();
}

static byte[] AesCtrDecrypt(byte[] input, byte[] key, byte[] iv, int skipBytes, int payloadOffset)
{
	if (input.Length <= payloadOffset)
	{
		return Array.Empty<byte>();
	}

	byte[] body = input.AsSpan(payloadOffset).ToArray();
	byte[] output = new byte[body.Length];
	byte[] counter = iv.Take(16).ToArray();
	byte[] stream = new byte[16];
	int streamOffset = 16;
	using Aes aes = Aes.Create();
	aes.Mode = CipherMode.ECB;
	aes.Padding = PaddingMode.None;
	aes.KeySize = 128;
	aes.Key = key.Take(16).ToArray();
	using ICryptoTransform encryptor = aes.CreateEncryptor();

	for (int i = 0; i < skipBytes; i++)
	{
		NextStreamByte(encryptor, counter, stream, ref streamOffset);
	}

	for (int i = 0; i < body.Length; i++)
	{
		output[i] = (byte)(body[i] ^ NextStreamByte(encryptor, counter, stream, ref streamOffset));
	}
	return output;
}

static byte NextStreamByte(ICryptoTransform encryptor, byte[] counter, byte[] stream, ref int streamOffset)
{
	if (streamOffset >= stream.Length)
	{
		encryptor.TransformBlock(counter, 0, counter.Length, stream, 0);
		IncrementCounter(counter);
		streamOffset = 0;
	}

	return stream[streamOffset++];
}

static void IncrementCounter(byte[] counter)
{
	for (int i = counter.Length - 1; i >= 0; i--)
	{
		counter[i]++;
		if (counter[i] != 0)
		{
			return;
		}
	}
}

static bool TryNormalizeH264(byte[] payload, out string format)
{
	format = string.Empty;
	if (payload.Length < 5)
	{
		return false;
	}

	if (payload.AsSpan(0).StartsWith(new byte[] { 0, 0, 0, 1 }) && IsPlausibleNal(payload[4]))
	{
		format = "annexb";
		return true;
	}

	int maxOffset = Math.Min(64, Math.Max(0, payload.Length - 5));
	for (int offset = 0; offset <= maxOffset; offset++)
	{
		if (TryAvcc(payload, offset, out int nalCount))
		{
			format = (offset == 0 ? "avcc-be" : "avcc-be@" + offset.ToString(CultureInfo.InvariantCulture)) + "/nal=" + nalCount.ToString(CultureInfo.InvariantCulture);
			return true;
		}
	}

	return false;
}

static bool TryAvcc(byte[] payload, int offset, out int nalCount)
{
	nalCount = 0;
	int cursor = offset;
	while (cursor + 4 <= payload.Length)
	{
		int nalSize = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(cursor, 4));
		int nalOffset = cursor + 4;
		if (nalSize <= 0 || nalSize > 8 * 1024 * 1024 || nalSize > payload.Length - nalOffset)
		{
			return false;
		}
		if (!IsPlausibleNal(payload[nalOffset]))
		{
			return false;
		}

		nalCount++;
		cursor = nalOffset + nalSize;
	}

	return nalCount > 0 && cursor == payload.Length;
}

static bool IsPlausibleNal(byte value)
{
	int type = value & 0x1f;
	return type is 1 or 5 or 6 or 7 or 8 or 9 or 14;
}

static string DescribeProbe(byte[] payload)
{
	if (payload.Length < 5)
	{
		return "short";
	}

	int be = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));
	int le = BinaryPrimitives.ReadInt32LittleEndian(payload.AsSpan(0, 4));
	int nal = payload[4] & 0x1f;
	return "beLen=" + be.ToString(CultureInfo.InvariantCulture) + ",leLen=" + le.ToString(CultureInfo.InvariantCulture) + ",nal4=" + nal.ToString(CultureInfo.InvariantCulture);
}

static string GetRequiredString(JsonElement root, string name)
{
	if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
	{
		throw new InvalidOperationException("Missing diagnostic field: " + name);
	}
	return value.GetString() ?? throw new InvalidOperationException("Empty diagnostic field: " + name);
}

static string? TryGetString(JsonElement root, string name)
{
	return root.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null
		? value.GetString()
		: null;
}

static byte[] FromHex(string value)
{
	return Convert.FromHexString(value.Trim());
}

internal sealed record Candidate(string Name, byte[] Key, byte[] Iv, int SkipBytes, int PayloadOffset);
