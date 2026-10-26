using System.Net;
using System.Net.Sockets;
using MacMirrorReceiver.Networking;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class AirPlayTimingClientTests
{
	[Fact]
	public void Start_DoesNotThrowWhenTheLocalPortIsInUse()
	{
		using var blocker = new UdpClient(AddressFamily.InterNetwork);
		blocker.Client.ExclusiveAddressUse = true;
		blocker.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
		int port = ((IPEndPoint)blocker.Client.LocalEndPoint!).Port;
		using var client = new AirPlayTimingClient(port);

		// Timing is best-effort; a bind failure must not propagate into the SETUP handler,
		// where it would end the mirror session before the SETUP response is written.
		client.Start(IPAddress.Loopback, 7010);

		Assert.False(client.IsRunning);
	}

	[Fact]
	public void Start_BindsAndRunsOnAFreePort()
	{
		using var client = new AirPlayTimingClient(0);

		client.Start(IPAddress.Loopback, 7010);

		Assert.True(client.IsRunning);
	}
}
