using System;
using System.Linq;
using System.Security.Cryptography;
using MacMirrorReceiver.Networking;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AirPlayAudioDecryptorTests
{
	private static readonly byte[] FairPlayKey = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
	private static readonly byte[] SharedSecret = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
	private static readonly byte[] Eiv = Enumerable.Range(200, 16).Select(i => (byte)i).ToArray();

	[Fact]
	public void TryCreate_ReturnsNullWhenAnyIngredientIsMissingOrShort()
	{
		Assert.Null(AirPlayAudioDecryptor.TryCreate(null, SharedSecret, Eiv));
		Assert.Null(AirPlayAudioDecryptor.TryCreate(FairPlayKey.Take(15).ToArray(), SharedSecret, Eiv));
		Assert.Null(AirPlayAudioDecryptor.TryCreate(FairPlayKey, null, Eiv));
		Assert.Null(AirPlayAudioDecryptor.TryCreate(FairPlayKey, Array.Empty<byte>(), Eiv));
		Assert.Null(AirPlayAudioDecryptor.TryCreate(FairPlayKey, SharedSecret, Eiv.Take(15).ToArray()));
		Assert.NotNull(AirPlayAudioDecryptor.TryCreate(FairPlayKey, SharedSecret, Eiv));
	}

	[Fact]
	public void Decrypt_UsesSha512MixedKeyCbcWithIvResetPerPacketAndClearTail()
	{
		// Reference derivation (see AirPlayAudioDecryptor header comment): AES key is the first 16
		// bytes of SHA512(fairPlayKey[0:16] || sharedSecret); whole blocks are CBC with IV = eiv;
		// the (len % 16) tail is sent in the clear.
		byte[] key = SHA512.HashData(FairPlayKey.Concat(SharedSecret).ToArray()).Take(16).ToArray();
		byte[] plaintextBlocks = Enumerable.Range(0, 48).Select(i => (byte)(i * 7)).ToArray();
		byte[] clearTail = { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };
		using Aes aes = Aes.Create();
		aes.Mode = CipherMode.CBC;
		aes.Padding = PaddingMode.None;
		aes.Key = key;
		byte[] encrypted = aes.EncryptCbc(plaintextBlocks, Eiv, PaddingMode.None);
		byte[] packet = encrypted.Concat(clearTail).ToArray();

		using AirPlayAudioDecryptor decryptor = AirPlayAudioDecryptor.TryCreate(FairPlayKey, SharedSecret, Eiv)!;
		byte[] first = decryptor.Decrypt(packet);
		byte[] second = decryptor.Decrypt(packet);

		Assert.Equal(plaintextBlocks.Concat(clearTail).ToArray(), first);
		Assert.Equal(first, second); // IV resets to eiv for every packet
	}

	[Fact]
	public void Decrypt_LeavesPacketsShorterThanOneBlockUntouched()
	{
		using AirPlayAudioDecryptor decryptor = AirPlayAudioDecryptor.TryCreate(FairPlayKey, SharedSecret, Eiv)!;
		byte[] small = { 1, 2, 3, 4, 5, 6, 7 };

		Assert.Equal(small, decryptor.Decrypt(small));
		Assert.Empty(decryptor.Decrypt(Array.Empty<byte>()));
	}
}
