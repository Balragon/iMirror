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

	[Fact]
	public void BuildFirewallRuleCheckScript_DetectsEnabledInboundAllowRuleForCurrentProgram()
	{
		string script = WindowsFirewallRuleInstaller.BuildFirewallRuleCheckScript(@"C:\Tools\iMirror\iMirror.exe");

		Assert.Contains("$displayName = 'iMirror AirPlay Receiver'", script);
		Assert.Contains("$hasMatchingAllowRule", script);
		Assert.Contains("Get-NetFirewallRule", script);
		Assert.Contains("$rule.Direction -ne 'Inbound'", script);
		Assert.Contains("$rule.Action -ne 'Allow'", script);
		Assert.Contains("$rule.Enabled -ne 'True'", script);
		Assert.Contains("Get-NetFirewallApplicationFilter", script);
		Assert.Contains(@"$program = 'C:\Tools\iMirror\iMirror.exe'", script);
		Assert.Contains("exit 0", script);
		Assert.Contains("exit 1", script);
	}

	[Fact]
	public void BuildFirewallRuleCheckScript_EscapesPowerShellSingleQuotes()
	{
		string script = WindowsFirewallRuleInstaller.BuildFirewallRuleCheckScript(@"C:\Users\John's PC\iMirror.exe");

		Assert.Contains(@"$program = 'C:\Users\John''s PC\iMirror.exe'", script);
	}
}
