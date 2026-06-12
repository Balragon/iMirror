# iMirror Windows AirPlay Receiver

Windows AirPlay screen-mirroring receiver prototype.

## Current Status

- iPhone Control Center > Screen Mirroring discovers `iMirror`.
- Pairing, pair-verify, FairPlay `fp-setup`, mirror `SETUP`, and `RECORD` complete.
- SPS/PPS and `type=0` video payloads are received.
- FairPlay key derivation was verified against the native `ThirdParty/playfair` C implementation.
- Mirror video decrypt and AVCC/Annex B conversion are confirmed by offline FFmpeg decode of captured H.264.
- Live in-app FFmpeg decode and on-screen render are confirmed.
- Session persistence is confirmed past the previous ~12 second failure point; render stats continued for 1+ minute with no H.264 input drops.

## Important Files

- `MacMirrorReceiver.Networking/AirPlayProbeService.cs`
  - RTSP/RAOP control handling, mirror data TCP receive loop, SPS/PPS parsing, FairPlay setup observation, mirror decryptor probing.
- `MacMirrorReceiver.Networking/AirPlayMirrorDecryptor.cs`
  - AirPlay mirror AES-CTR candidate construction and decryptor state.
- `MacMirrorReceiver.Networking/AirPlayFairPlayContext.cs`
  - `fp-setup` responses and FairPlay key-message handling.
- `MacMirrorReceiver.Networking/AirPlayPlayFair.cs`
  - C# port of `ThirdParty/playfair` for FairPlay AES key recovery.
- `MacMirrorReceiver.Video/FfmpegDecoder.cs`
  - FFmpeg process lifecycle, H.264 input queue, fallback handling, raw BGRA frame output.
- `MacMirrorReceiver/MainWindow.cs`
  - Stream config wiring, Annex B gate, decoder startup/restart handling, render stats.
- `tools/AirPlayDiagnosticProbe`
  - Local-only diagnostic helper for private mirror diagnostic JSON.

## Build

```powershell
dotnet build .\MacMirrorReceiver.csproj -c Release
```

## Run

```powershell
.\bin\Release\net8.0-windows\iMirror.exe
```

Then select `iMirror` from iPhone Control Center > Screen Mirroring.

## FFmpeg

Runtime video decoding expects:

```text
tools\ffmpeg\bin\ffmpeg.exe
```

The local Windows FFmpeg binary is intentionally not committed.

## Diagnostics And Privacy

Do not commit generated diagnostics or media captures. They may contain session key material or private screen content.

Ignored local outputs include:

- `bin/`
- `obj/`
- `Diagnostics/`
- `*.log`
- `*.h264`
- `*.bgra`
- `windows-receiver/tools/ffmpeg/`

Mirror diagnostic snapshots are opt-in because they include key material and encrypted payload samples:

```powershell
$env:IMIRROR_WRITE_DIAGNOSTICS = "1"
```

When enabled, snapshots are written under:

```text
bin\Release\net8.0-windows\Diagnostics\
```

Keep those JSON files local/private.

To analyze one locally:

```powershell
dotnet run --project .\tools\AirPlayDiagnosticProbe\AirPlayDiagnosticProbe.csproj -c Release -- .\bin\Release\net8.0-windows\Diagnostics\<snapshot>.json .\bin\Release\net8.0-windows\iMirror.dll
```

### H.264 Elementary-Stream Dump

Set `IMIRROR_DUMP_H264` before launching the app:

```powershell
$env:IMIRROR_DUMP_H264 = "1"
# or
$env:IMIRROR_DUMP_H264 = "C:\temp\imirror-capture.h264"
.\bin\Release\net8.0-windows\iMirror.exe
```

The decoder writes per-decoder-instance files so restarts do not truncate earlier captures:

- `*.d01.submitted.h264`
  - Complete post-gate Annex B stream submitted to the decoder before backpressure drops.
- `*.d01.written.h264`
  - Bytes successfully written to FFmpeg stdin after any drops.

These files do not contain key material, but they do contain screen content. Keep them private.

Validate locally:

```powershell
.\tools\ffmpeg\bin\ffmpeg.exe -f h264 -i C:\temp\imirror-capture.d01.submitted.h264 -f null -
.\tools\ffmpeg\bin\ffmpeg.exe -f h264 -i C:\temp\imirror-capture.d01.written.h264   -f null -
```

## Resolved Root Causes

### FairPlay Port Mismatch

The C# `AirPlayPlayFair` port now matches the native `ThirdParty/playfair` C implementation across deterministic and random vectors in FairPlay modes 0..3.

### Valid H.264 But Zero In-App Frames

Per-flag A/B tests found `-fflags nobuffer` as the decisive FFmpeg failure trigger:

| variant | frames |
|---|---:|
| baseline | 109 |
| `-probesize 1M -analyzeduration 200000` only | 109 |
| `-flags low_delay` only | 109 |
| `-avioflags direct` only | 109 |
| `-fflags nobuffer` only | 0 |
| full app args without `nobuffer` | 109 |

`-fflags nobuffer` sets `AVFMT_FLAG_NOBUFFER`, which discards packets consumed during `avformat_find_stream_info` instead of replaying them to the decoder. AirPlay mirror captures often need the initial SPS/PPS/IDR, so the decoder can be left with only mid-GOP P-frames. Do not reintroduce `-fflags nobuffer` for the rawvideo pipe path.

Current decoder input flags:

```text
-flags low_delay -probesize 1M -analyzeduration 200000 -avioflags direct -max_delay 0
```

Decoder order is software first, then d3d11va, then dxva2. Overrides:

- `IMIRROR_FORCE_SOFTWARE=1`
- `IMIRROR_PREFER_HARDWARE=1`

### Fallback Restart Garbage

When FFmpeg fallback restarts the process, pending input is now cleared before the new process starts. `FfmpegDecoder.DecoderRestarted` also makes `MainWindow` call `H264AnnexBStreamGate.RequireKeyframe()`, so the restarted decoder receives SPS/PPS on the next keyframe instead of stale mid-stream P-frames.

### Stream Stopped After About 12 Seconds

The RTSP control connection handler previously served only 16 requests per TCP connection, then closed it. iPhone keeps one RTSP control connection alive and sends `POST /feedback` every ~2 seconds; receiver-side close is treated as session teardown.

The request cap is removed. The control loop now runs until peer EOF, `Connection: close`, or cancellation, and logs why the control connection ended. The mirror data TCP loop also logs sender EOF with byte count and duration.

## Live Verification

Last verified flow:

- Latest Release build launched.
- iPhone connected to `iMirror`.
- Screen rendered in-app.
- Render stats continued for 1+ minute.
- H.264 accepted/written/dropped reached `3542/3542/0`.
- No control connection close or data stream EOF occurred while mirroring was active.

Expected healthy logs:

- `FFmpeg cmd:` does not contain `-fflags nobuffer`.
- First decoder path is `[decoder:software]`.
- `FFmpeg decoded first frame.` appears.
- `Render stats` received/decoded/rendered counters keep increasing.
- `h264 accepted/written/dropped` keeps dropped at `0` during normal operation.
