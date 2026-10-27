# Public Release

This page describes the Windows public release process. The tagged build in
`.github/workflows/release.yml` is the source of truth; the steps below are the
manual equivalent.

## Overview

Each release ships two artifacts built from `MacMirrorReceiver.csproj` in
`Release` mode, plus a `SHA256SUMS` file:

- `iMirror-<version>-setup.exe` — Inno Setup per-user installer (recommended).
  The in-app updater downloads this asset and verifies it against `SHA256SUMS`.
- `iMirror-<version>-win-x64.zip` — self-contained portable build; extract and
  run `iMirror.exe`.

No .NET runtime install is required. Both artifacts are unsigned, so Windows
SmartScreen may warn on first run.

## Prerequisites

- Windows x64 build machine.
- .NET 10 SDK.
- Inno Setup 6 (`choco install innosetup`) for the installer.
- FFmpeg Essentials available for packaging:
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
- bundles FFmpeg
- validates required files
- writes `README.txt`
- creates a zip unless `-NoZip` is passed

For public v0.2 release zips, FFmpeg must be included under
`tools\ffmpeg\bin\ffmpeg.exe`, with its license at
`tools\ffmpeg\bin\LICENSE.txt`. It is required for AAC-ELD audio decode and
for the software video fallback.

## Tagging A Release

Tag the release from the commit that should be published. The message of an
annotated tag becomes the GitHub release notes: its first line is the heading
and the rest is Markdown. Write it to a file and keep lines starting with `#`:

```powershell
git tag -a v0.8.0 --cleanup=verbatim -F release-notes.md
git push origin v0.8.0
```

A lightweight tag (`git tag v0.8.0`) publishes the default notes template
(installation, notes and license) instead.

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

If Windows Firewall prompts, allow `iMirror.exe` for both Private and Public
networks. AirPlay audio uses dynamic UDP ports negotiated at runtime; opening
only fixed ports can leave video working while audio stays silent.

## SmartScreen / Trust

The v0.2 public zip is unsigned, so Windows SmartScreen warnings are expected on
first launch or download. No publisher action is required for v0.2.

## FFmpeg Note

FFmpeg Essentials is bundled in the public v0.2 release artifact. The GitHub
release workflow downloads a pinned Gyan.FFmpeg.Essentials zip, verifies its
SHA-256 checksum, and fails the package if `tools\ffmpeg\bin\ffmpeg.exe` is
missing. The package also includes the FFmpeg license file at
`tools\ffmpeg\bin\LICENSE.txt`.

## Checksums

Generate a SHA-256 checksum after packaging:

```powershell
Get-FileHash .\artifacts\iMirror-0.7.5-win-x64.zip -Algorithm SHA256
Get-FileHash .\artifacts\iMirror-0.7.5-setup.exe -Algorithm SHA256
```

The release workflow writes both into `artifacts\SHA256SUMS` in the
`<lowercase sha256>  <file name>` format the updater parses; publish that file
with the release. The zip name has no `v` prefix (the tag `v0.7.5` becomes
`iMirror-0.7.5-...`).
