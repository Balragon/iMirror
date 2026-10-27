using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using MacMirrorReceiver.Networking;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AirPlayBinaryPlistWriterTests
{
	[Fact]
	public void Write_UsesUtf16ForNonAsciiStringsAndRoundTrips()
	{
		byte[] data = AirPlayBinaryPlist.Write(new Dictionary<string, object>
		{
			["name"] = "거실 TV",
			["model"] = "AppleTV3,2"
		});

		var root = Assert.IsType<Dictionary<string, object?>>(AirPlayBinaryPlist.Read(data));
		Assert.Equal("거실 TV", root["name"]);
		Assert.Equal("AppleTV3,2", root["model"]);
		// The UTF-16BE bytes of the name must appear verbatim; an ASCII write would have
		// replaced the Hangul with '?'.
		Assert.True(data.AsSpan().IndexOf(Encoding.BigEndianUnicode.GetBytes("거실 TV")) >= 0);
		Assert.True(data.AsSpan().IndexOf(Encoding.ASCII.GetBytes("AppleTV3,2")) >= 0);
	}

	[Fact]
	public void Write_KeepsAsciiStringsAsAscii()
	{
		byte[] data = AirPlayBinaryPlist.Write(new Dictionary<string, object> { ["name"] = "iMirror" });

		// 0x57 = ASCII string marker with count 7.
		Assert.True(data.AsSpan().IndexOf(new byte[] { 0x57 }.Concat(Encoding.ASCII.GetBytes("iMirror")).ToArray()) >= 0);
	}
}
