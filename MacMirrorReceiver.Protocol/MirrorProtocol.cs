using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace MacMirrorReceiver.Protocol;

public static class MirrorProtocol
{
	public const int HeaderSize = 18;

	private static readonly byte[] Magic = Encoding.ASCII.GetBytes("MMR1");

	public static byte[] BuildFrame(MirrorMessageType type, ReadOnlySpan<byte> payload)
	{
		byte[] array = new byte[18 + payload.Length];
		Magic.CopyTo(array.AsSpan(0, 4));
		BinaryPrimitives.WriteUInt16BigEndian(array.AsSpan(4, 2), (ushort)type);
		BinaryPrimitives.WriteUInt64BigEndian(array.AsSpan(6, 8), NowNanos());
		BinaryPrimitives.WriteUInt32BigEndian(array.AsSpan(14, 4), (uint)payload.Length);
		payload.CopyTo(array.AsSpan(18));
		return array;
	}

	public static MirrorHeader ParseHeader(ReadOnlySpan<byte> header)
	{
		if (header.Length != 18)
		{
			throw new InvalidDataException("Invalid header length.");
		}
		if (!header.Slice(0, 4).SequenceEqual(Magic))
		{
			throw new InvalidDataException("Invalid protocol magic.");
		}
		ushort type = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(4, 2));
		ulong timestamp = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(6, 8));
		uint num = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(14, 4));
		return new MirrorHeader((MirrorMessageType)type, timestamp, checked((int)num));
	}

	private static ulong NowNanos()
	{
		return (ulong)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000000);
	}
}
