# Real-Device Acceptance Evidence - 2026-06-13

Source log:

- `bin/Debug/net8.0-windows/iMirror.log`
- Last observed write time: 2026-06-13 16:00:36 KST

Scope of this note:

- Evidence for the final reconnect/display-recovery correctness fixes.
- Evidence that stable 1080 still routes through the stable FFmpeg path.
- This is not a claim that the final code has completed a fresh 30-minute HRD3D long-run. The earlier 34-minute clean run remains useful steady-state evidence, but it happened before the final reconnect-gate correction. Treat the final-code evidence below as reconnect and regression evidence.

## Build Verification

Commands run after the final reconnect-gate fix:

```powershell
dotnet build .\MacMirrorReceiver.csproj -c Debug -p:HighResolutionD3D=true
dotnet build .\MacMirrorReceiver.csproj -c Debug
```

Results:

- `HighResolutionD3D=true`: 0 errors, 0 warnings.
- Plain Debug: 0 errors, 5 existing nullable warnings in networking (`MirrorClient.cs`, `MdnsBrowser.cs`).

## HRD3D Reconnect Evidence

Runtime:

- Experimental quality enabled.
- Advertised/received stream: `2048x1152 @ 30`.
- Path: Media Foundation / D3D11 / D3D9Ex / D3DImage.
- Texture evidence on this GPU: `format=NV12 size=2048x1152 arraySize=1 subresourceIndex=0`.

Initial post-fix connection:

| Time | Evidence |
| --- | --- |
| 15:10:53-15:10:54 | `/info experimental quality display advertise: 2048x1152 @ 30` |
| 15:10:54 | AirPlay data stream connected |
| 15:10:54 | SPS/PPS `source=2048x1152, display=2048x1152` |
| 15:10:55 | `High-resolution D3D path active for stream config: 2048x1152@30` |
| 15:10:57 | `Media Foundation D3D11 first texture: format=NV12 size=2048x1152 arraySize=1 subresourceIndex=0` |
| 15:10:57 | `Media Foundation D3D11 decoder produced first NV12 texture` |
| 15:12:07 | latency window `p50=17ms p95=64ms max=141ms`, `h264 ... dropped=0` |

Reconnect attempts after the final gate fix:

| Attempt | Reconnect evidence | Renderer refresh | First texture | Recovery evidence |
| --- | --- | --- | --- | --- |
| 1 | 15:12:32 data stream connected | 15:12:32 repeated config refresh, 15:12:33 HRD3D active | 15:12:38 first NV12 texture | 15:12:48 latency `p50=8ms p95=108ms max=151ms`, `dropped=0` |
| 2 | 15:15:20 data stream connected | 15:15:20 repeated config refresh, 15:15:22 HRD3D active | 15:15:29 first NV12 texture | 15:16:01 current window `p50=7ms p95=23ms max=23ms`, `dropped=0` |
| 3 | 15:16:54 data stream connected | 15:16:54 repeated config refresh, 15:16:55 HRD3D active | 15:17:04 first NV12 texture | 15:18:02 current window `p50=7ms p95=18ms max=18ms`, `dropped=0` |

Marker counts from the post-fix HRD3D/stable evidence window (`>= 2026-06-13T15:10`):

| Marker | Count |
| --- | ---: |
| `High-resolution D3D decoder faulted:` | 0 |
| `High-resolution D3D stall:` | 0 |
| `High-resolution D3D present failed:` | 0 |
| `High-resolution D3D output geometry changed:` | 0 |
| `without matching NV12` | 0 |
| `decoder input overflow` | 0 |
| `Pre-render video queue dropped` | 0 |
| `Invalid data` | 0 |
| `non-existing PPS` | 0 |
| `sps_id` | 0 |
| `mb_width` | 0 |

Captured HRD3D dump artifacts:

| Dump | Size |
| --- | ---: |
| `imirror-20260613-151055.d01.received.h264` | 1,751,840 bytes |
| `imirror-20260613-151055.d01.submitted.h264` | 1,751,809 bytes |
| `imirror-20260613-151233.d02.received.h264` | 3,309,640 bytes |
| `imirror-20260613-151233.d02.submitted.h264` | 3,309,609 bytes |
| `imirror-20260613-151522.d03.received.h264` | 719,470 bytes |
| `imirror-20260613-151522.d03.submitted.h264` | 719,439 bytes |
| `imirror-20260613-151655.d04.received.h264` | 259,434 bytes |
| `imirror-20260613-151655.d04.submitted.h264` | 259,434 bytes |

Note: the byte difference between received/submitted on some reconnect segments is expected when the submitted dump only records packets after successful `ProcessInput`.

## Stable 1080 Regression Evidence

Runtime:

- Relaunched after stopping the experimental HRD3D process.
- No `IMIRROR_EXPERIMENTAL_QUALITY`, no `IMIRROR_RENDER_MODE`, no dump env.

Evidence:

| Time | Evidence |
| --- | --- |
| 15:19:17 | stable process start |
| 15:19:35 | AirPlay data stream connected |
| 15:19:35 | SPS/PPS `source=1920x1080, display=1920x1080` |
| 15:19:36 | `FFmpeg started [decoder:software] ... (1920x1080 -> 1920x1080 @ 60fps)` |
| 15:19:39 | `FFmpeg decoded first frame` |
| 15:20:01 | current window `p50=44ms p95=82ms max=93ms`, `decoderQueue=0`, `h264 ... dropped=0` |
| 15:21:00 | 60s window `p50=44ms p95=74ms max=93ms`, `decoderQueue=0`, `h264 ... dropped=0` |
| 15:23:59 | 60s window `p50=21ms p95=26ms max=31ms`, `decoderQueue=0`, `h264 ... dropped=0` |

Stable-start scoped marker counts (`>= 2026-06-13T15:19:17`):

| Marker | Count |
| --- | ---: |
| `High-resolution D3D path active` | 0 |
| `Media Foundation D3D11` | 0 |
| `High-resolution D3D decoder faulted:` | 0 |
| `decoder input overflow` | 0 |
| `Pre-render video queue dropped` | 0 |
| `Invalid data` | 0 |
| `non-existing PPS` | 0 |
| `sps_id` | 0 |
| `mb_width` | 0 |

Stable caveat:

- Later stable windows show sparse/static-screen cadence and p95/max spikes above 150ms without queue growth, drops, or corruption. Use this as functional no-regression evidence for the stable 1080 route, not as a strict latency acceptance run unless a fresh active-motion stable log is collected.

## Judgment

Evidence-backed conclusion:

- The observed reconnect/display-loss failure mode was reproduced before the final fix (`accepted=0/submitted=0`, first-texture timeout).
- The final reconnect-gate fix changes HRD3D same-config refresh from a full H.264 gate reset to keyframe re-acquisition while preserving SPS/PPS.
- After that fix, 3 Mac AirPlay reconnects all reached renderer refresh, produced a first NV12 D3D11 texture, resumed rendering, and logged no HRD3D fault/stall/present/geometry/NV12/overflow/corruption markers.
- Stable 1080 still uses the stable FFmpeg path at `1920x1080` and does not accidentally route into HRD3D/MF.

Remaining before a strict product-readiness claim:

- Run a fresh final-code 30-minute HRD3D long-run if the gate requires all evidence to come from the exact final code. The earlier 34-minute clean run supports steady-state behavior but predates the final reconnect-gate fix.
- If stable 1080 needs a strict latency pass, collect a short active-motion stable log instead of relying on sparse/static-screen windows.
