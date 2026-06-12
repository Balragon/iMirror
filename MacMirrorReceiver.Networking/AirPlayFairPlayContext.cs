using System;

namespace MacMirrorReceiver.Networking;

internal sealed class AirPlayFairPlayContext
{
	private const int SetupRequestLength = 16;
	private const int HandshakeRequestLength = 164;
	private const int HandshakeEchoOffset = 144;
	private const int HandshakeEchoLength = 20;

	private static readonly byte[][] SetupReplies =
	{
		Convert.FromHexString("46504C59030102000000008202000F9F3F9E0A2521DBDF312AB2BFB29E8D232B6376A8C818701D22AE93D82737FEAF9DB4FDF41C2DBA9D1F49CAAABF6591AC1F7BC6F7E0663D21AFE01565953EAB81F418CEED095ADB7C3D0E254909A79831D49C3982973434FACB42C63A1CD911A6FE941A8A6D4A743B46C3A7649E44C78955E49D8155009549C4E2F7A3F6D5BA"),
		Convert.FromHexString("46504C5903010200000000820201CF32A25714B2524F8AA0AD7AF164E37BCF4424E200047EFC0AD67AFCD95DED1C2730BB591B962ED63A9C4DED88BA8FC78DE64D91CCFD5C7B56DA88E31F5CCEAFC7431995A01665A54E1939D25B94DB64B9E45D8D063E1E6AF07E9656162B0EFA404275EA5A44D9591C7256B9FBE6513898B80227721988571650942AD946688A"),
		Convert.FromHexString("46504C5903010200000000820202C169A352EEED35B18CDD9C58D64F16C1519A89EB5317BD0D4336CD68F638FF9D016A5B52B7FA9216B2B65482C78444118121A2C7FED83DB7119E9182AAD7D18C7063E2A457555910AF9E0EFC76347D164043807F581EE4FBE42CA9DEDC1B5EB2A3AA3D2ECD59E7EEE70B3629F22AFD161D877353DDB99ADC8E07006E56F850CE"),
		Convert.FromHexString("46504C59030102000000008202039001E1727E0F57F9F5880DB104A6257A23F5CFFF1ABBE1E93045251AFB97EB9FC0011EBE0F3A81DF5B691D76ACB2F7A5C708E3D328F56BB39DBDE5F29C8A17F481487E3AE863C678325422E6F78E166D18AA7FD636258BCE28726F661F738893CE44311E4BE6C0535193E5EF72E8686233729C227D820C999445D89246C8C359")
	};

	private static readonly byte[] HandshakeHeader = Convert.FromHexString("46504C590301040000000014");

	private readonly object _gate = new object();
	private byte[]? _keyMessage;

	public byte[] BuildSetupResponse(byte[] requestBody)
	{
		if (requestBody.Length == SetupRequestLength)
		{
			return BuildInitialSetupResponse(requestBody);
		}

		if (requestBody.Length == HandshakeRequestLength)
		{
			return BuildHandshakeResponse(requestBody);
		}

		AppLog.Write($"AirPlay fp-setup unsupported request length={requestBody.Length}.");
		return Array.Empty<byte>();
	}

	private byte[] BuildInitialSetupResponse(byte[] requestBody)
	{
		if (requestBody[4] != 0x03)
		{
			AppLog.Write($"AirPlay fp-setup unsupported version byte=0x{requestBody[4]:X2}.");
			return Array.Empty<byte>();
		}

		int mode = requestBody[14];
		if (mode < 0 || mode >= SetupReplies.Length)
		{
			AppLog.Write($"AirPlay fp-setup unsupported mode={mode}.");
			return Array.Empty<byte>();
		}

		lock (_gate)
		{
			_keyMessage = null;
		}

		AppLog.Write($"AirPlay fp-setup initial response sent for mode={mode}.");
		return CopyOf(SetupReplies[mode]);
	}

	private byte[] BuildHandshakeResponse(byte[] requestBody)
	{
		if (requestBody[4] != 0x03)
		{
			AppLog.Write($"AirPlay fp-setup handshake unsupported version byte=0x{requestBody[4]:X2}.");
			return Array.Empty<byte>();
		}

		lock (_gate)
		{
			_keyMessage = CopyOf(requestBody);
		}

		byte[] response = new byte[HandshakeHeader.Length + HandshakeEchoLength];
		Buffer.BlockCopy(HandshakeHeader, 0, response, 0, HandshakeHeader.Length);
		Buffer.BlockCopy(requestBody, HandshakeEchoOffset, response, HandshakeHeader.Length, HandshakeEchoLength);
		AppLog.Write("AirPlay fp-setup handshake response sent.");
		return response;
	}

	public bool HasKeyMessage
	{
		get
		{
			lock (_gate)
			{
				return _keyMessage != null;
			}
		}
	}

	public byte[]? TryGetKeyMessage()
	{
		lock (_gate)
		{
			return _keyMessage == null ? null : CopyOf(_keyMessage);
		}
	}

	public byte[]? TryDecryptAesKey(byte[] encryptedKey)
	{
		byte[]? keyMessage;
		lock (_gate)
		{
			keyMessage = _keyMessage == null ? null : CopyOf(_keyMessage);
		}

		if (keyMessage == null)
		{
			AppLog.Write("AirPlay FairPlay AES key requested before fp-setup key message.");
			return null;
		}

		try
		{
			byte[] key = AirPlayPlayFair.DecryptAesKey(keyMessage, encryptedKey);
			AppLog.Write("AirPlay FairPlay AES key decrypted.");
			return key;
		}
		catch (Exception ex)
		{
			AppLog.Write("AirPlay FairPlay AES key decrypt failed: " + ex.Message);
			return null;
		}
	}

	private static byte[] CopyOf(byte[] source)
	{
		byte[] copy = new byte[source.Length];
		Buffer.BlockCopy(source, 0, copy, 0, source.Length);
		return copy;
	}
}
