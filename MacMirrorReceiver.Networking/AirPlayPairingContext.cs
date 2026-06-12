using System;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace MacMirrorReceiver.Networking;

internal sealed class AirPlayPairingContext
{
	private const int KeySize = 32;
	private const int SignatureSize = 64;
	private const string PairVerifyAesKeySalt = "Pair-Verify-AES-Key";
	private const string PairVerifyAesIvSalt = "Pair-Verify-AES-IV";

	private readonly object _gate = new object();
	private readonly Org.BouncyCastle.Security.SecureRandom _random = new Org.BouncyCastle.Security.SecureRandom();
	private readonly Ed25519PrivateKeyParameters _signingPrivateKey;
	private readonly byte[] _signingPublicKey;
	private PairVerifyState? _pairVerifyState;
	private bool _pairSetupComplete;

	public AirPlayPairingContext()
	{
		_signingPrivateKey = new Ed25519PrivateKeyParameters(_random);
		_signingPublicKey = new byte[KeySize];
		_signingPrivateKey.GeneratePublicKey().Encode(_signingPublicKey, 0);
	}

	public string PublicKeyHex => Convert.ToHexString(_signingPublicKey);

	public byte[]? TryGetSharedSecret()
	{
		lock (_gate)
		{
			return _pairVerifyState == null ? null : CopyOf(_pairVerifyState.SharedSecret);
		}
	}

	public byte[] BuildPairSetupResponse(byte[] requestBody)
	{
		if (requestBody.Length != KeySize)
		{
			AppLog.Write($"AirPlay pair-setup expected {KeySize} bytes, got {requestBody.Length}.");
		}

		lock (_gate)
		{
			_pairSetupComplete = true;
			_pairVerifyState = null;
		}

		AppLog.Write("AirPlay pair-setup response sent with receiver Ed25519 public key.");
		return CopyOf(_signingPublicKey);
	}

	public byte[] BuildPairVerifyResponse(byte[] requestBody)
	{
		if (requestBody.Length < 4)
		{
			AppLog.Write("AirPlay pair-verify request too short.");
			return Array.Empty<byte>();
		}

		try
		{
			return requestBody[0] switch
			{
				1 => BuildPairVerifyStageOne(requestBody),
				0 => FinishPairVerify(requestBody),
				3 => FinishPairVerify(requestBody),
				_ => UnknownPairVerifyStage(requestBody[0])
			};
		}
		catch (Exception ex)
		{
			AppLog.Write("AirPlay pair-verify crypto failed: " + ex.Message);
			return Array.Empty<byte>();
		}
	}

	private byte[] BuildPairVerifyStageOne(byte[] requestBody)
	{
		if (requestBody.Length != 4 + KeySize + KeySize)
		{
			AppLog.Write($"AirPlay pair-verify stage 1 expected 68 bytes, got {requestBody.Length}.");
			return Array.Empty<byte>();
		}

		bool setupComplete;
		lock (_gate)
		{
			setupComplete = _pairSetupComplete;
		}
		if (!setupComplete)
		{
			AppLog.Write("AirPlay pair-verify arrived before pair-setup; continuing for compatibility.");
		}

		byte[] clientEcdhPublic = requestBody.AsSpan(4, KeySize).ToArray();
		byte[] clientSigningPublic = requestBody.AsSpan(4 + KeySize, KeySize).ToArray();

		X25519PrivateKeyParameters serverEcdhPrivate = new X25519PrivateKeyParameters(_random);
		byte[] serverEcdhPublic = new byte[KeySize];
		serverEcdhPrivate.GeneratePublicKey().Encode(serverEcdhPublic, 0);

		byte[] sharedSecret = new byte[KeySize];
		X25519Agreement agreement = new X25519Agreement();
		agreement.Init(serverEcdhPrivate);
		agreement.CalculateAgreement(new X25519PublicKeyParameters(clientEcdhPublic, 0), sharedSecret, 0);

		byte[] signatureMessage = Concatenate(serverEcdhPublic, clientEcdhPublic);
		byte[] signature = Sign(signatureMessage);
		byte[] encryptedSignature = TransformPairVerifySignature(sharedSecret, signature, skipBytes: 0);

		lock (_gate)
		{
			_pairVerifyState = new PairVerifyState(clientEcdhPublic, clientSigningPublic, serverEcdhPublic, sharedSecret);
		}

		byte[] response = new byte[KeySize + SignatureSize];
		Buffer.BlockCopy(serverEcdhPublic, 0, response, 0, KeySize);
		Buffer.BlockCopy(encryptedSignature, 0, response, KeySize, SignatureSize);
		AppLog.Write("AirPlay pair-verify stage 1 response sent with signed receiver X25519 key.");
		return response;
	}

	private byte[] FinishPairVerify(byte[] requestBody)
	{
		if (requestBody.Length != 4 + SignatureSize)
		{
			AppLog.Write($"AirPlay pair-verify final stage expected 68 bytes, got {requestBody.Length}.");
			return Array.Empty<byte>();
		}

		PairVerifyState? state;
		lock (_gate)
		{
			state = _pairVerifyState;
		}
		if (state == null)
		{
			AppLog.Write("AirPlay pair-verify final stage arrived without a stage 1 session.");
			return Array.Empty<byte>();
		}

		byte[] encryptedSignature = requestBody.AsSpan(4, SignatureSize).ToArray();
		byte[] signature = TransformPairVerifySignature(state.SharedSecret, encryptedSignature, skipBytes: SignatureSize);
		byte[] signatureMessage = Concatenate(state.ClientEcdhPublic, state.ServerEcdhPublic);
		bool verified = Verify(signatureMessage, signature, state.ClientSigningPublic);

		AppLog.Write(verified
			? "AirPlay pair-verify final signature verified."
			: "AirPlay pair-verify final signature did not verify.");
		return Array.Empty<byte>();
	}

	private static byte[] UnknownPairVerifyStage(byte stage)
	{
		AppLog.Write($"AirPlay pair-verify unknown stage {stage}; empty response sent.");
		return Array.Empty<byte>();
	}

	private byte[] Sign(byte[] message)
	{
		Ed25519Signer signer = new Ed25519Signer();
		signer.Init(forSigning: true, _signingPrivateKey);
		signer.BlockUpdate(message, 0, message.Length);
		return signer.GenerateSignature();
	}

	private static bool Verify(byte[] message, byte[] signature, byte[] publicKey)
	{
		Ed25519Signer verifier = new Ed25519Signer();
		verifier.Init(forSigning: false, new Ed25519PublicKeyParameters(publicKey, 0));
		verifier.BlockUpdate(message, 0, message.Length);
		return verifier.VerifySignature(signature);
	}

	private static byte[] TransformPairVerifySignature(byte[] sharedSecret, byte[] signature, int skipBytes)
	{
		byte[] key = DerivePairVerifyBytes(PairVerifyAesKeySalt, sharedSecret);
		byte[] iv = DerivePairVerifyBytes(PairVerifyAesIvSalt, sharedSecret);
		return AesCtrTransform(signature, key, iv, skipBytes);
	}

	private static byte[] DerivePairVerifyBytes(string salt, byte[] sharedSecret)
	{
		byte[] saltBytes = Encoding.ASCII.GetBytes(salt);
		byte[] input = new byte[saltBytes.Length + sharedSecret.Length];
		Buffer.BlockCopy(saltBytes, 0, input, 0, saltBytes.Length);
		Buffer.BlockCopy(sharedSecret, 0, input, saltBytes.Length, sharedSecret.Length);

		byte[] hash = SHA512.HashData(input);
		byte[] output = new byte[16];
		Buffer.BlockCopy(hash, 0, output, 0, output.Length);
		return output;
	}

	private static byte[] AesCtrTransform(byte[] input, byte[] key, byte[] iv, int skipBytes)
	{
		using Aes aes = Aes.Create();
		aes.Mode = CipherMode.ECB;
		aes.Padding = PaddingMode.None;
		aes.KeySize = 128;
		aes.Key = key;

		using ICryptoTransform encryptor = aes.CreateEncryptor();
		byte[] counter = CopyOf(iv);
		SkipCtrBytes(encryptor, counter, skipBytes);

		byte[] output = new byte[input.Length];
		byte[] keyStream = new byte[16];
		for (int offset = 0; offset < input.Length;)
		{
			encryptor.TransformBlock(counter, 0, counter.Length, keyStream, 0);
			int count = Math.Min(keyStream.Length, input.Length - offset);
			for (int i = 0; i < count; i++)
			{
				output[offset + i] = (byte)(input[offset + i] ^ keyStream[i]);
			}
			offset += count;
			IncrementCounter(counter);
		}

		return output;
	}

	private static void SkipCtrBytes(ICryptoTransform encryptor, byte[] counter, int skipBytes)
	{
		if (skipBytes <= 0)
		{
			return;
		}

		byte[] keyStream = new byte[16];
		for (int skipped = 0; skipped < skipBytes;)
		{
			encryptor.TransformBlock(counter, 0, counter.Length, keyStream, 0);
			skipped += Math.Min(keyStream.Length, skipBytes - skipped);
			IncrementCounter(counter);
		}
	}

	private static void IncrementCounter(byte[] counter)
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

	private static byte[] Concatenate(byte[] first, byte[] second)
	{
		byte[] output = new byte[first.Length + second.Length];
		Buffer.BlockCopy(first, 0, output, 0, first.Length);
		Buffer.BlockCopy(second, 0, output, first.Length, second.Length);
		return output;
	}

	private static byte[] CopyOf(byte[] source)
	{
		byte[] copy = new byte[source.Length];
		Buffer.BlockCopy(source, 0, copy, 0, source.Length);
		return copy;
	}

	private sealed record PairVerifyState(
		byte[] ClientEcdhPublic,
		byte[] ClientSigningPublic,
		byte[] ServerEcdhPublic,
		byte[] SharedSecret);
}
