# iMirror High-Resolution Pipeline v2

## Goal

Ship Mac AirPlay high-resolution mirroring as a product-quality path.

- Minimum target: 2048x1152 stable Mac mirroring.
- Stretch target: 2560x1440 stable Mac mirroring.
- Acceptance: 30 minutes of use with no accumulated latency, no moving-app corruption, and normal disconnect/reconnect.

## Non-Goals

- Do not productize the existing mpv stdin path.
- Do not productize the existing FFmpeg/WPF `WriteableBitmap` path for high resolution.
- Do not preserve every frame at high resolution if presentation cannot keep up.

## Current Baseline

The current product default remains the stable 1920x1080 AirPlay advertisement. High-resolution paths are isolated behind experimental flags:

- `IMIRROR_RENDER_MODE=quality` plus `IMIRROR_EXPERIMENTAL_QUALITY=1`: 2048x1152 Media Foundation/D3D11 experiment, only in `HighResolutionD3D` builds.
- `IMIRROR_RENDER_MODE=quality` plus `IMIRROR_EXPERIMENTAL_MPV=1`: 2560x1440 mpv stdin experiment.

The legacy 2048x1152 FFmpeg/WPF `WriteableBitmap` path is retired for Quality advertising. This keeps normal users on the only path that has shown acceptable real-use behavior while v2 is investigated.

## Required Architecture Direction

### Decode

Investigate an in-process H.264 decoder that can expose GPU-native decoded frames:

1. Media Foundation H.264 decoder.
   - Verify whether the decoder can output D3D11 surfaces in this app shape.
   - Verify low-latency behavior on a live Annex B AirPlay stream.
   - Confirm recovery behavior after reconnect and stream restarts.

2. FFmpeg.AutoGen with D3D11VA.
   - Verify whether decoded `AVFrame` output can remain GPU-resident.
   - Avoid raw BGRA pipe output and CPU readback for the product path.
   - Compare queue/backpressure behavior against Media Foundation.

Opt-in capability probe:

```powershell
$env:IMIRROR_HR_V2_PROBE = "1"
.\bin\Debug\net8.0-windows\iMirror.exe
```

Expected log evidence in `iMirror.log`:

- `HR v2 probe: MFStartup hr=0x00000000.`
- `HR v2 probe: Microsoft H.264 Decoder MFT can be created in-process.`
- `HR v2 probe: Microsoft H.264 Decoder MF_SA_D3D11_AWARE hr=0x00000000, value=1.`
- `HR v2 probe: H.264 Decoder accepted D3D11 manager hr=0x00000000.`
- `HR v2 probe: D3D11 hardware device created, featureLevel=...`

This does not prove end-to-end GPU-surface H.264 decode yet; it proves that the first Media Foundation and D3D11 building blocks are present and that the H.264 MFT accepts an `IMFDXGIDeviceManager` before media type negotiation on this machine.

Offline Media Foundation H.264 probe:

```powershell
dotnet run --project .\tools\MediaFoundationH264Probe\MediaFoundationH264Probe.csproj -c Release -- C:\temp\imirror-capture.d01.submitted.h264
```

This reads a post-gate Annex B dump, reports SPS/PPS/IDR/slice composition, creates the Microsoft H.264 Decoder MFT, sets the H.264 input media type from SPS geometry when available, selects an NV12 output type when possible, splits Annex B data into access units, feeds them as `IMFSample`s, and drains `ProcessOutput`.

Useful evidence:

- `spsGeometry=2048x1152, profile=..., level=...`
- `probeGeometry=2048x1152@30, source=sps`
- `SetInputType(H264 2048x1152@30)=0x00000000`
- `SetOutputType(preferred NV12)=0x00000000 ...`
- `decodeProbe: ... processInputOk=... decodedOutputs=... dxgiBuffers=... d3d11Textures=... failures=0`

The probe reads SPS geometry from the dump and uses it for the Media Foundation input type. If SPS geometry cannot be parsed, it falls back to 2048x1152 and prints `source=fallback`. If the dump geometry is known, pass an explicit override such as `2048x1152@30`; the probe will print `source=override`.

```powershell
dotnet run --project .\tools\MediaFoundationH264Probe\MediaFoundationH264Probe.csproj -c Release -- C:\temp\imirror-capture.d01.submitted.h264 600 2048x1152@30
```

An invalid or too-short synthetic stream can legitimately report `decodedOutputs=0` with `needMoreInput>0`. A real `*.submitted.h264` dump should produce `decodedOutputs>0` before this step is considered proven.

D3D11 decoder capability evidence from the same tool:

- `D3D11Probe.GetAttributes=0x00000000, MF_SA_D3D11_AWARE=0x00000000/1`
- `D3D11Probe.D3D11CreateDevice=0x00000000, featureLevel=...`
- `D3D11Probe.MFCreateDXGIDeviceManager=0x00000000`
- `D3D11Probe.ResetDevice=0x00000000`
- `D3D11Probe.SetD3DManager=0x00000000`
- `D3D11Probe.SetOutputType(preferred NV12)=0x00000000 ...`
- `D3D11Probe.result=d3d11-manager-accepted`

When a valid dump is supplied, the same tool also feeds the stream through the D3D11-manager configured transform:

- `D3D11Probe.decodeProbe: ... decodedOutputs=... dxgiBuffers=... d3d11Textures=... failures=0`
- `Decoded D3D11 texture: format=NV12(103) size=2048x1152 ...`

Passing the capability probe means the Microsoft H.264 decoder accepts an `IMFDXGIDeviceManager` before media type negotiation on this machine. GPU-surface decode is only proven when a valid stream produces `D3D11Probe.decodeProbe decodedOutputs>0` and `d3d11Textures>0`. A decoded texture description with `format=NV12` and the expected frame size is the preferred evidence because it matches the render-side bridge validated below. End-to-end product rendering is still not proven until those real decoded textures reach presentation without CPU readback and without accumulating latency.

Combined high-resolution probe report:

```powershell
dotnet run --project .\tools\HighResolutionProbeReport\HighResolutionProbeReport.csproj -c Release -- C:\temp\imirror-capture.d01.submitted.h264 600
```

Use the same optional geometry override when SPS is absent or wrong:

```powershell
dotnet run --project .\tools\HighResolutionProbeReport\HighResolutionProbeReport.csproj -c Release -- C:\temp\imirror-capture.d01.submitted.h264 600 2048x1152@30
```

This runs the Media Foundation decode probe, the D3D11-manager decode-output inspection, and the D3D11/D3D9 `D3DImage` shared-handle probe. It passes only when:

- The Media Foundation probe process exits successfully.
- CPU-path `decodedOutputs>0`.
- D3D11-manager-path `decodedOutputs>0`.
- D3D11-manager-path `d3d11Textures>0`.
- A decoded texture description with `format=NV12` and a size matching the capture `probeGeometry`.
- The shared-handle `D3DImage` bridge passes at the capture `probeGeometry`.
- The D3D11 video processor render bridge passes at the capture `probeGeometry`.
- The product `MediaFoundationD3D11Decoder` + `D3D11VideoProcessorD3DImagePresenter` replay passes at the capture `probeGeometry`.

The report also runs 2048x1152 and 2560x1440 reference bridge probes when those sizes differ from the capture geometry, but the pass/fail decision follows the capture geometry.

If `decodedOutputs=0` and `needMoreInput>0`, the file is either too short, starts without enough decoder configuration/keyframe data, or is otherwise not a usable decode proof. If decoded output exists but `d3d11Textures=0`, treat Media Foundation GPU-surface decode as unproven and compare against FFmpeg.AutoGen + D3D11VA before product implementation.

### Render

Investigate GPU-direct presentation into WPF:

1. D3DImage interop.
   - Existing `DIRECTX_PROBE` still copies CPU BGRA into a D3D9 texture, so it is not the v2 target.
   - v2 needs decoded GPU surfaces to reach presentation without CPU readback.

2. D3D11/D3D9 shared-handle bridge.
   - Confirm whether D3D11 decode output can be shared or copied GPU-side into the D3D9 surface required by `D3DImage`.
   - Measure whether any GPU copy is bounded and does not accumulate latency.

3. SwapChainPanel-style alternatives.
   - WPF does not directly host UWP `SwapChainPanel`; document any viable HWND/swap-chain host alternative before implementation.

Offline D3D11/D3D9 shared-handle probe:

```powershell
dotnet run --project .\tools\D3DSharedHandleProbe\D3DSharedHandleProbe.csproj -c Release -- 2048 1152
dotnet run --project .\tools\D3DSharedHandleProbe\D3DSharedHandleProbe.csproj -c Release -- 2560 1440
```

Useful evidence:

- `D3D11 sharedHandle=...`
- `D3D9Ex shared texture opened; surface=...`
- `D3DImage backBuffer accepted; pixelSize=2048x1152`
- `result=d3d11-d3d9-d3dimage-shared-handle-opened`

This proves that a D3D11 shared texture can be opened through D3D9Ex and attached to WPF `D3DImage` at both 2048x1152 and 2560x1440 on the probe machine. It does not prove product rendering yet, because the decoded H.264 output surface still has to be connected to this path without CPU readback and without accumulating latency.

Offline D3D11 video processor probe:

```powershell
dotnet run --project .\tools\D3DVideoProcessorProbe\D3DVideoProcessorProbe.csproj -c Release -- 2048 1152
dotnet run --project .\tools\D3DVideoProcessorProbe\D3DVideoProcessorProbe.csproj -c Release -- 2560 1440
```

Useful evidence:

- `formatSupport nv12Input=True, bgraOutput=True`
- `VideoProcessor/input/output views created`
- `VideoProcessorBlt completed`
- `D3DImage accepted video processor output; pixelSize=...`
- `result=nv12-to-bgra-video-processor-d3dimage-completed`

This proves that the render-side bridge can accept an NV12-sized D3D11 input texture, execute GPU-side `VideoProcessorBlt` into a shared BGRA D3D11 render target, open that shared target through D3D9Ex, and attach it to WPF `D3DImage` at both 2048x1152 and 2560x1440 on the probe machine. It is the expected GPU-side color conversion and presentation bridge for decoded H.264 NV12 surfaces.

Product-side render bridge:

- `MacMirrorReceiver.Video/D3D11VideoProcessorD3DImagePresenter.cs`
- `MacMirrorReceiver.Video/D3D11VideoFrame.cs`
- `MacMirrorReceiver.Video/MediaFoundationD3D11Decoder.cs`
- Compiled only with `-p:HighResolutionD3D=true`.
- The presenter owns the D3D11 video device/context and exposes the D3D11 device the in-process decoder should use for its `IMFDXGIDeviceManager`.
- The Media Foundation decoder creates the Microsoft H.264 Decoder MFT, attaches the presenter's D3D11 device through `IMFDXGIDeviceManager`, sets H.264 input and preferred NV12 output media types, queues H.264 payloads with GOP-safe overflow recovery, and emits decoded NV12 `Texture2D` frames.
- The decoder records the `IMFDXGIBuffer` subresource index on each frame and validates the texture format, size, and array slice before handing it to the presenter.
- The presenter accepts decoded NV12 `Texture2D` frames, runs `VideoProcessorBlt` into a shared BGRA texture using the decoder-provided array slice, and presents that shared output through D3D9Ex/`D3DImage` without CPU readback.
- `MainWindow` routes experimental high-resolution Quality streams through this path when compiled with `-p:HighResolutionD3D=true`, `IMIRROR_RENDER_MODE=quality`, and `IMIRROR_EXPERIMENTAL_QUALITY=1`.
- The route keeps compressed H.264 input GOP-safe, then applies latest-frame-wins only after Media Foundation has produced decoded D3D11 frames.
- The shared D3D11 device has `ID3D11Multithread` protection enabled because Media Foundation decode and WPF presentation can touch the device from different threads.
- In a `HighResolutionD3D` build, failure to start, decode, or present this route is a hard high-resolution failure. iMirror does not fall back to the legacy FFmpeg/WPF high-resolution path after advertising the experimental high-resolution stream.
- This still needs real-device proof: use a Mac 2048x1152 session to confirm decoded NV12 textures are presented correctly, then run the 30-minute latency/reconnect acceptance gate.

Compile check:

```powershell
dotnet build .\MacMirrorReceiver.csproj -c Debug -p:HighResolutionD3D=true
```

Experimental run:

```powershell
$env:IMIRROR_RENDER_MODE = "quality"
$env:IMIRROR_EXPERIMENTAL_QUALITY = "1"
$env:IMIRROR_DUMP_H264 = "1"
dotnet run --project .\MacMirrorReceiver.csproj -c Debug -p:HighResolutionD3D=true
```

Expected startup/runtime evidence:

- `High-resolution D3D path active for stream config: 2048x1152@..., d3d11MultithreadProtected=True`
- `H264 dump enabled for Media Foundation D3D11 path ... received=...d01.received.h264, submitted=...d01.submitted.h264`
- `Media Foundation D3D11 decoder started: 2048x1152@..., output=NV12 texture`
- `Media Foundation D3D11 first texture: format=NV12 size=2048x1152 arraySize=... subresourceIndex=...`
- `Media Foundation D3D11 decoder produced first NV12 texture.`
- `output: Media Foundation D3D11 2048x1152, GPU NV12`
- `Presentation latency window: ... p95=... max=...`

For Media Foundation/D3D11, `*.received.h264` is the full post-gate stream delivered to the decoder object. `*.submitted.h264` is the subset accepted by `ProcessInput`; it excludes packets flushed or dropped by decoder backpressure and is the artifact used by offline replay.

After the run, feed the generated `*.submitted.h264` into the combined report:

```powershell
dotnet run --project .\tools\HighResolutionProbeReport\HighResolutionProbeReport.csproj -c Release -- .\bin\Debug\net8.0-windows\imirror-YYYYMMDD-HHMMSS.d01.submitted.h264 600
```

The combined report already runs product replay. For focused debugging, replay the same dump through only the product MF/D3D11 decoder and product D3DImage presenter:

```powershell
dotnet run --project .\tools\HighResolutionD3DReplayProbe\HighResolutionD3DReplayProbe.csproj -c Release -- .\bin\Debug\net8.0-windows\imirror-YYYYMMDD-HHMMSS.d01.submitted.h264 600 2048x1152@30
```

Use the two tools differently:

- `HighResolutionProbeReport` proves offline MF decode, D3D11 texture output, render bridge capability, and product-class replay with detailed pass/fail criteria.
- `HighResolutionD3DReplayProbe` is the focused product-class replay tool for isolating `MediaFoundationD3D11Decoder` and `D3D11VideoProcessorD3DImagePresenter` behavior.

Failure evidence to capture:

- `Media Foundation D3D11 decoder produced no NV12 texture within 12s...`
- `Media Foundation produced output sample without matching NV12 D3D11 texture...`
- `High-resolution D3D decoder faulted: ...`
- `High-resolution D3D stall: ...`
- `High-resolution D3D present failed: ...`
- `High-resolution D3D output geometry changed: ...`
- Any `ProcessInput` or `ProcessOutput` HRESULT in decoder status.

## Latency Policy

- Never randomly drop compressed H.264 P-frames.
- Compressed input must remain GOP-consistent; overflow recovery can require a fresh keyframe.
- At high resolution, drop only after decode, at the presentation boundary.
- Presentation should be latest-frame-wins: at most one pending decoded frame beyond the currently presenting frame.
- The receiver must report receive-to-present p50/p95/max over fixed windows so latency growth is visible.

Current instrumentation:

- WPF/FFmpeg presentation logs a `Presentation latency window` line every completed 10-second receive-to-present window.
- The diagnostics panel shows the current or last 10-second receive-to-present window as `latency window`.
- A 30-minute acceptance run should archive these window lines and check that p95 remains below target and max does not trend upward over time.

Automated report:

```powershell
dotnet run --project .\tools\LatencyAcceptanceReport\LatencyAcceptanceReport.csproj -c Release -- .\bin\Release\net8.0-windows\iMirror.log 150 30
```

The report fails when there are no presentation latency windows, the run has less than 30 minutes of evidence, any completed window has `p95 >= 150ms`, max latency forms a 6-window non-decreasing streak, or H.264 corruption signatures appear in the log.

For reconnect validation, pass the required reconnect attempt count:

```powershell
dotnet run --project .\tools\LatencyAcceptanceReport\LatencyAcceptanceReport.csproj -c Release -- .\bin\Release\net8.0-windows\iMirror.log 150 30 3
```

For the experimental MF/D3D11 high-resolution path, also require log evidence that the path started with D3D11 multithread protection and produced a first NV12 D3D11 texture:

```powershell
dotnet run --project .\tools\LatencyAcceptanceReport\LatencyAcceptanceReport.csproj -c Release -- .\bin\Debug\net8.0-windows\iMirror.log 150 30 3 false true
```

For a stable 1080 regression log, require stable AirPlay advertisement evidence:

```powershell
dotnet run --project .\tools\LatencyAcceptanceReport\LatencyAcceptanceReport.csproj -c Release -- .\bin\Release\net8.0-windows\iMirror.log 150 30 0 true
```

After collecting the high-resolution 30-minute log, the matching `*.submitted.h264` dump, and a stable 1080 regression log, run the bundled real-device report:

```powershell
dotnet run --project .\tools\RealDeviceAcceptanceReport\RealDeviceAcceptanceReport.csproj -c Release -- .\bin\Debug\net8.0-windows\iMirror.log .\bin\Debug\net8.0-windows\imirror-YYYYMMDD-HHMMSS.d01.submitted.h264 .\bin\Release\net8.0-windows\iMirror.log 150 30 3 2048x1152@30
```

The bundled report runs:

- High-resolution `LatencyAcceptanceReport` with reconnect count and MF/D3D11 log evidence required.
- A preflight check that the supplied `*.submitted.h264` path is the same file referenced by the high-resolution log's `submitted=...` line and that the close-line `bytes=` value matches the actual file size.
- `HighResolutionProbeReport` against the captured submitted H.264 dump.
- Stable 1080 `LatencyAcceptanceReport` with stable advertisement evidence required.

It still cannot replace the manual visual check for moving app-window tearing/corruption.

## Acceptance Gates

Run these before calling v2 product-ready:

1. 2048x1152 Mac motion for 30 minutes.
   - `receive->present p95 < 150ms`.
   - Max latency does not monotonically increase over time.
   - No H.264 corruption warnings that stop decoding.
   - Moving app windows do not visibly tear or corrupt.

2. Disconnect/reconnect.
   - Three consecutive disconnect/reconnect cycles.
   - No stale decoder, stale GPU surface, or hung RTSP/data connection.

3. Stable regression.
   - Default launch still advertises 1920x1080.
   - 1080p Mac mirroring remains stable.
   - No experimental flag is required for normal 1080p use.

4. 2560x1440 stretch.
   - Same gates as 2048x1152, but this remains secondary until 2048x1152 passes.

## Next Implementation Slice

1. Run the real Mac 2048x1152 MF/D3D11 path for 30 minutes with `IMIRROR_DUMP_H264=1`.
2. Run `tools/RealDeviceAcceptanceReport` with the high-resolution log, matching `*.submitted.h264` dump, and stable 1080 regression log.
3. If the bundled report fails at `HighResolutionProbeReport` with `d3d11Textures=0`, run an FFmpeg.AutoGen + D3D11VA comparison probe before relying on Media Foundation.
4. If latency fails but probe replay passes, inspect presentation-boundary dropping and D3DImage presentation timing; do not reintroduce arbitrary compressed P-frame dropping.
5. Promote the path only after the bundled report passes and the manual visual check shows no moving-window tearing/corruption.
