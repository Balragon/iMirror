using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MacMirrorReceiver.Networking;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AirPlayBinaryPlistTests
{
	[Fact]
	public void Read_RoundTripsWrittenDictionary()
	{
		byte[] data = AirPlayBinaryPlist.Write(new Dictionary<string, object>
		{
			["name"] = "iMirror",
			["port"] = 7000,
			["flag"] = true,
			["streams"] = new List<object>
			{
				new Dictionary<string, object> { ["type"] = 110, ["streamConnectionID"] = 123456789L }
			}
		});

		var root = Assert.IsType<Dictionary<string, object?>>(AirPlayBinaryPlist.Read(data));
		Assert.Equal("iMirror", root["name"]);
		Assert.Equal(7000L, root["port"]);
		Assert.Equal(true, root["flag"]);
		var streams = Assert.IsType<List<object?>>(root["streams"]);
		var stream = Assert.IsType<Dictionary<string, object?>>(Assert.Single(streams));
		Assert.Equal(110L, stream["type"]);
		Assert.Equal(123456789L, stream["streamConnectionID"]);
	}

	[Fact]
	public void Read_ReturnsNullForSelfReferencingArray()
	{
		// Object 0 is a one-element array whose only element is object 0.
		byte[] data = BuildPlist(new byte[][] { new byte[] { 0xA1, 0x00 } }, topObject: 0);

		Assert.Null(AirPlayBinaryPlist.Read(data));
	}

	[Fact]
	public void Read_ReturnsNullForDictionaryCycle()
	{
		// Object 0 = { "a": object 0 }, object 1 = "a".
		byte[] data = BuildPlist(new byte[][]
		{
			new byte[] { 0xD1, 0x01, 0x00 },
			new byte[] { 0x51, (byte)'a' }
		}, topObject: 0);

		Assert.Null(AirPlayBinaryPlist.Read(data));
	}

	[Fact]
	public void Read_ReturnsNullForIndirectCycle()
	{
		// Object 0 = [object 1], object 1 = [object 0].
		byte[] data = BuildPlist(new byte[][]
		{
			new byte[] { 0xA1, 0x01 },
			new byte[] { 0xA1, 0x00 }
		}, topObject: 0);

		Assert.Null(AirPlayBinaryPlist.Read(data));
	}

	[Fact]
	public void Read_AllowsSharedReferencesThatAreNotCycles()
	{
		// Object 0 = [object 1, object 1], object 1 = "x". Sharing is legal in bplist.
		byte[] data = BuildPlist(new byte[][]
		{
			new byte[] { 0xA2, 0x01, 0x01 },
			new byte[] { 0x51, (byte)'x' }
		}, topObject: 0);

		var list = Assert.IsType<List<object?>>(AirPlayBinaryPlist.Read(data));
		Assert.Equal(new object?[] { "x", "x" }, list);
	}

	[Fact]
	public void Read_ReturnsNullWhenNestingIsTooDeep()
	{
		object inner = new List<object>();
		for (int i = 0; i < 100; i++)
		{
			inner = new List<object> { inner };
		}
		byte[] data = AirPlayBinaryPlist.Write(new Dictionary<string, object> { ["deep"] = inner });

		Assert.Null(AirPlayBinaryPlist.Read(data));
	}

	[Fact]
	public void Read_AcceptsModerateNesting()
	{
		object inner = new List<object>();
		for (int i = 0; i < 8; i++)
		{
			inner = new List<object> { inner };
		}
		byte[] data = AirPlayBinaryPlist.Write(new Dictionary<string, object> { ["deep"] = inner });

		Assert.NotNull(AirPlayBinaryPlist.Read(data));
	}

	[Theory]
	[InlineData(0)]
	[InlineData(3)]
	[InlineData(255)]
	public void Read_ReturnsNullForInvalidOffsetSize(byte offsetSize)
	{
		byte[] data = BuildPlist(new byte[][] { new byte[] { 0x51, (byte)'a' } }, topObject: 0);
		data[data.Length - 32 + 6] = offsetSize;

		Assert.Null(AirPlayBinaryPlist.Read(data));
	}

	[Fact]
	public void Read_ReturnsNullForObjectRefOutOfRange()
	{
		// Object 0 = [object 7], but only one object exists.
		byte[] data = BuildPlist(new byte[][] { new byte[] { 0xA1, 0x07 } }, topObject: 0);

		Assert.Null(AirPlayBinaryPlist.Read(data));
	}

	[Fact]
	public void Read_ReturnsNullForOffsetTableBeyondData()
	{
		byte[] data = BuildPlist(new byte[][] { new byte[] { 0x51, (byte)'a' } }, topObject: 0);
		WriteUInt64(data, data.Length - 32 + 24, (ulong)data.Length + 100);

		Assert.Null(AirPlayBinaryPlist.Read(data));
	}

	[Fact]
	public void Read_ReturnsNullForHugeObjectCount()
	{
		byte[] data = BuildPlist(new byte[][] { new byte[] { 0x51, (byte)'a' } }, topObject: 0);
		WriteUInt64(data, data.Length - 32 + 8, ulong.MaxValue);

		Assert.Null(AirPlayBinaryPlist.Read(data));
	}

	[Fact]
	public void Read_ReturnsNullForTruncatedStringLength()
	{
		// ASCII string claiming 200 bytes when only 1 follows.
		byte[] data = BuildPlist(new byte[][] { new byte[] { 0x5F, 0x10, 200, (byte)'a' } }, topObject: 0);

		Assert.Null(AirPlayBinaryPlist.Read(data));
	}

	[Fact]
	public void Read_ReturnsNullForTruncatedData()
	{
		byte[] data = AirPlayBinaryPlist.Write(new Dictionary<string, object> { ["name"] = "iMirror" });

		for (int length = 40; length < data.Length; length++)
		{
			// Any truncation that keeps the 40-byte minimum must still fail cleanly.
			AirPlayBinaryPlist.Read(data.Take(length).ToArray());
		}
		Assert.Null(AirPlayBinaryPlist.Read(data.Take(8).ToArray()));
	}

	/// <summary>
	/// Builds a bplist00 image with one-byte offsets and object references so tests can
	/// describe malformed object graphs that the writer would never produce.
	/// </summary>
	private static byte[] BuildPlist(byte[][] objects, int topObject)
	{
		using MemoryStream output = new MemoryStream();
		output.Write(Encoding.ASCII.GetBytes("bplist00"));
		List<byte> offsets = new List<byte>();
		foreach (byte[] obj in objects)
		{
			offsets.Add((byte)output.Length);
			output.Write(obj);
		}
		long offsetTableOffset = output.Length;
		output.Write(offsets.ToArray());

		output.Write(new byte[6]);
		output.WriteByte(1); // offsetSize
		output.WriteByte(1); // objectRefSize
		byte[] trailer = new byte[24];
		WriteUInt64(trailer, 0, (ulong)objects.Length);
		WriteUInt64(trailer, 8, (ulong)topObject);
		WriteUInt64(trailer, 16, (ulong)offsetTableOffset);
		output.Write(trailer);
		return output.ToArray();
	}

	private static void WriteUInt64(byte[] buffer, int offset, ulong value)
	{
		for (int i = 7; i >= 0; i--)
		{
			buffer[offset + i] = (byte)value;
			value >>= 8;
		}
	}
}
