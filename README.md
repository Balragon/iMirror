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
- iPhone sender: stable (1+ minute clean session, `h264 dropped=0`, no stalls).
- Mac sender: stable. A multi-minute freeze was root-caused to an AVCC-length-prefix /
  Annex B-start-code confusion in the H.264 normalizer and fixed; a Mac session now decodes
  9,000+ frames at `received:decoded:rendered ~ 1:1:1` with no `sps_id`/`mb_width`/stall
  warnings (see "Mac Sender Stabilization" below).

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

- `*.d01.received.h264`
  - Media Foundation/D3D11 only: complete post-gate Annex B stream delivered to the decoder object.
- `*.d01.submitted.h264`
  - FFmpeg path: complete post-gate Annex B stream submitted to the decoder before backpressure drops.
  - Media Foundation/D3D11 path: bytes accepted by the Microsoft H.264 MFT after successful `ProcessInput`.
- `*.d01.written.h264`
  - Bytes successfully written to FFmpeg stdin after any drops.

The Media Foundation/D3D11 high-resolution path writes `received=<path>` and `submitted=<path> bytes=<n>` close lines. The real-device acceptance bundle checks that the supplied `*.submitted.h264` path and byte count match the log. It does not write `*.written.h264` because there is no FFmpeg stdin pipe in that path.

These files do not contain key material, but they do contain screen content. Keep them private.

Validate locally:

```powershell
.\tools\ffmpeg\bin\ffmpeg.exe -f h264 -i C:\temp\imirror-capture.d01.submitted.h264 -f null -
.\tools\ffmpeg\bin\ffmpeg.exe -f h264 -i C:\temp\imirror-capture.d01.written.h264   -f null -
```

For the high-resolution v2 Media Foundation/GPU investigation, run the combined probe report on the submitted dump:

```powershell
dotnet run --project .\tools\HighResolutionProbeReport\HighResolutionProbeReport.csproj -c Release -- C:\temp\imirror-capture.d01.submitted.h264 600
```

The report only passes when the offline Media Foundation decoder produces frames, the D3D11-manager path exposes decoded `ID3D11Texture2D` output with `NV12` texture descriptions matching the dump's `probeGeometry`, the render-side bridge can run NV12-to-BGRA `VideoProcessorBlt` into a shared D3D11 target that WPF `D3DImage` accepts through D3D9Ex at that same geometry, and the product MF/D3D11 classes can replay the dump through the same D3DImage path. Passing this report is still not product readiness; the real Mac latency/reconnect acceptance run is separate.

To replay the same dump through the product MF/D3D11 classes, use `tools\HighResolutionD3DReplayProbe` with the known capture geometry.

For high-resolution latency acceptance, `tools\LatencyAcceptanceReport` can also require MF/D3D11 path-start and first-NV12-texture evidence; see `docs\high-resolution-pipeline-v2.md`.

The Media Foundation probe reads SPS geometry from the dump and uses that size for the decoder input type. Check for `probeGeometry=... source=sps`; `source=fallback` means the dump did not contain a parseable SPS and the probe used its 2048x1152 fallback.

If the dump geometry is known but SPS parsing fails, pass an explicit override:

```powershell
dotnet run --project .\tools\HighResolutionProbeReport\HighResolutionProbeReport.csproj -c Release -- C:\temp\imirror-capture.d01.submitted.h264 600 2048x1152@30
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

Render quality is stable-first by default:

- Stable mode: advertise a 1920x1080 non-overscanned AirPlay display.
- Quality mode without experimental flags still advertises the stable 1920x1080 display. Real Mac testing showed that the high-resolution paths are not reliable enough for normal use on this receiver pipeline.

The render mode is read at process startup because AirPlay display capabilities are advertised before the sender starts the mirror session. The in-app high-quality selector is disabled unless an experimental quality flag is present. The saved setting lives at `%APPDATA%\iMirror\settings.json`.

`IMIRROR_RENDER_MODE=quality` remains a debug override and takes priority over the saved setting. `IMIRROR_RENDER_MODE=stable` or `default` forces stable mode for the process.

The experimental quality flags are for local validation only and may change or be removed without compatibility guarantees. Product-facing Quality mode UX is intentionally on hold until a redesigned high-resolution pipeline can stay stable under real Mac motion.

The product high-resolution redesign is tracked in `docs/high-resolution-pipeline-v2.md`.

### Experimental 2048 Quality Path

The legacy 2048x1152 WPF/FFmpeg path is retired for Quality advertising. It avoided visible corruption from dropping compressed P-frames, but real Mac motion testing still accumulated decoder/render latency, including with hardware decode enabled.

`IMIRROR_EXPERIMENTAL_QUALITY=1` only opens 2048x1152 in `HighResolutionD3D` builds, where it selects the Media Foundation/D3D11 GPU path. If that path fails after advertising high resolution, iMirror does not fall back to the legacy WPF path.

To test the 2048 MF/D3D11 path, build and launch with:

```powershell
$env:IMIRROR_RENDER_MODE = "quality"
$env:IMIRROR_EXPERIMENTAL_QUALITY = "1"
dotnet run --project .\MacMirrorReceiver.csproj -c Debug -p:HighResolutionD3D=true
```

If this path starts lagging behind the Mac, return to stable mode.

### Experimental mpv Quality Path

The Windows mpv binary is intentionally not committed or bundled. mpv builds are GPL-licensed and large enough that users should bring their own copy, the same way FFmpeg is handled here.

The 2560x1440 mpv path is experimental. It is not the default Quality mode because real Mac mirroring can keep sending roughly 60 H.264 payloads per second even when a lower refresh rate is advertised; if the receiver drops compressed P-frames to stay low-latency, moving content can become visibly corrupted.

To test the experimental 2560x1440 mpv path, install mpv from the Windows links on <https://mpv.io/installation/> or from the shinchiro Windows builds, then place it at:

```text
tools\mpv\mpv.exe
```

Putting `mpv.exe` on `PATH` is also supported. Then launch with:

```powershell
$env:IMIRROR_RENDER_MODE = "quality"
$env:IMIRROR_EXPERIMENTAL_MPV = "1"
```

At process startup, iMirror runs `mpv --version`; if that check fails, quality mode advertises stable 1920x1080 unless a `HighResolutionD3D` build also has `IMIRROR_EXPERIMENTAL_QUALITY=1` set. If the experimental path stutters or corrupts moving content, return to Stable mode.

### mpv Quality Diagnostics

mpv quality diagnostics are opt-in:

```powershell
$env:IMIRROR_RENDER_MODE = "quality"
$env:IMIRROR_EXPERIMENTAL_MPV = "1"
$env:IMIRROR_DIAG = "1"
dotnet run -c Release
```

With diagnostics enabled, iMirror logs a 10-second `mpv diag interval` line with post-gate `accepted_to_mpv` rate, `stdin_write` rate, `dropped_total (+delta)`, stdin stall count/max, `receive->stdin_write_complete` latency p50/p95/max, and queue depth. This latency ends at successful write to mpv stdin; it is not mpv's internal render latency.

The mpv path keeps compressed H.264 packets in order. Random packet drops can corrupt moving content, so the queue is allowed to absorb short startup stalls and only forces a keyframe resync if it overflows.

Use this motion test before changing the quality target:

1. Static baseline: mirror a mostly static Mac desktop for 2 minutes and record the `accepted_to_mpv` / `stdin_write` rates.
2. Motion run: open a 60 fps source full-screen on the Mac, such as TestUFO or a known 60 fps video, for 2 minutes.
3. Pass: source rate matches the advertised fps, `stdin_write` stays at 97%+ of accepted rate, `dropped_total` does not increase during steady state, and p95 `receive->stdin_write_complete` stays below 100 ms.
4. Sender-limited: source rate stays below the advertised fps while latency/drop remain healthy. The sender is not producing a full-motion scene for that interval.
5. Receiver-limited: source rate reaches the advertised fps but stdin write rate, drop count, stalls, or p95 latency break the pass criteria. Lower the quality target or return to stable mode.

Run the 30-minute stability test only after the 2-minute motion run passes.

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

## Mac Sender Stabilization (Resolved)

### Root Cause: AVCC Length Prefix Misread As An Annex B Start Code

The Mac sender, after a few minutes, would freeze the decoder: `received` kept rising while
`decoded`/`rendered` stuck, with repeated `sps_id 6 out of range`, `mb_width/height overflow`,
`non-intra slice in an IDR NAL unit`, and `Invalid data found` warnings.

All of those were **symptoms of one bug** in `TryNormalizeClearH264Payload`
(`AirPlayProbeService`). Mac mirror video is AVCC (4-byte big-endian length prefix per NAL),
but the normalizer probed Annex B framing **first**. `TryUseAnnexBPayloadAtOffset` only
checks for a start code at offset 0 and then copies the payload verbatim. When an AVCC length
prefix coincides with a start code - NAL size 1 -> `00 00 00 01`, or size 256..511 ->
`00 00 01 xx` - the payload was treated as Annex B and copied raw, leaving the length prefix
inline. Every NAL was then shifted by a byte, corrupting slice headers (decoder reads
`first_mb_in_slice != 0`, a "non-intra slice in an IDR NAL unit", or garbage parsed as
`sps_id 6`). A corrupt IDR cannot be decoded, and following P-slices reference the broken
frame, so the stream stalls until a clean IDR (which the Mac emits only rarely).

This was proven by counting start codes in the in-app `submitted.h264` dump (post-gate, the
exact decoder input). The normalizer's AVCC path always writes 4-byte start codes, yet the
dump contained **219 three-byte start codes** (`XX 00 00 01`) - the fingerprint of raw AVCC
length prefixes leaking through the Annex B path.

**Fix:** probe AVCC first (it requires the whole payload to resolve exactly, `cursor ==
length`, so it is the strict, safe check) and fall back to Annex B only if AVCC fails. After
the fix the dump has **0 three-byte start codes**, and a Mac session decodes 9,000+ frames
with `received:decoded:rendered ~ 1:1:1` and zero `sps_id`/`mb_width`/`non-intra`/stall
warnings, well past the previous ~7,300-frame freeze point.

Note: `sps_id 6` was never a real parameter set the Mac sent - with the normalize fix it does
not appear at all, even with parameter-set filtering disabled. Earlier symptom-oriented
attempts (filtering non-zero SPS/PPS ids in the gate, restarting the decoder on these
warnings) were unnecessary and were reverted; restarting in particular caused freezes because
it re-armed the keyframe gate while the Mac was not sending a fresh IDR.

### Render And Rate Notes

- The render path is latest-frame-wins (single pending slot): `renderDropped` means the
  WPF UI thread presents slower than the decode rate. Latency does not accumulate; this is
  frame skipping, not a stall.
- `received:decoded` is ~1:1 in healthy sessions. The previously observed ~2:1 was decode
  failures during the freeze. With the default 1920x1080 non-overscanned advertisement,
  Mac sessions should report `source=1920x1080`. Higher-resolution quality targets are
  experimental only: 2048x1152 via `IMIRROR_EXPERIMENTAL_QUALITY=1` in a
  `HighResolutionD3D` build, and 2560x1440 via `IMIRROR_EXPERIMENTAL_MPV=1`.
  Direct high-resolution WPF fallback proved too heavy:
  FFmpeg still had to consume the sender's high-resolution stream and accumulated stdin
  stalls plus receive-to-render latency. The payload-shape log line confirms sender cadence and composition
  (`AirPlay mirror video payload shape (10.0s): payloads=..., idr=..., slice=...,
  paramSetOnly=..., multiNal=..., avgSize=...`, composition counts only, no screen content
  or key material).

Verification target for a Mac session (3+ minutes): screen stays live, `/feedback`
continues, no control close / data EOF / fallback / timeout, `h264 dropped=0`,
decoded/rendered keep increasing, and `sps_id`/`mb_width` warnings do not stop decoding.

Current Mac status: promising but not final. Continue collecting 3+ minute sessions and
watch for fallback cascades, long keyframe waits after real corruption, and high
`renderDropped` under motion-heavy desktop activity.
