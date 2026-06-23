# Spec: v0.2 Phase 3 — Settings Window, Live Video, And Tray Lifecycle

**Handoff:** codex-backend
**Branch:** `claude/magical-faraday-6ex3c7`
**Source roadmap:** `docs/specs/v02-roadmap.md` (Phase 3)
**Decisions:** `docs/specs/v02-decisions.md`

## Goal

Fix the two public-facing UX blockers that remain after Phase 2 stability:
1. The Settings panel is occluded by the D3D11 live-video child HWND.
2. Closing the window kills the whole app, so background receiving is impossible.

Both are resolved by structural changes to the window model. Do them in one
cycle because they both touch the window lifecycle (`Window_Closing`, `App`
shutdown) and splitting risks merge conflicts in `MainWindow.cs`.

## Locked design decisions (do not redesign — implement these)

These are the architectural calls for this phase. They follow the Phase 1
decisions and resolve the airspace/lifecycle problems with the lowest risk.

### DD1 — Settings becomes a separate top-level `Window`
The current `<Border x:Name="SettingsOverlay">` (MainWindow.xaml line 770,
`Grid.Column="0"`, `Panel.ZIndex="100"`) cannot float above the D3D11 swap-chain
because that swap chain is a **child HWND of the main window**, outside WPF
composition. ZIndex within the WPF tree has no effect over it.

A separate top-level `Window` is itself a distinct HWND. An **owned** window
(`Owner = mainWindow`) is always rendered above its owner — including the owner's
child HWNDs — by the OS window manager. This makes Settings reliably visible over
live video on both the GPU and software paths, with no airspace hacks.

- Create `MacMirrorReceiver/SettingsWindow.xaml` + `SettingsWindow.xaml.cs`.
- Move the entire Settings markup (the `SettingsOverlay` Border subtree, lines
  ~770–766..) into `SettingsWindow.xaml`. Remove `SettingsOverlay` from
  `MainWindow.xaml`.
- Window chrome: `WindowStyle="SingleBorderWindow"`, `ResizeMode="NoResize"`,
  `ShowInTaskbar="False"`, `WindowStartupLocation="CenterOwner"`,
  `SizeToContent="Height"` with a fixed width (~360), Title "iMirror Settings".
- Show **modeless** (`settingsWindow.Show()`, NOT `ShowDialog()`). Rationale: the
  audio-sync slider applies live and the user must see the video react while
  dragging it. A modal dialog would freeze that feedback loop.
- One instance only: if Settings is already open, `Activate()` it instead of
  opening a second one.
- Escape closes the window (already wired via the existing Escape handler —
  re-point it at the new window).
- The window closes automatically when the main window closes (owned-window
  behavior) and when the app exits.

### DD2 — Settings state ownership
The live audio-sync value MUST keep writing to `MainWindow._audioSyncOffsetMilliseconds`
(the `Volatile` field the audio thread reads — MainWindow.cs line 70). So
`SettingsWindow` cannot fully own state.

- Define a small host contract the SettingsWindow calls back into. Either an
  `ISettingsHost` interface implemented by MainWindow, or a direct typed
  reference passed to the SettingsWindow constructor — Codex's choice, but the
  live audio-sync path MUST route through MainWindow so the audio thread sees
  updates with no restart (preserve the current `Volatile.Write` +
  `PersistLiveAudioSyncOffset` behavior, MainWindow.cs lines 1607–1628).
- Pending receiver-name / video-engine / render-mode state and the
  save-and-restart action (currently `SettingsRestartButton_Click`, line 1630)
  move with the markup but keep calling the existing `ReceiverSettings.UpdateDto`
  persistence. Do not change the save→restart semantics (Phase 1: save/restart
  separation is deferred to v0.3).
- `StartupReceiverSettings` / `StartupRenderModeSettings` are `static` on
  MainWindow; expose what SettingsWindow needs via the host or move the snapshot
  references as needed. Do not duplicate the load logic.

### DD3 — System tray via WinForms `NotifyIcon`
WPF has no native tray icon. Use `System.Windows.Forms.NotifyIcon` — it ships
with the framework (no new NuGet dependency) and is the standard pragmatic
choice.

- Add `<UseWindowsForms>true</UseWindowsForms>` to `MacMirrorReceiver.csproj`
  (coexists with `<UseWPF>true</UseWPF>`).
- Tray icon uses the app icon. Context menu items: **"Show iMirror"** (restores +
  activates the window) and **"Exit"** (the only real teardown path).
- Double-click / left-click the tray icon also restores the window.
- Create and own the `NotifyIcon` in MainWindow (or a small `TrayIcon` helper
  class); dispose it in the explicit-shutdown path.

### DD4 — Shutdown model: explicit only
Change `App` shutdown so hiding the window does not quit the app.

- In `App.OnStartup` set `this.ShutdownMode = ShutdownMode.OnExplicitShutdown;`
  (App.cs around line 50–66).
- The app exits ONLY via an explicit `Application.Current.Shutdown()` from the
  tray "Exit" path. Nothing else should terminate the process.

### DD5 — Window close = hide to tray
- In `Window_Closing` (MainWindow.cs line 398), when the close is a normal user
  close (not an explicit exit), set `e.Cancel = true` and hide the window
  (`Hide()`), then return WITHOUT tearing down services.
- Distinguish explicit exit with a flag (e.g. `_isExplicitExit`) set by the tray
  "Exit" handler before it triggers teardown + shutdown.
- On the FIRST hide only, show a tray balloon tip / tooltip:
  "iMirror is still running. Right-click the tray icon to exit." (one-time;
  persist a flag in memory for the session — no settings.json change needed).

### DD6 — Active-session hide policy (Phase 1: allow, keep rendering)
- Hiding the window during an active mirror session MUST NOT stop the decoder,
  audio, or D3D presenter. Rendering continues offscreen.
- The D3D11 child HWND is a child of the main window, so `Hide()` hides it too;
  the decode/GPU pipeline keeps running. Verify no crash, no
  `D3DImage`/swap-chain exception when the window is hidden mid-session, and that
  restoring shows live video again immediately.

### DD7 — Single explicit-shutdown teardown path
- Consolidate real teardown into one method (e.g. `ShutdownApplication()`):
  `DisconnectAsync` → dispose `_airPlayProbe`, `_browser`, and the `NotifyIcon`
  → `Application.Current.Shutdown()`. Reuse the existing `CleanupGuards.RunStep`
  hardening (already in `Window_Closing` finally block, lines 411–412).
- The tray "Exit" handler sets `_isExplicitExit = true` then calls this path.
- Keep `Window_Closing` exception-safe (Phase 2 hardening must remain).

---

## Tasks (roadmap ids)

| id | What it maps to here |
|---|---|
| `settings-above-video` / `settings-airspace-implementation` | DD1, DD2 — Settings as owned top-level Window |
| `settings-modal-accessibility` | Keyboard + screen-reader semantics on the new window (see below) |
| `tray-background-receiving` | DD3, DD5, DD6 |
| `explicit-service-shutdown` | DD4, DD7 |
| `active-session-hide-policy` | DD6 |
| `windows-lifecycle-e2e` | Manual validation checklist (below) |
| `gpu-settings-release-qa` | Verify Settings-over-video on the GPU path specifically |

### Settings modal accessibility (`settings-modal-accessibility`)
On the new `SettingsWindow`:
- Tab order flows through all controls; focus lands on the first control on open.
- Escape closes; Enter on the restart button activates it.
- `AutomationProperties.Name` on the receiver-name box, both video-engine radios,
  the audio-sync slider, and the close/restart buttons.
- Window has an accessible Title ("iMirror Settings").

---

## Acceptance

**Settings airspace (the core fix):**
- Start GPU mirroring (real device, HIGH_RESOLUTION_D3D path active), open
  Settings → the Settings window is fully visible and interactive **above** the
  live video. No occlusion, no flicker of the video behind it.
- Repeat on the software path (`IMIRROR_FORCE_SOFTWARE_VIDEO=1`) → same result.
- Drag the audio-sync slider while mirroring → audio offset changes live, video
  keeps playing behind the window.

**Tray lifecycle:**
- Click the window close (X) while idle → window hides, tray icon present, app
  still running, one-time tooltip shown on first hide.
- Close (X) during an active mirror session → window hides, mirroring continues
  (audio still plays), tray icon present.
- Tray "Show iMirror" → window restores with live video intact.
- Tray "Exit" → app fully terminates, no orphaned listener ports (verify
  `netstat`), tray icon removed.
- Incoming AirPlay session while hidden → receiver still accepts it (discovery
  and listeners stay up because the app never shut down).

**Regression:**
- Phase 2 stability behavior intact (session-end on TEARDOWN, hardened cleanup,
  stale-callback guards).

---

## Sequencing within the phase

1. **Settings window first** (DD1, DD2, DD3-independent): create SettingsWindow,
   move markup + handlers, wire host callback, delete old overlay. Verify
   airspace fix.
2. **Tray + shutdown model** (DD3, DD4, DD5, DD7): add NotifyIcon, flip
   ShutdownMode, make close=hide, build the explicit-exit teardown path.
3. **Hide policy + accessibility** (DD6, modal a11y): verify rendering continues
   while hidden; add automation names.
4. Run the manual lifecycle/GPU QA checklist.

## Out of scope (later phases)

- Visual/theme/color-token/spacing rework (Phase 5).
- Empty-state redesign, Fluent icon pass (Phase 5).
- Startup-on-login (no installer in v0.2).
- Separating Save from Restart in Settings (deferred to v0.3).
- Firewall remediation automation (Phase 5, stays manual).

## Build & validation (Codex)

1. `dotnet build .\MacMirrorReceiver.csproj -c Release` after adding
   `<UseWindowsForms>` — must succeed, no new warnings.
2. Unit tests where pure-logic permits (e.g. the first-hide-once flag, the
   explicit-exit gate predicate). GUI/tray/airspace behavior needs the manual
   checklist; it cannot be unit-tested headless.
3. Report a Windows manual validation checklist covering every Acceptance bullet
   above, run on real Mac/iPhone hardware (Windows side, not the Codex env).
   Explicitly include: GPU-path Settings-over-video, software-path
   Settings-over-video, close-while-mirroring, tray Exit port cleanup, and
   incoming session while hidden.
