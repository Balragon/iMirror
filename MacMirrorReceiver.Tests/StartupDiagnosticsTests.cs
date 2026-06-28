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
		Assert.Contains("Allow iMirror through Windows Firewall", check.Message);
		Assert.Contains("mDNS UDP 5353", check.Detail);
		Assert.Contains("Screen Mirroring", check.Detail);
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
