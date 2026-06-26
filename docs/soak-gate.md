# v0.3 Release Soak Gate

The v0.3 roadmap introduces a **minimum 1-hour soak** as a release gate:

> No crash, hang, or unrecoverable A/V loss across a 1-hour real-device
> mirroring session.

This document defines how that gate is run and evaluated.

## Why this is not a CI-automated gate

The soak requires a **real AirPlay sender** (a physical iPhone/iPad/Mac) and a
**real GPU** presenting video. GitHub-hosted runners have neither, so the soak
capture is produced manually (or on a hardware-equipped self-hosted runner) and
then evaluated by a deterministic tool. CI verifies the *build*; the soak gate
verifies *runtime behaviour* that CI cannot synthesise.

## Procedure

1. Run a Release build of iMirror on the target machine.
2. Mirror a real device to it continuously for **at least 60 minutes**. Exercise
   at least one reconnect (stop/start mirroring on the sender) during the run.
3. iMirror writes its log to the v0.3 location:

   ```
   %LOCALAPPDATA%\iMirror\Logs\iMirror.log
   ```

4. Evaluate the log against the soak gate:

   ```powershell
   pwsh scripts/soak-gate.ps1
   ```

   Add `-RequireHighResolutionD3D` when the GPU decode/present path is expected
   to be active for the whole run. Pass `-LogPath` to point at a specific
   capture.

5. The gate passes only if the underlying report prints `PASS` (exit code 0).

## What the gate checks

`scripts/soak-gate.ps1` wraps `tools/LatencyAcceptanceReport` with soak
thresholds (`minimum-minutes=60`, `p95 < 150ms`). The report FAILS the run on
any of:

| Gate | Meaning |
|------|---------|
| `duration` | Less than 60 minutes of evidence. |
| `p95` | A latency window's p95 reached the target (default 150ms). |
| `severeMax` | A single-frame stutter at/above 2× the p95 target. |
| `corruption` | FFmpeg/decoder corruption markers (e.g. invalid data, PPS). |
| **`crash`** | **Unhandled-exception markers (`Unhandled domain exception:` / `Dispatcher exception:`) — a crashed session.** |
| `videoAudioLiveness` | Audio activity continued for 30s+ after the last decoded/rendered video progress, which indicates a video freeze while audio keeps running. |
| `keyframeStarvation` | The H.264 gate stayed in `waiting for SPS/PPS keyframe` for 30s+, which indicates unrecovered keyframe starvation. |
| `highResolutionD3D` | (Opt-in) GPU D3D path never became healthy, or it faulted. |

It also WARNs (non-gating) on non-contiguous evidence (large timestamp gaps),
which can indicate a hang or a spliced capture and should be investigated before
accepting a release.

## Notes

- The `crash` gate is backward compatible: a clean acceptance capture has zero
  crash markers, so existing non-soak invocations are unaffected.
- Prefer a single continuous capture. A spliced "clean slice" can hide spikes
  and trips the contiguity warning.
