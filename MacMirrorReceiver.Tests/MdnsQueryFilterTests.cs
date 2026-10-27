using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using MacMirrorReceiver.Networking;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class MdnsQueryFilterTests
{
	private static readonly string[] AnsweredNames =
	{
		"_airplay._tcp.local",
		"_raop._tcp.local",
		"_services._dns-sd._udp.local",
		"Living Room._airplay._tcp.local",
		"AABBCCDDEEFF@Living Room._raop._tcp.local",
		"DESKTOP-1.local"
	};

	[Theory]
	[InlineData("_airplay._tcp.local")]
	[InlineData("_RAOP._tcp.LOCAL")]
	[InlineData("_services._dns-sd._udp.local")]
	[InlineData("Living Room._airplay._tcp.local")]
	[InlineData("DESKTOP-1.local")]
	[InlineData("DESKTOP-1.local.")]
	public void AnswersQueriesForItsOwnNames(string name)
	{
		Assert.True(AirPlayProbeService.PacketAsksForProbe(Query(name), AnsweredNames));
	}

	[Theory]
	[InlineData("_googlecast._tcp.local")]
	[InlineData("_ipp._tcp.local")]
	[InlineData("Kitchen._airplay._tcp.local")]
	[InlineData("OTHER-PC.local")]
	[InlineData("local")]
	public void IgnoresQueriesForOtherNames(string name)
	{
		Assert.False(AirPlayProbeService.PacketAsksForProbe(Query(name), AnsweredNames));
	}

	[Fact]
	public void IgnoresResponsesEvenWhenTheyNameTheService()
	{
		byte[] response = Query("_airplay._tcp.local");
		response[2] |= 0x80; // QR = response
		Assert.False(AirPlayProbeService.PacketAsksForProbe(response, AnsweredNames));
	}

	[Fact]
	public void IgnoresPacketsThatOnlyMentionTheServiceInOtherSections()
	{
		// A query for an unrelated name whose payload happens to contain "_airplay" text
		// (e.g. in an additional record) used to trigger an announcement via substring search.
		byte[] query = Query("_ipp._tcp.local", trailing: Encoding.ASCII.GetBytes("_airplay._tcp.local _raop"));
		Assert.False(AirPlayProbeService.PacketAsksForProbe(query, AnsweredNames));
	}

	[Fact]
	public void AnswersWhenAnyQuestionMatches()
	{
		byte[] query = Query("_ipp._tcp.local", "_raop._tcp.local");
		Assert.True(AirPlayProbeService.PacketAsksForProbe(query, AnsweredNames));
	}

	[Fact]
	public void IgnoresTruncatedAndTinyPackets()
	{
		Assert.False(AirPlayProbeService.PacketAsksForProbe(Array.Empty<byte>(), AnsweredNames));
		Assert.False(AirPlayProbeService.PacketAsksForProbe(new byte[11], AnsweredNames));
		byte[] query = Query("_airplay._tcp.local");
		byte[] truncated = new byte[query.Length - 6];
		Array.Copy(query, truncated, truncated.Length);
		Assert.False(AirPlayProbeService.PacketAsksForProbe(truncated, AnsweredNames));
	}

	private static byte[] Query(params string[] names) => Query(names, trailing: null);

	private static byte[] Query(string name, byte[] trailing) => Query(new[] { name }, trailing);

	private static byte[] Query(IReadOnlyList<string> names, byte[]? trailing)
	{
		using var stream = new MemoryStream();
		stream.Write(new byte[] { 0, 0, 0, 0 }); // id, flags (QR = query)
		stream.WriteByte((byte)(names.Count >> 8));
		stream.WriteByte((byte)names.Count);
		stream.Write(new byte[6]); // an, ns, ar counts
		foreach (string name in names)
		{
			foreach (string label in name.TrimEnd('.').Split('.'))
			{
				byte[] bytes = Encoding.UTF8.GetBytes(label);
				stream.WriteByte((byte)bytes.Length);
				stream.Write(bytes);
			}
			stream.WriteByte(0);
			stream.Write(new byte[] { 0, 12, 0x80, 1 }); // PTR, QU | IN
		}
		if (trailing != null)
		{
			stream.Write(trailing);
		}
		return stream.ToArray();
	}
}
