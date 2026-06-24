using MacMirrorReceiver;
using Xunit;

namespace MacMirrorReceiver.Tests;

public sealed class WindowsFirewallRuleInstallerTests
{
	[Fact]
	public void BuildFirewallRuleScript_AllowsCurrentProgramForPrivateAndPublicProfiles()
	{
		string script = WindowsFirewallRuleInstaller.BuildFirewallRuleScript(@"C:\Tools\iMirror\iMirror.exe");

		Assert.Contains("$displayName = 'iMirror AirPlay Receiver'", script);
		Assert.Contains("-Direction Inbound", script);
		Assert.Contains("-Action Allow", script);
		Assert.Contains("-Program $program", script);
		Assert.Contains("-Profile Private,Public", script);
		Assert.Contains(@"$program = 'C:\Tools\iMirror\iMirror.exe'", script);
		Assert.Contains("dynamic audio RTP", script);
	}

	[Fact]
	public void BuildFirewallRuleScript_EscapesPowerShellSingleQuotes()
	{
		string script = WindowsFirewallRuleInstaller.BuildFirewallRuleScript(@"C:\Users\John's PC\iMirror.exe");

		Assert.Contains(@"$program = 'C:\Users\John''s PC\iMirror.exe'", script);
	}
}
