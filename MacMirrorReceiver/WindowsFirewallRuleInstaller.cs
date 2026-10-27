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
	private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);

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

	public static async Task<bool> HasAllowRuleForCurrentExecutableAsync(
		CancellationToken cancellationToken = default)
	{
		string? processPath = Environment.ProcessPath;
		if (string.IsNullOrWhiteSpace(processPath))
		{
			return false;
		}

		return await HasAllowRuleForExecutableAsync(processPath, cancellationToken);
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

		try
		{
			// The script is passed inline (-EncodedCommand) rather than through a file in %TEMP%:
			// an elevated PowerShell only reads a -File script after the user accepts the UAC
			// prompt, which gave any unelevated process running as the same user a window to
			// replace the file and have its contents run as administrator. PowerShell itself is
			// resolved by absolute path so ShellExecute cannot pick up a planted powershell.exe
			// from the working directory.
			using Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = PowerShellPath,
				Arguments = BuildEncodedCommandArguments(BuildFirewallRuleScript(fullPath)),
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
	}

	/// <summary>Absolute path of Windows PowerShell; never resolved through PATH or the CWD.</summary>
	internal static string PowerShellPath { get; } =
		Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

	/// <summary>
	/// Builds the PowerShell command line that runs <paramref name="script"/> inline. The script
	/// is base64 of its UTF-16LE bytes, which is what -EncodedCommand expects, so no quoting or
	/// escaping of the script text is involved.
	/// </summary>
	internal static string BuildEncodedCommandArguments(string script)
	{
		string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
		return "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
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

	internal static async Task<bool> HasAllowRuleForExecutableAsync(
		string executablePath,
		CancellationToken cancellationToken = default)
	{
		string fullPath;
		try
		{
			fullPath = Path.GetFullPath(executablePath);
		}
		catch
		{
			return false;
		}

		try
		{
			using Process? process = Process.Start(new ProcessStartInfo
			{
				FileName = PowerShellPath,
				Arguments = BuildEncodedCommandArguments(BuildFirewallRuleCheckScript(fullPath)),
				UseShellExecute = false,
				CreateNoWindow = true,
				WindowStyle = ProcessWindowStyle.Hidden
			});

			if (process == null)
			{
				return false;
			}

			Task waitTask = process.WaitForExitAsync(cancellationToken);
			Task timeoutTask = Task.Delay(CheckTimeout, cancellationToken);
			Task completed = await Task.WhenAny(waitTask, timeoutTask);
			if (completed == timeoutTask)
			{
				TryKillProcess(process);
				return false;
			}

			await waitTask;
			return process.ExitCode == 0;
		}
		catch
		{
			return false;
		}
	}

	internal static string BuildFirewallRuleCheckScript(string executablePath)
	{
		string program = QuotePowerShellSingleQuotedString(Path.GetFullPath(executablePath));

		return $$"""
$ErrorActionPreference = 'Stop'
$displayName = 'iMirror AirPlay Receiver'
$program = {{program}}
$hasMatchingAllowRule = $false

foreach ($rule in Get-NetFirewallRule -DisplayName $displayName -ErrorAction SilentlyContinue) {
    if ($rule.Direction -ne 'Inbound' -or $rule.Action -ne 'Allow' -or $rule.Enabled -ne 'True') {
        continue
    }

    $appFilter = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $rule -ErrorAction SilentlyContinue
    if ($appFilter -and [System.String]::Equals($appFilter.Program, $program, [System.StringComparison]::OrdinalIgnoreCase)) {
        $hasMatchingAllowRule = $true
        break
    }
}

if ($hasMatchingAllowRule) {
    exit 0
}

exit 1
""";
	}

	private static string QuotePowerShellSingleQuotedString(string value)
	{
		return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
	}

	private static void TryKillProcess(Process process)
	{
		try
		{
			process.Kill(entireProcessTree: true);
		}
		catch
		{
		}
	}
}
