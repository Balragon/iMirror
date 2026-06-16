# Spec: High-Resolution AirPlay Review Fixes (Codex/GPT-5.5 xhigh handoff)

Status: design locked, ready to implement. Do **not** run the 30-minute acceptance yet.
Scope: only the five review findings below. Do not refactor AirPlay pairing/networking.
Source review: `../claude review.txt`. Brief: `docs/claude-review-brief.md`.

## Build/layout facts the implementer must know

- Single app assembly: `iMirror/MacMirrorReceiver.csproj` globs `MacMirrorReceiver.Networking/**` and
  `MacMirrorReceiver.Video/**`. There is **no** separate Networking/Video csproj.
- `HIGH_RESOLUTION_D3D` is defined for the whole app compilation when `-p:HighResolutionD3D=true`
  (`MacMirrorReceiver.csproj` lines 18-20). So `#if HIGH_RESOLUTION_D3D` is usable in **any** app file,
  including `AirPlayProbeService.cs`.
- The MF/D3D11 decoder, D3D11 presenter, and `D3D11VideoFrame` are entirely inside `#if HIGH_RESOLUTION_D3D`.
- The Microsoft H.264 decoder MFT in D3D11 mode sets `MFT_OUTPUT_STREAM_PROVIDES_SAMPLES` and returns its own
  samples; output textures are commonly a **texture array** (`ArraySize > 1`) where each sample identifies its
  slice via `IMFDXGIBuffer.GetSubresourceIndex`. The current code always uses slice 0 — that is HIGH-1.

## Log-marker contract (MUST stay exact — acceptance tools key on these substrings)

Keep existing (already consumed by `tools/LatencyAcceptanceReport/Program.cs`):
- `High-resolution D3D path active`
- `d3d11MultithreadProtected=True`
- `Media Foundation D3D11 decoder produced first NV12 texture`
- existing failure substrings in `IsHighResolutionD3DFailureLine` (do not remove).

Add these NEW failure markers (emitted by code AND added to `IsHighResolutionD3DFailureLine`):
- `High-resolution D3D decoder faulted:`
- `High-resolution D3D stall:`
- `High-resolution D3D present failed:`
- `High-resolution D3D output geometry changed:`

Add these NEW informational markers:
- `Media Foundation D3D11 first texture:` (one-time texture detail; HIGH-1)
- `received=<path>` and `submitted=<path> bytes=<n>` dump lines (MEDIUM-4)

---

## HIGH-1 — D3D11 subresource index handling

Files: `MacMirrorReceiver.Video/MediaFoundationD3D11Decoder.cs`,
`MacMirrorReceiver.Video/D3D11VideoFrame.cs`,
`MacMirrorReceiver.Video/D3D11VideoProcessorD3DImagePresenter.cs`,
`MacMirrorReceiver/MainWindow.cs`.

### Decoder — `TryCreateD3D11Frame` (currently ~lines 454-528)
1. After `dxgiBuffer.GetResource(ref textureIid, out texturePtr)` succeeds, call
   `int subHr = dxgiBuffer.GetSubresourceIndex(out int subresourceIndex);`
   - `GetSubresourceIndex` is already declared on `IMFDXGIBuffer` (line ~1032). It returns an int only — no AddRef,
     no extra release needed.
   - If `subHr < 0`: log `"Media Foundation D3D11 subresource index unavailable: 0x...."` and `return false`
     (do not guess 0).
2. After reading `desc` (already done), extend validation. Reject (dispose texture, `return false`) when **any** of:
   - `desc.Format != NV12`
   - `desc.Width != _width || desc.Height != _height`
   - `subresourceIndex < 0 || subresourceIndex >= desc.ArraySize`  ← NEW defensive check; log a clear message
     `"Media Foundation D3D11 subresource out of range: index={subresourceIndex}, arraySize={desc.ArraySize}."`
3. Set `SubresourceIndex = subresourceIndex` on the constructed `D3D11VideoFrame`.
4. One-time detail log (guard with a private `bool _loggedFirstTextureDetails` set on the decode thread):
   `StatusChanged?.Invoke($"Media Foundation D3D11 first texture: format={desc.Format} size={desc.Width}x{desc.Height} arraySize={desc.ArraySize} subresourceIndex={subresourceIndex}.");`
   - Keep the existing `"...produced first NV12 texture."` line in `DrainOutput` unchanged (LatencyAcceptanceReport
     depends on it).

### `D3D11VideoFrame`
- Add `public required int SubresourceIndex { get; init; }`.

### Presenter — `PresentNv12Texture`
- Change signature to `PresentNv12Texture(D3D11.Texture2D inputTexture, int subresourceIndex, int width, int height, int fps)`.
- In `VideoProcessorInputViewDescription.Texture2D` use `ArraySlice = subresourceIndex` (replace the hard-coded `ArraySlice = 0` at line ~97).
- Belt-and-suspenders guard (non-fatal here — the decoder already hard-validated): if
  `subresourceIndex < 0 || subresourceIndex >= inputTexture.Description.ArraySize`, log once and `return`
  (skip the frame) rather than blitting slice 0.

### MainWindow — `PresentD3DFrame` (~line 2144)
- Pass `frame.SubresourceIndex`: `presenter.PresentNv12Texture(frame.Texture, frame.SubresourceIndex, frame.Width, frame.Height, frame.Fps);`

Acceptance: HRD3D smoke shows the new `first texture:` line with `arraySize=` and `subresourceIndex=`; image
still correct (if this GPU returns `arraySize=1`, behavior is unchanged — the fix is for arrayed-output GPUs).

---

## HIGH-2 — decoder-loop fatal detection + liveness watchdog

Files: `MacMirrorReceiver.Video/MediaFoundationD3D11Decoder.cs`, `MacMirrorReceiver/MainWindow.cs`,
`tools/LatencyAcceptanceReport/Program.cs`.

### Decoder
- Add `public bool IsFaulted { get; private set; }` and `public event Action<string>? Faulted;`.
- Add a guarded `private void Fault(string message)`: if `IsFaulted` return; set `IsFaulted = true`;
  `StatusChanged?.Invoke(message);` `Faulted?.Invoke(message);`
- `DecodeLoopAsync` catch: keep `OperationCanceledException` as clean exit. For any other exception call
  `Fault("Media Foundation D3D11 decoder stopped: " + ex.Message);` (the `decoder stopped:` substring is already a
  failure marker — keep it; `Fault` just also raises the event).
- New liveness loop `WatchDecodeLivenessAsync`, started from `Start()` next to `WatchFirstFrameTimeoutAsync`:
  - Period 1s; honor `_cts`.
  - Track `lastAccepted`, `lastDecoded`, `stallSeconds`.
  - Each tick read `accepted = AcceptedInputPackets`, `decoded = Interlocked.Read(ref _decodedOutputFrames)`.
  - **Only arm after first frame**: if `decoded == 0`, reset counters and continue (startup is owned by
    `WatchFirstFrameTimeoutAsync`).
  - If `accepted > lastAccepted && decoded == lastDecoded` → `stallSeconds++`, else `stallSeconds = 0`.
  - Update snapshots. If `stallSeconds >= 5` → `Fault($"High-resolution D3D stall: input advancing but no decoded NV12 frames for {stallSeconds}s; accepted={accepted}, decoded={decoded}.")` and break.
  - Rationale for the `accepted` guard: a static Mac screen legitimately produces no input; we must not fault on idle.

### MainWindow
- Factor `private void HandleHighResolutionD3DFatal(string message)` (UI thread):
  `AppLog.Write(message);` set `_decoderStatus`; `SetStatus("High-quality mirroring failed. Restart iMirror in stable mode.");`
  then tear down: `_mediaFoundationD3DDecoder?.Dispose(); _mediaFoundationD3DDecoder = null; DisposeHighResolutionD3DPresenter(); ReleasePendingD3DFrame();` **Do not** start the FFmpeg/WPF fallback (policy: no silent fallback after advertising high-res).
- In `TryStartHighResolutionD3DPath`, subscribe:
  `decoder.Faulted += m => Dispatcher.BeginInvoke(new Action(() => HandleHighResolutionD3DFatal("High-resolution D3D decoder faulted: " + m)));`
- In `PresentD3DFrame`, wrap the present call:
  ```
  try { presenter.PresentNv12Texture(frame.Texture, frame.SubresourceIndex, frame.Width, frame.Height, frame.Fps); }
  catch (Exception ex) { HandleHighResolutionD3DFatal("High-resolution D3D present failed: " + ex.Message); return; }
  ```
  (keep the outer `finally { frame.Dispose(); }`).

### LatencyAcceptanceReport — `IsHighResolutionD3DFailureLine`
- Add OR-clauses for: `High-resolution D3D decoder faulted`, `High-resolution D3D stall:`,
  `High-resolution D3D present failed`, `High-resolution D3D output geometry changed`.

Acceptance: a forced decode exception or a stall produces one of the markers, and
`LatencyAcceptanceReport ... true` (require-high-res) returns non-zero.

---

## MEDIUM-4 — true "submitted to MFT" dump semantics

Files: `MacMirrorReceiver.Video/MediaFoundationD3D11Decoder.cs`, `tools/RealDeviceAcceptanceReport/Program.cs`,
`docs/high-resolution-pipeline-v2.md`, `docs/claude-review-brief.md` (dump description).

Problem: the current `*.submitted.h264` is written in `QueueH264` **before** the overflow/backpressure check and
before `ProcessInput`, so it is "received", not "submitted to the MFT".

### Decoder
- Two `DumpFile`s when `IMIRROR_DUMP_H264` is set:
  - `_receivedDump` — suffix `received`, written in `QueueH264` exactly where the current write is (every received
    packet). Emit `received=<path>` in the enable status line.
  - `_submittedDump` — suffix `submitted`, written **only** in `ProcessPacket` immediately after
    `ThrowIfFailed(hr, "ProcessInput")` (the bytes the MFT actually accepted). Emit `submitted=<path>` in the enable
    status line so `RealDeviceAcceptanceReport.ExtractSubmittedDumpPath` resolves the **true** submitted stream.
  - Both are opt-in (null unless the env var is set); `ProcessPacket` runs on the decode thread, `DumpFile` already
    has its own lock — safe.
- Add `public long BytesWritten` to `DumpFile`. On `Dispose`, emit:
  `StatusChanged?.Invoke($"H264 submitted dump closed: submitted={path} bytes={n}.");`
  (Emit the analogous `received=... bytes=...` close line too.)

### RealDeviceAcceptanceReport
- Keep the path-match in `CheckSubmittedDumpMatchesHighResolutionLog`.
- Strengthen (the "if easy" size check): parse the logged `submitted=<path> bytes=<n>` **close** line; compare `<n>`
  to the actual on-disk length of the passed dump (`new FileInfo(submittedDump).Length`). On mismatch → fail with a
  clear message (`submittedDumpMatchesLog=fail: byte size mismatch logged=<n> file=<m>`). Hash is optional/future.
- The submitted dump now equals the MFT-fed stream (drops excluded), so `HighResolutionProbeReport` replays exactly
  what was decoded. Update the report `Note` text and `docs/high-resolution-pipeline-v2.md` to say
  "submitted = MFT-accepted stream; received = full network capture."

---

## MEDIUM-3 — stream-change geometry validation (hard-fail, no live resize)

File: `MacMirrorReceiver.Video/MediaFoundationD3D11Decoder.cs`.

Decision: **hard-fail** on a real output-geometry/format change. A live resize would require rebuilding the
presenter pipeline AND the D3DImage back buffer, and the AirPlay display geometry is already advertised and fixed
for the session — a clean resize is not simple and is out of scope. Normal startup renegotiates to the **same**
advertised size and must NOT fault.

### `ConfigureOutputType(reason)` (currently ~lines 423-440)
- After `SetOutputType(...)` succeeds, query and validate the actual current output type:
  - `_transform.GetOutputCurrentType(0, out IntPtr current)`; wrap via `Marshal.GetObjectForIUnknown(current)` as
    `MfMediaType`; read `Subtype` (`GetGUID`) and `FrameSize` (`GetUINT64`; width = high 32 bits, height = low 32
    bits via the existing `PackRatio` convention). Release the type (`Marshal.Release(current)` + `ReleaseComObject`
    on the wrapper).
  - If `subtype != NV12 || width != _width || height != _height`:
    `Fault($"High-resolution D3D output geometry changed: expected {_width}x{_height} NV12, got {gw}x{gh} {subtypeName}.");`
    then `throw new InvalidOperationException(<same message>);` to unwind `DrainOutput`/`DecodeLoopAsync`.
    (`Fault` is guarded, so the later catch's `Fault("...decoder stopped...")` is a no-op and the precise geometry
    marker is what lands in the log.)

Acceptance: startup `stream change` renegotiation to the advertised size does not fault; a synthetic size change
produces the `output geometry changed` marker and fails the gate.

---

## LOW — cleanup + policy

### (a) `MacMirrorReceiver/App.cs` resolver condition (lines ~30-35)
Replace the convoluted condition with:
```
if (assemblyName.Name?.StartsWith("SharpDX", StringComparison.OrdinalIgnoreCase) != true)
{
    return null;
}
```
(Keep the `AssemblyLoadContext.Default.Resolving` fallback itself — it is an acceptable, narrowly-scoped fix for
SharpDX loading from `AppContext.BaseDirectory` under the HRD3D/DIRECTX_PROBE builds.)

### (b) Policy: block legacy 2048 WPF advertise in non-HRD3D builds
Decision: **high-resolution Quality ships only via the GPU (MF/D3D11) path.** The legacy WriteableBitmap 2048 path
accumulated latency and failed acceptance, so a non-HRD3D build must not advertise 2048 and then serve it via the
rejected renderer. The mpv 2560 experiment is independent and unaffected.

- `MacMirrorReceiver.Networking/AirPlayProbeService.cs` — gate `QualityWpfAvailable` (lines 54-55) with the define:
  ```
  private static readonly bool QualityWpfAvailable =
  #if HIGH_RESOLUTION_D3D
      QualityRenderMode && RenderModeSettings.ExperimentalWpfQualityEnabled;
  #else
      false;
  #endif
  ```
  Effect in a non-HRD3D build: `IMIRROR_EXPERIMENTAL_QUALITY=1` no longer advertises 2048; the existing
  "quality requested but experimental quality is disabled or unavailable; advertising stable 1920x1080" log path
  (line ~1620) is taken. `IMIRROR_EXPERIMENTAL_MPV` (2560) still works in all builds.
- `MacMirrorReceiver/MainWindow.cs` — make the app side consistent so `CurrentRenderMode` cannot select a legacy
  2048 WPF render in non-HRD3D builds. Gate the WPF-quality intent with the define, e.g. compute
  `ExperimentalWpfQualityRequested` as `false` when `!HIGH_RESOLUTION_D3D`:
  ```
  private static readonly bool ExperimentalWpfQualityRequested =
  #if HIGH_RESOLUTION_D3D
      RenderModeSettings.ExperimentalWpfQualityEnabled;
  #else
      false;
  #endif
  ```
  In HRD3D builds this still drives `ShouldUseHighResolutionD3DPath` (the MF/D3D11 path). The now-unreachable
  non-HRD3D "Experimental 2048x1152 WPF path" UI strings (MainWindow ~966-991) may be simplified but that is
  optional.
- Document in `docs/high-resolution-pipeline-v2.md`: "`IMIRROR_EXPERIMENTAL_QUALITY` has effect only in
  HighResolutionD3D builds, where it selects the MF/D3D11 GPU path. The legacy WriteableBitmap 2048 path is retired."

---

## Validation (run all; both build configs must be 0 errors)

```powershell
dotnet build .\MacMirrorReceiver.csproj -c Debug -p:HighResolutionD3D=true
dotnet build .\MacMirrorReceiver.csproj -c Debug
dotnet build .\tools\MediaFoundationH264Probe\MediaFoundationH264Probe.csproj -c Debug
dotnet build .\tools\HighResolutionProbeReport\HighResolutionProbeReport.csproj -c Debug
dotnet build .\tools\LatencyAcceptanceReport\LatencyAcceptanceReport.csproj -c Debug
dotnet build .\tools\RealDeviceAcceptanceReport\RealDeviceAcceptanceReport.csproj -c Debug
```
The plain `-c Debug` (non-HRD3D) build must still compile: verify all new `#if HIGH_RESOLUTION_D3D` blocks are
balanced and nothing outside the guards references SharpDX/`D3D11VideoFrame`.

## Optional hardening (only if cheap; otherwise skip)
- `tools/MediaFoundationH264Probe` could log `arraySize=`/`subresourceIndex=` per decoded texture so the offline
  probe surfaces arrayed output before a live run. Not required for the gate.

## Live-log verification context (2026-06-13, ~34 min HRD3D smoke — read before implementing)

A continuous ~34 min live session was captured (`bin/Debug/net8.0-windows/iMirror.log`):
MF/D3D11 path, 2048x1152@30, `d3d11MultithreadProtected=True`, single session, no reconnects.
Health: **h264 dropped=0 for the whole session**, `decoderQueue=0` throughout, p50 4-7ms flat (no drift),
no corruption/fatal markers, ~20 benign `ProcessInput stall` events (35-58ms, all `dropped=0`, `queued<=3`).

What this means for the implementer (so you don't chase ghosts):

- **HIGH-1 did NOT manifest on this GPU.** There are zero `without matching NV12` lines and the image is correct,
  which means this GPU returns `ArraySize=1` (slice 0 == the correct slice). So the HIGH-1 fix is **behaviorally a
  no-op on this machine** — do not expect any visible image change. **Verify HIGH-1 via the new
  `Media Foundation D3D11 first texture: ... arraySize=N subresourceIndex=K` line** (expect `arraySize=1
  subresourceIndex=0` here), NOT via the picture. The fix stays required for GPUs that return arrayed output.
  Suggestion: implement HIGH-1's one-time detail log FIRST (cheap), so the next smoke confirms `arraySize` on this
  hardware.
- **HIGH-2 and MEDIUM-3 were never exercised** by this clean run (no decode death, no escalating stall, no geometry
  change). Do not treat the clean smoke as evidence they are unnecessary — they are the failure detection the
  30-minute acceptance needs. Implement them.
- The ~20 `ProcessInput stall` events are benign (`dropped=0`, queue drains) and correlate with the p95/max latency
  spikes. They are **out of review scope** — do not add latency-smoothing/queue rework.
- Recommended order: HIGH-1 (logging first, then plumbing) → HIGH-2 → MEDIUM-4 → MEDIUM-3 → LOW-5.

## What remains AFTER these fixes (do NOT do now)
1. Short HRD3D smoke (2048x1152): confirm `first texture:` detail line, correct image, no new failure markers,
   low latency windows.
2. Offline probe of the **new** true-submitted dump via `HighResolutionProbeReport`.
3. The full 30-minute real-device acceptance + 3 reconnects + stable-1080 regression via
   `RealDeviceAcceptanceReport`.
