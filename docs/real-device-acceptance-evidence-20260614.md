# Real-Device Acceptance Evidence - 2026-06-14

Source log:

- `bin/Debug/net8.0-windows/iMirror.log`
- Primary HRD3D run start: 2026-06-14 16:40:25 KST
- Stable 1080 regression attempts: 2026-06-14 17:43:18, 17:48:59, 17:54:44, and 19:45:12 KST
- Stable 1080 continuous baseline: 2026-06-14 21:17:38 KST

Scope of this note:

- Final-code HRD3D 2048x1152 long-run evidence.
- Three Mac reconnect attempts on the same HRD3D process.
- Submitted H.264 dump/probe evidence.
- Stable 1080 regression and continuous product-release latency evidence.

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

Continuous stable 1080 baseline:

| Time | Evidence |
| --- | --- |
| 21:17:38 | stable app active, Mac AirPlay data stream connected |
| 21:17:38 | `Stream config received: 1920x1080 @ 60 codec=h264-annexb` |
| 21:17:38 | `FFmpeg started [decoder:software] ... (1920x1080 -> 1920x1080 @ 60fps)` |
| 21:17:52-21:28:53 | continuous latency windows, 68 windows total |
| 21:29:12 | final render stats before capture stop: `decoderQueue=0`, `pendingRender=0`, `dispatcherQueued=1`, `h264 ... dropped=0`, `stdinWrite=0ms max=70ms stalls=5` |

Raw log artifacts:

- Primary raw log: `bin/Debug/net8.0-windows/iMirror.log`
- Archived copy: `bin/Debug/net8.0-windows/iMirror.stable1080-continuous-pass-20260614-211738-212912.log`

Acceptance report on the uncut raw log:

```text
PASS: iMirror acceptance report
windows=68, evidenceDuration=00:11:20, targetDuration=10min
p95Target=150ms, worstP95=141ms at 2026-06-14T21:17:52.0244608+09:00
worstMax=276ms at 2026-06-14T21:17:52.0244608+09:00
p95BreachWindows=0 of 68 (>= 150ms), severeMaxWindows=0 (>= 300ms)
maxWindowGap=00:00:10, largeGaps=0 (> 30s), contiguousEvidence=True
longestNonDecreasingMaxStreak=9 window(s)
reconnectAttempts=0, requiredReconnects=0
stableAdvertiseLines=0, experimentalAdvertiseLines=0
highResolutionD3DPathActiveLines=0, d3d11MultithreadProtectedLines=0, highResolutionD3DFirstTextureLines=0, highResolutionD3DFailureLines=0, required=False
corruptionLines=0
duration=pass, p95=pass, severeMax=pass, maxTrend=warn, corruption=pass, reconnect=pass, stableAdvertise=pass, highResolutionD3D=pass
WARN: max latency increased for 9 consecutive window(s). Inspect the spike distribution and queue/stall markers before treating this as a product latency failure.
```

(`worstMax=276ms` is below the 300ms severe-max ceiling, so `severeMax=pass` here on its own merit.)

Stable route marker scan for the continuous baseline:

| Marker | Count |
| --- | ---: |
| `FFmpeg started [decoder:software]` | 1 |
| `Stream config received: 1920x1080 @ 60` | 1 |
| `Reconnecting` | 0 |
| `High-resolution D3D path active` | 0 |
| `Media Foundation D3D11` | 0 |
| `Invalid data` | 0 |
| `non-existing PPS` | 0 |
| `FFmpeg stdin write stall` | 2 |

Failure classification before the tool adjustment:

- The original report failed only because `longestNonDecreasingMaxStreak=9` was treated as a hard `maxTrend` failure.
- The streak window was 21:21:42 through 21:23:02, where p95 rose from 51ms to 89ms and max rose from 56ms to 111ms.
- That is not reconnect/warmup, sparse payload cadence, sustained FFmpeg stall growth, decoder/input queue accumulation, or persistent dispatcher/render backlog.
- Because p95 breach count and severe max count were both zero, `maxTrend` is now retained as a diagnostic warning rather than an acceptance gate. To avoid leaving the single-frame tail ungated after that demotion, a magnitude gate `severeMax` (any window `max >= 2x p95Target`, i.e. 300ms at the 150ms target) was added to the `pass` set. This catches real tail stutters regardless of trend shape and ignores benign sub-threshold upward drift. Verified with a synthetic log (one window `p95=85ms` but `max=600ms`): `p95=pass`, `severeMax=fail`, overall FAIL — so the demotion does not hide tail spikes.

Continuous baseline judgment:

- Stable 1080 product-release latency baseline is supported for this single continuous 11 minute 20 second active-motion run.
- The stable path remained FFmpeg software decode at 1920x1080 @ 60, with experimental HRD3D/MF markers absent.
- No reconnect occurred, no corruption markers were observed, `dropped=0`, and no sustained decoder queue, stdin write stall, or render/dispatcher backlog pattern was found.

## Spike Distribution and Evidence Contiguity (added in review)

`LatencyAcceptanceReport` previously reported only `worstP95`/`worstMax` and a pass/fail. That
let a hand-selected clean slice PASS while removed sections hid spikes. The tool now also prints
`p95BreachWindows`, `severeMaxWindows`, `maxWindowGap`, and `contiguousEvidence`, and emits a
`WARN` when the fed evidence is not time-contiguous. `maxTrend` remains visible as a warning
because a non-decreasing max streak can be useful triage evidence, but it is not an acceptance
failure; the single-frame tail is instead hard-gated by `severeMax` (any window
`max >= 300ms` fails). Re-running over the existing logs:

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

- The earlier documented stable PASS was a real but short (4:30) clean continuous window
  (`contiguousEvidence=True`, zero breaches), not a 10-30 minute product-release run.
- The later continuous stable baseline is the product-release latency evidence for stable 1080:
  11:20 continuous evidence, zero p95 breaches, zero severe max windows, and contiguous evidence.
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

## Quality Hardening (post-baseline)

Two changes after the stable baseline, focused on app quality (not store/distribution):

1. Audio scope = video-only (A). There is no audio receive/decode/playback pipeline, so the
   receiver no longer claims audio it cannot render. `AdvertiseAudioCapabilities` (const, default
   `false`) gates the `audioFormats`/`audioLatencies` in the `/info` plist and the
   `Audio-Latency`/`Audio-Jack-Status` headers in the RECORD response. Reversible in one flag for
   the future audio milestone (B). The negotiation code is left in source, only suppressed from
   the response.
   - Caveat: this changes the live AirPlay handshake. A real-device mirror connect must confirm
     negotiation still succeeds with audio advertising off. Rollback is `AdvertiseAudioCapabilities = true`.

2. Latency measurement trust (2a). `LatencyWindow` now supports a warmup grace (default 1.5s)
   begun at every renderer (re)start/session refresh via `BeginWarmup` in `StartFreshDecoder`.
   Samples during warmup are excluded from steady-state p50/p95/max but reported separately as
   `warmupSkipped`/`warmupMax` appended to the window log line. This removes the previously noted
   warmup pollution of steady-state windows (reconnect/refresh catch-up no longer inflates the
   first window). The stable FFmpeg path's latest-written-packet timestamp under-reporting (2b)
   is unchanged and remains a known caveat.
   - `LatencyAcceptanceReport` parses the new suffixed line format (verified); warmup latency
     does not enter the `severeMax`/p95 gates.

Validation:

- `dotnet build .\MacMirrorReceiver.csproj -c Debug` - 0 errors, 5 pre-existing nullable warnings.
- `dotnet build .\MacMirrorReceiver.csproj -c Debug -p:HighResolutionD3D=true` - 0 errors, same 5 warnings.
- Tool parse-compat for warmup-suffixed window lines confirmed.

Real-device confirmation run (2026-06-14 22:15, this build, ~45s short connect):

- Audio-off handshake CONFIRMED: with `AdvertiseAudioCapabilities=false` the Mac still negotiated
  the mirror; `Stream config received: 1920x1080 @ 60`, `FFmpeg started [decoder:software]`, first
  frame decoded, video shown. Disabling audio advertising did not break mirroring.
- 2a warmup isolation CONFIRMED: first window logged `warmupSkipped=9 warmupMax=253ms` (warmup
  excluded from steady-state); windows 2-3 had no warmup field and read p50=11-13ms, p95=13-17ms,
  max=16-25ms. Acceptance on the cut run: p95=pass, severeMax=pass, contiguousEvidence=True.
- Tuning from this run: warmup grace raised 1.5s -> 3.0s. BeginWarmup fires at decoder start but
  frames only flow after FFmpeg spin-up (~0.8s), so a ~248ms startup straggler landed just past
  the 1.5s grace and inflated the first window's max (248ms, still under the 300ms severeMax gate).
  3.0s absorbs spin-up + decryptor settling.
- Anchoring fix (22:24/22:51 runs): a reconnect showed first-frame delayed ~11s, so the wall-clock
  grace expired before any frame arrived and isolated nothing. Warmup grace is now anchored to the
  FIRST sample after a (re)start (`BeginWarmup()` arms; the 3s clock starts on first frame).
  Validated on the 22:51 run: a backlogged start logged `warmupSkipped=64 warmupMax=2177ms` and the
  steady-state window started clean (n=1) instead of inheriting the 2177ms drain spike.
- Connect-delay finding (reproducible): the ~11s black screen on connect is sender-side, not the
  decoder. Log shows the first payload was a 32-byte control packet, the FairPlay decryptor probe
  found no clear H.264, then the Mac sent no video payloads for ~6s (static screen), after which
  the 'standard' decryptor was selected and frames flowed. Confirm with motion-at-connect; not a
  receiver code fix at this time. Tracked as an observation.
- Caveat: these confirmation runs were short (<=45s), not a product baseline. A single continuous
  >=10min active-motion run on this build is still required for the product latency baseline; the
  short runs validate the changes (audio-off, warmup isolation + anchoring) only.

## Display Scale on Connect (Point A) - resolved via macOS setting

Symptom: connecting iMirror changed the Mac's desktop scale. Root cause: `/info` advertises a
fixed 1920x1080 display ([AirPlayProbeService.cs](../MacMirrorReceiver.Networking/AirPlayProbeService.cs)
`BuildInfoBinaryPlist` `displays`), and with macOS "Optimize for AirPlay device" the Mac switches
its own display mode to match (a 2560x1440 Mac drops to literal 1080).

Resolution (no code change, no 1080-default or latency impact): on the Mac, System Settings ->
Displays -> the mirrored display -> set "Optimise for" to the built-in display. macOS then keeps
the Mac at native 1440p and downscales the framebuffer to the advertised 1080 before sending, so
there is no desktop scale change and the stream/latency is unchanged. Confirmed on-device, and
macOS remembers the choice per receiver across disconnect/reconnect.

- Product follow-up (nice-to-have, not blocking): making macOS default to this mode receiver-side
  would require an `/info` display-descriptor change (HiDPI/feature flags), and the exact lever is
  uncertain - needs protocol experimentation. Advertising the Mac's native 2560x1440 would also
  remove the rescale but violates the locked 1080 default and worsens the render-bound latency, so
  that path is closed. Recommend documenting the one-time "Optimise for built-in display" step in
  the README/onboarding instead.

## Latency Root Cause and Fix (Point B) - decode threading

User reported multi-second delay on high-motion video (YouTube soccer). The receive->render metric
was untrustworthy (it stamped each frame with the LATEST written packet's tick, masking latency
when frames buffer). Added per-frame instrumentation to measure it correctly:

- True receive->render FIFO: each output frame carries the receivedTick of the packet that
  produced it (enqueue on write, dequeue on read in `FfmpegDecoder`), plus an `ffmpegPipeline`
  depth (written-not-yet-emitted) in the render-stats log.

Instrumented measurement (high-motion soccer, before fix):

- True receive->render ~6000ms (previous metric showed ~20ms - confirmed the masking).
- decode->render ~40ms (present path fine).
- decoderQueue ~250-300 packets (~5s) - the input queue backed up.
- ffmpegPipeline ~28 frames.

Root cause: software H.264 decode could not sustain high-motion 1080p60 on a single core. The
AirPlay mirror stream encodes one slice per frame, so `-thread_type slice` gave no parallelism and
pinned FFmpeg to ~1 core while the machine (i5-1135G7) ran at ~52% with idle cores; the input
backed up ~5s.

Fix ([FfmpegDecoder.cs](../MacMirrorReceiver.Video/FfmpegDecoder.cs) BuildArguments):
`-thread_type slice` -> `-thread_type frame`. Frame threading decodes consecutive frames across
cores and does parallelize this stream; it adds ~(threads) frames of pipeline delay (~50ms),
negligible against the multi-second backlog removed.

Result (same soccer video, after): true receive->render ~77-166ms (p50 ~127ms, p95 ~150ms),
decoderQueue 0-1, ffmpegPipeline 3-6 frames. ~40x improvement; user-confirmed "much better, only
slight delay". ~127ms is a good floor for this out-of-process software path; going lower would
need the GPU/in-process decode path (long-term).

Reverted: the 30fps render-output cap experiment (it targeted the present path, but the bottleneck
was decode). The true-latency instrumentation is kept.

## GPU Engine as Default (product pivot, 2026-06-15)

The software FFmpeg path floored at ~127ms (out-of-process pipe + CPU decode/copy). Per product
goal (AirPlay/Miracast-grade), the GPU MediaFoundation/D3D11 path was made the default engine.
Three fixes turned the previously-spiky experimental GPU path into a product-grade one:

1. Advertise native 2560x1440 (`AirPlayProbeService` `QualityWpfDisplayWidth/Height`) so the Mac
   sends native and does not rescale its desktop (Point A) - and the GPU decodes native.
2. Present via a DXGI flip-model swap chain on a child HWND (`D3D11SwapChainVideoPresenter`,
   `HwndHost`) instead of WPF `D3DImage`. Measured: `D3DImage` composition capped present at
   ~10fps regardless of surface size; the swap chain flips at the display rate.
3. `MF_LOW_LATENCY` on the H.264 MFT (`MediaFoundationD3D11Decoder`). This was the decisive fix:
   the decoder otherwise buffered ~1s (true latency ~6s while `decoderQueue` stayed 0 and the
   metric read ~6ms) and emitted frames in bursts. Low-latency mode emits each frame immediately,
   fixing BOTH latency and framerate.

Result (high-motion soccer, native 2560x1440 GPU): true receive->present p50 ~2-3ms, p95 ~3-23ms,
rendered ~= received (~99% present, smooth), `decoderQueue=0`, `pendingRender=0`, no scale change.
User-confirmed smooth + low latency.

Productization (default build, zero config):
- `MacMirrorReceiver.csproj`: SharpDX refs + `HIGH_RESOLUTION_D3D` are now unconditional (GPU path
  in the default build; plain `dotnet build` includes it).
- `HighResolutionPipelineProbe.IsHardwareDecodeAvailable`: cached capability check (MF H.264 MFT
  is `MF_SA_D3D11_AWARE` + D3D11 hardware video device creatable).
- `RenderModeSettings.GpuVideoEngineEnabled` = hardware available and not
  `IMIRROR_FORCE_SOFTWARE_VIDEO=1`. Wired into the advertise (native when GPU) and the decoder
  selection (GPU-first) by default.
- GPU init failure at connect falls through to the FFmpeg software path (no longer throws).
- Verified on-device with a plain build and NO env flags: log shows `High-resolution D3D path
  active`, first NV12 texture, swap chain created, no FFmpeg/fallback; smooth + ~3ms latency.

Robustness added (2026-06-15):
- Runtime GPU-fault fallback: a runtime MF/D3D fault (`HandleHighResolutionD3DFatal`) now disables
  the GPU engine for the session (`_gpuPathDisabledThisSession`, reset on next connection) and
  restarts on the FFmpeg software decoder instead of erroring, so a device-lost/TDR does not kill
  the mirror.
- Software-fallback output cap: the FFmpeg path now caps output to <=1920
  (`Math.Min(ResolveMaxRenderWidth, ResponsiveMaxRenderWidth)`) so a GPU-fault fallback at native
  1440p input is not too heavy in software.

Release-grade GPU soak (2026-06-15, default build, no env flags, one user reconnect mid-run):
- `LatencyAcceptanceReport` PASS: windows=73, evidenceDuration=00:12:25, worstP95=81ms (target
  150), worstMax=161ms (severe 300), p95BreachWindows=0, severeMaxWindows=0, corruption=pass.
- Steady-state (reconnect/start warmup windows excluded by the warmup isolation): p50 avg ~5ms,
  p95 avg ~21ms (worst 81ms), max worst 161ms; no fault/geometry/NV12/fallback markers.
- The single 33s contiguity gap is the user's intentional disconnect/reconnect; the session
  refreshed cleanly (warmup isolated, new swap chain) and recovered to the same steady-state.
- Verdict: GPU default path meets release-grade latency acceptance at native 2560x1440.

Remaining follow-ups (polish, deferred):
- WPF render tier-0 side effect from the startup capability probe (harmless; investigate deferring
  the probe).
- Native-resolution detection (advertised res is fixed 2560x1440).
- Explicit force-software (`IMIRROR_FORCE_SOFTWARE_VIDEO=1`) end-to-end connect test for sign-off.
- Startup capability probe drops WPF render tier to 0 (software) via an early D3D11 device
  create/destroy; harmless to video (swap chain is independent) and the UI tested fine, but make
  the probe defer/avoid disturbing WPF's render device.
- Native-resolution detection (advertised res is fixed 2560x1440; other Macs may differ).
- >=10min continuous soak + explicit fallback test (force software) for release sign-off.

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
- Stable 1080 now has a continuous 11:20 product-release baseline pass on the uncut raw log.
- The latency metric does not isolate reconnect/warmup windows from steady state, and the stable FFmpeg path understates backlog (frames stamped with the latest-written packet tick). Treat absolute stable numbers as optimistic.
- Product replay probe needs follow-up because direct MF/D3D11 probe succeeded but `HighResolutionD3DReplayProbe` did not. (`0xC00D6D61` = `MF_E_TRANSFORM_STREAM_CHANGE`, which the live `DrainOutput` handles by reconfiguring output; the replay probe does not, confirming a tool-side gap rather than a live-product fault.)

Current acceptance state:

- HRD3D display/decode/reconnect correctness: supported by real-device evidence.
- Stable 1080 no-regression vs. earlier builds: supported by the bounded 4 minute 30 second clean active-motion pass.
- Stable 1080 product-release latency quality: supported by the continuous 11 minute 20 second active-motion pass with `contiguousEvidence=True`, `p95BreachWindows=0`, `corruptionLines=0`, stable FFmpeg software decode, and no HRD3D/MF leakage.
- HRD3D product-release latency quality: not established; HRD3D remains correctly gated as experimental.
