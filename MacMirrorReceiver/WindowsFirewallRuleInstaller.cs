using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MacMirrorReceiver;

internal enum FirewallRuleInstallStatus
{
	Success,
	Cancelled,
	Failed,
	TimedOut
}

internal sealed record FirewallRuleInstallResult(
	FirewallRuleInstallStatus Status,
	string Message);

internal static class WindowsFirewallRuleInstaller
{
	private const int UserCancelledElevationError = 1223;
	private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(5);

	public static async Task<FirewallRuleInstallResult> AllowCurrentExecutableAsync(
		CancellationToken cancellationToken = default)
	{
		string? processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(processPath))
		{
			return new FirewallRuleInstallResult(
				FirewallRuleInstallStatus.Failed,
				"Could not resolve the current iMirror executable path.");
		}

		return await AllowExecutableAsync(processPath, cancellationToken);
	}

	internal static async Task<FirewallRuleInstallResult> AllowExecutableAsync(
		string executablePath,
		CancellationToken cancellationToken = default)
	{
		string fullPath;
		try
		{
			fullPath = Path.GetFullPath(executablePath);
		}
		catch (Exception ex)
		{
			return new FirewallRuleInstallResult(
				FirewallRuleInstallStatus.Failed,
				"Could not normalize the iMirror executable path: " + ex.Message);
		}

		if (!File.Exists(fullPath))
		{
			return new FirewallRuleInstallResult(
				FirewallRuleInstallStatus.Failed,
				"Could not find the iMirror executable at " + fullPath);
		}

		string scriptPath;
		try
		{
			scriptPath = Path.Combine(Path.GetTempPath(), $"iMirror-firewall-{Guid.NewGuid():N}.ps1");
			File.WriteAllText(scriptPath, BuildFirewallRuleScript(fullPath), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
		}
		catch (Exception ex)
		{
			return new FirewallRuleInstallResult(
				FirewallRuleInstallStatus.Failed,
				"Could not prepare the firewall rule installer: " + ex.Message);
		}

		try
		{
			using Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = "powershell.exe",
				Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + QuoteCommandLineArgument(scriptPath),
				UseShellExecute = true,
				Verb = "runas",
				WindowStyle = ProcessWindowStyle.Hidden
			});

			if (process == null)
			{
				return new FirewallRuleInstallResult(
					FirewallRuleInstallStatus.Failed,
					"Windows did not start the elevated firewall rule installer.");
			}

			Task waitTask = process.WaitForExitAsync(cancellationToken);
			Task timeoutTask = Task.Delay(InstallTimeout, cancellationToken);
			Task completed = await Task.WhenAny(waitTask, timeoutTask);
			if (completed == timeoutTask)
			{
				return new FirewallRuleInstallResult(
					FirewallRuleInstallStatus.TimedOut,
					"Timed out waiting for the Windows Firewall rule installer.");
			}

			await waitTask;
			if (process.ExitCode == 0)
			{
				return new FirewallRuleInstallResult(
					FirewallRuleInstallStatus.Success,
					"Windows Firewall now allows inbound traffic for iMirror.");
			}

			return new FirewallRuleInstallResult(
				FirewallRuleInstallStatus.Failed,
				$"Windows Firewall rule installer exited with code {process.ExitCode}.");
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == UserCancelledElevationError)
		{
			return new FirewallRuleInstallResult(
				FirewallRuleInstallStatus.Cancelled,
				"Windows Firewall permission was cancelled.");
		}
		catch (OperationCanceledException)
		{
			return new FirewallRuleInstallResult(
				FirewallRuleInstallStatus.Cancelled,
				"Windows Firewall permission was cancelled.");
		}
		catch (Exception ex)
		{
			return new FirewallRuleInstallResult(
				FirewallRuleInstallStatus.Failed,
				"Could not update Windows Firewall: " + ex.Message);
		}
		finally
		{
			TryDeleteFile(scriptPath);
		}
	}

	internal static string BuildFirewallRuleScript(string executablePath)
	{
		string program = QuotePowerShellSingleQuotedString(Path.GetFullPath(executablePath));

		return $$"""
$ErrorActionPreference = 'Stop'
$displayName = 'iMirror AirPlay Receiver'
$program = {{program}}
$description = 'Allows AirPlay discovery, video, RAOP, and dynamic audio RTP for iMirror.'
$matchingRules = @()

foreach ($rule in Get-NetFirewallRule -DisplayName $displayName -ErrorAction SilentlyContinue) {
    $appFilter = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $rule -ErrorAction SilentlyContinue
    if ($appFilter -and [System.String]::Equals($appFilter.Program, $program, [System.StringComparison]::OrdinalIgnoreCase)) {
        $matchingRules += $rule
    }
}

if ($matchingRules.Count -gt 0) {
    $matchingRules | Set-NetFirewallRule -Enabled True -Action Allow -Profile Private,Public
} else {
    New-NetFirewallRule `
        -DisplayName $displayName `
        -Group 'iMirror' `
        -Direction Inbound `
        -Action Allow `
        -Program $program `
        -Profile Private,Public `
        -Enabled True `
        -Description $description | Out-Null
}
""";
	}

	private static string QuotePowerShellSingleQuotedString(string value)
	{
		return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
	}

	private static string QuoteCommandLineArgument(string value)
	{
		return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
	}

	private static void TryDeleteFile(string path)
	{
		try
		{
			File.Delete(path);
		}
		catch
		{
		}
	}
}
