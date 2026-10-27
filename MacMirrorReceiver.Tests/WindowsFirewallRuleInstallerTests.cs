using System;
using System.Diagnostics;
using System.IO;
using System.Text;
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
	public void BuildEncodedCommandArguments_EncodesScriptAsUtf16Base64WithoutAFile()
	{
		string script = WindowsFirewallRuleInstaller.BuildFirewallRuleScript(@"C:\Users\John's PC\iMirror.exe");

		string arguments = WindowsFirewallRuleInstaller.BuildEncodedCommandArguments(script);

		Assert.StartsWith("-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ", arguments);
		Assert.DoesNotContain("-File", arguments);
		string encoded = arguments.Substring(arguments.LastIndexOf(' ') + 1);
		Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(encoded)));
	}

	[Fact]
	public void PowerShellPath_IsAbsoluteUnderTheSystemDirectory()
	{
		string path = WindowsFirewallRuleInstaller.PowerShellPath;

		Assert.True(Path.IsPathRooted(path));
		Assert.StartsWith(Environment.SystemDirectory, path, StringComparison.OrdinalIgnoreCase);
		Assert.EndsWith(@"WindowsPowerShell\v1.0\powershell.exe", path, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void BuildFirewallRuleCheckScript_EscapesPowerShellSingleQuotes()
	{
		string script = WindowsFirewallRuleInstaller.BuildFirewallRuleCheckScript(@"C:\Users\John's PC\iMirror.exe");

		Assert.Contains(@"$program = 'C:\Users\John''s PC\iMirror.exe'", script);
	}

	[Fact]
	public void BuildInboundCoverageCheckScript_MatchesAnyRuleForTheProgramAndLetsBlockRulesWin()
	{
		string script = WindowsFirewallRuleInstaller.BuildInboundCoverageCheckScript(@"C:\Users\John's PC\iMirror.exe");

		Assert.Contains(@"$program = 'C:\Users\John''s PC\iMirror.exe'", script);
		// Rules Windows creates from its own prompt are named after the exe, so match on the program only.
		Assert.Contains("Get-NetFirewallApplicationFilter -Program $program", script);
		Assert.DoesNotContain("$displayName", script);
		Assert.True(
			script.IndexOf("-eq 'Block'", StringComparison.Ordinal) < script.IndexOf("-eq 'Allow'", StringComparison.Ordinal),
			"a block rule must be checked before allow rules");
		Assert.Contains("trap { exit 2 }", script);
	}

	[Fact]
	public void BuildInboundCoverageCheckScript_IsValidPowerShell()
	{
		string scriptPath = Path.Combine(Path.GetTempPath(), "iMirror-coverage-" + Guid.NewGuid().ToString("N") + ".ps1");
		File.WriteAllText(scriptPath, WindowsFirewallRuleInstaller.BuildInboundCoverageCheckScript(@"C:\Users\John's PC\iMirror.exe"));
		try
		{
			string parse =
				"$errors = $null; " +
				$"[void][System.Management.Automation.Language.Parser]::ParseFile('{scriptPath.Replace("'", "''", StringComparison.Ordinal)}', [ref]$null, [ref]$errors); " +
				"$errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; exit $errors.Count";
			using Process process = Process.Start(new ProcessStartInfo
			{
				FileName = WindowsFirewallRuleInstaller.PowerShellPath,
				Arguments = WindowsFirewallRuleInstaller.BuildEncodedCommandArguments(parse),
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardError = true
			})!;
			string errors = process.StandardError.ReadToEnd();
			Assert.True(process.WaitForExit(30_000), "PowerShell parser did not finish");
			Assert.True(process.ExitCode == 0, "parse errors: " + errors);
		}
		finally
		{
			File.Delete(scriptPath);
		}
	}
}
