# Validation

Use this page as the product validation checklist before promoting work to `main`.

For the current Windows real-device gate, use
`docs/windows-e2e-validation.md`.

## Build Gate

```powershell
dotnet build .\MacMirrorReceiver.csproj -c Release
```

## Release Package Gate

Create a self-contained Windows x64 package before handing builds to users.

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\publish-win-x64.ps1
```

Then extract the generated zip outside the repository and launch that copy of
`iMirror.exe`. The package must include `tools\ffmpeg\bin\ffmpeg.exe` and
`ThirdParty\playfair\` files. See `docs/release.md` for the full packaging
smoke test.

## Smoke Test

1. Launch `iMirror.exe`.
2. Confirm the sender discovers `iMirror`.
3. Start mirroring from an iPhone or Mac.
4. Confirm video appears, audio plays when the sender provides audio, and disconnect returns the UI to the ready state.
5. Confirm `iMirror.log` contains no decoder fault loop, H.264 corruption loop, or repeated reconnect loop.

## Current Smoke Evidence

Latest smoke run: 2026-06-16, Release build, real sender at `192.168.0.22`.

- Advertised and received `2560x1440 @ 30`.
- Media Foundation/D3D11 path started with `d3d11MultithreadProtected=True`.
- The decoder produced its first `NV12` D3D11 texture.
- Audio RTP was received, decrypted as AAC-ELD, decoded by FFmpeg, and sent to WASAPI.
- `LatencyAcceptanceReport` on the smoke slice passed with 12 windows over 2 minutes.
- Worst p95 latency was `57ms`; worst max latency was `118ms`.
- `highResolutionD3D=pass`, `corruption=pass`, and `contiguousEvidence=True`.

## Latency Gate

Capture one continuous active-motion session. Prefer 10 minutes or longer for product validation.

```powershell
dotnet run --project .\tools\LatencyAcceptanceReport\LatencyAcceptanceReport.csproj -c Release -- .\bin\Release\net10.0-windows\iMirror.log 150 10
```

Expected result:

- `duration=pass`
- `p95=pass`
- `severeMax=pass`
- `corruption=pass`
- `contiguousEvidence=True`

Warnings about max-trend should be reviewed with queue, stall, and reconnect markers before being treated as product failures.

## GPU Decode Probe

When validating a captured GPU session, enable H.264 dumping and run the probe tools on the matching `*.submitted.h264` file.

```powershell
$env:IMIRROR_DUMP_H264 = "1"

dotnet run --project .\tools\MediaFoundationH264Probe\MediaFoundationH264Probe.csproj -c Release -- C:\temp\capture.d01.submitted.h264

dotnet run --project .\tools\HighResolutionProbeReport\HighResolutionProbeReport.csproj -c Release -- C:\temp\capture.d01.submitted.h264 600
```

The useful evidence is decoded output, D3D11 texture output, NV12 texture geometry matching the capture, and render-bridge success.

## Reconnect Gate

Run at least three disconnect/reconnect cycles on the same app process.

Expected result:

- each reconnect creates a fresh stream config
- the GPU path or FFmpeg fallback restarts cleanly
- audio restarts cleanly or degrades without interrupting video
- no stale decoder, stale GPU surface, or hung RTSP/data connection remains
