using System;
using System.IO;
using System.Text;
using MacMirrorReceiver.Protocol;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class MirrorProtocolTests
{
	[Fact]
	public void BuildFrame_RoundTripsThroughParseHeader()
	{
		byte[] payload = Encoding.ASCII.GetBytes("hello");

		byte[] frame = MirrorProtocol.BuildFrame(MirrorMessageType.Video, payload);
		MirrorHeader header = MirrorProtocol.ParseHeader(frame.AsSpan(0, MirrorProtocol.HeaderSize));

		Assert.Equal(MirrorProtocol.HeaderSize + payload.Length, frame.Length);
		Assert.Equal(MirrorMessageType.Video, header.Type);
		Assert.Equal(payload.Length, header.PayloadLength);
		Assert.True(header.Timestamp > 0);
		Assert.Equal(payload, frame.AsSpan(MirrorProtocol.HeaderSize).ToArray());
	}

	[Fact]
	public void ParseHeader_RejectsWrongLength()
	{
		Assert.Throws<InvalidDataException>(() => MirrorProtocol.ParseHeader(new byte[17]));
		Assert.Throws<InvalidDataException>(() => MirrorProtocol.ParseHeader(new byte[19]));
	}

	[Fact]
	public void ParseHeader_RejectsBadMagic()
	{
		byte[] frame = MirrorProtocol.BuildFrame(MirrorMessageType.Auth, Array.Empty<byte>());
		frame[0] = (byte)'X';

		Assert.Throws<InvalidDataException>(() => MirrorProtocol.ParseHeader(frame));
	}

	[Fact]
	public void ParseHeader_RejectsPayloadLengthAboveInt32()
	{
		byte[] frame = MirrorProtocol.BuildFrame(MirrorMessageType.Auth, Array.Empty<byte>());
		frame[14] = 0xFF;
		frame[15] = 0xFF;
		frame[16] = 0xFF;
		frame[17] = 0xFF;

		Assert.Throws<OverflowException>(() => MirrorProtocol.ParseHeader(frame));
	}
}
