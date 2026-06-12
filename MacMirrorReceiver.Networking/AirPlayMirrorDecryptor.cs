using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MacMirrorReceiver.Networking;

internal sealed class AirPlayMirrorDecryptor : IDisposable
{
	private readonly ICryptoTransform _encryptor;
	private readonly byte[] _counter;
	private readonly bool _littleEndianCounter;
	private readonly byte[] _keyStream = new byte[16];
	private readonly byte[] _partialKeyStream = new byte[16];
	private int _keyStreamOffset = 16;
	private int _partialKeyStreamOffset = 16;
	private readonly int _payloadOffset;

	private AirPlayMirrorDecryptor(byte[] key, byte[] iv, bool littleEndianCounter, int initialSkipBytes, int payloadOffset, string name)
	{
		Aes aes = Aes.Create();
		aes.Mode = CipherMode.ECB;
		aes.Padding = PaddingMode.None;
		aes.KeySize = 128;
		aes.Key = key;
		_encryptor = aes.CreateEncryptor();
		_counter = CopyOf(iv);
		_littleEndianCounter = littleEndianCounter;
		_payloadOffset = payloadOffset;
		Name = name;
		if (initialSkipBytes > 0)
		{
			SkipBytes(initialSkipBytes);
		}
	}

	public string Name { get; }

	public static AirPlayMirrorDecryptor Create(byte[] fairPlayAesKey, byte[] ecdhSecret, ulong streamConnectionId)
	{
		return Create(BuildStandardCandidate(fairPlayAesKey, ecdhSecret, streamConnectionId.ToString(CultureInfo.InvariantCulture), reverseSharedSecret: false, littleEndianCounter: false, "standard"));
	}

	public static AirPlayMirrorDecryptor Create(AirPlayMirrorDecryptorCandidate candidate)
	{
		return new AirPlayMirrorDecryptor(candidate.Key, candidate.Iv, candidate.LittleEndianCounter, candidate.InitialSkipBytes, candidate.PayloadOffset, candidate.Name);
	}

	public static IReadOnlyList<AirPlayMirrorDecryptorCandidate> BuildCandidates(
		byte[] fairPlayAesKey,
		byte[] ecdhSecret,
		ulong streamConnectionId,
		byte[]? eiv,
		ulong? rtspTargetSessionId = null)
	{
		List<AirPlayMirrorDecryptorCandidate> candidates = new List<AirPlayMirrorDecryptorCandidate>();

		AddUxPlayCandidates(candidates, fairPlayAesKey, streamConnectionId, prefix: string.Empty, eiv);
		if (rtspTargetSessionId.HasValue && rtspTargetSessionId.Value != streamConnectionId)
		{
			AddUxPlayCandidates(candidates, fairPlayAesKey, rtspTargetSessionId.Value, prefix: "rtsp-target-", eiv);
		}

		AddStreamIdCandidates(candidates, fairPlayAesKey, ecdhSecret, streamConnectionId, prefix: string.Empty);
		if (rtspTargetSessionId.HasValue && rtspTargetSessionId.Value != streamConnectionId)
		{
			AddStreamIdCandidates(candidates, fairPlayAesKey, ecdhSecret, rtspTargetSessionId.Value, prefix: "rtsp-target-");
		}

		if (eiv != null && eiv.Length >= 16)
			{
				byte[] eiv16 = eiv.Take(16).ToArray();
				AirPlayMirrorDecryptorCandidate standard = candidates[0];
				AddWithInitialSkips(candidates, new AirPlayMirrorDecryptorCandidate("standard-eiv", standard.Key, eiv16, LittleEndianCounter: false, InitialSkipBytes: 0));
				AddWithInitialSkips(candidates, new AirPlayMirrorDecryptorCandidate("raw-key-eiv", Take16(fairPlayAesKey), eiv16, LittleEndianCounter: false, InitialSkipBytes: 0));
		}

		return candidates
			.GroupBy(candidate => candidate.Name, StringComparer.Ordinal)
			.Select(group => group.First())
			.ToArray();
	}

	private static void AddUxPlayCandidates(
		List<AirPlayMirrorDecryptorCandidate> candidates,
		byte[] fairPlayAesKey,
		ulong streamConnectionId,
		string prefix,
		byte[]? eiv)
	{
		string unsignedId = streamConnectionId.ToString(CultureInfo.InvariantCulture);
		string signedId = unchecked((long)streamConnectionId).ToString(CultureInfo.InvariantCulture);

		foreach ((string keyVariantName, byte[] keyVariant) in BuildFairPlayKeyVariants(fairPlayAesKey))
		{
			AddWithInitialSkips(candidates, BuildDirectFairPlayCandidate(keyVariant, unsignedId, littleEndianCounter: false, prefix + "uxplay-direct" + keyVariantName));
			if (!string.Equals(unsignedId, signedId, StringComparison.Ordinal))
			{
				AddWithInitialSkips(candidates, BuildDirectFairPlayCandidate(keyVariant, signedId, littleEndianCounter: false, prefix + "uxplay-direct-signed" + keyVariantName));
			}

			if (eiv != null && eiv.Length >= 16)
			{
				byte[] eiv16 = eiv.Take(16).ToArray();
				AirPlayMirrorDecryptorCandidate direct = BuildDirectFairPlayCandidate(keyVariant, unsignedId, littleEndianCounter: false, prefix + "uxplay-direct-eiv" + keyVariantName);
				AddWithInitialSkips(candidates, direct with { Iv = eiv16 });
			}
		}
	}

	private static IReadOnlyList<(string Name, byte[] Key)> BuildFairPlayKeyVariants(byte[] fairPlayAesKey)
	{
		byte[] key = Take16(fairPlayAesKey);
		return new[]
		{
			(string.Empty, key),
			("-reverse", key.Reverse().ToArray()),
			("-word-byteswap", ReverseWordBytes(key)),
			("-word-order", ReverseWordOrder(key)),
			("-word-order-byteswap", ReverseWordBytes(ReverseWordOrder(key)))
		};
	}

	private static byte[] ReverseWordBytes(byte[] source)
	{
		byte[] output = CopyOf(source);
		for (int offset = 0; offset + 3 < output.Length; offset += 4)
		{
			Array.Reverse(output, offset, 4);
		}
		return output;
	}

	private static byte[] ReverseWordOrder(byte[] source)
	{
		byte[] output = new byte[source.Length];
		for (int offset = 0; offset + 3 < source.Length; offset += 4)
		{
			Buffer.BlockCopy(source, offset, output, source.Length - offset - 4, 4);
		}
		return output;
	}

	private static void AddStreamIdCandidates(
		List<AirPlayMirrorDecryptorCandidate> candidates,
		byte[] fairPlayAesKey,
		byte[] ecdhSecret,
		ulong streamConnectionId,
		string prefix)
	{
		string unsignedId = streamConnectionId.ToString(CultureInfo.InvariantCulture);
		string signedId = unchecked((long)streamConnectionId).ToString(CultureInfo.InvariantCulture);

		AddWithInitialSkips(candidates, BuildStandardCandidate(fairPlayAesKey, ecdhSecret, unsignedId, reverseSharedSecret: false, littleEndianCounter: false, prefix + "standard"));
		AddWithInitialSkips(candidates, BuildStandardCandidate(fairPlayAesKey, ecdhSecret, unsignedId, reverseSharedSecret: false, littleEndianCounter: true, prefix + "standard-little-counter"));

		if (!string.Equals(unsignedId, signedId, StringComparison.Ordinal))
		{
			AddWithInitialSkips(candidates, BuildStandardCandidate(fairPlayAesKey, ecdhSecret, signedId, reverseSharedSecret: false, littleEndianCounter: false, prefix + "signed-stream-id"));
			AddWithInitialSkips(candidates, BuildStandardCandidate(fairPlayAesKey, ecdhSecret, signedId, reverseSharedSecret: false, littleEndianCounter: true, prefix + "signed-stream-id-little-counter"));
		}

		AddWithInitialSkips(candidates, BuildStandardCandidate(fairPlayAesKey, ecdhSecret, streamConnectionId.ToString("x16", CultureInfo.InvariantCulture), reverseSharedSecret: false, littleEndianCounter: false, prefix + "hex-stream-id-lower"));
		AddWithInitialSkips(candidates, BuildStandardCandidate(fairPlayAesKey, ecdhSecret, streamConnectionId.ToString("X16", CultureInfo.InvariantCulture), reverseSharedSecret: false, littleEndianCounter: false, prefix + "hex-stream-id-upper"));
		AddWithInitialSkips(candidates, BuildStandardCandidate(fairPlayAesKey, ecdhSecret, UInt64Bytes(streamConnectionId, littleEndian: true), reverseSharedSecret: false, littleEndianCounter: false, prefix + "binary-stream-id-le"));
		AddWithInitialSkips(candidates, BuildStandardCandidate(fairPlayAesKey, ecdhSecret, UInt64Bytes(streamConnectionId, littleEndian: false), reverseSharedSecret: false, littleEndianCounter: false, prefix + "binary-stream-id-be"));
		AddWithInitialSkips(candidates, BuildStandardCandidate(fairPlayAesKey, ecdhSecret, unsignedId, reverseSharedSecret: true, littleEndianCounter: false, prefix + "reversed-secret"));
		AddWithInitialSkips(candidates, BuildDirectFairPlayCandidate(fairPlayAesKey, unsignedId, littleEndianCounter: false, prefix + "direct-fairplay"));
	}

	private static void AddWithInitialSkips(List<AirPlayMirrorDecryptorCandidate> candidates, AirPlayMirrorDecryptorCandidate baseCandidate)
	{
		candidates.Add(baseCandidate);
		for (int skip = 1; skip < 16; skip++)
		{
			candidates.Add(baseCandidate with
			{
				Name = baseCandidate.Name + "-skip" + skip.ToString(CultureInfo.InvariantCulture),
				InitialSkipBytes = skip
			});
		}
	}

	public static byte[] DecryptOnce(AirPlayMirrorDecryptorCandidate candidate, byte[] input)
	{
		using AirPlayMirrorDecryptor decryptor = Create(candidate);
		return decryptor.Decrypt(input);
	}

	private static AirPlayMirrorDecryptorCandidate BuildStandardCandidate(
		byte[] fairPlayAesKey,
		byte[] ecdhSecret,
		string streamConnectionIdText,
		bool reverseSharedSecret,
		bool littleEndianCounter,
		string name)
	{
		return BuildStandardCandidate(fairPlayAesKey, ecdhSecret, Encoding.ASCII.GetBytes(streamConnectionIdText), reverseSharedSecret, littleEndianCounter, name);
	}

	private static AirPlayMirrorDecryptorCandidate BuildStandardCandidate(
		byte[] fairPlayAesKey,
		byte[] ecdhSecret,
		byte[] streamConnectionIdBytes,
		bool reverseSharedSecret,
		bool littleEndianCounter,
		string name)
	{
		byte[] secret = reverseSharedSecret ? ecdhSecret.Reverse().ToArray() : ecdhSecret;
		byte[] firstHashInput = new byte[16 + secret.Length];
		Buffer.BlockCopy(fairPlayAesKey, 0, firstHashInput, 0, Math.Min(16, fairPlayAesKey.Length));
		Buffer.BlockCopy(secret, 0, firstHashInput, 16, secret.Length);
		byte[] mixedKey = SHA512.HashData(firstHashInput);

		return BuildCandidate(name, streamConnectionIdBytes, mixedKey, littleEndianCounter);
	}

	private static AirPlayMirrorDecryptorCandidate BuildDirectFairPlayCandidate(byte[] fairPlayAesKey, string streamConnectionIdText, bool littleEndianCounter, string name)
	{
		byte[] mixedKey = new byte[16];
		Buffer.BlockCopy(fairPlayAesKey, 0, mixedKey, 0, Math.Min(16, fairPlayAesKey.Length));
		return BuildCandidate(name, Encoding.ASCII.GetBytes(streamConnectionIdText), mixedKey, littleEndianCounter);
	}

	private static AirPlayMirrorDecryptorCandidate BuildCandidate(string name, byte[] streamConnectionIdBytes, byte[] mixedKey, bool littleEndianCounter)
	{
		byte[] streamKeyInput = BuildStreamHashInput("AirPlayStreamKey", streamConnectionIdBytes, mixedKey);
		byte[] streamIvInput = BuildStreamHashInput("AirPlayStreamIV", streamConnectionIdBytes, mixedKey);
		byte[] keyHash = SHA512.HashData(streamKeyInput);
		byte[] ivHash = SHA512.HashData(streamIvInput);

		byte[] key = new byte[16];
		byte[] iv = new byte[16];
		Buffer.BlockCopy(keyHash, 0, key, 0, key.Length);
		Buffer.BlockCopy(ivHash, 0, iv, 0, iv.Length);
		return new AirPlayMirrorDecryptorCandidate(name, key, iv, littleEndianCounter, InitialSkipBytes: 0);
	}

	public byte[] Decrypt(byte[] input)
	{
		if (_payloadOffset <= 0)
		{
			return DecryptRpiplayStyle(input);
		}

		if (input.Length <= _payloadOffset)
		{
			return Array.Empty<byte>();
		}

		byte[] encryptedBody = new byte[input.Length - _payloadOffset];
		Buffer.BlockCopy(input, _payloadOffset, encryptedBody, 0, encryptedBody.Length);
		return DecryptRpiplayStyle(encryptedBody);
	}

	public byte[] DecryptRpiplayStyle(byte[] input)
	{
		byte[] output = new byte[input.Length];
		int inputOffset = 0;
		if (_partialKeyStreamOffset < 16)
		{
			int count = Math.Min(16 - _partialKeyStreamOffset, input.Length);
			for (int i = 0; i < count; i++)
			{
				output[i] = (byte)(input[i] ^ _partialKeyStream[_partialKeyStreamOffset + i]);
			}
			_partialKeyStreamOffset += count;
			inputOffset += count;
		}

		int fullBlockBytes = ((input.Length - inputOffset) / 16) * 16;
		if (fullBlockBytes > 0)
		{
			byte[] fullBlocks = DecryptContinuous(input.AsSpan(inputOffset, fullBlockBytes).ToArray());
			Buffer.BlockCopy(fullBlocks, 0, output, inputOffset, fullBlockBytes);
			inputOffset += fullBlockBytes;
		}

		int rest = input.Length - inputOffset;
		if (rest > 0)
		{
			byte[] block = new byte[16];
			Buffer.BlockCopy(input, inputOffset, block, 0, rest);
			byte[] decrypted = DecryptContinuous(block);
			Buffer.BlockCopy(decrypted, 0, output, inputOffset, rest);
			Buffer.BlockCopy(decrypted, 0, _partialKeyStream, 0, decrypted.Length);
			_partialKeyStreamOffset = rest;
		}

		return output;
	}

	private byte[] DecryptContinuous(byte[] input)
	{
		byte[] output = new byte[input.Length];
		for (int i = 0; i < input.Length; i++)
		{
			if (_keyStreamOffset >= _keyStream.Length)
			{
				_encryptor.TransformBlock(_counter, 0, _counter.Length, _keyStream, 0);
				IncrementCounter(_counter, _littleEndianCounter);
				_keyStreamOffset = 0;
			}

			output[i] = (byte)(input[i] ^ _keyStream[_keyStreamOffset]);
			_keyStreamOffset++;
		}

		return output;
	}

	private void SkipBytes(int count)
	{
		for (int i = 0; i < count; i++)
		{
			if (_keyStreamOffset >= _keyStream.Length)
			{
				_encryptor.TransformBlock(_counter, 0, _counter.Length, _keyStream, 0);
				IncrementCounter(_counter, _littleEndianCounter);
				_keyStreamOffset = 0;
			}

			_keyStreamOffset++;
		}
	}

	public void Dispose()
	{
		_encryptor.Dispose();
	}

	private static byte[] BuildStreamHashInput(string label, byte[] streamConnectionIdBytes, byte[] mixedKey)
	{
		byte[] labelBytes = Encoding.ASCII.GetBytes(label);
		byte[] input = new byte[labelBytes.Length + streamConnectionIdBytes.Length + 16];
		Buffer.BlockCopy(labelBytes, 0, input, 0, labelBytes.Length);
		Buffer.BlockCopy(streamConnectionIdBytes, 0, input, labelBytes.Length, streamConnectionIdBytes.Length);
		Buffer.BlockCopy(mixedKey, 0, input, labelBytes.Length + streamConnectionIdBytes.Length, 16);
		return input;
	}

	private static byte[] UInt64Bytes(ulong value, bool littleEndian)
	{
		byte[] bytes = BitConverter.GetBytes(value);
		if (BitConverter.IsLittleEndian != littleEndian)
		{
			Array.Reverse(bytes);
		}
		return bytes;
	}

	private static void IncrementCounter(byte[] counter, bool littleEndianCounter)
	{
		int start = littleEndianCounter ? 0 : counter.Length - 1;
		int end = littleEndianCounter ? counter.Length : -1;
		int step = littleEndianCounter ? 1 : -1;
		for (int i = start; i != end; i += step)
		{
			counter[i]++;
			if (counter[i] != 0)
			{
				return;
			}
		}
	}

	private static byte[] CopyOf(byte[] source)
	{
		byte[] copy = new byte[source.Length];
		Buffer.BlockCopy(source, 0, copy, 0, source.Length);
		return copy;
	}

	private static byte[] Take16(byte[] source)
	{
		byte[] output = new byte[16];
		Buffer.BlockCopy(source, 0, output, 0, Math.Min(source.Length, output.Length));
		return output;
	}
}

internal sealed record AirPlayMirrorDecryptorCandidate(string Name, byte[] Key, byte[] Iv, bool LittleEndianCounter, int InitialSkipBytes, int PayloadOffset = 0);
