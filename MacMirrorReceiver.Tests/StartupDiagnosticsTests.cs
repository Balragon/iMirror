using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class StartupDiagnosticsTests
{
	[Fact]
	public void CheckListeners_ReturnsOk_WhenAllListenersAreBound()
	{
		PreflightCheck check = StartupDiagnostics.CheckListeners(new StartupListenerState(
			IsMdnsBound: true,
			IsAirPlayListenerBound: true,
			IsRaopListenerBound: true,
			AirPlayListenerPort: 7000,
			RaopListenerPort: 5000,
			AirPlayListenerBindError: null,
			RaopListenerBindError: null));

		Assert.Equal(PreflightStatus.Ok, check.Status);
		Assert.Equal("All listeners bound.", check.Message);
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void CheckListeners_ReturnsOk_WhenTheFirewallAllowsOrCannotBeRead(bool allowed)
	{
		FirewallInboundCoverage firewall = allowed ? FirewallInboundCoverage.Allowed : FirewallInboundCoverage.Unknown;

		PreflightCheck check = StartupDiagnostics.CheckListeners(AllBound(), firewall);

		Assert.Equal(PreflightStatus.Ok, check.Status);
	}

	[Fact]
	public void CheckListeners_ReturnsBlocked_WhenTheFirewallDoesNotAllowIMirror_EvenThoughEveryPortBound()
	{
		// Binding succeeds under a blocking firewall; this used to report "All listeners bound."
		PreflightCheck check = StartupDiagnostics.CheckListeners(AllBound(), FirewallInboundCoverage.NotAllowed);

		Assert.Equal(PreflightStatus.Blocked, check.Status);
		Assert.Equal("listeners", check.Id);
		Assert.Contains("Windows Firewall does not allow iMirror", check.Message);
		Assert.Contains("Allow in Firewall", check.Detail);
	}

	private static StartupListenerState AllBound()
	{
		return new StartupListenerState(
			IsMdnsBound: true,
			IsAirPlayListenerBound: true,
			IsRaopListenerBound: true,
			AirPlayListenerPort: 7000,
			RaopListenerPort: 5000,
			AirPlayListenerBindError: null,
			RaopListenerBindError: null);
	}

	[Fact]
	public void CheckListeners_ReturnsBlocked_WhenAirPlayPortIsUnavailable()
	{
		PreflightCheck check = StartupDiagnostics.CheckListeners(new StartupListenerState(
			IsMdnsBound: true,
			IsAirPlayListenerBound: false,
			IsRaopListenerBound: true,
			AirPlayListenerPort: 7000,
			RaopListenerPort: 5000,
			AirPlayListenerBindError: "already in use",
			RaopListenerBindError: null));

		Assert.Equal(PreflightStatus.Blocked, check.Status);
		Assert.Contains("Port 7000 is unavailable", check.Message);
		Assert.Contains("already in use", check.Detail);
	}

	[Fact]
	public void CheckListeners_ReturnsBlocked_WhenMdnsIsBlocked()
	{
		PreflightCheck check = StartupDiagnostics.CheckListeners(new StartupListenerState(
			IsMdnsBound: false,
			IsAirPlayListenerBound: true,
			IsRaopListenerBound: true,
			AirPlayListenerPort: 7000,
			RaopListenerPort: 5000,
			AirPlayListenerBindError: null,
			RaopListenerBindError: null));

		Assert.Equal(PreflightStatus.Blocked, check.Status);
		Assert.Contains("Windows Firewall is blocking AirPlay", check.Message);
		Assert.Contains("mDNS UDP 5353", check.Detail);
		Assert.Contains("Private and Public", check.Detail);
		Assert.Contains("dynamic UDP", check.Detail);
	}

	[Fact]
	public void CheckListeners_ReturnsWarning_WhenOnlyRaopIsUnavailable()
	{
		PreflightCheck check = StartupDiagnostics.CheckListeners(new StartupListenerState(
			IsMdnsBound: true,
			IsAirPlayListenerBound: true,
			IsRaopListenerBound: false,
			AirPlayListenerPort: 7000,
			RaopListenerPort: 5000,
			AirPlayListenerBindError: null,
			RaopListenerBindError: "busy"));

		Assert.Equal(PreflightStatus.Warning, check.Status);
		Assert.Contains("Legacy audio port unavailable", check.Message);
		Assert.Contains("busy", check.Detail);
	}
}
