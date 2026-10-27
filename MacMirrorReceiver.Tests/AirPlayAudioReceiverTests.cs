using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using MacMirrorReceiver.Networking;
using Xunit;

namespace MacMirrorReceiver.Tests;

/// <summary>
/// Fixture tests for the RTP data path of <see cref="AirPlayAudioReceiver"/> (HandleDataPacket):
/// real loopback UDP into the receiver's ephemeral data port, encrypted with the same derivation
/// the receiver uses. Packets that must be ignored are followed by a valid "sentinel" packet, so
/// every assertion waits for a frame that must arrive instead of for a timeout.
/// </summary>
public sealed class AirPlayAudioReceiverTests : IDisposable
{
	private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);
	private static readonly byte[] FairPlayKey = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
	private static readonly byte[] SharedSecret = Enumerable.Range(100, 32).Select(i => (byte)i).ToArray();
	private static readonly byte[] Eiv = Enumerable.Range(200, 16).Select(i => (byte)i).ToArray();
	private static readonly AirPlayAudioStreamInfo AacEld = new AirPlayAudioStreamInfo(8, 480, 44100, 2, 0x01000000, 2);

	private readonly AirPlayAudioReceiver _receiver = new AirPlayAudioReceiver();
	private readonly UdpClient _sender = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
	private readonly BlockingCollection<(byte[] Frame, uint Timestamp, ushort Sequence)> _frames = new();

	public AirPlayAudioReceiverTests()
	{
		_receiver.AudioFrameReceived += (frame, timestamp, sequence) => _frames.Add((frame, timestamp, sequence));
	}

	public void Dispose()
	{
		_receiver.Dispose();
		_sender.Dispose();
		_frames.Dispose();
	}

	[Fact]
	public void DecryptsAnAacFrameAndPassesItsRtpTimestampAndSequence()
	{
		Start(IPAddress.Loopback, Crypto());
		byte[] audio = Enumerable.Range(0, 53).Select(i => (byte)(i * 3)).ToArray(); // 3 blocks + clear tail

		Send(Rtp(sequence: 4321, timestamp: 0xDEADBEEF, Encrypt(audio)));

		(byte[] frame, uint timestamp, ushort sequence) = Next();
		Assert.Equal(audio, frame);
		Assert.Equal(0xDEADBEEFu, timestamp);
		Assert.Equal((ushort)4321, sequence);
	}

	[Fact]
	public void PassesFramesThroughInArrivalOrderIncludingDuplicates()
	{
		Start(IPAddress.Loopback, Crypto());
		byte[] audio = new byte[32];

		Send(Rtp(10, 4800, Encrypt(audio)));
		Send(Rtp(12, 5760, Encrypt(audio)));
		Send(Rtp(11, 5280, Encrypt(audio)));
		Send(Rtp(11, 5280, Encrypt(audio))); // redundant copy: dedup belongs to the consumer

		Assert.Equal(new ushort[] { 10, 12, 11, 11 }, new[] { Next(), Next(), Next(), Next() }.Select(f => f.Sequence).ToArray());
	}

	[Theory]
	[InlineData("short header")]
	[InlineData("rtp version 1")]
	[InlineData("keepalive under one AES block")]
	public void IgnoresPacketsThatAreNotDecodableAudio(string kind)
	{
		Start(IPAddress.Loopback, Crypto());
		byte[] ignored = kind switch
		{
			"short header" => new byte[] { 0x80, 0x60, 0x00, 0x01, 0x00 },
			"rtp version 1" => WithVersion(Rtp(1, 480, Encrypt(new byte[32])), 1),
			_ => Rtp(1, 480, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }),
		};

		Send(ignored);
		Send(Rtp(2, 960, Encrypt(new byte[16]))); // sentinel

		Assert.Equal((ushort)2, Next().Sequence);
		Assert.Empty(_frames);
	}

	[Fact]
	public void RejectsPacketsFromAHostOtherThanTheSetupSender()
	{
		// SETUP came from 192.0.2.10 (TEST-NET-1); the loopback sender is someone else.
		Start(IPAddress.Parse("192.0.2.10"), Crypto());

		Send(Rtp(1, 480, Encrypt(new byte[32])));

		Assert.False(_frames.TryTake(out _, TimeSpan.FromMilliseconds(500)), "a packet from another host must not reach the decoder");
	}

	[Fact]
	public void WithoutSessionKeysNothingIsDecrypted()
	{
		Start(IPAddress.Loopback, crypto: null);

		Send(Rtp(1, 480, new byte[32]));

		Assert.False(_frames.TryTake(out _, TimeSpan.FromMilliseconds(500)));
		Assert.True(_receiver.IsRunning);
	}

	[Fact]
	public void StopReleasesTheSocketsAndAnotherStartBindsAgain()
	{
		Start(IPAddress.Loopback, Crypto());
		int firstPort = _receiver.DataPort;

		_receiver.Stop();
		Assert.False(_receiver.IsRunning);
		Start(IPAddress.Loopback, Crypto());

		Assert.True(_receiver.IsRunning);
		Send(Rtp(7, 480, Encrypt(new byte[16])));
		Assert.Equal((ushort)7, Next().Sequence);
		Assert.NotEqual(0, firstPort);
	}

	private void Start(IPAddress sender, AirPlayAudioCrypto? crypto)
	{
		Assert.True(_receiver.Start(sender, macControlPort: 0, AacEld, crypto));
		Assert.NotEqual(0, _receiver.DataPort);
	}

	private void Send(byte[] packet)
	{
		_sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, _receiver.DataPort));
	}

	private (byte[] Frame, uint Timestamp, ushort Sequence) Next()
	{
		Assert.True(_frames.TryTake(out var frame, FrameTimeout), "expected a decrypted audio frame");
		return frame;
	}

	private static AirPlayAudioCrypto Crypto() => new AirPlayAudioCrypto(FairPlayKey, Eiv, SharedSecret, EncryptedEkey: null, RtspTargetSessionId: null);

	// Same derivation as the receiver: key = SHA512(fairPlayKey || sharedSecret)[0:16], whole blocks
	// CBC with IV = eiv reset per packet, the (len % 16) tail in the clear.
	private static byte[] Encrypt(byte[] audio)
	{
		byte[] key = SHA512.HashData(FairPlayKey.Concat(SharedSecret).ToArray()).Take(16).ToArray();
		int blockBytes = audio.Length / 16 * 16;
		using Aes aes = Aes.Create();
		aes.Key = key;
		byte[] encrypted = aes.EncryptCbc(audio.AsSpan(0, blockBytes), Eiv, PaddingMode.None);
		return encrypted.Concat(audio.Skip(blockBytes)).ToArray();
	}

	private static byte[] Rtp(ushort sequence, uint timestamp, byte[] payload)
	{
		byte[] packet = new byte[12 + payload.Length];
		packet[0] = 0x80; // version 2
		packet[1] = 0x60; // payload type 96
		BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), sequence);
		BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), timestamp);
		BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), 0x12345678);
		payload.CopyTo(packet, 12);
		return packet;
	}

	private static byte[] WithVersion(byte[] packet, int version)
	{
		packet[0] = (byte)((packet[0] & 0x3F) | (version << 6));
		return packet;
	}
}
