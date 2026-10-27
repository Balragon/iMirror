using MacMirrorReceiver.Networking;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class RtpPacketHeaderTests
{
	[Fact]
	public void TryParse_ReadsAllFixedHeaderFields()
	{
		byte[] packet = { 0x80, 0xE0, 0x12, 0x34, 0x00, 0x00, 0x01, 0x00, 0xDE, 0xAD, 0xBE, 0xEF, 0x55 };

		Assert.True(RtpPacketHeader.TryParse(packet, out RtpPacketHeader header));
		Assert.Equal(2, header.Version);
		Assert.False(header.Padding);
		Assert.False(header.Extension);
		Assert.Equal(0, header.CsrcCount);
		Assert.True(header.Marker);
		Assert.Equal(96, header.PayloadType);
		Assert.Equal(0x1234, header.SequenceNumber);
		Assert.Equal(256u, header.Timestamp);
		Assert.Equal(0xDEADBEEFu, header.Ssrc);
	}

	[Fact]
	public void TryParse_ReadsPaddingExtensionAndCsrcBits()
	{
		byte[] packet = { 0xB3, 0x60, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0 };

		Assert.True(RtpPacketHeader.TryParse(packet, out RtpPacketHeader header));
		Assert.Equal(2, header.Version);
		Assert.True(header.Padding);
		Assert.True(header.Extension);
		Assert.Equal(3, header.CsrcCount);
		Assert.False(header.Marker);
		Assert.Equal(96, header.PayloadType);
		Assert.Equal(1, header.SequenceNumber);
	}

	[Fact]
	public void TryParse_RejectsPacketsShorterThanTheFixedHeader()
	{
		Assert.False(RtpPacketHeader.TryParse(new byte[11], out RtpPacketHeader header));
		Assert.Equal(default, header);
		Assert.False(RtpPacketHeader.TryParse(System.Array.Empty<byte>(), out _));
	}
}
