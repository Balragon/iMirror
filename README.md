# iMirror

iMirror is a Windows AirPlay mirroring receiver for local development and real-device validation.

The product path is now centered on native GPU video: AirPlay control and media are handled in-process, H.264 is decoded through Media Foundation with D3D11 surfaces when hardware support is available, and frames are presented through the WPF host. The FFmpeg software decoder remains as a compatibility fallback. AirPlay audio is received over RTP, decrypted, decoded with FFmpeg AAC-ELD support, and played through WASAPI.

## Status

- AirPlay discovery, pairing, pair-verify, FairPlay setup, mirror setup, and record handling are implemented.
- Mac and iPhone screen mirroring have been validated on real devices.
- The default video engine is Media Foundation/D3D11 when hardware decode is available.
- FFmpeg remains required for the software video fallback and AAC-ELD audio decode.
- Local diagnostic dumps can contain private screen content or session material and must not be committed.

## Repository Layout

- `MacMirrorReceiver/` - WPF application shell, app logging, settings, and main session orchestration.
- `MacMirrorReceiver.Networking/` - AirPlay RTSP/RAOP, mDNS, pairing, FairPlay, mirror data, timing, and audio RTP receive paths.
- `MacMirrorReceiver.Video/` - H.264 stream gate, FFmpeg fallback decoder, Media Foundation/D3D11 decoder, GPU presenters, and latency windows.
- `MacMirrorReceiver.Audio/` - WASAPI PCM output.
- `MacMirrorReceiver.Protocol/` - Shared protocol models for mirror/auth/status/config messages.
- `ThirdParty/playfair/` - FairPlay reference sources used for validation.
- `tools/` - Focused diagnostics and acceptance helpers.
- `docs/` - Product architecture, validation notes, and archived investigation history.

## Build

```powershell
dotnet build .\MacMirrorReceiver.csproj -c Release
```

The project targets `net8.0-windows`, `x64`, and WPF.

## Runtime Dependencies

FFmpeg is intentionally not committed. Put a Windows FFmpeg build here:

```text
tools\ffmpeg\bin\ffmpeg.exe
```

FFmpeg is used by:

- the software video fallback
- AAC-ELD audio decode
- local dump validation

## Run

```powershell
.\bin\Release\net8.0-windows\iMirror.exe
```

Then open Control Center on the sender, choose Screen Mirroring, and select `iMirror`.

## Video Engine Controls

The normal product path chooses Media Foundation/D3D11 automatically when hardware decode is available.

Useful overrides:

```powershell
$env:IMIRROR_FORCE_SOFTWARE_VIDEO = "1"  # force FFmpeg software video
$env:IMIRROR_RENDER_MODE = "stable"      # request the 1080p compatibility display
$env:IMIRROR_RENDER_MODE = "quality"     # request the native GPU display
```

Legacy validation override:

```powershell
$env:IMIRROR_EXPERIMENTAL_QUALITY = "1"  # force the GPU path for local validation
```

## Diagnostics

Logs are written next to the running app as `iMirror.log`.

Private diagnostic capture is opt-in:

```powershell
$env:IMIRROR_WRITE_DIAGNOSTICS = "1"
$env:IMIRROR_DUMP_H264 = "1"
$env:IMIRROR_DUMP_AUDIO = "1"
```

Generated logs, H.264 dumps, audio dumps, and diagnostic snapshots can contain private screen content or key material. They are ignored by git and should stay local.

## Validation Tools

Common checks:

```powershell
dotnet run --project .\tools\LatencyAcceptanceReport\LatencyAcceptanceReport.csproj -c Release -- .\bin\Release\net8.0-windows\iMirror.log 150 10

dotnet run --project .\tools\MediaFoundationH264Probe\MediaFoundationH264Probe.csproj -c Release -- C:\temp\capture.d01.submitted.h264

dotnet run --project .\tools\HighResolutionProbeReport\HighResolutionProbeReport.csproj -c Release -- C:\temp\capture.d01.submitted.h264 600
```

See `docs/validation.md` for the recommended release gate.

## Documentation

- `docs/architecture.md` - current product architecture.
- `docs/validation.md` - validation and acceptance workflow.
- `docs/archive/` - historical investigation notes retained for context.
