# iMirror v0.2 Phase 1 Decisions

All Phase 1 human decisions are resolved. This document is the canonical record.

---

## Code Signing — `signing-path`

**Decision: Ship v0.2 unsigned.**

SmartScreen will show "Windows protected your PC". Users click "More info → Run anyway" (2-click workaround). Acceptable for a technical public preview. Release notes must document this.

Path forward: Microsoft Trusted Signing (~$9.99/month, no hardware token, CI-compatible) for v0.3 once audience warrants it.

---

## Distribution Format — `distribution-policy` / `release-posture`

**Decision: Self-contained win-x64 zip only.**

No installer, no MSIX, no auto-update for v0.2. Installer and auto-update decisions deferred to v0.3 after public feedback. The existing `publish-win-x64.ps1` packaging pipeline covers this.

---

## Settings Over Live Video — `settings-modal-strategy` / `settings-airspace-design`

**Decision: Open Settings as an independent top-level WPF `Window`.**

D3D11 HWND sits outside WPF composition so ZIndex cannot overlay it. A separate Window sits above all HWND children naturally. Modal vs. modeless: owner-set (`Owner = this`) so it stays in front and is dismissed on main window close.

Implementation tasks in Phase 3: `settings-above-video`, `settings-airspace-implementation`.

---

## GPU/D3D11 Path Default — (Open Decision: GPU enabled by default)

**Decision: GPU/D3D11 path enabled by default.**

Matches current code behavior. Hardware decode available → D3D11 is used. Software fallback activates automatically when hardware decode is unavailable. Already validated on real devices. `IMIRROR_FORCE_SOFTWARE_VIDEO=1` override remains available.

---

## Tray Icon and Window Close — `lifecycle-ux-spec` / `tray-background-receiving`

**Decision: X button minimizes to tray; only explicit "Exit" terminates the process.**

- Closing the window hides it and shows a system tray icon.
- First close shows a one-time tooltip: "iMirror is still running. Right-click the tray icon to exit."
- Tray context menu: "Show iMirror" and "Exit".
- No installer, so no startup-on-login for v0.2.

---

## Active Mirroring While Hidden — `active-session-hide-policy`

**Decision: Allow hide; GPU/rendering continues uninterrupted.**

When the window is sent to tray during an active mirroring session, the receiver keeps running and D3D11 rendering continues. No resource conservation mode for v0.2. Simple and predictable behavior.

---

## Minimum Windows Version — (Open Decision: min version)

**Decision: Windows 10 22H2 (build 19045) and above.**

.NET 8, WPF, and D3D11 all work on Win10 22H2. Covers the majority of the install base. Older Win10 builds (pre-22H2) and Win10 LTSC variants are not officially tested or supported.

---

## Version Policy — `version-policy`

**Decision: SemVer v0.2.0, assembly 0.2.0.0.**

| Field | Value |
|---|---|
| Git tag | `v0.2.0` |
| `AssemblyVersion` | `0.2.0.0` |
| `FileVersion` | `0.2.0.0` |
| `InformationalVersion` | `0.2.0+{short-commit-sha}` |

CI sets `InformationalVersion` from the tag + `git rev-parse --short HEAD`. The About/version display shows the informational version.

---

## FFmpeg Redistribution Source — `ffmpeg-source`

**Decision: Gyan.FFmpeg.Essentials (LGPL-compatible), pinned to a specific release.**

- Package: `Gyan.FFmpeg.Essentials` via winget or direct download.
- Flavor verification: `publish-win-x64.ps1` already warns on `full_build`.
- License: LGPL-compatible. Include FFmpeg license notice file in the zip package.
- The full_build (GPLv3) is explicitly excluded from release packages.

Pinned checksum verification to be added in Phase 4 (`unify-ffmpeg-resolution`).

---

## IMIRROR_AUDIO_DISCOVERY Semantics — `audio-override-decision`

**Decision: Logging-only; no behavioral change.**

`IMIRROR_AUDIO_DISCOVERY` is a debug/diagnostic flag that produces additional log output. It does not alter audio advertisement, discovery, or decode behavior at runtime. No Settings UI exposure for v0.2.

---

## Settings Save/Restart Persistence — `settings-save-design`

**Decision: Defer to v0.3.**

Current behavior (save triggers restart) is preserved. Separating Save from Restart is cut from v0.2 unless release validation confirms data loss in common paths. Tracked as a v0.3 candidate.

---

## Theme Support — (Open Decision: theme switching)

**Decision: Dark mode only, officially supported.**

v0.2 is dark-mode-only. Light mode and High Contrast are not supported and not tested. The app does not crash or visually break on theme switch, but rendering correctness outside dark mode is not guaranteed. Documented in release notes.

---

## Self-Hosted Validation Runner — (Cut)

**Decision: Cut from v0.2.**

Manual hardware validation is sufficient for v0.2. A self-hosted runner is a v0.3+ investment. Defined in the roadmap risk register and the "Cut from v0.2" section.

---

## Audio Sync Persist on Close — `audio-sync-close-save`

**Decision: Cut from v0.2 unless confirmed regression.**

Not included unless release validation identifies data loss as a confirmed regression in common paths.

---

## Summary Table

| Decision | Choice |
|---|---|
| Code signing | Unsigned; SmartScreen workaround in release notes |
| Distribution | zip only |
| Settings overlay | Separate top-level WPF Window |
| GPU default | Enabled by default |
| Window close | Minimize to tray; Exit from tray menu |
| Active session hide | Allow; rendering continues |
| Min Windows version | Windows 10 22H2+ |
| Version policy | v0.2.0 SemVer, assembly 0.2.0.0 |
| FFmpeg source | Gyan.FFmpeg.Essentials (LGPL) |
| IMIRROR_AUDIO_DISCOVERY | Logging-only |
| Settings save/restart | Defer to v0.3 |
| Theme | Dark mode only (official) |
| Self-hosted runner | Cut from v0.2 |
| Audio sync persist | Cut unless confirmed regression |
