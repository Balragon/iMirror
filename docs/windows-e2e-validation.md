# Windows Real-Device E2E Validation Checklist

Use this checklist before merging `claude/magical-faraday-6ex3c7` into `main`.

## Current Status

**Branch:** `claude/magical-faraday-6ex3c7` (12 commits ahead of `main`)

| Feature | Frontend | Backend | Static validation | Build | Real device |
|---|---|---|---|---|---|
| **P1.0 Settings UI** | Done | Done | Done | Done | Pending |
| **P1.1 First-Run Diagnostics** | Done | Done | Done | Done | Pending |
| **FFmpeg Packaging (Essentials)** | N/A | Done | Done | N/A | Pending |

Remaining gate: **one Windows real-device E2E validation run**.

## 0. Preparation

- [ ] Download the published release zip, or build locally with `powershell -ExecutionPolicy Bypass -File .\scripts\publish-win-x64.ps1 -Version 0.2.0`.
- [ ] If building locally, make Gyan.FFmpeg.Essentials available via `tools\ffmpeg\bin\ffmpeg.exe`, `PATH`, or `-FfmpegPath`.
- [ ] Confirm packaging log prints `Bundled FFmpeg: ...Gyan.FFmpeg.Essentials...` or a local `tools\ffmpeg\bin\ffmpeg.exe` verified as `essentials_build`.
- [ ] Confirm no `full_build` warning is printed.
- [ ] Extract the zip outside the repository and run from that folder to block source-tree dependency leaks.
- [ ] Confirm `tools\ffmpeg\bin\ffmpeg.exe` is included in the package.

## 1. First-Run Diagnostics Strip (P1.1)

- [ ] **Normal environment:** all checks pass, the strip is completely hidden, and the sidebar matches the existing UI.
- [ ] **Missing FFmpeg:** delete `tools\ffmpeg\bin\ffmpeg.exe` and launch. Confirm red header `Setup needs attention` plus the FFmpeg row.
- [ ] **Firewall blocked:** block inbound iMirror in Windows Firewall. Confirm `Firewall is blocking` row and `Open Windows Firewall settings` button behavior.
- [ ] **Audio UDP blocked:** with video mirroring connected, block inbound UDP for `iMirror.exe`. Confirm audio is silent, the app warns that Windows Firewall may be blocking audio, and `iMirror.log` records no audio RTP after SETUP.
- [ ] **mDNS port occupied:** launch iMirror while iTunes/Bonjour is holding UDP 5353. Confirm the strip detail correctly names `mDNS UDP 5353` as blocked. This verifies `9164dab`.
- [ ] **VPN/virtual adapter only:** enable VPN and launch. Confirm yellow `Minor setup notes` plus VPN warning, not `Blocked`.
- [ ] **Re-check button:** allow firewall, click `Re-check`, and confirm the strip refreshes/disappears without restart.
- [ ] Confirm the status dot is not affected by the strip; it remains tied to connection state.

## 2. Settings UI (P1.0)

- [ ] Gear button opens and closes the Settings overlay.
- [ ] Change **Receiver name**, restart, and confirm the new name appears in the AirPlay picker.
- [ ] **Audio sync offset slider:** while mirroring, confirm the value applies live without restart or reconnect, and the displayed value updates.
- [ ] Change **Render mode / Video engine**. Confirm restart banner appears and the setting applies after restart.
- [ ] Turn **Audio enabled** off. After restart, confirm AirPlay picker does not advertise audio.
- [ ] Enable advanced diagnostics checkboxes (**Write diagnostics**, **H.264 dump**, **Audio dump**). After restart, confirm dump files are created.
- [ ] Set `IMIRROR_FORCE_SOFTWARE_VIDEO=1` and launch. Confirm the relevant control is disabled and the override note appears.

## 3. Basic Mirroring Regression

- [ ] Start screen mirroring from Mac/iPhone, select iMirror, and confirm video appears.
- [ ] For iPhone long-run checks, keep the device awake and unlocked so iOS does not hide private content behind the lock screen.
- [ ] Confirm audio playback through AAC-ELD decode.
- [ ] Confirm `iMirror.log` records `AirPlay audio RTP #1`, first AAC-ELD decrypt, and WASAPI buffer status.
- [ ] Run three reconnect cycles: connect, disconnect, reconnect.
- [ ] Confirm new session state reset is correct. This verifies `7feefa9`.

## 4. Latency Gate

- [ ] After an active mirroring session, run the latency report against the package log:

```powershell
dotnet run --project .\tools\LatencyAcceptanceReport\LatencyAcceptanceReport.csproj -c Release -- <package>\iMirror.log 150 10
```

- [ ] Confirm p95 latency is `<= 150ms`.
- [ ] Confirm the report passes on a fresh session log. The previous log failed with `contiguousEvidence=False`, so it should not be reused.

## Merge Decision

After every item passes, merge `claude/magical-faraday-6ex3c7` into `main`.

If any item fails, keep the branch open and attach the relevant log and screenshot to the follow-up fix.
