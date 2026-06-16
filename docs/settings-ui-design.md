# Settings UI Design

Status: design proposal (v0.2 target). This document is the source of truth for
the in-app **Settings** experience and the `settings.json` schema. It is meant to
be read by two audiences:

- the WPF/front-end work that builds the panel and the settings model, and
- the back-end work (separate session) that rewires each option's consumer to
  read the persisted setting instead of an environment variable.

## Goal

Today every runtime option except render mode is an `IMIRROR_*` environment
variable. A normal user cannot set those. The goal is a single in-app Settings
surface that exposes the *user-relevant* options, persists them, and keeps the
existing environment variables working as a power-user / CI override.

Non-goal: exposing pure diagnostic dumps as first-class buttons. Those stay in an
"Advanced / diagnostics" area, off by default.

## Settings inventory

Every option iMirror currently reads, with its target home.

### User-facing (Settings panel, primary)

| Setting | Current env var | Type / range | Default | Apply |
| --- | --- | --- | --- | --- |
| Receiver name | (hardcoded `"iMirror"`) | string, 1–32 chars | `iMirror` | restart |
| Render / quality mode | `IMIRROR_RENDER_MODE` | Stable \| Quality | Quality (HW) | restart |
| Video engine | `IMIRROR_FORCE_SOFTWARE_VIDEO` | Auto (GPU) \| Force software | Auto | restart |
| Audio enabled | `IMIRROR_AUDIO_DISCOVERY` (logging only today) | bool | on | restart* |
| Audio sync offset | `IMIRROR_AUDIO_SYNC_OFFSET_MS` | int 60–220 ms | 120 | **live** |

\* Audio advertise/disable is restart-scoped because it changes what the receiver
advertises over mDNS. Audio sync offset is the one option that should apply live
(no reconnect) — it only retunes the WASAPI target latency.

### Advanced / diagnostics (collapsed, off by default)

| Setting | Current env var | Notes |
| --- | --- | --- |
| Write diagnostics | `IMIRROR_WRITE_DIAGNOSTICS` | may capture private screen/session material |
| Dump H.264 | `IMIRROR_DUMP_H264` | elementary stream dump |
| Dump audio | `IMIRROR_DUMP_AUDIO` | decoded PCM dump |

These three carry a privacy warning (see `docs/architecture.md` → Privacy). They
should be grouped under one expander with a single "These files can contain
private screen content and key material" caption.

### Stay environment-only (not in UI)

These are decoder-internal / experimental knobs with no stable user meaning.
Leave them as env vars; do not surface them.

- `IMIRROR_EXPERIMENTAL_QUALITY` — forces GPU path for local validation
- `IMIRROR_FORCE_SOFTWARE` / `IMIRROR_PREFER_HARDWARE` — FFmpeg HW-accel probing
- `IMIRROR_HR_V2_PROBE` — high-resolution pipeline probe

## Precedence model (already established, generalize it)

`RenderModeSettings.Load()` already implements the rule we standardize on:

```
effective = env override (if present and valid)
          > persisted value (settings.json)
          > built-in default
```

When an environment override is present for a setting, the UI control for that
setting is **disabled** and shows a note: `IMIRROR_X is overriding this setting.`
This mirrors the current render-mode behavior (`RenderModeOverrideTextBlock`,
`canEditSetting`). Keep that exact UX for every overridable control so CI/power
users are never surprised by a control that silently does nothing.

## settings.json schema

Location is unchanged: `%AppData%/iMirror/settings.json`. Extend the existing
single-field DTO into a versioned object. Unknown fields are ignored; missing
fields fall back to default (forward/backward compatible).

```jsonc
{
  "schemaVersion": 1,
  "receiverName": "iMirror",
  "renderMode": "quality",          // "stable" | "quality"  (existing field)
  "videoEngine": "auto",            // "auto" | "software"
  "audioEnabled": true,
  "audioSyncOffsetMs": 120,         // clamped 60..220
  "diagnostics": {
    "writeDiagnostics": false,
    "dumpH264": false,
    "dumpAudio": false
  }
}
```

Migration: the current file only has `renderMode`. Loading an old file must
succeed and leave every new field at its default. `schemaVersion` absent ⇒ treat
as 1.

## UX / layout

The sidebar already hosts setting cards inline (Quality mode card, lines ~280-372
of `MainWindow.xaml`). For v0.2 keep the same visual language but consolidate:

```
┌ Sidebar ────────────────────────┐
│  iMirror              [ ⚙ ]      │  ← gear toggles the Settings overlay
│  Ready to receive               │
│  How to mirror  (1·2·3)         │
└─────────────────────────────────┘

Settings overlay (slide-in panel, same card styling):
  ▸ General
      • Receiver name           [ iMirror            ]
  ▸ Video
      • Quality mode            ( ) Stable  ( ) High quality
      • Video engine            ( ) Auto (GPU)  ( ) Force software
  ▸ Audio
      • Audio                   [x] Enabled
      • Sync offset             [—————•———]  120 ms   (live)
  ▸ Advanced / diagnostics  (expander, collapsed)
      ⚠ Files here can contain private screen content and key material.
      • [ ] Write diagnostics
      • [ ] Dump H.264
      • [ ] Dump audio
  ──────────────────────────────────
  ⚠ Restart required. AirPlay connections will disconnect.   [ Restart now ]
```

Rules:
- One shared **restart-required** banner at the bottom of the overlay, shown when
  any restart-scoped pending value differs from the persisted value. Reuse the
  existing `RenderModeRestartPanel` styling and `RestartForRenderModeButton`
  click logic, generalized to save the whole DTO.
- The **audio sync offset** slider applies live and never triggers the restart
  banner. Wire its `ValueChanged` to the existing audio-sync target path.
- Any control whose env override is active is disabled with the standard
  override note; the rest stay editable.

## Live-apply vs restart matrix

| Setting | Live | Restart |
| --- | --- | --- |
| Audio sync offset | ✅ | |
| Render mode | | ✅ |
| Video engine | | ✅ |
| Receiver name | | ✅ |
| Audio enabled | | ✅ |
| Diagnostics toggles | | ✅ (read at session/dump start) |

## Work split

### This session (front-end / design) — DONE
1. `ReceiverSettings` model (`MacMirrorReceiver/ReceiverSettings.cs`): the
   versioned DTO above, `Load()` with per-field env-override resolution,
   `UpdateDto()` read-modify-write so single-field saves never clobber others,
   and legacy single-field migration. `RenderModeSettings` now delegates its file
   IO here, so `settings.json` has a single owner.
2. Settings overlay XAML + handlers in `MainWindow`: receiver name, render/quality
   mode (moved into the overlay), video engine, audio toggle, **live** audio-sync
   slider, diagnostics expander, per-field override-disable notes, and one shared
   restart banner.
3. Audio sync offset is wired live: `MainWindow._audioSyncOffsetMilliseconds` is a
   `Volatile` instance field the slider writes and the audio thread reads, and the
   value persists when the overlay closes.

### Hand off to Codex/GPT-5.5 session (back-end wiring) — spec
The model and UI are in place. Remaining work is to make the *consumers* read
`ReceiverSettings.Load().Effective.X` instead of `Environment.GetEnvironmentVariable`,
preserving the env override (UI already disables overridden controls):
- `AirPlayProbeService`: use `Effective.ReceiverName` instead of the hardcoded
  `"iMirror"` advertised name, and gate audio advertise on `Effective.AudioEnabled`
  (today `IMIRROR_AUDIO_DISCOVERY` only toggles verbose logging).
- Diagnostics gates (`IMIRROR_WRITE_DIAGNOSTICS`, `IMIRROR_DUMP_H264`,
  `IMIRROR_DUMP_AUDIO`): read `Effective.WriteDiagnostics/DumpH264/DumpAudio` at
  session/dump start.
- Video engine: `Effective.VideoEngine == Software` should map to the existing
  force-software path (`RenderModeSettings.ForceSoftwareVideoRequested`), so the
  persisted choice and `IMIRROR_FORCE_SOFTWARE_VIDEO` resolve identically.
- Acceptance per option: env var set ⇒ control disabled + override note + old
  behavior; env var unset ⇒ the persisted setting drives behavior and survives a
  restart. Audio sync offset is already fully wired (live + persisted) — no
  back-end change needed there.
```
