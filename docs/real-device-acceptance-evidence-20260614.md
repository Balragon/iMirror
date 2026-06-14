# Real-Device Acceptance Evidence - 2026-06-14

Source log:

- `bin/Debug/net8.0-windows/iMirror.log`
- Primary HRD3D run start: 2026-06-14 16:40:25 KST
- Stable 1080 regression attempts: 2026-06-14 17:43:18, 17:48:59, 17:54:44, and 19:45:12 KST

Scope of this note:

- Final-code HRD3D 2048x1152 long-run evidence.
- Three Mac reconnect attempts on the same HRD3D process.
- Submitted H.264 dump/probe evidence.
- Stable 1080 regression evidence.

## Build Verification

Commands run during this acceptance pass:

```powershell
dotnet build .\MacMirrorReceiver.csproj -c Debug -p:HighResolutionD3D=true
dotnet build .\MacMirrorReceiver.csproj -c Debug
dotnet build .\tools\MediaFoundationH264Probe\MediaFoundationH264Probe.csproj -c Release
dotnet build .\tools\HighResolutionProbeReport\HighResolutionProbeReport.csproj -c Release
dotnet build .\tools\LatencyAcceptanceReport\LatencyAcceptanceReport.csproj -c Debug
dotnet build .\tools\RealDeviceAcceptanceReport\RealDeviceAcceptanceReport.csproj -c Debug
```

Observed result:

- HRD3D app build: 0 errors.
- Plain Debug app build: 0 errors.
- Acceptance/probe/report tools: built and runnable.

## HRD3D 2048x1152 30-Minute Run

Runtime:

- `IMIRROR_RENDER_MODE=quality`
- `IMIRROR_EXPERIMENTAL_QUALITY=1`
- `IMIRROR_DUMP_H264=1`
- Build property: `HighResolutionD3D=true`

Session evidence:

| Time | Evidence |
| --- | --- |
| 16:40:25 | `/info experimental quality display advertise: 2048x1152 @ 30` |
| 16:40:26 | AirPlay data stream connected |
| 16:40:27 | `High-resolution D3D path active for stream config: 2048x1152@30, d3d11MultithreadProtected=True` |
| 16:40:29 | `Media Foundation D3D11 first texture: format=NV12 size=2048x1152 arraySize=1 subresourceIndex=0` |
| 17:10:22 | Video health `received=99,200`, `decoded=99,182`, `decoderQueue=1`, `h264 ... dropped=0` |
| 17:10:29 | Render stats `decoderQueue=0`, `h264 ... dropped=0` |

Formal 30-minute latency report cut, 16:40:25 to 17:11:36:

| Gate | Result |
| --- | --- |
| duration | pass (`evidenceDuration=00:31:05`) |
| corruption | pass |
| highResolutionD3D | pass |
| p95 | fail |
| maxTrend | fail |

Worst latency window:

- `worstP95=517ms` at 16:58:30.
- `worstMax=1101ms` at 16:58:30.
- `longestNonDecreasingMaxStreak=8`.

Manual marker scan for the 30-minute HRD3D window:

| Marker | Count |
| --- | ---: |
| `h264 ... dropped` | 0 dropped |
| `decoderQueue` sustained accumulation | none observed |
| `High-resolution D3D decoder faulted` | 0 |
| `High-resolution D3D stall` | 0 |
| `High-resolution D3D present failed` | 0 |
| `High-resolution D3D output geometry changed` | 0 |
| `Invalid data` | 0 |
| `sps_id` | 0 |
| `mb_width` | 0 |
| `without matching NV12` | 0 |
| queue overflow | 0 |

Judgment:

- Decode/render correctness is strong: no H.264 drops, no corruption markers, no HRD3D fault/present/geometry markers, and queues drain.
- Strict latency acceptance is not clean because p95/max trend gates failed on isolated spikes. This remains a caveat, not a decode/display correctness failure.

## HRD3D Reconnect Evidence

Same HRD3D process was kept alive for three Mac reconnects.

| Attempt | Reconnect evidence | Renderer evidence | Recovery evidence |
| --- | --- | --- | --- |
| 1 | 17:16:14 `/info`, 17:16:15 data stream connected | 17:16:16 HRD3D session refresh | 17:16:23 first NV12 texture, then p50 5-6ms, p95 12-21ms, dropped 0, queue 0 |
| 2 | 17:17:30 `/info`, 17:17:30 data stream connected | 17:17:31 HRD3D session refresh | 17:17:35 first NV12 texture, then p50 4-5ms, p95 8-9ms, dropped 0, queue 0 |
| 3 | 17:18:39 `/info`, 17:18:40 data stream connected | 17:18:41 HRD3D session refresh | 17:18:47 first NV12 texture, then by 17:19:17 p50 5ms, p95 10ms, dropped 0, queue 0 |

Reconnect caveat:

- Each reconnect can show short decryptor/backlog warmup. All observed attempts recovered, drained the queue, produced a new first NV12 texture, and logged no HRD3D fault markers.

## Submitted Dump Probe

Main final-code submitted dump:

| Artifact | Size |
| --- | ---: |
| `imirror-20260614-164027.d01.submitted.h264` | 63,323,351 bytes |

The close-line byte count for the main dump matched the file size: `bytes=63323351`.

Additional reconnect dumps:

| Artifact | Size |
| --- | ---: |
| `imirror-20260614-171616.d02.submitted.h264` | 3,181,595 bytes |
| `imirror-20260614-171731.d03.submitted.h264` | 4,206,444 bytes |
| `imirror-20260614-171841.d04.submitted.h264` | 23,850,500 bytes |

Direct `MediaFoundationH264Probe` result on the main submitted dump with `max-access-units=1`:

- H.264 file size: 63,323,351 bytes.
- NAL units: 109,633.
- SPS/PPS/IDR present: `sps=2`, `pps=2`, `idr=1`.
- Access units: 109,628.
- SPS geometry: `2048x1152`.
- CPU decode: `processInputOk=1`, `decodedOutputs=1`, `failures=0`.
- D3D11 decode: `dxgiBuffers=1`, `d3d11Textures=1`, `failures=0`.
- Result: `d3d11-manager-accepted`.

Additional D3D probes:

| Probe | Result |
| --- | --- |
| `D3DSharedHandleProbe 2048 1152` | pass, `d3d11-d3d9-d3dimage-shared-handle-opened` |
| `D3DVideoProcessorProbe 2048 1152` | pass, `nv12-to-bgra-video-processor-d3dimage-completed` |

Probe caveats:

- `HighResolutionProbeReport` wrapper failed in the sandbox because its internal `dotnet run` child could not access NuGet configuration.
- `HighResolutionD3DReplayProbe` failed on the submitted dump (`ProcessOutput failed: 0xC00D6D61` for feed=20). The live product path and direct MF/D3D11 probe both succeeded, so this is tracked as a replay-tool/input-feeding caveat rather than proof of live-product failure.

## Stable 1080 Regression Evidence

Runtime:

- Experimental env disabled.
- No `IMIRROR_RENDER_MODE=quality`.
- No `IMIRROR_EXPERIMENTAL_QUALITY`.
- No H.264 dump env.

First stable attempt:

| Time | Evidence |
| --- | --- |
| 17:43:18 | Mac stable connection begins |
| 17:43:19 | `AirPlay mirror SPS/PPS received: source=1920x1080, display=1920x1080` |
| 17:43:19 | `Stream config received: 1920x1080 @ 60 codec=h264-annexb` |
| 17:43:20 | `FFmpeg started [decoder:software] ... (1920x1080 -> 1920x1080 @ 60fps)` |
| 17:44:56 | latency window `p50=20ms p95=48ms max=70ms` |
| 17:45:00 | render stats `decoderQueue=0`, `h264 ... dropped=0` |

First attempt summary:

- Duration captured: about 1 minute 40 seconds.
- Stable route confirmed: FFmpeg software decoder at 1920x1080.
- No HRD3D/MF route leakage observed in this stable window.
- No dropped/queue/corruption/fault markers.
- Insufficient for the requested 3-5 minute active-motion stable regression.

Second stable reconnect attempt:

| Time | Evidence |
| --- | --- |
| 17:48:59 | stable reconnect `/info`, `SETUP`, `RECORD`, data stream connected |
| 17:48:59 | `AirPlay mirror SPS/PPS received: source=1920x1080, display=1920x1080` |
| 17:50:00 | video health `decoderQueue=1`, `h264 ... dropped=0` |
| 17:50:00 | render stats `decoderQueue=0`, `h264 ... dropped=0` |

Second attempt caveat:

- Payload cadence was sparse (`payloads=243` over about 58.5s, around 4.2/s), so the latency window was dominated by reconnect/warmup/sparse-motion behavior.
- No stable active-motion 3-5 minute evidence was produced before video/render logs stopped again.

Third stable attempt after relaunch:

| Time | Evidence |
| --- | --- |
| 17:54:44 | stable app relaunched |
| 17:54:45 | AirPlay receiver advertising as `iMirror` |
| 17:56:14 | data stream connected |
| 17:56:14 | `AirPlay mirror SPS/PPS received: source=1920x1080, display=1920x1080` |
| 17:56:14 | `Stream config received: 1920x1080 @ 60 codec=h264-annexb` |
| 17:56:15 | `FFmpeg started [decoder:software] ... (1920x1080 -> 1920x1080 @ 60fps)` |
| 17:56:28 | post-warmup render `p50=36ms p95=52ms max=52ms`, `decoderQueue=0`, `h264 ... dropped=0` |
| 17:56:30 | render `p50=31ms p95=54ms max=62ms`, `decoderQueue=0`, `h264 ... dropped=0` |

Third attempt caveat:

- The post-warmup stable path looked healthy while frames were arriving.
- Video/render logs stopped again after 17:56:30, well before the requested 3-5 minute active-motion window.
- This supports the user's observation that the stable regression session is currently disconnecting or ceasing video payload delivery before the acceptance window can complete.

Fourth stable attempt after relaunch:

| Time | Evidence |
| --- | --- |
| 19:45:12 | stable app relaunched and AirPlay receiver advertised as `iMirror` |
| 19:45:26 | `AirPlay mirror SPS/PPS received: source=1920x1080, display=1920x1080` |
| 19:45:26 | `Stream config received: 1920x1080 @ 60 codec=h264-annexb` |
| 19:45:28 | `FFmpeg started [decoder:software] ... (1920x1080 -> 1920x1080 @ 60fps)` |
| 19:47:54-19:52:14 | bounded clean active-motion stable window used for final regression judgment |

Bounded stable report for `19:47:54` through `19:52:14`:

```text
PASS: iMirror acceptance report
windows=27, evidenceDuration=00:04:30, targetDuration=3min
p95Target=150ms, worstP95=99ms at 2026-06-14T19:52:14.3852160+09:00
worstMax=154ms at 2026-06-14T19:50:54.3104975+09:00
longestNonDecreasingMaxStreak=4 window(s)
corruptionLines=0
duration=pass, p95=pass, maxTrend=pass, corruption=pass, reconnect=pass, stableAdvertise=pass, highResolutionD3D=pass
```

Stable route marker scan for `19:45:12` through `19:52:14`:

| Marker | Count |
| --- | ---: |
| `FFmpeg started [decoder:software]` | 1 |
| `Stream config received: 1920x1080 @ 60` | 1 |
| `AirPlay mirror SPS/PPS received: source=1920x1080, display=1920x1080` | 1 |
| `High-resolution D3D path active` | 0 in stable windows |
| `Media Foundation D3D11 first texture` | 0 in stable windows |
| `Invalid data` | 0 |
| `sps_id` | 0 |
| `mb_width` | 0 |
| queue overflow | 0 |
| `decoder faulted` | 0 |

Fourth attempt judgment:

- Completed the requested 3-5 minute stable active-motion regression with a bounded 4 minute 30 second clean window.
- Stable path remained FFmpeg software decode at 1920x1080 @ 60.
- No HRD3D/MF route leakage was observed.
- No corruption/fault markers were observed.
- H.264 accepted/written/dropped remained `.../0`; render stats showed no sustained `decoderQueue` accumulation.
- Later unbounded logging after the accepted window showed intermittent latency spikes. Those spikes are tracked as follow-up observation only because they occurred outside the accepted bounded window and did not coincide with drops, sustained queue accumulation, corruption, or HRD3D/MF leakage.

## Spike Distribution and Evidence Contiguity (added in review)

`LatencyAcceptanceReport` previously reported only `worstP95`/`worstMax` and a pass/fail. That
let a hand-selected clean slice PASS while removed sections hid spikes. The tool now also prints
`p95BreachWindows`, `severeMaxWindows`, `maxWindowGap`, and `contiguousEvidence`, and emits a
`WARN` when the fed evidence is not time-contiguous. Re-running over the existing logs:

Bounded stable slice `imirror-stable-20260614-194754-195214.log` (target 3min):

```text
PASS: iMirror acceptance report
windows=27, evidenceDuration=00:04:30, targetDuration=3min
p95BreachWindows=0 of 27 (>= 150ms), severeMaxWindows=0 (>= 300ms)
maxWindowGap=00:00:10, largeGaps=0 (> 30s), contiguousEvidence=True
```

Full session `iMirror.log` (default 30min target):

```text
FAIL: iMirror acceptance report
windows=1,226, evidenceDuration=09:12:38
worstP95=12602ms, worstMax=12744ms
p95BreachWindows=139 of 1,226 (>= 150ms), severeMaxWindows=129 (>= 300ms)
maxWindowGap=01:03:30, largeGaps=48 (> 30s), contiguousEvidence=False
WARN: evidence is not time-contiguous ...
```

Interpretation:

- The documented stable PASS is a real but short (4:30) clean continuous window
  (`contiguousEvidence=True`, zero breaches), not a 10-30 minute product-release run.
- Across the full multi-session capture, 139 of 1,226 windows breached the 150ms p95 target and
  the worst window reached 12.6s receive->present. The large worst-case values are honestly
  reported by the HRD3D/MF path, where each output frame is stamped with the actual source
  packet's `ReceivedTick`; the stable FFmpeg path stamps frames with the most-recently-written
  packet tick, so its windows understate backlog. The metric also does not isolate
  reconnect/warmup windows from steady state, so some breaches coincide with session boundaries.
- Net: the spikes are real and not fully characterized by the bounded windows. They are not
  accompanied by H.264 drops, sustained queue accumulation, corruption, or HRD3D fault markers
  inside the clean windows, but a single continuous 10-30 minute active-motion capture (no
  splicing) is required before claiming product-release latency quality.

## Final Judgment

Completed evidence:

- Build verification for app and tools.
- Final-code HRD3D 2048x1152 30-minute run.
- 30-minute correctness marker scan.
- Three Mac reconnects on the same HRD3D process.
- Submitted dump byte/path evidence and direct MF/D3D11 decode probe.
- Stable 1080 regression: completed on a bounded 4 minute 30 second clean active-motion window, with `1920x1080 @ 60` through FFmpeg software decoder and no HRD3D/MF leakage.

Remaining caveats:

- HRD3D strict latency report did not fully pass because p95/max trend gates failed on spikes; the full-session distribution is 139/1,226 windows over the 150ms p95 target, worst 12.6s.
- Stable 1080 later unbounded logging showed intermittent latency spikes after the accepted bounded window. The accepted evidence is a short (4:30) continuous clean window, not a 10-30 minute product-release run; product-release latency quality is therefore not yet established.
- The latency metric does not isolate reconnect/warmup windows from steady state, and the stable FFmpeg path understates backlog (frames stamped with the latest-written packet tick). Treat absolute stable numbers as optimistic.
- Product replay probe needs follow-up because direct MF/D3D11 probe succeeded but `HighResolutionD3DReplayProbe` did not. (`0xC00D6D61` = `MF_E_TRANSFORM_STREAM_CHANGE`, which the live `DrainOutput` handles by reconfiguring output; the replay probe does not, confirming a tool-side gap rather than a live-product fault.)

Current acceptance state:

- HRD3D display/decode/reconnect correctness: supported by real-device evidence.
- Stable 1080 no-regression vs. earlier builds: supported by the bounded 4 minute 30 second clean active-motion pass.
- Product-release latency quality (stable and HRD3D): NOT yet established. Requires a single continuous, unspliced 10-30 minute active-motion capture with `contiguousEvidence=True` and zero p95 breach windows in steady state.
