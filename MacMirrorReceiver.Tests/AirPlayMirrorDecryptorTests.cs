using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using MacMirrorReceiver.Networking;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AirPlayMirrorDecryptorTests
{
	[Theory]
	[InlineData(false, 0, false)]
	[InlineData(false, 7, false)]
	[InlineData(true, 0, false)]
	[InlineData(true, 13, false)]
	[InlineData(false, 3, true)]
	[InlineData(true, 0, true)]
	public void Decrypt_MatchesAesCtrKeystreamAcrossChunkSizes(bool littleEndianCounter, int initialSkipBytes, bool counterWrapsToZero)
	{
		byte[] key = RandomBytes(16, seed: 1);
		byte[] iv = RandomBytes(16, seed: 2);
		if (counterWrapsToZero)
		{
			Array.Fill(iv, (byte)0xFF);
		}
		else if (littleEndianCounter)
		{
			// Carries out of the two low-order counter bytes within the first 16 blocks.
			iv[0] = 0xF0;
			iv[1] = 0xFF;
		}
		else
		{
			iv[14] = 0xFF;
			iv[15] = 0xF0;
		}

		AirPlayMirrorDecryptorCandidate candidate = new AirPlayMirrorDecryptorCandidate("test", key, iv, littleEndianCounter, initialSkipBytes);
		// Mixes partial blocks, single blocks and runs that span several keystream batches.
		int[] chunkSizes = { 0, 1, 15, 16, 17, 31, 33, 4095, 4096, 4097, 8197, 65539, 3, 20000 };
		byte[] data = RandomBytes(chunkSizes.Sum(), seed: 3);

		List<byte> decrypted = new List<byte>();
		using (AirPlayMirrorDecryptor decryptor = AirPlayMirrorDecryptor.Create(candidate))
		{
			int offset = 0;
			foreach (int size in chunkSizes)
			{
				decrypted.AddRange(decryptor.Decrypt(data.AsSpan(offset, size).ToArray()));
				offset += size;
			}
		}

		Assert.Equal(ReferenceCtr(key, iv, littleEndianCounter, initialSkipBytes, data), decrypted.ToArray());
	}

	[Theory]
	[InlineData(0, 0, 100, 69)]
	[InlineData(5, 0, 100, 69)]
	[InlineData(0, 9, 100, 69)]
	[InlineData(11, 64, 100, 69)]
	[InlineData(0, 0, 40, 69)]
	[InlineData(0, 120, 100, 69)]
	public void DecryptPrefixOnce_MatchesStartOfFullDecrypt(int initialSkipBytes, int payloadOffset, int payloadLength, int count)
	{
		AirPlayMirrorDecryptorCandidate candidate = new AirPlayMirrorDecryptorCandidate("test", RandomBytes(16, seed: 4), RandomBytes(16, seed: 5), LittleEndianCounter: false, initialSkipBytes, payloadOffset);
		byte[] payload = RandomBytes(payloadLength, seed: 6);
		byte[] full;
		using (AirPlayMirrorDecryptor decryptor = AirPlayMirrorDecryptor.Create(candidate))
		{
			full = decryptor.Decrypt(payload);
		}

		Assert.Equal(full.Take(count).ToArray(), AirPlayMirrorDecryptor.DecryptPrefixOnce(candidate, payload, count));
	}

	// AES-128-CTR keystream from the IV, entered InitialSkipBytes in: what a mirror decryptor
	// applies across consecutive Decrypt calls.
	private static byte[] ReferenceCtr(byte[] key, byte[] iv, bool littleEndianCounter, int initialSkipBytes, byte[] data)
	{
		using Aes aes = Aes.Create();
		aes.Key = key;
		byte[] counter = (byte[])iv.Clone();
		byte[] keyStream = new byte[(initialSkipBytes + data.Length + 15) / 16 * 16];
		for (int offset = 0; offset < keyStream.Length; offset += 16)
		{
			aes.EncryptEcb(counter, keyStream.AsSpan(offset, 16), PaddingMode.None);
			for (int n = 0; n < counter.Length; n++)
			{
				int i = littleEndianCounter ? n : counter.Length - 1 - n;
				if (++counter[i] != 0)
				{
					break;
				}
			}
		}

		byte[] output = new byte[data.Length];
		for (int i = 0; i < data.Length; i++)
		{
			output[i] = (byte)(data[i] ^ keyStream[initialSkipBytes + i]);
		}
		return output;
	}

	private static byte[] RandomBytes(int count, int seed)
	{
		byte[] bytes = new byte[count];
		new Random(seed).NextBytes(bytes);
		return bytes;
	}
}
