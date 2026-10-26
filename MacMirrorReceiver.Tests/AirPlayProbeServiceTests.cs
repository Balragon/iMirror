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
		// Ephemeral TCP ports so the test never competes with a running iMirror, iTunes or
		// Bonjour for 7000/5000/7100 on the machine running the tests.
		using var service = new AirPlayProbeService("iMirror Test", mdnsPort: mdnsPort, listenerPorts: AirPlayListenerPorts.Ephemeral);

		await service.StartAsync();

		Assert.False(service.IsMdnsBound);
		Assert.False(service.IsAirPlayListenerBound);

		blocker.Dispose();
		await service.StartAsync();

		Assert.True(service.IsMdnsBound);
		Assert.True(service.IsAirPlayListenerBound);
		Assert.True(service.IsRaopListenerBound);
		Assert.NotEqual(0, service.AirPlayListenerPort);
		Assert.NotEqual(0, service.RaopListenerPort);
		Assert.Null(service.AirPlayListenerBindError);
	}

	[Fact]
	public async Task StartAsync_ReportsBindErrorForTheAirPlayPortOnly()
	{
		using var blocker = new TcpListener(IPAddress.Any, 0);
		blocker.Server.ExclusiveAddressUse = true;
		blocker.Start();
		int busyPort = ((IPEndPoint)blocker.LocalEndpoint).Port;
		using UdpClient mdnsBlocker = BindExclusiveUdpPort();
		int mdnsPort = ((IPEndPoint)mdnsBlocker.Client.LocalEndPoint!).Port;
		mdnsBlocker.Dispose();
		using var service = new AirPlayProbeService(
			"iMirror Test",
			mdnsPort: mdnsPort,
			listenerPorts: AirPlayListenerPorts.Ephemeral with { AirPlay = busyPort });

		await service.StartAsync();

		Assert.True(service.IsMdnsBound);
		Assert.False(service.IsAirPlayListenerBound);
		Assert.NotNull(service.AirPlayListenerBindError);
		Assert.True(service.IsRaopListenerBound);
		Assert.Null(service.RaopListenerBindError);
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
