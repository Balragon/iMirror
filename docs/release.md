# v0.2 Public Release

This page describes the v0.2 Windows public release process.

## Overview

The public artifact is a self-contained `win-x64` zip built from
`MacMirrorReceiver.csproj` in `Release` mode.

- No .NET runtime install is required.
- The zip is unsigned.
- There is no installer or auto-updater.
- Users extract the zip and run `iMirror.exe`.

## Prerequisites

- Windows x64 build machine.
- .NET 8 SDK.
- FFmpeg Essentials available for local validation or private packages:
  - `tools\ffmpeg\bin\ffmpeg.exe`
  - `ffmpeg.exe` on `PATH`
  - `-FfmpegPath C:\path\to\ffmpeg.exe`

Install FFmpeg Essentials with:

```powershell
winget install Gyan.FFmpeg.Essentials
```

## Building The Release Zip

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-win-x64.ps1
```

Output is written under `artifacts\`.

The publish script:

- publishes `MacMirrorReceiver.csproj` as a self-contained `win-x64` Release build
- copies publish output into an artifacts package folder
- removes debug, log, and capture files
- bundles FFmpeg when found
- validates required files
- writes `README.txt`
- creates a zip unless `-NoZip` is passed

For the public v0.2 zip, do not include FFmpeg in the uploaded artifact. Users
install FFmpeg separately with `winget`.

## Tagging A Release

Tag the release from the commit that should be published:

```powershell
git tag v0.2.0 -m "v0.2.0"
git push origin v0.2.0
```

CI publishes the tagged release automatically.

## Manual Smoke Test Checklist

Use `docs/validation.md` as the source checklist. At minimum:

1. Build the release zip.
2. Extract the zip outside the repository.
3. Launch the extracted `iMirror.exe`.
4. Confirm a sender discovers `iMirror`.
5. Start mirroring from an iPhone or Mac.
6. Confirm video appears and audio plays when the sender provides audio.
7. Disconnect and confirm the UI returns to the ready state.
8. Confirm `iMirror.log` has no decoder fault loop, H.264 corruption loop, or repeated reconnect loop.

## SmartScreen / Trust

The v0.2 public zip is unsigned, so Windows SmartScreen warnings are expected on
first launch or download. No publisher action is required for v0.2.

## FFmpeg Note

FFmpeg is not bundled in the public v0.2 release artifact. Users should install
FFmpeg Essentials:

```powershell
winget install Gyan.FFmpeg.Essentials
```

## Checksums

Generate a SHA-256 checksum after packaging:

```powershell
Get-FileHash .\artifacts\iMirror-v0.2.0-win-x64.zip -Algorithm SHA256
```

Publish the checksum alongside the release zip.
