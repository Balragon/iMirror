param(
	[string]$Configuration = "Release",
	[string]$Runtime = "win-x64",
	[string]$OutputRoot = "artifacts",
	[string]$Version = "",
	[string]$FfmpegPath = "",
	[switch]$AllowMissingFfmpeg,
	[switch]$KeepPublishOutput,
	[switch]$NoZip
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Resolve-RepoPath([string]$Path)
{
	return (Resolve-Path -LiteralPath $Path).Path
}

function Assert-ChildPath([string]$Root, [string]$Path)
{
	$rootWithSlash = $Root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
	if ($Path -ne $Root -and -not $Path.StartsWith($rootWithSlash, [StringComparison]::OrdinalIgnoreCase))
	{
		throw "Refusing to operate outside repository: $Path"
	}
}

function Remove-PathIfExists([string]$Root, [string]$Path)
{
	if (Test-Path -LiteralPath $Path)
	{
		$resolved = Resolve-RepoPath $Path
		Assert-ChildPath $Root $resolved
		Remove-Item -LiteralPath $resolved -Recurse -Force
	}
}

function Find-Ffmpeg([string]$RepoRoot, [string]$ExplicitPath)
{
	if ($ExplicitPath)
	{
		if (-not (Test-Path -LiteralPath $ExplicitPath -PathType Leaf))
		{
			throw "FFmpeg was not found at -FfmpegPath '$ExplicitPath'."
		}
		return (Resolve-Path -LiteralPath $ExplicitPath).Path
	}

	$repoFfmpeg = Join-Path $RepoRoot "tools\ffmpeg\bin\ffmpeg.exe"
	if (Test-Path -LiteralPath $repoFfmpeg -PathType Leaf)
	{
		return (Resolve-Path -LiteralPath $repoFfmpeg).Path
	}

	$pathFfmpeg = Get-Command "ffmpeg.exe" -ErrorAction SilentlyContinue
	if ($pathFfmpeg -and (Test-Path -LiteralPath $pathFfmpeg.Source -PathType Leaf))
	{
		return $pathFfmpeg.Source
	}

	$wingetPackages = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) "Microsoft\WinGet\Packages"
	if (Test-Path -LiteralPath $wingetPackages -PathType Container)
	{
		# Prefer Gyan.FFmpeg.Essentials (LGPL-compatible) over Gyan.FFmpeg full_build (GPLv3).
		$wingetFfmpeg = Get-ChildItem -LiteralPath $wingetPackages -Filter "ffmpeg.exe" -Recurse -ErrorAction SilentlyContinue |
			Where-Object { $_.FullName -like "*Gyan.FFmpeg.Essentials*" } |
			Select-Object -First 1
		if ($wingetFfmpeg)
		{
			return $wingetFfmpeg.FullName
		}
		# Fall back to full_build — redistributing requires GPL compliance.
		$wingetFfmpegFull = Get-ChildItem -LiteralPath $wingetPackages -Filter "ffmpeg.exe" -Recurse -ErrorAction SilentlyContinue |
			Where-Object { $_.FullName -like "*Gyan.FFmpeg*" -and $_.FullName -notlike "*Gyan.FFmpeg.Essentials*" } |
			Select-Object -First 1
		if ($wingetFfmpegFull)
		{
			Write-Warning "Found Gyan.FFmpeg full_build (GPLv3). For redistribution use Gyan.FFmpeg.Essentials (LGPL). Install with: winget install Gyan.FFmpeg.Essentials"
			return $wingetFfmpegFull.FullName
		}
	}

	return $null
}

$repoRoot = Resolve-RepoPath (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try
{
	if (-not $Version)
	{
		$gitShort = ""
		try
		{
			$gitShort = (& git rev-parse --short HEAD 2>$null).Trim()
		}
		catch
		{
			$gitShort = ""
		}

		$Version = if ($gitShort) { $gitShort } else { Get-Date -Format "yyyyMMdd-HHmmss" }
	}

	$packageName = "iMirror-$Version-$Runtime"
	$outputRootFull = Join-Path $repoRoot $OutputRoot
	$publishRoot = Join-Path $outputRootFull "publish"
	$publishDir = Join-Path $publishRoot $packageName
	$packageDir = Join-Path $outputRootFull "package\$packageName"
	$zipPath = Join-Path $outputRootFull "$packageName.zip"

	New-Item -ItemType Directory -Force -Path $outputRootFull | Out-Null
	Remove-PathIfExists $repoRoot $publishDir
	Remove-PathIfExists $repoRoot $packageDir
	Remove-PathIfExists $repoRoot $zipPath
	New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
	New-Item -ItemType Directory -Force -Path $packageDir | Out-Null

	Write-Host "Publishing iMirror ($Configuration, $Runtime, self-contained)..."
	& dotnet publish ".\MacMirrorReceiver.csproj" `
		-c $Configuration `
		-r $Runtime `
		--self-contained true `
		-p:PublishSingleFile=false `
		-p:DebugType=none `
		-p:DebugSymbols=false `
		-o $publishDir
	if ($LASTEXITCODE -ne 0)
	{
		exit $LASTEXITCODE
	}

	Copy-Item -Path (Join-Path $publishDir "*") -Destination $packageDir -Recurse -Force

	Get-ChildItem -LiteralPath $packageDir -Recurse -File -ErrorAction SilentlyContinue |
		Where-Object { $_.Extension -in @(".pdb", ".log", ".h264", ".bgra") } |
		Remove-Item -Force

	$resolvedFfmpeg = Find-Ffmpeg $repoRoot $FfmpegPath
	if ($resolvedFfmpeg)
	{
		$packageFfmpegDir = Join-Path $packageDir "tools\ffmpeg\bin"
		New-Item -ItemType Directory -Force -Path $packageFfmpegDir | Out-Null
		Copy-Item -LiteralPath $resolvedFfmpeg -Destination (Join-Path $packageFfmpegDir "ffmpeg.exe") -Force
		Write-Host "Bundled FFmpeg: $resolvedFfmpeg"
	}
	elseif (-not $AllowMissingFfmpeg)
	{
		throw "FFmpeg was not found. Put it at tools\ffmpeg\bin\ffmpeg.exe, put ffmpeg.exe on PATH, or pass -FfmpegPath."
	}
	else
	{
		Write-Warning "FFmpeg was not bundled. Audio decode and software video fallback will require ffmpeg.exe on PATH."
	}

	$requiredFiles = @(
		"iMirror.exe",
		"iMirror.dll",
		"iMirror.runtimeconfig.json",
		"iMirror.deps.json",
		"ThirdParty\playfair\omg_hax.h",
		"ThirdParty\playfair\omg_hax.c",
		"ThirdParty\playfair\sap_hash.c"
	)
	foreach ($relative in $requiredFiles)
	{
		$requiredPath = Join-Path $packageDir $relative
		if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf))
		{
			throw "Package is missing required file: $relative"
		}
	}

	if (-not $AllowMissingFfmpeg)
	{
		$packageFfmpeg = Join-Path $packageDir "tools\ffmpeg\bin\ffmpeg.exe"
		if (-not (Test-Path -LiteralPath $packageFfmpeg -PathType Leaf))
		{
			throw "Package is missing bundled FFmpeg."
		}
	}

	$readme = @"
iMirror Windows AirPlay Receiver

Run:
  iMirror.exe

Use:
  1. Keep this folder together; do not move files out of it.
  2. Make sure this PC and the sender are on the same local network.
  3. Open Screen Mirroring / AirPlay on the sender and choose iMirror.

Notes:
  - This win-x64 package is self-contained; .NET does not need to be installed.
  - FFmpeg is bundled under tools\ffmpeg\bin when available at package time.
  - iMirror.log is written next to iMirror.exe and can contain local session details.
  - Delete logs before sharing the package folder or screenshots.
"@
	Set-Content -LiteralPath (Join-Path $packageDir "README.txt") -Value $readme -Encoding ASCII

	if (-not $NoZip)
	{
		Write-Host "Creating zip: $zipPath"
		Add-Type -AssemblyName System.IO.Compression.FileSystem
		[IO.Compression.ZipFile]::CreateFromDirectory(
			$packageDir,
			$zipPath,
			[IO.Compression.CompressionLevel]::Fastest,
			$true)
	}

	if (-not $KeepPublishOutput)
	{
		Remove-PathIfExists $repoRoot $publishDir
		if ((Test-Path -LiteralPath $publishRoot -PathType Container) -and -not (Get-ChildItem -LiteralPath $publishRoot -Force))
		{
			Remove-PathIfExists $repoRoot $publishRoot
		}
	}

	Write-Host "Package directory: $packageDir"
	if (-not $NoZip)
	{
		Write-Host "Package zip: $zipPath"
	}
}
finally
{
	Pop-Location
}
