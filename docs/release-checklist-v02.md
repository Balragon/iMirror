# iMirror v0.2 Public Release Checklist

Manual release validation checklist to be completed before publishing the GitHub Release.

## Build & CI

- [ ] CI build passes on Windows runner (green on the release tag)
- [ ] dotnet test passes with 0 failures
- [ ] Zip artifact produced in `artifacts\` directory
- [ ] SHA-256 checksum recorded

## Smoke test — AirPlay mirroring

- [ ] App launches on a clean Windows 10 22H2 or Windows 11 machine (no prior install)
- [ ] mDNS advertisement visible: iMirror appears in iOS/macOS Screen Mirroring picker
- [ ] Mac screen mirroring connects and displays video
- [ ] iPhone screen mirroring connects and displays video
- [ ] Audio plays through Windows speakers when audio is enabled
- [ ] Disconnect from sender — app returns to empty state cleanly
- [ ] Reconnect after disconnect — no hang or crash
- [ ] App exits cleanly from tray icon

## Settings & lifecycle

- [ ] Settings window opens and closes without freezing video
- [ ] Receiver name change prompts restart
- [ ] Render mode switch (Stable ↔ High quality) persists after restart
- [ ] Audio sync offset saves and restores correctly
- [ ] Minimize to tray works; double-click tray restores window

## Diagnostic & firewall

- [ ] Empty state shows correct status dot (green = ready, warning = firewall/FFmpeg issue)
- [ ] "Re-check" link re-runs diagnostics after user fixes firewall
- [ ] FFmpeg missing → orange dot + "FFmpeg not found" message shown
- [ ] Windows Firewall prompt appears on first launch (allow both network types)

## GPU & video engine

- [ ] GPU path (Media Foundation/D3D11) activates when hardware decode is available
- [ ] Force software override (`IMIRROR_FORCE_SOFTWARE_VIDEO=1`) works
- [ ] No GPU decode → software fallback activates silently

## Trust & distribution

- [ ] SmartScreen shows "More info" → "Run anyway" on unsigned build (expected)
- [ ] SHA-256 of published zip matches recorded checksum
- [ ] Release notes posted with correct version, requirements, and installation steps

## Go / No-Go

- [ ] All P0 items above checked
- [ ] No known crash or data-loss regression
- [ ] Release notes reviewed and published
- Approver: ________________  Date: ________________
