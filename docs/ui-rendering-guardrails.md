# UI Rendering Guardrails

These rules are canonical for iMirror UI work. Cosmetic changes must never destabilize
the video pipeline.

## 1. Two Render Paths

### Stable Render Path

- Pipeline: `WriteableBitmap` -> WPF `Image` control.
- Surface: `VideoImage`.
- Compatibility: works with WPF-UI, Mica, Acrylic, blur, and normal WPF composition.
- Purpose: default, stable presentation path for live video.
- Relevant symbols: `VideoImage`, `_bitmap`, `PresentFrameToWriteableBitmap`,
  `CopyFrameToBitmap`.

### High-Resolution Render Path

- Pipeline: `D3D11SwapChainVideoPresenter : HwndHost`.
- File: `MacMirrorReceiver.Video/D3D11SwapChainVideoPresenter.cs`.
- Enabled by the `HIGH_RESOLUTION_D3D` compile constant, which is always defined.
- Activates for Quality mode with streams above 1080p.
- `HwndHost` is an airspace surface: WPF cannot reliably composite over it.
- Relevant symbols: `_highResolutionD3DPresenter`, `ShouldUseHighResolutionD3DPath`,
  `TryStartHighResolutionD3DPath`, `UpdateHighResolutionD3DPresenterLayout`,
  `QueueD3DFrameForPresentation`, `PresentD3DFrame`.

## 2. Where Mica, Acrylic, Blur, and Transparency Are Allowed

These effects are allowed only in ordinary WPF UI outside the live video surface:
`TitleBar`, `EmptyState`, `SettingsWindow`, and ordinary cards, panels, dialogs,
and chrome that do not overlap live video.

## 3. Where They Are Forbidden

Mica, Acrylic, blur, transparency, overlays, and decorative composition effects are
forbidden on the live video surface, `VideoStage` while mirroring, `VideoImage`,
and anything positioned over the high-resolution `HwndHost` presenter.

Do not rely on WPF z-order, transparency, or clipping to decorate content above
`HwndHost`; it is an airspace boundary.

## 4. VideoStage Background Rule

- Disconnected / empty state: `VideoStage` may be transparent so Mica shows behind
  the empty-state art.
- Mirroring: `VideoStage` must be opaque black, `#000000`.

Do not use semi-transparent, blurred, gradient, or themed backgrounds behind active
video.

## 5. Never Modify for Cosmetic Reasons

Cosmetic UI work must never touch FFmpeg decode, TCP receive, frame timing,
decoder fallback, D3D presenter behavior, or
`MacMirrorReceiver.Video/D3D11SwapChainVideoPresenter.cs`.

Cosmetic UI work must never modify these `MainWindow.cs` symbols:

`_decoder`, `_bitmap`, `_frameGate`, `_pendingFrame`, `_renderQueued`,
`_decodedFrames`, `_renderedFrames`, `_renderDroppedFrames`,
`_latestReceiveToRenderMs`, `_latestDecodeToRenderMs`,
`_receiveToPresentLatencyWindow`, `_decoderOutputFps`, `_decoderMaxRenderWidth`,
`_videoWatchdogCts`, `_highResolutionD3DPresenter`,
`_mediaFoundationD3DDecoder`, `_pendingD3DFrame`, `_gpuPathDisabledThisSession`,
`StartDecoder`, `StartFreshDecoder`, `RestartDecoderIfRenderWidthChanged`,
`ShouldUseHighResolutionD3DPath`, `TryStartHighResolutionD3DPath`,
`UpdateHighResolutionD3DPresenterLayout`, `HandleHighResolutionD3DFatal`,
`StartVideoWatchdog`, `StopVideoWatchdog`, `QueueFrameForPresentation`,
`QueueD3DFrameForPresentation`, `DrainLatestFrame`, `PresentFrame`,
`PresentD3DFrame`, `PresentFrameToActiveRenderer`,
`PresentFrameToWriteableBitmap`, `CopyFrameToBitmap`, `ReleasePendingFrame`,
`ReleasePendingD3DFrame`, `LogRenderLatencyThrottled`,
`LogVideoHealthThrottled`.

## 6. UI PR Review Checklist

- Does the PR leave decode, receive, timing, fallback, and D3D presenter code alone?
- Does it avoid all off-limits `MainWindow.cs` symbols listed above?
- Is Mica / Acrylic / blur / transparency limited to allowed WPF UI areas?
- Is `VideoStage` transparent only when disconnected and opaque black while mirroring?
- Are there no overlays, effects, or WPF-composited decorations above `HwndHost`?
- Has live mirroring been smoke-tested in both normal and Quality mode paths?
