# Spec: v0.2 Phase 2 — P0 Runtime Stability Foundation

**Handoff:** codex-backend
**Source roadmap:** `docs/specs/v02-roadmap.md` (Phase 2)
**Decisions that constrain this work:** `docs/specs/v02-decisions.md`

## Goal

Make the receiver lifecycle deterministic: session end, disconnect cleanup, app
close, software-engine selection, and stale callbacks must all be exception-safe
and idempotent **before** any Settings/tray/visual rework. These are the bugs
that make a public build feel broken during normal use.

## Constraints from Phase 1 decisions

- **Tray / window-close UX is Phase 3, not here.** Do NOT add a tray icon or
  change "close = exit" behavior in this phase. Phase 2 only hardens the
  teardown path so Phase 3 can build on a clean shutdown. `Window_Closing` must
  remain functionally "close exits the app" for now — just make the teardown
  itself crash-proof and idempotent.
- **GPU path stays default-on.** `force-software-runtime` must NOT change the
  default; it only makes the *Software* selection actually take effect.
- **`IMIRROR_AUDIO_DISCOVERY` is logging-only.** No behavioral/audio change.

## Ground truth (verified file:line anchors)

- `MacMirrorReceiver.Networking/AirPlayProbeService.cs`
  - `MirrorSessionStarted` event declared at line 183; fired at line 1427. There
    is **no** `MirrorSessionEnded` event.
  - TEARDOWN/FLUSH handled at line 1767 — replies `200 OK` and resets nothing.
  - Control connection close logged at lines 306 / 318 — no event raised.
  - Session identity fields: `_rtspTargetSessionId` (set line 1359),
    `_announcedMirrorRtspTargetSessionId`, `_announcedMirrorStreamConnectionId`
    (lines 115–116), `_announcedIdentitylessMirrorSession`. Mutated under a lock
    in `TryAnnounceMirrorSessionStarted` (line 1436). None are reset on teardown.
- `MacMirrorReceiver/MainWindow.cs`
  - `HandleAirPlayMirrorSessionStarted` (line 437) → `ResetAirPlayMirrorSessionState`
    (line 448) increments `_connectionGeneration` and tears down decoder/audio.
    This is the only thing that recovers stale state today — and only when a
    *new* session starts.
  - `DisconnectAsync` (line 2414): unguarded `_client.DisposeAsync()`,
    `_decoder?.Dispose()`, `StopAudioPipeline()`, `_mediaFoundationD3DDecoder?.Dispose()`.
    `DisposeHighResolutionD3DPresenter` (line 2457) is the only internally
    try/catch-wrapped step.
  - `Window_Closing` (line 390): `await DisconnectAsync(...)` then
    `_airPlayProbe.Dispose()` / `_browser.Dispose()` — no try/catch, `async void`.
  - `GpuQualityRequested` (line 77) and `QualityPathAvailable` (line 83) are
    `static readonly`, computed once at process start from `RenderModeSettings`.
    The decode decision (`ShouldUseHighResolutionD3DPath` line 1794, `StartDecoder`
    line 1680) consults these statics, **not** `ReceiverSettings.VideoEngine`
    (Auto/Software from the Settings UI). So selecting "Software" in Settings does
    not disable the GPU path.
  - Legacy `MirrorClient` callbacks: `ConnectionClosed` (line 732) and
    `ConfigReceived` (line 742) guard with `ReferenceEquals(_client, client)`, but
    `StatusChanged` (line 722) and `VideoReceived` (line 753 → `HandleVideoPayload`)
    do **not** — late frames from a stale client are processed unconditionally.

---

## Tasks

### Task 1 — `airplay-session-ended`: guarded AirPlay session lifecycle (L, P0)

**Problem:** When the sender ends mirroring (RTSP `TEARDOWN`) or the control
connection drops, the receiver replies/logs but raises no event and resets no
session state. Result: the UI stays "connected" on a frozen last frame, the
audio pipeline keeps running, and per-session identity fields (`_rtspTargetSessionId`,
`_announcedMirror*`, `_announcedIdentitylessMirrorSession`) leak into the next
session. Today recovery only happens incidentally when the *next* mirror session
fires `MirrorSessionStarted`.

**Required change (AirPlayProbeService.cs):**
1. Add `public event Action? MirrorSessionEnded;`.
2. Add a private `ResetMirrorSessionState()` that, under the same lock used by
   `TryAnnounceMirrorSessionStarted`, clears `_rtspTargetSessionId`,
   `_announcedMirrorRtspTargetSessionId`, `_announcedMirrorStreamConnectionId`,
   and `_announcedIdentitylessMirrorSession`, and calls `_audioReceiver.Stop()`.
3. Fire `MirrorSessionEnded` + `ResetMirrorSessionState()` exactly once per
   ended session from BOTH:
   - the `TEARDOWN` branch (line 1767), and
   - the control-connection-close paths (lines 306 / 318) — but only if a mirror
     session had actually been announced (avoid firing on idle probe connections).
   Guard against double-fire (TEARDOWN followed by connection close).
4. `FLUSH` must NOT end the session (it's a mid-stream reset). Keep it `200 OK`
   only.

**Required change (MainWindow.cs):**
5. Subscribe `_airPlayProbe.MirrorSessionEnded += HandleAirPlayMirrorSessionEnded;`
   near line 276.
6. `HandleAirPlayMirrorSessionEnded` (Dispatcher-marshalled like
   `HandleAirPlayMirrorSessionStarted`) must: increment `_connectionGeneration`,
   tear down decoder + audio + D3D presenter (reuse the existing reset body), show
   the empty state, and set status to something like "AirPlay session ended."

**Acceptance:**
- Start mirroring from a Mac, then stop mirroring from Control Center → iMirror
  returns to the empty state within ~1s, audio stops, no frozen frame.
- Start → stop → start again from the same Mac works cleanly with no stale-session
  log warnings.
- Killing the sender (force-close) → control connection drop path also returns to
  empty state.

---

### Task 2 — `disconnect-cleanup-hardening`: harden DisconnectAsync (M, P0)

**Problem:** `DisconnectAsync` (line 2414) disposes client, decoder, audio, and
D3D resources with no per-step exception isolation. One throwing dispose aborts
the rest, leaking ports/GPU/audio.

**Required change:**
- Wrap each teardown step (`_client.DisposeAsync`, `StopVideoWatchdog`,
  `_decoder?.Dispose`, `StopAudioPipeline`, MF/D3D dispose) in its own try/catch
  that logs and continues. Null the field even if its dispose throws.
- Add a re-entrancy guard so overlapping `DisconnectAsync` calls (e.g. user click
  racing an auto-reconnect) don't double-dispose. The existing
  `_connectionGeneration` increment helps; add an explicit guard if needed.
- Keep behavior identical on the happy path.

**Acceptance:** Unit-testable cleanup helper where a deliberately throwing
disposable does not prevent subsequent steps from running. Manual: rapid
connect/disconnect spam leaves no orphaned listener ports (verify with
`netstat`).

---

### Task 3 — `window-closing-hardening`: harden Window_Closing (M, P0)

**Problem:** `Window_Closing` (line 390) is `async void` with an unguarded
`await DisconnectAsync` followed by unguarded `_airPlayProbe.Dispose()` /
`_browser.Dispose()`. Any throw crashes the close or skips disposal.

**Required change:**
- Wrap the whole teardown in try/catch (log, never throw out of `async void`).
- Dispose `_airPlayProbe` and `_browser` each in their own try/catch so one
  failure doesn't skip the other.
- Behavior stays "close = exit" for v0.2. Add a short comment noting Phase 3
  (tray) will extend this handler with hide-vs-exit logic — do not implement that
  now.

**Acceptance:** Close the window mid-session and during an active reconnect
attempt → process exits cleanly, no unhandled-exception dialog, no lingering
listener ports.

---

### Task 4 — `force-software-runtime`: honor Software engine selection (M, P0)

**Problem:** The decode path keys off `static readonly GpuQualityRequested` /
`QualityPathAvailable` (lines 77/83), which ignore `ReceiverSettings.VideoEngine`
(Auto/Software). Selecting "Software" in the Settings UI never disables the GPU
path. The statics are also computed once at process start.

**Required change:**
- At decoder-selection time (`StartDecoder` line 1680 and
  `ShouldUseHighResolutionD3DPath` line 1794), additionally require that the
  effective video engine is **not** Software before taking the D3D11/GPU path.
  Read the *current* effective setting (env override > persisted) rather than a
  value frozen at startup, so the choice applies to the next session without an
  app restart.
- Honor the `IMIRROR_FORCE_SOFTWARE_VIDEO` env override here too (README line 83
  documents it) — verify it actually forces FFmpeg; wire it through the same
  gate if it currently isn't respected by the GPU decision.
- Default remains GPU-on when the setting is Auto (Phase 1 decision). Do not
  change Auto behavior.

**Acceptance:** With Software selected (or `IMIRROR_FORCE_SOFTWARE_VIDEO=1`), a
new mirror session uses `FfmpegDecoder` and never constructs
`MediaFoundationD3D11Decoder`/`D3D11SwapChainVideoPresenter` (verify via log:
"Decoder started" reason should indicate software path). With Auto, GPU path is
taken when hardware decode is available, as today.

---

### Task 5 — `stale-callback-guards`: gate stale callbacks (M, P0)

**Problem:** `MirrorClient.VideoReceived` (line 753) and `StatusChanged`
(line 722) are not gated by `ReferenceEquals(_client, client)`, unlike
`ConnectionClosed`/`ConfigReceived`. Late frames/status from a superseded client
can mutate UI for the wrong session. The AirPlay probe payload handlers should
also be audited against `_connectionGeneration`.

**Required change:**
- Gate `VideoReceived` and `StatusChanged` with the same `ReferenceEquals(_client,
  client)` check used by the sibling callbacks.
- Capture the session `generation` when an AirPlay session starts and have
  `HandleAirPlayVideoPayload` / audio handlers drop payloads whose generation no
  longer matches `_connectionGeneration` (the watchdog already uses generation —
  extend the same guard to the payload entry points).
- Confirm `ScheduleAutoReconnectAsync` / `HandleUnexpectedDisconnectAsync`
  (lines 1042–1082) cannot run against a newer generation (they already check
  `generation == _connectionGeneration`; verify no path bypasses it).

**Acceptance:** Forced rapid reconnect does not render frames from a previous
session; no "frame for stale generation" artifacts. Unit-test the generation
gate as a pure predicate where feasible.

---

### Task 6 — `airplay-preflight-ports`: expand preflight readiness (M, P0)

**Problem:** Startup diagnostics check FFmpeg, listener-bound flags, and network,
but do not surface AirPlay/RAOP TCP port-bind failures distinctly. A port already
in use (another receiver, prior crashed instance) currently shows up only as a
generic listener problem.

**Required change (StartupDiagnostics.cs + AirPlayProbeService.cs):**
- Expose which of `AirPlayPort` / `RaopPort` failed to bind (the probe already
  has `IsAirPlayListenerBound` / `IsRaopListenerBound`; add the specific port
  numbers and the bind error reason if available).
- Add/refine a preflight check that reports a `Blocked` status with an actionable
  message when a required AirPlay/RAOP port is taken (e.g. "Port 7000 is in use —
  close other AirPlay receivers and recheck").
- Keep it consistent with the existing `PreflightCheck`/`PreflightReport` shape
  and the readiness strip binding (`BindReadinessStrip`).

**Acceptance:** Launch a second iMirror instance (or bind the port externally) →
the readiness strip shows a Blocked port check with a clear message; the recheck
button clears it once the port frees.

---

### Task 7 — `audio-override-implementation`: lock IMIRROR_AUDIO_DISCOVERY as logging-only (S, P0)

**Decision (Phase 1):** logging-only, no behavioral change.

**Required change (AirPlayProbeService.cs, lines 51–54):**
- Confirm `IMIRROR_AUDIO_DISCOVERY` only gates extra diagnostic logging and never
  alters audio advertisement, discovery, decode, or `_audioAdvertised`.
- Add a one-line clarifying comment + a single startup log line when the flag is
  on ("IMIRROR_AUDIO_DISCOVERY enabled: verbose data-stream logging only.").
- Do NOT expose it in the Settings UI.

**Acceptance:** With the flag set, only log verbosity changes; audio behavior is
byte-identical to flag-unset. Documented in README diagnostics section if not
already.

---

## Suggested order & dependencies

1. **Task 1** (session-ended) and **Task 4** (force-software) are the highest
   user-visible wins and are independent — do them first, in parallel if split.
2. **Tasks 2 & 3** (cleanup/closing hardening) depend on nothing and make 1 & 5
   safer; land them alongside.
3. **Task 5** (stale guards) builds on the generation plumbing touched by Task 1.
4. **Task 6** (preflight ports) and **Task 7** (audio override) are independent,
   smaller, and can go last.

## Out of scope (do not implement here)

- Tray icon / minimize-to-tray / hide-while-mirroring (Phase 3).
- Settings-over-video top-level window (Phase 3).
- Separating Save from Restart (deferred to v0.3).
- Any visual/theme/accessibility work (Phase 5).

## Build & validation task (Codex)

After implementation:
1. `dotnet build .\MacMirrorReceiver.csproj -c Release` — must succeed, no new
   warnings introduced by these changes.
2. Add/extend unit tests for the pure-logic pieces that don't need a GUI/AirPlay
   device: the disconnect-cleanup step isolation (Task 2), the stale-generation
   predicate (Task 5), and the software-engine gate decision (Task 4). If no test
   project exists yet, a minimal scaffold is acceptable (this overlaps Phase 4
   `solution-test-scaffold` — coordinate, don't duplicate).
3. Report a manual hardware validation checklist (real Mac/iPhone) covering:
   start/stop mirroring, start→stop→start, force-close sender, Software-engine
   session, rapid reconnect, app close mid-session, and a port-in-use launch.
   Hardware runs happen on the Windows side, not in the Codex environment.
