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
- Mac sender: basic connection/decode/render has been observed, but long-session
  stability is still under validation. FFmpeg can log `sps_id out of range` /
  `mb_width/height overflow`; current evidence suggests these spare-parameter-set
  warnings are non-fatal and should not trigger decoder restart, but more 3+ minute
  tests across Mac resolutions and activity levels are still needed.

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

## Mac Sender Stabilization (Ongoing)

### `sps_id out of range` / `mb_width/height overflow` Are Benign

The Mac sender carries **two H.264 parameter sets** in one stream:

- Set A: SPS id=0 / PPS id=0 - used by every real slice.
- Set B: SPS id=6 / PPS id=3 - an unused spare set.

FFmpeg warns (`sps_id 6 out of range`) while parsing the spare set, but all coded slices
reference sps_id 0, so decoding continues. This was confirmed by offline-decoding the
in-app `submitted.h264` dump (the exact post-gate stream, before any backpressure drop):
the dump contains the same warnings yet decodes 272 frames cleanly, and
`-bsf:v trace_headers` shows all 269 slices use `pic_parameter_set_id = 0`.

Therefore the decoder must **not** be restarted on these warnings. An earlier attempt to
restart on `sps_id out of range` / `mb_width/height overflow` caused a freeze: restarting
re-arms the keyframe gate (`RequireKeyframe`), but the Mac does not emit a fresh IDR on
demand, so the gate gets stuck dropping P-slices (`saw NAL 1`) until the next natural IDR
(which can be tens of seconds away), and the repeated restarts triggered hwaccel-fallback
cascades. `MainWindow.IsDecoderResyncError` now classifies these as resync warnings
(suppressed from the UI, no restart); ffmpeg keeps its state and resyncs at the next IDR.

### Render And Rate Notes

- The render path is latest-frame-wins (single pending slot): `renderDropped` means the
  WPF UI thread presents slower than the decode rate. Latency does not accumulate; this is
  frame skipping, not a stall. Lower priority.
- `received:decoded` is ~1:1 in healthy sessions. The previously observed ~2:1 was decode
  failures during the freeze, **not** the 30 fps output cap; the cap (`ResolveOutputFps`)
  only fires at source width >= 3000 px, and the Mac session is 1804 px wide. The Mac
  actually sends ~30 payloads/s (the declared 60 fps in the stream config is nominal); the
  payload-shape log line confirms this (`AirPlay mirror video payload shape (10.0s):
  payloads=..., idr=..., slice=..., paramSetOnly=..., multiNal=..., avgSize=...`,
  composition counts only, no screen content or key material).
- The Mac emits IDRs rarely (payload-shape windows often show `idr=0` over 10 s), so a real
  mid-stream corruption recovers only at the next keyframe. Requesting a keyframe from the
  Mac on corruption is possible future work (AirPlay mechanism TBD).
- Queue overflow recovers in GOP units: on overflow the whole pending queue is flushed; if
  the incoming payload is an IDR, feeding resumes from it immediately, otherwise the gate
  holds input until the next keyframe (`InputQueueOverflowed ->
  H264AnnexBStreamGate.RequireKeyframe()`). Dropping individual P-frames corrupts the GOP.

Verification target for a Mac session (3+ minutes): screen stays live, `/feedback`
continues, no control close / data EOF / fallback / timeout, `h264 dropped=0`,
decoded/rendered keep increasing, and `sps_id`/`mb_width` warnings do not stop decoding.

Current Mac status: promising but not final. Continue collecting 3+ minute sessions and
watch for fallback cascades, long keyframe waits after real corruption, and high
`renderDropped` under motion-heavy desktop activity.
