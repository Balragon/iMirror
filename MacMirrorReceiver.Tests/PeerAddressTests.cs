using System.Net;
using MacMirrorReceiver.Networking;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class PeerAddressTests
{
	[Fact]
	public void Matches_SameIPv4Address()
	{
		Assert.True(PeerAddress.Matches(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("192.168.1.20")));
	}

	[Fact]
	public void Matches_RejectsDifferentHost()
	{
		Assert.False(PeerAddress.Matches(IPAddress.Parse("192.168.1.20"), IPAddress.Parse("192.168.1.21")));
	}

	[Fact]
	public void Matches_TreatsIPv4MappedIPv6AsTheSameHost()
	{
		IPAddress mapped = IPAddress.Parse("192.168.1.20").MapToIPv6();

		Assert.True(PeerAddress.Matches(IPAddress.Parse("192.168.1.20"), mapped));
		Assert.True(PeerAddress.Matches(mapped, IPAddress.Parse("192.168.1.20")));
	}

	[Fact]
	public void Matches_RejectsWhenNoSetupPeerIsKnown()
	{
		Assert.False(PeerAddress.Matches(null, IPAddress.Parse("192.168.1.20")));
		Assert.False(PeerAddress.Matches(IPAddress.Parse("192.168.1.20"), null));
	}
}
