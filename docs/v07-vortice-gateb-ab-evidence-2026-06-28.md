# v0.7 Vortice Gate B A/B Evidence - 2026-06-28

Branch: `main`

Code under test: `835e593` (`Merge pull request #34 from Balragon/claude/imirror-code-review-ibnu5q`)

Package under test: `artifacts\v07-vortice-gateb-835e593\package\iMirror-835e593-win-x64`

Local evidence root: `C:\Users\User\Documents\Codex\2026-06-27\ha\outputs\v07-vortice-gateb-ab-2026-06-27\logs`

## Verdict

Vortice Gate B A/B is a default-flip PASS.

The Vortice GPU path passed the measured Mac/iPhone latency, reconnect, GPU probe, and software fallback checks collected in this run. A follow-up same-session Mac A/B removed the last latency caveat: SharpDX 30m and Vortice 30m both passed on the same sender, resolution, and content.

The monitor power-cycle/front-buffer exercise exposed a product-level audio recovery caveat: after the monitor was powered off/on while audio was routed through display audio, video and incoming audio RTP continued, but audible output was lost until the AirPlay session was reconnected. The same behavior was then reproduced on the SharpDX baseline, so it is binding-independent and tracked separately as GitHub issue #35. It is not a Vortice default-flip blocker.

## Build and Unit Evidence

- `dotnet build iMirror.sln -c Release`: PASS, 0 warnings / 0 errors.
- `dotnet test MacMirrorReceiver.Tests\MacMirrorReceiver.Tests.csproj -c Release --no-build`: PASS, 58 passed.
- Publish: `scripts\publish-win-x64.ps1 -OutputRoot artifacts\v07-vortice-gateb-835e593 -Version 835e593 -NoZip -KeepPublishOutput`: PASS.

## SharpDX Baseline

Baseline reused from `docs\b3-hardening-evidence-2026-06-27.md` and raw B3 logs.

| Target | Binding | Evidence | Result |
| --- | --- | --- | --- |
| Mac GPU 2h | SharpDX | `mac-gpu-reconnect-2h-20260627-175154.log` | PASS: `worstP95=91ms`, `worstMax=126ms`, `contiguousEvidence=True` |
| iPhone GPU 10m | SharpDX | `iphone-gpu-10m-20260627-200509.log` | PASS: `worstP95=70ms`, `worstMax=96ms`, `contiguousEvidence=True` |

Both baseline logs include `gpuBinding=SharpDX`.

## Mac Same-Session A/B Follow-Up

Evidence:

- `mac-sameday-sharpdx-30m-20260628.log`
- `mac-sameday-sharpdx-30m-latency-report-20260628.log`
- `mac-sameday-vortice-30m-20260628.log`
- `mac-sameday-vortice-30m-latency-report-20260628.log`

Result: PASS for Vortice parity on the Mac sender.

| Binding | Stream | Evidence duration | Worst p95 | Worst max | Result |
| --- | --- | --- | --- | --- | --- |
| SharpDX | `2560x1440 @ 30` | `00:30:00` | `38ms` | `70ms` | PASS |
| Vortice | `2560x1440 @ 30` | `00:30:50` | `63ms` | `68ms` | PASS |

Both runs passed duration, p95, severeMax, corruption, crash, video/audio liveness, keyframe starvation, contiguous evidence, and high-resolution D3D checks. Process counters were comparable at the end of each run: SharpDX handles `1050`, working set `514.0MB`, private memory `420.5MB`; Vortice handles `1055`, working set `513.6MB`, private memory `423.4MB`.

## Vortice GPU Probes

| Probe | Evidence | Result |
| --- | --- | --- |
| D3D shared handle | `probe-d3d-shared-handle-vortice-20260627.log` | PASS, `gpuBinding=Vortice`, D3DImage back buffer accepted |
| D3D video processor | `probe-d3d-video-processor-vortice-20260627.log` | PASS, NV12/BGRA support and D3DImage output accepted |
| Media Foundation H.264 | `probe-mediafoundation-h264-vortice-20260627.log` | PASS, D3D11 manager accepted |
| High-resolution replay | `probe-highres-replay-vortice-20260627.log` | PASS, product MF/D3D11 decode-to-D3DImage replay |

High-resolution replay used `vortice-session.d04.submitted.h264`, `2560x1440@30`, `gpuBinding=Vortice`.

## Mac Sender, Vortice

Evidence:

- `mac-vortice-10m-20260627-224125.log`
- `mac-vortice-30m-20260627-224125.log`
- `mac-vortice-1h-20260627-224125.log`
- `mac-vortice-1h-latency-report-20260627.log`
- `mac-vortice-process-trend-20260627.csv`

Result: PASS for the measured Vortice GPU path.

1h report summary:

- `evidenceDuration=01:01:00`
- `worstP95=112ms`
- `worstMax=146ms`
- `contiguousEvidence=True`
- `duration=pass`
- `p95=pass`
- `severeMax=pass`
- `corruption=pass`
- `crash=pass`
- `videoAudioLiveness=pass`
- `keyframeStarvation=pass`
- `highResolutionD3D=pass`

Process trend notes:

- Initial sample near `22:42:48`: handles `998`, working set `562,532,352`, private bytes `488,001,536`.
- Later sample near `23:45:52`: handles `1126`, working set `506,703,872`, private bytes `404,934,656`.
- GPU process memory counters reported `0` on this system, so VRAM trend was not available from the counter set.

Reconnect summary:

- Mac Vortice reconnect was exercised at least five times.
- Each checked cycle showed `TEARDOWN` followed by fresh session reset, `2560x1440 @ 30` stream config, `gpuBinding=Vortice`, decoder restart, first NV12 texture, and swap-chain creation.
- Two transient stale D3D frame drops were logged during warmup after reconnect, then video health and latency windows recovered immediately.

## iPhone Sender, Vortice

Evidence:

- `iphone-vortice-contiguous-3m-with-d3d-20260628.log`
- `iphone-vortice-contiguous-3m-with-d3d-latency-report-20260628.log`
- `iphone-vortice-reconnect-markers-20260628.log`

Result: PASS for the measured Vortice GPU path.

Contiguous report summary:

- Stream config: `666x1440 @ 60 codec=h264-annexb`
- Binding: `gpuBinding=Vortice`
- `evidenceDuration=00:05:10`
- `worstP95=18ms`
- `worstMax=49ms`
- `contiguousEvidence=True`
- `duration=pass`
- `p95=pass`
- `severeMax=pass`
- `corruption=pass`
- `crash=pass`
- `videoAudioLiveness=pass`
- `keyframeStarvation=pass`
- `highResolutionD3D=pass`

Reconnect summary:

- iPhone Vortice reconnect was exercised three times in the same process.
- Each cycle showed session reset, fresh `666x1440 @ 60` stream config, `gpuBinding=Vortice`, first NV12 texture, and swap-chain creation.
- Reconnect marker evidence is in `iphone-vortice-reconnect-markers-20260628.log`.

## Front-Buffer / Monitor Power-Cycle

Evidence:

- `front-buffer-vortice-markers-20260628.log`
- `audio-muted-after-monitor-power-20260628.log`
- `audio-recovery-after-reconnect-20260628.log`
- `sharpdx-monitor-cycle-audio-stuck-confirmed-20260628.log`

Result: binding-independent product issue, decoupled from the Vortice default flip.

During the monitor power-cycle check, explicit `D3DImage front buffer became unavailable` / `D3DImage front buffer restored` markers were not observed. Video continued producing health and latency lines, but the user reported audible output was gone after the monitor was turned off/on.

The muted-state log shows incoming audio RTP continued, while the WASAPI output stopped making forward progress:

- Before reconnect, `AirPlay audio RTP` and `AirPlay audio RTP rate` continued.
- `Audio status` repeatedly showed `frames=2,515` stuck while `dropped` / `syncDropped` climbed from about `24,653` to `29,261`.
- Windows default render endpoint at that time was `LG HDR 4K (HD Audio Driver for Display Audio)`, `volume=16%`, `mute=False`.

After AirPlay disconnect/reconnect, audio recovered without restarting iMirror:

- `frames` advanced again from `8,285` through `15,650+`.
- `dropped` / `syncDropped` stayed near `1-2`.
- The user confirmed audio was audible again after reconnect.

The same monitor power-cycle behavior was reproduced on the SharpDX baseline: audio frames stopped advancing while `dropped` / `syncDropped` climbed, incoming audio RTP continued, and AirPlay reconnect restored audio without restarting the app. This is an audio endpoint/reinitialization issue triggered by monitor power state or output routing, not a Vortice GPU decode/render failure.

## Software Fallback

Evidence:

- `software-fallback-app-launch-20260628.json`
- `software-fallback-vortice-markers-20260628.log`
- `software-fallback-vortice-3m-20260628.log`
- `software-fallback-vortice-3m-latency-report-20260628.log`

Run configuration:

- `IMIRROR_GPU_BINDING=vortice`
- `IMIRROR_FORCE_SOFTWARE_VIDEO=1`

Result: PASS for functional fallback unaffected by the Vortice flag.

3m report summary:

- `evidenceDuration=00:03:50`
- `worstP95=140ms`
- `worstMax=184ms`
- `contiguousEvidence=True`
- `duration=pass`
- `p95=pass`
- `severeMax=pass`
- `corruption=pass`
- `crash=pass`
- `videoAudioLiveness=pass`
- `keyframeStarvation=pass`
- `highResolutionD3D=pass` with high-resolution D3D not required for this software run
- Logs show `ffmpegPipeline=4-6 frames`, video progress, and audio progress.

## Decision

- Vortice GPU path: measured PASS on Mac and iPhone.
- Mac same-session A/B: PASS; Vortice remained inside the 150ms gate and comparable to SharpDX.
- Vortice probes: PASS.
- Vortice reconnect: PASS on Mac and iPhone.
- Software fallback under the Vortice flag: functional PASS.
- Front-buffer / monitor power-cycle: reproduced on SharpDX and Vortice, tracked separately as binding-independent #35.

Action: proceed with the default-flip cleanup. Remove SharpDX package references, remove the `IMIRROR_GPU_BINDING` selector wiring, and make Vortice the only high-resolution D3D binding. Keep #35 as an independent WASAPI/display-audio endpoint recovery bug.

## Default-Flip Cleanup Verification

Branch: `codex/v07-vortice-default-flip-cleanup`

- `dotnet build iMirror.sln -c Release`: PASS, 0 warnings / 0 errors.
- `dotnet test MacMirrorReceiver.Tests\MacMirrorReceiver.Tests.csproj -c Release --no-build`: PASS, 58 passed.
- Publish with local .NET 10 SDK: PASS, package `artifacts\v07-vortice-default-flip-cleanup\package\iMirror-v0.7-default-flip-win-x64`.
- Package check: no `SharpDX*` DLLs; Vortice DLLs present.
- Startup smoke: published `iMirror.exe` constructed and was stopped cleanly.
- Cleanup probes:
  - `probe-d3d-shared-handle-default-vortice-cleanup-20260628.log`: PASS, `gpuBinding=Vortice`.
  - `probe-d3d-video-processor-default-vortice-cleanup-20260628.log`: PASS, `gpuBinding=Vortice`.
  - `probe-highres-replay-default-vortice-cleanup-20260628.log`: PASS, 120 decoded / 120 presented.
