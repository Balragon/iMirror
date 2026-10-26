using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MacMirrorReceiver.Video;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class FfmpegAudioFragmentTests
{
	[Theory]
	[InlineData(44100, 2, 480, new byte[] { 0xF8, 0xE8, 0x50, 0x00 })]
	[InlineData(48000, 1, 480, new byte[] { 0xF8, 0xE6, 0x30, 0x00 })]
	[InlineData(44100, 2, 512, new byte[] { 0xF8, 0xE8, 0x40, 0x00 })]
	public void BuildEldAudioSpecificConfig_EncodesAot39WithEscape(int sampleRate, int channels, int samplesPerFrame, byte[] expected)
	{
		// AOT 31 escape (5 bits) + (39-32) (6 bits) + frequency index (4) + channels (4) +
		// frameLengthFlag (1, set for 480-sample frames) + four zero flags + 4 zero bits.
		Assert.Equal(expected, FfmpegAudioDecoder.BuildEldAudioSpecificConfig(sampleRate, channels, samplesPerFrame));
	}

	[Fact]
	public void BuildEldAudioSpecificConfig_RejectsUnsupportedSampleRate()
	{
		Assert.Throws<InvalidOperationException>(() => FfmpegAudioDecoder.BuildEldAudioSpecificConfig(12345, 2, 480));
	}

	[Fact]
	public void BuildInitSegment_IsFtypFollowedByMoovWithConsistentBoxSizes()
	{
		byte[] init = FfmpegAudioDecoder.BuildInitSegment(44100, 2, 480);

		List<(string Type, int Offset, int Size)> boxes = TopLevelBoxes(init);
		Assert.Equal(new[] { "ftyp", "moov" }, boxes.Select(b => b.Type));
		Assert.Equal(init.Length, boxes.Sum(b => b.Size));
		Assert.Contains("mvex", Encoding.ASCII.GetString(init));
		Assert.Contains("esds", Encoding.ASCII.GetString(init));
		Assert.Contains("SoundHandler", Encoding.ASCII.GetString(init));
	}

	[Fact]
	public void BuildMediaSegment_PointsTrunDataOffsetAtTheMdatPayload()
	{
		byte[] frame = Enumerable.Range(0, 37).Select(i => (byte)i).ToArray();

		byte[] segment = FfmpegAudioDecoder.BuildMediaSegment(frame, sequenceNumber: 7, decodeTime: 960, samplesPerFrame: 480);

		List<(string Type, int Offset, int Size)> boxes = TopLevelBoxes(segment);
		Assert.Equal(new[] { "moof", "mdat" }, boxes.Select(b => b.Type));
		(string _, int moofOffset, int moofSize) = boxes[0];
		(string _, int mdatOffset, int mdatSize) = boxes[1];
		Assert.Equal(frame, segment.AsSpan(mdatOffset + 8, mdatSize - 8).ToArray());

		int trun = IndexOf(segment, "trun");
		Assert.True(trun > 0);
		// trun: size(4) type(4) version/flags(4) sample_count(4) data_offset(4) sample_size(4)
		int dataOffset = BinaryPrimitives.ReadInt32BigEndian(segment.AsSpan(trun + 12, 4));
		int sampleSize = (int)BinaryPrimitives.ReadUInt32BigEndian(segment.AsSpan(trun + 16, 4));
		Assert.Equal(moofSize + 8, dataOffset); // first byte of the mdat payload, relative to moof
		Assert.Equal(frame.Length, sampleSize);
		Assert.Equal(mdatOffset + 8, moofOffset + dataOffset);
	}

	private static List<(string Type, int Offset, int Size)> TopLevelBoxes(byte[] data)
	{
		var boxes = new List<(string, int, int)>();
		int offset = 0;
		while (offset + 8 <= data.Length)
		{
			int size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
			string type = Encoding.ASCII.GetString(data, offset + 4, 4);
			Assert.True(size >= 8 && offset + size <= data.Length, $"box {type} at {offset} has bad size {size}");
			boxes.Add((type, offset, size));
			offset += size;
		}
		Assert.Equal(data.Length, offset);
		return boxes;
	}

	private static int IndexOf(byte[] data, string fourCc)
	{
		byte[] needle = Encoding.ASCII.GetBytes(fourCc);
		for (int i = 0; i + 4 <= data.Length; i++)
		{
			if (data.AsSpan(i, 4).SequenceEqual(needle))
			{
				return i - 4;
			}
		}
		return -1;
	}
}
