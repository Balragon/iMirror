# Release Soak Gate

The release soak gate is a **minimum 1-hour real-device mirroring session**:

> No crash, hang, unrecoverable A/V loss, or unacceptable latency across a
> 1-hour real-device mirroring session.

This document defines how that gate is run and evaluated.

## Why This Is Not CI-Automated

The soak requires a **real AirPlay sender** (a physical iPhone/iPad/Mac) and a
**real GPU** presenting video. GitHub-hosted runners have neither, so the soak
capture is produced manually (or on a hardware-equipped self-hosted runner) and
then evaluated by a deterministic tool. CI verifies the build; the soak gate
verifies runtime behaviour that CI cannot synthesize.

## Latency Tiers

iMirror has two release latency tiers:

| Path | Role | P95 Target |
|------|------|------------|
| GPU / high-resolution D3D | Primary quality path | `< 150ms` |
| FFmpeg software fallback | Compatibility fallback | `< 250ms` |

The FFmpeg software tier is not GPU-quality parity. It exists so software decode
can remain a usable compatibility fallback while the GPU path keeps the stricter
release-quality latency target.

For both tiers, `severeMax` fails when any latency window has a max at or above
2x the selected p95 target.

## Procedure

1. Run a Release build of iMirror on the target machine.
2. Mirror a real device to it continuously for **at least 60 minutes**. Exercise
   at least one reconnect (stop/start mirroring on the sender) during the run.
3. iMirror writes its log to:

   ```text
   %LOCALAPPDATA%\iMirror\Logs\iMirror.log
   ```

4. Evaluate a GPU-path log against the primary gate:

   ```powershell
   pwsh scripts/soak-gate.ps1 -RequireHighResolutionD3D
   ```

   Pass `-LogPath` to point at a specific capture.

5. Evaluate an intentional FFmpeg software-fallback log against the fallback
   tier:

   ```powershell
   pwsh scripts/soak-gate.ps1 -LogPath <software-run.log> -P95TargetMs 250
   ```

6. The gate passes only if the underlying report prints `PASS` (exit code 0).

## What The Gate Checks

`scripts/soak-gate.ps1` wraps `tools/LatencyAcceptanceReport` with soak
thresholds (`minimum-minutes=60`). The report fails the run on any of:

| Gate | Meaning |
|------|---------|
| `duration` | Less than the requested evidence duration. |
| `p95` | A latency window's p95 reached the selected target: 150ms for GPU, 250ms for FFmpeg software fallback. |
| `severeMax` | A single-frame stutter at or above 2x the selected p95 target. |
| `corruption` | FFmpeg/decoder corruption markers, such as invalid data or missing PPS. |
| `crash` | Unhandled-exception markers indicating a crashed session. |
| `videoAudioLiveness` | Audio activity continued for 30s+ after the last decoded/rendered video progress, indicating a video freeze while audio keeps running. |
| `keyframeStarvation` | The H.264 gate stayed in `waiting for SPS/PPS keyframe` for 30s+, indicating unrecovered keyframe starvation. |
| `highResolutionD3D` | Opt-in: GPU D3D path never became healthy, or it faulted. |

The report also warns, non-gating, on non-contiguous evidence. Large timestamp
gaps can indicate a hang or a spliced capture and should be investigated before
accepting a release.

## Notes

- Prefer a single continuous capture. A spliced clean slice can hide spikes and
  trips the contiguity warning.
- For software-fallback runs, investigate any `Dropped stale FFmpeg frame` line
  or multi-second max spike even if the 250ms p95 tier passes.
