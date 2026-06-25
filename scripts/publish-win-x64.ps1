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
		# Prefer Gyan.FFmpeg.Essentials over the larger Gyan.FFmpeg full_build.
		$wingetFfmpeg = Get-ChildItem -LiteralPath $wingetPackages -Filter "ffmpeg.exe" -Recurse -ErrorAction SilentlyContinue |
			Where-Object { $_.FullName -like "*Gyan.FFmpeg.Essentials*" } |
			Select-Object -First 1
		if ($wingetFfmpeg)
		{
			return $wingetFfmpeg.FullName
		}
		# Fall back to full_build; redistribution needs explicit license compliance review.
		$wingetFfmpegFull = Get-ChildItem -LiteralPath $wingetPackages -Filter "ffmpeg.exe" -Recurse -ErrorAction SilentlyContinue |
			Where-Object { $_.FullName -like "*Gyan.FFmpeg*" -and $_.FullName -notlike "*Gyan.FFmpeg.Essentials*" } |
			Select-Object -First 1
		if ($wingetFfmpegFull)
		{
			Write-Warning "Found Gyan.FFmpeg full_build. For this package use Gyan.FFmpeg.Essentials instead. Install with: winget install Gyan.FFmpeg.Essentials"
			return $wingetFfmpegFull.FullName
		}
	}

	return $null
}

function Test-FfmpegBuild([string]$Path)
{
	$output = & $Path -version 2>&1
	$versionText = $output -join "`n"
	if ($versionText -match "full_build")
	{
		Write-Warning "Resolved FFmpeg is a full_build. Use Gyan.FFmpeg.Essentials for the release package."
	}
	elseif ($versionText -notmatch "essentials_build")
	{
		Write-Warning "Resolved FFmpeg build flavor was not recognized as Gyan essentials_build. Review licensing before redistribution."
	}
}

function Find-FfmpegLicense([string]$FfmpegPath)
{
	$searchRoot = Split-Path -Parent $FfmpegPath
	for ($i = 0; $i -lt 3 -and $searchRoot; $i++)
	{
		if ($searchRoot -eq [IO.Path]::GetPathRoot($searchRoot))
		{
			break
		}

		$license = Get-ChildItem -LiteralPath $searchRoot -File -Recurse -ErrorAction SilentlyContinue |
			Where-Object { $_.Name -in @("LICENSE", "LICENSE.txt", "COPYING", "COPYING.txt", "COPYING.GPLv3", "COPYING.LGPLv3") } |
			Select-Object -First 1
		if ($license)
		{
			return $license.FullName
		}

		$parent = Split-Path -Parent $searchRoot
		if ($parent -eq $searchRoot)
		{
			break
		}
		$searchRoot = $parent
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

	# Resolve the short commit SHA so InformationalVersion gets a "+<sha>" provenance suffix
	# (see docs/specs/v02-decisions.md: version-policy). The SDK appends SourceRevisionId to
	# InformationalVersion automatically.
	$shortSha = ""
	try
	{
		$shortSha = (& git rev-parse --short HEAD 2>$null).Trim()
	}
	catch
	{
		$shortSha = ""
	}

	# A tagged release passes a real SemVer (e.g. "0.2.0" or "0.2.0-rc.1") and should drive the
	# assembly version. Dev/local runs use a bare commit SHA or timestamp as $Version (used only
	# for the package filename), so the assembly version stays at the csproj default there.
	$versionMsbuildArgs = @()
	if ($Version -match '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(-[0-9A-Za-z.-]+)?$')
	{
		$fourth = if ($Matches[4]) { $Matches[4] } else { "0" }
		$assemblyVersion = "$($Matches[1]).$($Matches[2]).$($Matches[3]).$fourth"
		$versionMsbuildArgs += "-p:Version=$Version"
		$versionMsbuildArgs += "-p:FileVersion=$assemblyVersion"
		$versionMsbuildArgs += "-p:AssemblyVersion=$assemblyVersion"
	}
	if ($shortSha)
	{
		$versionMsbuildArgs += "-p:SourceRevisionId=$shortSha"
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
	if ($Runtime -ne "win-x64")
	{
		throw "This release package is configured for win-x64 only. Runtime was '$Runtime'."
	}

	$publishArgs = @(
		".\MacMirrorReceiver.csproj",
		"-c", $Configuration,
		"-p:DebugType=none",
		"-p:DebugSymbols=false",
		"-o", $publishDir
	) + $versionMsbuildArgs
	& dotnet publish @publishArgs
	if ($LASTEXITCODE -ne 0)
	{
		exit $LASTEXITCODE
	}

	Copy-Item -Path (Join-Path $publishDir "*") -Destination $packageDir -Recurse -Force

	Get-ChildItem -LiteralPath $packageDir -Recurse -File -ErrorAction SilentlyContinue |
		Where-Object { $_.Extension -in @(".pdb", ".log", ".h264", ".bgra") } |
		Remove-Item -Force

	$ffmpegBundled = $false
	$resolvedFfmpeg = Find-Ffmpeg $repoRoot $FfmpegPath
	if ($resolvedFfmpeg)
	{
		Test-FfmpegBuild $resolvedFfmpeg
		$packageFfmpegDir = Join-Path $packageDir "tools\ffmpeg\bin"
		New-Item -ItemType Directory -Force -Path $packageFfmpegDir | Out-Null
		Copy-Item -LiteralPath $resolvedFfmpeg -Destination (Join-Path $packageFfmpegDir "ffmpeg.exe") -Force
		$ffmpegLicense = Find-FfmpegLicense $resolvedFfmpeg
		if (-not $ffmpegLicense)
		{
			throw "FFmpeg license file was not found near '$resolvedFfmpeg'. Bundle a LICENSE/COPYING file with ffmpeg.exe before packaging."
		}
		Copy-Item -LiteralPath $ffmpegLicense -Destination (Join-Path $packageFfmpegDir "LICENSE.txt") -Force
		$ffmpegBundled = $true
		Write-Host "Bundled FFmpeg: $resolvedFfmpeg"
		Write-Host "Bundled FFmpeg license: $ffmpegLicense"
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
		"THIRD_PARTY_NOTICES.txt",
		"ThirdParty\playfair\LICENSE.md",
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
		$packageFfmpegLicense = Join-Path $packageDir "tools\ffmpeg\bin\LICENSE.txt"
		if (-not (Test-Path -LiteralPath $packageFfmpegLicense -PathType Leaf))
		{
			throw "Package is missing bundled FFmpeg license file."
		}
	}

	$ffmpegNote = if ($ffmpegBundled) {
		"  - FFmpeg is bundled under tools\ffmpeg\bin, with its license at tools\ffmpeg\bin\LICENSE.txt."
	} else {
		"  - FFmpeg was not bundled; put ffmpeg.exe on PATH or at tools\ffmpeg\bin before mirroring."
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
$ffmpegNote
  - Logs, diagnostics and settings are written under %LOCALAPPDATA%\iMirror
    (not next to iMirror.exe), so the app works when installed read-only.
  - iMirror.log (under %LOCALAPPDATA%\iMirror\Logs) can contain local session
    details; delete it before sharing logs or screenshots.
  - Third-party license and source notices: see THIRD_PARTY_NOTICES.txt.
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
