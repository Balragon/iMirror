# Issue #32 FFmpeg Fallback Latency Posture

Issue #32 tracks the FFmpeg software-decode fallback after v0.7.1. This is a
compatibility path, not the primary quality path. GPU/high-resolution D3D remains
the default path and keeps the release-quality latency gate.

## Gate Decision

| Path | Release role | P95 target |
|------|--------------|------------|
| GPU / high-resolution D3D | Primary quality path | `< 150ms` |
| FFmpeg software fallback | Compatibility fallback | `< 250ms` |

The FFmpeg fallback gate still requires no corruption, no crash, no keyframe
starvation, no A/V liveness failure, and no severe max spike. The relaxed tier
only applies to steady-state p95 latency.

## Evidence Behind The Decision

Existing B3 evidence split #32 into two separate observations:

- Steady-state software latency was above the GPU 150ms tier but below the
  proposed 250ms fallback tier:
  - iPhone FFmpeg 10m: `worstP95=174ms`, `worstMax=327ms`.
  - Mac FFmpeg post-warmup slice: `worstP95=228ms`, `worstMax=251ms`.
- The Mac strict 1h `worstMax=10256ms` was isolated to the first latency window.
  The next windows returned to normal software-fallback latency. Treat this as
  startup/reconnect stale-frame contamination unless a fresh run reproduces a
  recurring multi-second max spike.

PR #39 adds two mitigations:

- FFmpeg frames older than 240ms are dropped before presentation, keeping the
  software fallback under its 250ms p95 target while matching the existing
  GPU-path policy of not presenting stale live frames.
- Small phone-sized software fallback streams use fewer FFmpeg frame threads to
  reduce pipeline depth while 1080p-class fallback keeps the existing
  parallelism.

Follow-up diagnosis narrowed both observed failures to stale/reconnect-adjacent
windows rather than a decoder process stall:

- Mac FFmpeg strict 1h (`mac-ffmpeg-1h-strict-20260627-203214.log`) failed only
  `severeMax`: the first completed latency window reported `max=10256ms` while
  the same window had `p95=107ms`, `decoderQueue=0`, `ffmpegPipeline=5`, and
  `stdinWrite ... stalls=0`. Subsequent windows stayed inside the 250ms
  software tier, so the 10.2s sample is treated as stale startup/reconnect
  contamination.
- iPhone FFmpeg with small-stream thread tuning and a 450ms stale cutoff passed
  a 10m smoke (`worstP95=190ms`, `worstMax=355ms`) but failed a 30m run by one
  reconnect-adjacent window (`worstP95=253ms` immediately after RAOP TEARDOWN).
  Lowering the FFmpeg raw-frame cutoff to 240ms is intended to drop those
  over-target frames before they enter the latency window.
- Fresh iPhone FFmpeg validation after the 240ms cutoff passed both smoke and
  soak tiers on 2026-06-28:
  - 10m: `worstP95=108ms`, `worstMax=209ms`, breach windows `0`.
  - 30m: `worstP95=157ms`, `worstMax=224ms`, breach windows `0`,
    `severeMax=pass`, `contiguousEvidence=True`.
- Fresh Mac FFmpeg validation after the 240ms cutoff passed the 30m fallback
  tier on 2026-06-28: `worstP95=229ms`, `worstMax=241ms`, breach windows `0`,
  `severeMax=pass`, `contiguousEvidence=True`. The earlier 10.2s startup
  max spike did not reproduce.

## Validation Required Before Closing #32

Run a fresh Release build with:

```powershell
$env:IMIRROR_FORCE_SOFTWARE_VIDEO = "1"
```

Then capture real-device FFmpeg fallback evidence:

1. Mac sender, at least 30 minutes. Done on 2026-06-28 for PR #39.
2. iPhone sender, at least 30 minutes. Done on 2026-06-28 for PR #39.
3. At least three connect/disconnect/reconnect cycles in the same process if the
   run is being used to close the reconnect/stale-frame portion of #32.

Evaluate each log with:

```powershell
dotnet run --project .\tools\LatencyAcceptanceReport\LatencyAcceptanceReport.csproj -c Release -- <log> 250 30
```

Acceptance for closing #32:

- `duration=pass`
- `p95=pass`
- `severeMax=pass`
- `corruption=pass`
- `crash=pass`
- `videoAudioLiveness=pass`
- `keyframeStarvation=pass`
- no recurring `Dropped stale FFmpeg frame` lines after startup/reconnect warmup
- no repeated multi-second max spikes

If p95 stays under 250ms but stale-frame drops recur during steady state, keep
#32 open and investigate the software decoder queue/drop policy. If p95 exceeds
250ms in steady state, keep #32 open as a true fallback-latency optimization
track rather than a gate-posture issue.
