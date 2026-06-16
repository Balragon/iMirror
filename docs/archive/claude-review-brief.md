# Claude Review Brief: iMirror High-Resolution AirPlay Path

Please review the current iMirror worktree before the 30-minute real-device acceptance run.

## Goal

The product default should remain stable 1920x1080 AirPlay. High-resolution Mac AirPlay should stay experimental and only use the new in-process Media Foundation + D3D11 decode + WPF GPU presentation path. Legacy high-resolution FFmpeg/WPF WriteableBitmap and mpv stdin paths must not silently become product defaults.

## Current Real-Device Smoke Result

On June 13, 2026, a Mac connected to iMirror with:

- Advertised display: `2048x1152 @ 30`
- Product path: Media Foundation H.264 decoder with D3D11 output
- Presentation: D3D11 NV12 texture -> D3D11 video processor -> shared BGRA D3D11 texture -> D3D9Ex -> WPF `D3DImage`

Observed evidence from `bin/Debug/net8.0-windows/iMirror.log`:

- `High-resolution D3D path active for stream config: 2048x1152@30, d3d11MultithreadProtected=True.`
- `Media Foundation D3D11 decoder output type set: initial, NV12 texture.`
- `Media Foundation D3D11 decoder output type set: stream change, NV12 texture.`
- `Media Foundation D3D11 first texture: format=NV12 size=2048x1152 arraySize=... subresourceIndex=...`
- `Media Foundation D3D11 decoder produced first NV12 texture.`
- User confirmed the Mac screen is visible.
- Recent latency windows after startup settled:
  - `p95=23ms max=92ms`
  - `p95=10ms max=13ms`
  - `p95=8ms max=10ms`
  - later `p95=13ms max=14ms`

This is only a smoke result. The 30-minute acceptance run is not complete yet.

## Important Fixes Made During Smoke

1. SharpDX assembly loading failed at runtime even though DLLs were copied to output.
   - Added a guarded resolver in `MacMirrorReceiver/App.cs` for `HIGH_RESOLUTION_D3D` / `DIRECTX_PROBE`.
   - It loads `SharpDX*.dll` from `AppContext.BaseDirectory`.

2. Media Foundation returned `0xC00D6D61` after first input.
   - Treated as `MF_E_TRANSFORM_STREAM_CHANGE`, not fatal.
   - Product decoder now calls `ConfigureOutputType("stream change")` and continues.
   - `tools/MediaFoundationH264Probe` now counts `streamChanges` and also renegotiates output type.

## Files To Inspect First

- `MacMirrorReceiver/App.cs`
  - SharpDX assembly resolver.
  - Check whether this is the right long-term fix or only a local workaround.

- `MacMirrorReceiver/MainWindow.cs`
  - `ShouldUseHighResolutionD3DPath`
  - `TryStartHighResolutionD3DPath`
  - `QueueD3DFrameForPresentation`
  - `PresentD3DFrame`
  - Verify high-resolution fallback policy: if MF/D3D11 fails after advertising high-res, do not silently fall back to legacy WPF high-res.

- `MacMirrorReceiver.Video/MediaFoundationD3D11Decoder.cs`
  - `QueueH264`
  - `ProcessPacket`
  - `DrainOutput`
  - `ConfigureOutputType`
  - `TryCreateD3D11Frame`
  - `Dispose`
  - Review COM lifetime, `IMFDXGIBuffer` handling, `Texture2D` ownership, queue overflow behavior, stream-change handling, and dump semantics.

- `MacMirrorReceiver.Video/D3D11VideoProcessorD3DImagePresenter.cs`
  - Review D3D11 multithread protection, `VideoProcessorBlt`, shared BGRA output texture, D3D9Ex shared texture open, WPF `D3DImage` lock/unlock/add-dirty-rect, resize handling, and disposal ordering.

- `MacMirrorReceiver.Networking/AirPlayProbeService.cs`
  - Review stable vs experimental advertisement behavior.
  - Default should advertise stable 1920x1080 without experimental flags.

- `MacMirrorReceiver/RenderModeSettings.cs`
  - Review persisted render mode, env overrides, and experimental flags.

- `tools/LatencyAcceptanceReport/Program.cs`
  - Review high-resolution D3D evidence requirements:
    - path active
    - `d3d11MultithreadProtected=True`
    - first NV12 texture
    - no D3D failure lines

- `tools/HighResolutionProbeReport/Program.cs`
  - Review whether offline probe criteria are strong enough.

- `tools/RealDeviceAcceptanceReport/Program.cs`
  - Review bundled acceptance gates and submitted-dump-to-log matching.

- `docs/high-resolution-pipeline-v2.md`
  - Review whether the implementation and remaining acceptance gates match the stated product policy.

## Questions For Review

1. Is the `AssemblyLoadContext.Default.Resolving` SharpDX fallback in `App.cs` acceptable, or should package output/deps handling be fixed instead?

2. Is `MF_E_TRANSFORM_STREAM_CHANGE` handling complete enough?
   - It currently chooses preferred NV12 output type and calls `SetOutputType` again.
   - Should it also re-query output stream info, flush, restart streaming messages, or handle format changes beyond same-size NV12?

3. Does `MediaFoundationD3D11Decoder.TryCreateD3D11Frame` manage COM and SharpDX texture ownership correctly?
   - Especially `IMFDXGIBuffer.GetResource`, `new D3D11.Texture2D(texturePtr)`, and whether the raw pointer should be released after SharpDX wraps it.

4. Is the queue policy aligned with the latency goal?
   - No arbitrary compressed P-frame dropping.
   - Overflow recovery is GOP-safe: flush queue and wait for next keyframe if incoming packet is not IDR.
   - Latest-frame-wins happens after decode at presentation boundary.

5. Does `D3D11VideoProcessorD3DImagePresenter` safely use a shared D3D11 device from decoder and presenter threads?
   - `ID3D11Multithread` protection is enabled and logged.
   - Check if WPF/D3DImage calls are strictly on UI thread.

6. Is the product high-resolution gating correct?
   - Normal Quality without experimental flags should remain 1080 stable.
   - `IMIRROR_EXPERIMENTAL_QUALITY=1` opens 2048 MF/D3D11 only in `HighResolutionD3D` builds.
   - `IMIRROR_EXPERIMENTAL_MPV=1` keeps 2560 mpv isolated.

7. Are the acceptance reports strict enough before promotion?
   - 2048x1152 real Mac motion for 30 minutes.
   - receive->present p95 < 150ms.
   - max latency not monotonically growing.
   - no H.264 corruption.
   - reconnect 3 times.
   - stable 1080 regression.
   - matching `*.submitted.h264` replay/probe PASS, with close-line byte count matching the on-disk file size.

## Commands To Run

```powershell
dotnet build .\MacMirrorReceiver.csproj -c Debug -p:HighResolutionD3D=true
dotnet build .\MacMirrorReceiver.csproj -c Debug
dotnet build .\tools\MediaFoundationH264Probe\MediaFoundationH264Probe.csproj -c Debug
dotnet build .\tools\HighResolutionProbeReport\HighResolutionProbeReport.csproj -c Debug
dotnet build .\tools\LatencyAcceptanceReport\LatencyAcceptanceReport.csproj -c Debug
dotnet build .\tools\RealDeviceAcceptanceReport\RealDeviceAcceptanceReport.csproj -c Debug
```

After the live smoke session is stopped, run the high-resolution dump through:

```powershell
dotnet run --project .\tools\HighResolutionProbeReport\HighResolutionProbeReport.csproj -c Debug -- .\bin\Debug\net8.0-windows\imirror-20260613-105444.d01.submitted.h264 600 2048x1152@30
```

For final acceptance, use the bundled report with the high-resolution log, the matching submitted dump from that same log, and a separate stable 1080 regression log:

```powershell
dotnet run --project .\tools\RealDeviceAcceptanceReport\RealDeviceAcceptanceReport.csproj -c Release -- <highres-iMirror.log> <matching-capture.submitted.h264> <stable-iMirror.log> 150 30 3 2048x1152@30
```

## Known Non-Completion

Do not mark this work product-ready yet. The path has passed a live smoke test, but not the full 30-minute real-device acceptance run and reconnect regression.
