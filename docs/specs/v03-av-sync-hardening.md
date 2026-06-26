# Issue #3 — Long-run A/V Sync Hardening: Execution Note

**Tracked by:** Issue #3 (enhancement, standing track) · **Status:** prepped,
**gated on hardware window** · **Deadline:** none (field hardening, not a release
blocker).

This is a **measurement-first** track. The audio-sync machinery already exists; the
open work is to *run the long sessions that have never been run*, decide a stable
default from the data, and add drift correction **only if** the data shows
cumulative drift. Do not pre-emptively add correction code — measure first.

---

## Why this is serial after Vortice (not parallel hardware)

The soak runs need the **same single real device + GPU** as v0.7 Gate B, and — more
importantly — running an audio-sync change in the *same* window as the Vortice GPU
swap destroys bisectability: if audio drifts, was it the binding swap or the sync
change? **Run #3's hardware sessions only after v0.7 Gate B is merged**, against the
known-good Vortice (or v0.5.0 SharpDX) baseline.

The *planning* (this note) is the parallel-prep deliverable; the *runs* are serial.

---

## What already exists (confirmed in code)

- **Live offset:** `MainWindow._audioSyncOffsetMilliseconds` (volatile), written by
  the Settings slider via `ISettingsHost.SetLiveAudioSyncOffsetMilliseconds`, read
  by the audio thread through `ResolveAudioSyncTargetLatencyMilliseconds()`.
- **Target latency window:** `MinAudioSyncTargetMilliseconds = 120`,
  `MaxAudioSyncTargetMilliseconds = 220` (`MainWindow.cs:61,63`).
- **WASAPI sink:** `WasapiAudioOutput.SetSyncTargetLatencyMilliseconds(...)` applies
  the target live.
- **Persisted default:** `ReceiverSettings.AudioSyncOffsetMs`
  (`ClampAudioOffset`), surfaced in `SettingsWindow`.
- **Prior field fix (already shipped, v0.2.1/PR #13):** silent-audio-on-Public-network
  was a firewall issue, **resolved** — not part of this track. #3 is now purely the
  long-run drift/stability observation that has never been performed.

So the code path is in place. #3 is about **data → default → (conditional) drift
correction**, not new plumbing.

---

## Phase 1 — Observation protocol (real hardware; the missing data)

Run with audio active, GPU present path, against the merged baseline. Per the issue
acceptance criteria:

- [ ] **30-minute** session: log WASAPI buffer depth, sync-drop count, and
      subjective lip-sync at start / 15m / 30m.
- [ ] **1-hour** session: same, plus confirm no audio mute/stutter/over-buffer.
- [ ] **2-hour** session: same; this is where cumulative drift, if any, surfaces.
- [ ] **Reconnect cycles:** 3× disconnect/reconnect mid-session; confirm audio
      restores cleanly (decoder re-inits, WASAPI buffer does not balloon).
- [ ] Repeat the 1-hour run at **2–3 distinct `IMIRROR_AUDIO_SYNC_OFFSET_MS`
      values** spanning the 120–220ms window to find the subjective sweet spot.

**Instrumentation to capture (mostly already logged):** WASAPI buffer status lines,
sync-drop/latency-trim events, RTP timestamp vs. video `SourceTimestampNanos` delta
over time (this delta growing monotonically = cumulative drift = the trigger for
Phase 3).

## Phase 2 — Promote a stable default (low-risk code change)

- [ ] From Phase 1 data, pick the offset that holds lip-sync best across the 2-hour
      run and reconnects. Set it as the persisted default
      (`ReceiverSettings.AudioSyncOffsetMs` default), keep the slider for override.
- [ ] Document the chosen default and the window rationale in `docs/` (and the
      Settings help text if user-facing).

This is the **expected terminal state** if no cumulative drift is observed: a
data-backed default, slider retained, issue closed.

## Phase 3 — Drift correction (CONDITIONAL — only if Phase 1 shows it)

Trigger: the RTP/`SourceTimestampNanos` delta grows monotonically over the 1h/2h
runs (not just jitter around a fixed offset).

- [ ] Add RTP-timestamp / video-timestamp drift correction (slow-rate resampling or
      periodic latency re-target toward the window) rather than a one-shot offset.
- [ ] Re-validate with another 2-hour run; confirm the delta stays bounded.

If Phase 1 shows only bounded jitter (no monotonic growth), **skip Phase 3** — the
fixed default from Phase 2 is sufficient. Do not build correction for drift that
isn't there.

---

## Acceptance (mirrors Issue #3)

- [ ] Long-running playback keeps audio active without decoder failure.
- [ ] WASAPI buffer does not grow unbounded or repeatedly clear.
- [ ] Sync drops remain low and act only as latency trimming.
- [ ] No visible long-term A/V drift during normal use.
- [ ] A stable default sync offset is documented / exposed.

---

## Reference map

| Thing | Location |
|---|---|
| Live offset + target window | `MacMirrorReceiver/MainWindow.cs:61,63,80-83,724-731` |
| WASAPI sink | `MacMirrorReceiver.Audio` (`WasapiAudioOutput.SetSyncTargetLatencyMilliseconds`) |
| Persisted default / clamp | `MacMirrorReceiver/ReceiverSettings.cs` (`AudioSyncOffsetMs`, `ClampAudioOffset`) |
| Soak harness | `scripts/soak-gate.ps1`, `docs/soak-gate.md` |
| Real-device E2E + latency | `docs/windows-e2e-validation.md` |
| Issue | #3 |

**Boundary reminder:** keep these runs out of the v0.7 Vortice validation window —
an A/V-sync change and a GPU-binding swap must stay independently bisectable.
