using System;
using System.Security.Cryptography;

namespace MacMirrorReceiver.Networking;

// Decrypts AirPlay 2 screen-mirroring audio (RAOP type=96) RTP payloads. Confirmed on real-device
// capture (2026-06-16): the per-packet payload is AES-128-CBC with the IV reset to eiv at the start
// of every packet; only the whole 16-byte blocks are encrypted and the trailing (len % 16) bytes
// are left in cleartext (the classic RAOP realtime layout). The AES key is the first 16 bytes of
// SHA512(fairPlayAesKey[0:16] || pairVerifySharedSecret) -- i.e. the same "mixed key" the video
// mirror path computes, but used directly as the CBC key (the audio stream has no
// streamConnectionID, so the video path's per-stream AirPlayStreamKey/IV derivation is not applied).
internal sealed class AirPlayAudioDecryptor : IDisposable
{
	private readonly Aes _aes;
	private readonly byte[] _iv;

	private AirPlayAudioDecryptor(byte[] key, byte[] iv)
	{
		_aes = Aes.Create();
		_aes.Mode = CipherMode.CBC;
		_aes.Padding = PaddingMode.None;
		_aes.KeySize = 128;
		_aes.Key = key;
		_iv = iv;
	}

	// Builds the decryptor from the FairPlay-unwrapped session key + pair-verify shared secret + eiv.
	// Returns null when any ingredient is missing or malformed so the caller can fall back to silence.
	public static AirPlayAudioDecryptor? TryCreate(byte[]? fairPlayAesKey, byte[]? sharedSecret, byte[]? eiv)
	{
		if (fairPlayAesKey == null || fairPlayAesKey.Length < 16 || sharedSecret == null || sharedSecret.Length == 0 || eiv == null || eiv.Length < 16)
		{
			return null;
		}

		byte[] mixInput = new byte[16 + sharedSecret.Length];
		Buffer.BlockCopy(fairPlayAesKey, 0, mixInput, 0, 16);
		Buffer.BlockCopy(sharedSecret, 0, mixInput, 16, sharedSecret.Length);

		byte[] key = new byte[16];
		Buffer.BlockCopy(SHA512.HashData(mixInput), 0, key, 0, 16);

		byte[] iv = new byte[16];
		Buffer.BlockCopy(eiv, 0, iv, 0, 16);
		return new AirPlayAudioDecryptor(key, iv);
	}

	// Decrypts one RTP audio payload in place semantics: the encrypted whole-block prefix is
	// CBC-decrypted with the IV reset to eiv, and the (len % 16) cleartext tail is copied verbatim.
	public byte[] Decrypt(ReadOnlySpan<byte> payload)
	{
		byte[] output = new byte[payload.Length];
		int blockBytes = (payload.Length / 16) * 16;
		if (blockBytes > 0)
		{
			using ICryptoTransform decryptor = _aes.CreateDecryptor(_aes.Key, _iv);
			decryptor.TransformBlock(payload.Slice(0, blockBytes).ToArray(), 0, blockBytes, output, 0);
		}

		int tail = payload.Length - blockBytes;
		if (tail > 0)
		{
			payload.Slice(blockBytes, tail).CopyTo(output.AsSpan(blockBytes));
		}

		return output;
	}

	public void Dispose()
	{
		_aes.Dispose();
	}
}
