using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using MacMirrorReceiver.Networking;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AirPlayProbeServiceTests
{
	[Theory]
	[InlineData("AirPlay", true)]
	[InlineData("airplay", true)]
	[InlineData("RAOP", false)]
	[InlineData("AirPlay data", false)]
	[InlineData("", false)]
	public void IsMirrorControlLabel_MatchesOnlyAirPlayControlConnection(string label, bool expected)
	{
		Assert.Equal(expected, AirPlayProbeService.IsMirrorControlLabel(label));
	}

	[Fact]
	public async Task StartAsync_CleansUpPartialMdnsStartAndCanRetry()
	{
		using UdpClient blocker = BindExclusiveUdpPort();
		int mdnsPort = ((IPEndPoint)blocker.Client.LocalEndPoint!).Port;
		using var service = new AirPlayProbeService("iMirror Test", mdnsPort: mdnsPort);

		await service.StartAsync();

		Assert.False(service.IsMdnsBound);
		Assert.False(service.IsAirPlayListenerBound);

		blocker.Dispose();
		await service.StartAsync();

		Assert.True(service.IsMdnsBound);
	}

	private static UdpClient BindExclusiveUdpPort()
	{
		var client = new UdpClient(AddressFamily.InterNetwork);
		try
		{
			client.Client.ExclusiveAddressUse = true;
			client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
			return client;
		}
		catch
		{
			client.Dispose();
			throw;
		}
	}
}
