using System;
using System.Buffers.Binary;

namespace MacMirrorReceiver.Networking;

/// <summary>Fixed 12-byte RTP header (RFC 3550 §5.1) as the AirPlay audio data stream sends it.</summary>
internal readonly record struct RtpPacketHeader(
	int Version,
	bool Padding,
	bool Extension,
	int CsrcCount,
	bool Marker,
	int PayloadType,
	ushort SequenceNumber,
	uint Timestamp,
	uint Ssrc)
{
	public const int Length = 12;

	/// <summary>Parses the fixed header. Returns false when the packet is shorter than 12 bytes.</summary>
	public static bool TryParse(ReadOnlySpan<byte> packet, out RtpPacketHeader header)
	{
		if (packet.Length < Length)
		{
			header = default;
			return false;
		}

		header = new RtpPacketHeader(
			Version: (packet[0] >> 6) & 0x03,
			Padding: (packet[0] & 0x20) != 0,
			Extension: (packet[0] & 0x10) != 0,
			CsrcCount: packet[0] & 0x0F,
			Marker: (packet[1] & 0x80) != 0,
			PayloadType: packet[1] & 0x7F,
			SequenceNumber: BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(2, 2)),
			Timestamp: BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(4, 4)),
			Ssrc: BinaryPrimitives.ReadUInt32BigEndian(packet.Slice(8, 4)));
		return true;
	}
}
