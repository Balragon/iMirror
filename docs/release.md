# Release Packaging

This page describes the Windows user package flow. The package target is a
self-contained `win-x64` zip that can run without installing .NET.

## Prerequisites

- Windows x64
- .NET 8 SDK for building
- FFmpeg available from one of these locations:
  - `tools\ffmpeg\bin\ffmpeg.exe`
  - `ffmpeg.exe` on `PATH`
  - an explicit `-FfmpegPath C:\path\to\ffmpeg.exe`

FFmpeg is required for AAC-ELD audio decode and for the software video fallback.
A release package should bundle it.

**Use `Gyan.FFmpeg.Essentials` (LGPL-compatible).** The `Gyan.FFmpeg` full_build
includes GPL components and must not be redistributed without GPL compliance.
Install the correct build:

```powershell
winget install Gyan.FFmpeg.Essentials
```

The publish script auto-detects and prefers Essentials; it warns and falls back
to full_build if only that is installed.

## Build The Zip

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-win-x64.ps1
```

Optional versioned package name:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-win-x64.ps1 -Version 0.1.0
```

Outputs:

- `artifacts\package\iMirror-<version>-win-x64\`
- `artifacts\iMirror-<version>-win-x64.zip`

The zip contains only the runnable app payload, bundled FFmpeg, FairPlay table
files, runtime dependencies, and a short `README.txt`.
Intermediate publish output is removed by default; pass `-KeepPublishOutput` if
you need to inspect it.

## Package Smoke Test

Test the zip from a folder outside the repository so the app cannot accidentally
read source-tree files.

```powershell
$zip = Resolve-Path .\artifacts\iMirror-<version>-win-x64.zip
$testRoot = Join-Path $env:TEMP "imirror-package-smoke"
Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
Expand-Archive -LiteralPath $zip -DestinationPath $testRoot -Force
Start-Process -FilePath (Join-Path $testRoot "iMirror-<version>-win-x64\iMirror.exe")
```

Expected result:

- `iMirror.exe` starts without requiring .NET installation.
- The package folder contains `ThirdParty\playfair\*.c` and `*.h`.
- The package folder contains `tools\ffmpeg\bin\ffmpeg.exe`.
- A sender discovers `iMirror` on the local network.
- Mirroring starts, video appears, and audio plays when the sender provides audio.
- `iMirror.log` is written next to `iMirror.exe`.

For product validation, run the latency report from the source tree against the
package log after an active mirroring session:

```powershell
dotnet run --project .\tools\LatencyAcceptanceReport\LatencyAcceptanceReport.csproj -c Release -- C:\path\to\package\iMirror.log 150 10
```

## Release Hygiene

Do not include these in a user zip:

- `.git\`
- source files
- `docs\`
- validation `tools\` projects
- `bin\`, `obj\`, or `artifacts\publish\`
- `*.pdb`
- `*.log`
- `*.h264`
- `*.bgra`
- diagnostic snapshots
