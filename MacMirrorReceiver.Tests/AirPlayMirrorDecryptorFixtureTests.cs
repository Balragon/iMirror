using System;
using System.Linq;
using System.Security.Cryptography;
using MacMirrorReceiver.Networking;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AirPlayMirrorDecryptorFixtureTests
{
	private static readonly byte[] Key = Enumerable.Range(0, 16).Select(i => (byte)(0x10 + i)).ToArray();
	private static readonly byte[] Iv = Enumerable.Range(0, 16).Select(i => (byte)(0xF0 + i)).ToArray();

	[Fact]
	public void Decrypt_IsAes128CtrWithBigEndianCounter()
	{
		byte[] input = Enumerable.Range(0, 100).Select(i => (byte)(i * 13)).ToArray();
		using AirPlayMirrorDecryptor decryptor = AirPlayMirrorDecryptor.Create(Candidate(littleEndian: false));

		byte[] output = decryptor.Decrypt(input);

		Assert.Equal(Xor(input, Keystream(input.Length, littleEndian: false)), output);
	}

	[Fact]
	public void Decrypt_SupportsLittleEndianCounter()
	{
		byte[] input = new byte[64];
		using AirPlayMirrorDecryptor decryptor = AirPlayMirrorDecryptor.Create(Candidate(littleEndian: true));

		byte[] output = decryptor.Decrypt(input);

		Assert.Equal(Keystream(64, littleEndian: true), output);
		Assert.NotEqual(Keystream(64, littleEndian: false), output);
	}

	[Fact]
	public void Decrypt_ContinuesTheKeystreamAcrossPacketsOfAnyLength()
	{
		byte[] input = Enumerable.Range(0, 90).Select(i => (byte)(255 - i)).ToArray();
		using AirPlayMirrorDecryptor oneShot = AirPlayMirrorDecryptor.Create(Candidate(littleEndian: false));
		using AirPlayMirrorDecryptor chunked = AirPlayMirrorDecryptor.Create(Candidate(littleEndian: false));

		byte[] expected = oneShot.Decrypt(input);
		// 7 + 25 + 58: the splits land mid-block so the partial-keystream carry-over is exercised.
		byte[] actual = chunked.Decrypt(input.Take(7).ToArray())
			.Concat(chunked.Decrypt(input.Skip(7).Take(25).ToArray()))
			.Concat(chunked.Decrypt(input.Skip(32).ToArray()))
			.ToArray();

		Assert.Equal(expected, actual);
	}

	[Fact]
	public void DecryptPrefixOnce_HonoursInitialSkipBytes()
	{
		byte[] input = new byte[40];
		var candidate = new AirPlayMirrorDecryptorCandidate("skip", Key, Iv, LittleEndianCounter: false, InitialSkipBytes: 5);

		byte[] output = AirPlayMirrorDecryptor.DecryptPrefixOnce(candidate, input, input.Length);

		Assert.Equal(Keystream(45, littleEndian: false).Skip(5).ToArray(), output);
	}

	[Fact]
	public void Decrypt_HonoursPayloadOffset()
	{
		byte[] body = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
		byte[] input = new byte[] { 9, 9, 9 }.Concat(body).ToArray();
		var candidate = new AirPlayMirrorDecryptorCandidate("offset", Key, Iv, LittleEndianCounter: false, InitialSkipBytes: 0, PayloadOffset: 3);
		using AirPlayMirrorDecryptor decryptor = AirPlayMirrorDecryptor.Create(candidate);

		byte[] output = decryptor.Decrypt(input);

		Assert.Equal(Xor(body, Keystream(body.Length, littleEndian: false)), output);
		Assert.Empty(AirPlayMirrorDecryptor.Create(candidate).Decrypt(new byte[] { 1, 2, 3 }));
	}

	[Fact]
	public void BuildCandidates_ProducesNamedSixteenByteKeyAndIvPairs()
	{
		byte[] secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

		var candidates = AirPlayMirrorDecryptor.BuildCandidates(Key, secret, 123456789UL, Iv, 42UL);

		Assert.NotEmpty(candidates);
		Assert.All(candidates, c => Assert.False(string.IsNullOrWhiteSpace(c.Name)));
		Assert.All(candidates, c =>
		{
			Assert.Equal(16, c.Key.Length);
			Assert.Equal(16, c.Iv.Length);
		});
	}

	private static AirPlayMirrorDecryptorCandidate Candidate(bool littleEndian)
	{
		return new AirPlayMirrorDecryptorCandidate("test", Key, Iv, littleEndian, InitialSkipBytes: 0);
	}

	/// <summary>Reference AES-128-CTR keystream: ECB-encrypt the counter block, then increment it.</summary>
	private static byte[] Keystream(int length, bool littleEndian)
	{
		using Aes aes = Aes.Create();
		aes.Mode = CipherMode.ECB;
		aes.Padding = PaddingMode.None;
		aes.Key = Key;
		byte[] counter = (byte[])Iv.Clone();
		byte[] output = new byte[length];
		for (int offset = 0; offset < length; offset += 16)
		{
			byte[] block = aes.EncryptEcb(counter, PaddingMode.None);
			Buffer.BlockCopy(block, 0, output, offset, Math.Min(16, length - offset));
			int i = littleEndian ? 0 : 15;
			int step = littleEndian ? 1 : -1;
			while (i >= 0 && i < 16 && ++counter[i] == 0)
			{
				i += step;
			}
		}
		return output;
	}

	private static byte[] Xor(byte[] a, byte[] b)
	{
		return a.Select((value, i) => (byte)(value ^ b[i])).ToArray();
	}
}
