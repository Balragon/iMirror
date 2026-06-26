param(
	[string]$Version,
	[string]$Runtime = "win-x64",
	[string]$OutputRoot = "artifacts",
	[string]$SourceDir = "",
	[string]$IsccPath = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-RepoPath([string]$Path)
{
	return (Resolve-Path -LiteralPath $Path).Path
}

function Find-Iscc([string]$ExplicitPath)
{
	if ($ExplicitPath)
	{
		if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf))
		{
			throw "ISCC.exe was not found at '$ExplicitPath'."
		}
		return (Resolve-Path -LiteralPath $ExplicitPath).Path
	}

	$command = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
	if ($command -and (Test-Path -LiteralPath $command.Source -PathType Leaf))
	{
		return $command.Source
	}

	$candidates = @()
	if (${env:ProgramFiles(x86)})
	{
		$candidates += (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
	}
	if ($env:ProgramFiles)
	{
		$candidates += (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
	}
	if ($env:LOCALAPPDATA)
	{
		$candidates += (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
	}
	foreach ($candidate in $candidates)
	{
		if (Test-Path -LiteralPath $candidate -PathType Leaf)
		{
			return $candidate
		}
	}

	throw "ISCC.exe was not found. Install Inno Setup 6 or pass -IsccPath."
}

if (-not $Version)
{
	throw "-Version is required, for example: -Version 0.4.0"
}
if ($Runtime -ne "win-x64")
{
	throw "The iMirror installer is currently win-x64 only. Runtime was '$Runtime'."
}

$repoRoot = Resolve-RepoPath (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try
{
	$outputRootFull = Join-Path $repoRoot $OutputRoot
	if (-not $SourceDir)
	{
		$SourceDir = Join-Path $outputRootFull "package\iMirror-$Version-$Runtime"
	}

	$sourceDirFull = Resolve-RepoPath $SourceDir
	$outputRootFull = (New-Item -ItemType Directory -Force -Path $outputRootFull).FullName
	$iscc = Find-Iscc $IsccPath
	$scriptPath = Resolve-RepoPath "installer\iMirror.iss"

	if (-not (Test-Path -LiteralPath (Join-Path $sourceDirFull "iMirror.exe") -PathType Leaf))
	{
		throw "Installer source directory must contain iMirror.exe: $sourceDirFull"
	}

	Write-Host "Building Inno Setup installer..."
	Write-Host "Source: $sourceDirFull"
	Write-Host "Output: $outputRootFull"
	& $iscc "/DMyAppVersion=$Version" "/DMySourceDir=$sourceDirFull" "/DMyOutputDir=$outputRootFull" $scriptPath
	if ($LASTEXITCODE -ne 0)
	{
		exit $LASTEXITCODE
	}

	$setupPath = Join-Path $outputRootFull "iMirror-$Version-setup.exe"
	if (-not (Test-Path -LiteralPath $setupPath -PathType Leaf))
	{
		throw "Installer build finished but expected setup was not found: $setupPath"
	}

	Write-Host "Installer: $setupPath"
}
finally
{
	Pop-Location
}
