# UI Fluent Refresh Verification

## Automated Result
- Build success: true
- Tests success: true
- Build warnings: 0
- Summary: Release build succeeded with 0 warnings, and all 28 tests passed.

## Automated (already run)
- [ ] dotnet build -c Release succeeds (result above)
- [ ] dotnet test passes (result above)

## Rendering integrity (the whole point of the guardrails)
- [ ] STABLE path: connect an iPhone (1080p) — video renders via WriteableBitmap/Image, no flicker
- [ ] HIGH-RES path: Quality mode + a >1080p source — D3D11/HwndHost video still renders, no black box, no airspace artifact over the video
- [ ] During mirroring the area behind the video is opaque black (no Mica bleed onto the video)
- [ ] While disconnected, the empty state shows Mica behind the art (transparent VideoStage)
- [ ] Disconnect returns to empty state cleanly; reconnect works; no hang/crash

## Fluent shell
- [ ] TitleBar: drag to move, minimize, maximize/restore, close all work
- [ ] Window resize + Windows snap (Win+Arrow / drag to edge) behave normally
- [ ] Mica backdrop renders on the title bar and empty state

## SettingsWindow
- [ ] Opens and closes without freezing video
- [ ] Receiver name edit persists / prompts restart as before
- [ ] Render-mode radios still switch and persist
- [ ] Audio toggle + audio-sync slider save and restore correctly
- [ ] Diagnostics toggles + warning banners + "Check for updates" link still work

## Accessibility
- [ ] Keyboard focus visuals present; Tab order logical
- [ ] High-contrast theme still legible
