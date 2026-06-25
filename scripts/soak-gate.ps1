<#
.SYNOPSIS
    v0.3 release soak gate: evaluate a real mirroring session log against the
    minimum-1-hour soak criteria.

.DESCRIPTION
    Wraps tools/LatencyAcceptanceReport with the soak-release thresholds so the
    gate is a single, reproducible command. The 1-hour soak itself is a real
    capture (a physical device mirroring to this PC) — CI cannot synthesise an
    AirPlay sender or a GPU, so this gate is run after such a session, locally
    or on a hardware-equipped self-hosted runner.

    Reads the log iMirror actually writes after the v0.3 writable-path change:
        %LOCALAPPDATA%\iMirror\Logs\iMirror.log

    Exit code is the underlying report's exit code: 0 = PASS, 1 = FAIL,
    2 = usage/log-not-found.

.EXAMPLE
    pwsh scripts/soak-gate.ps1
    # Evaluate the default LocalAppData log with the 60-minute soak gate.

.EXAMPLE
    pwsh scripts/soak-gate.ps1 -LogPath C:\captures\session.log -RequireHighResolutionD3D
    # Evaluate a specific capture and additionally require the GPU D3D path.
#>
param(
	[string]$LogPath = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "iMirror\Logs\iMirror.log"),
	[double]$MinimumMinutes = 60,
	[long]$P95TargetMs = 150,
	[switch]$RequireHighResolutionD3D
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$project = Join-Path $repoRoot "tools\LatencyAcceptanceReport\LatencyAcceptanceReport.csproj"

if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf))
{
	Write-Error "Soak log not found: $LogPath`nRun a >=1h mirroring session first, or pass -LogPath."
	exit 2
}

# Report arg order: <log> [p95-ms] [minimum-minutes] [required-reconnects]
#                   [require-stable-advertise] [require-high-resolution-d3d]
$requireD3D = if ($RequireHighResolutionD3D) { "true" } else { "false" }

Write-Host "Soak gate: $LogPath (>= $MinimumMinutes min, p95 < ${P95TargetMs}ms, requireHighResolutionD3D=$requireD3D)"

& dotnet run -c Release --project $project -- `
	$LogPath `
	$P95TargetMs `
	$MinimumMinutes `
	0 `
	false `
	$requireD3D

exit $LASTEXITCODE
