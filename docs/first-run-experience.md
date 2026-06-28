# First-Run Experience Design

Status: design proposal (v0.2 target, P1.1). Source of truth for the startup
**preflight self-check** and its readiness UI. Two audiences:

- the WPF / front-end work that builds the readiness panel and remediation UX, and
- the back-end work (separate Codex session) that implements the diagnostic
  probes (`StartupDiagnostics`) and threads results into the app lifecycle.

## Goal

The v0.1.0 program works once a session connects, but three setup conditions
fail **silently** today and each one ends with the user concluding "it doesn't
work" and uninstalling. None of them is surfaced before a sender tries to
connect:

1. **FFmpeg missing.** FFmpeg is resolved lazily, only when the first audio or
   video stream arrives (`FfmpegDecoder.cs:815-855`). If it is absent the app
   launches and advertises normally, then throws
   `InvalidOperationException("ffmpeg.exe was not found ...")` deep inside
   `Decoder.Start()`, surfaced only as a transient "Decoder failed: ..." status
   (`MainWindow.cs:1570-1574`) *after* the user has already started mirroring.
   Audio (AAC-ELD) and the software video fallback both depend on it.
2. **Firewall blocks the listeners.** The receiver binds mDNS 5353 (UDP),
   AirPlay 7000/7100/7101/7102 (TCP) and RAOP 5000 (TCP) at startup. Each
   listener catches bind failures, logs them, and returns `null`
   (`AirPlayProbeService.cs:215-224`) — the app stays up but advertises nothing,
   so iMirror never appears in the macOS AirPlay picker and the user has no idea
   why.
3. **Wrong / no usable network.** AirPlay requires sender and receiver on the
   same L2 subnet (multicast 224.0.0.251). On a metered hotspot, client-isolated
   guest Wi-Fi, or VPN-only interface, discovery silently fails. Nothing tells
   the user this is the cause.

The goal: a **startup preflight** that runs these checks once at launch,
surfaces a single readiness verdict in the sidebar, and gives one concrete next
action per failed check. A working program should never look broken because of
setup friction it could have detected.

Non-goal: a multi-step wizard or modal onboarding that blocks the window. The
app must stay launch-to-ready in one screen; the preflight augments the existing
status surface, it does not gate it.

## Preflight check inventory

Each check is cheap, runs off the UI thread at startup, and resolves to one of
`Ok` / `Warning` / `Blocked`, plus a short message and (when not `Ok`) a remedy
action.

| # | Check | How it probes | Verdicts |
| --- | --- | --- | --- |
| 1 | **FFmpeg present** | Reuse the existing resolver search order (`tools/ffmpeg/bin`, source-tree fallback, `PATH`, WinGet `Gyan.FFmpeg`). Resolve the path *without* launching a decode. | `Ok` = found · `Blocked` = not found |
| 2 | **Listeners bound** | After `AirPlayProbeService.StartAsync()`, report which of the required ports actually bound. mDNS 5353 + AirPlay 7000 are mandatory; a miss on either is `Blocked`. A miss on RAOP 5000 only (legacy audio) is `Warning`. | `Ok` = all bound · `Warning` = optional miss · `Blocked` = mandatory miss |
| 3 | **Network reachable** | Enumerate `NetworkInterface` for an up, non-loopback IPv4 interface that supports multicast. Capture the chosen local IP for display. | `Ok` = usable IPv4 multicast NIC · `Warning` = only VPN/virtual or multicast-incapable NIC · `Blocked` = no IPv4 NIC up |

These run **once at startup** and on demand from a "Re-check" affordance (so a
user who just allowed the firewall prompt or plugged in Wi-Fi can re-verify
without relaunching). They are not continuous monitors.

### Why these three and not more

The checks map 1:1 to the three silent-failure modes above and nothing else.
Connection-time problems (pairing, FairPlay, decode faults) already surface
through the existing status path and are out of scope here — preflight is
strictly about *can a sender even find and feed this receiver*.

## Remediation per check

Every non-`Ok` verdict carries exactly one primary action. Keep copy short and
imperative; full detail goes to the log, not the panel.

| Check | Verdict | Panel message | Primary action |
| --- | --- | --- | --- |
| FFmpeg | `Blocked` | "FFmpeg not found. Audio and software video are disabled." | **How to fix** → opens `docs`/inline help with the bundled-vs-PATH instructions; if we ship the bundled binary (below), this should rarely fire. |
| Listeners | `Blocked` | "Windows Firewall is blocking AirPlay. iMirror is not discoverable." | **Allow in Firewall** → see "Firewall remediation" below. |
| Listeners | `Warning` | "Legacy audio port unavailable; mirroring still works." | none (informational). |
| Network | `Warning` | "Connected via VPN/virtual adapter. Senders on your Wi-Fi may not see iMirror." | **Show network help** (inline text: join the same Wi-Fi, disable client isolation). |
| Network | `Blocked` | "No network connection. Connect to the same Wi-Fi as your Mac/iPhone." | **Re-check** after connecting. |

### Firewall remediation

**Current decision:** provide a guided **Allow in Firewall** action without
running the whole app elevated. When clicked, iMirror launches a short
PowerShell firewall installer through UAC and creates/enables an inbound
program rule for the currently running `iMirror.exe`.

The rule allows the app for Private and Public Windows Firewall profiles because
AirPlay video, RAOP, mDNS, and audio RTP do not all use one fixed port set.
Audio RTP is negotiated dynamically during SETUP, so the remediation must allow
the application itself rather than only TCP 7000/5000 or UDP 5353.

If the user cancels UAC or the rule install fails, iMirror keeps the warning and
opens Windows Firewall settings as a manual fallback.

### FFmpeg: bundle vs. detect

Detection (check 1) is the safety net, but the real fix for most users is to
**ship FFmpeg in the release zip** so the `tools/ffmpeg/bin/ffmpeg.exe` search
hit is satisfied out of the box (`release.md` already names FFmpeg as required).
The preflight check then only fires for people who unpacked the zip wrong.
Bundling is a release-packaging change, tracked separately from the in-app UI;
the design assumes it lands so check 1 is a rare edge, not the common path.

## UX / layout

Reuse the existing sidebar status language (the blue "Ready to receive" box,
`ReceiverStatusTextBlock` at `MainWindow.xaml:223`, and `SidebarStatusDot` at
`:176`). The preflight adds a **readiness strip** directly under the status box,
visible only while any check is non-`Ok`. When all checks pass it collapses and
the sidebar looks exactly like today.

```
┌ Sidebar ──────────────────────────────────┐
│  iMirror                         [ ⚙ ]     │
│  ● Ready to receive                        │  ← existing status box + dot
│                                            │
│  ⚠ Setup needs attention        [ Re-check ]│  ← readiness strip (only if not all Ok)
│    ✕ Firewall is blocking AirPlay          │
│         [ Allow in Firewall ]              │
│    ⚠ FFmpeg not found                      │
│         [ How to fix ]                     │
│    ✓ Network: 192.168.0.42 (Wi-Fi)         │
│                                            │
│  How to mirror  (1·2·3)                    │  ← existing help block
└────────────────────────────────────────────┘
```

Rules:

- The strip header summarizes the worst verdict: any `Blocked` ⇒ red
  "Setup needs attention"; only `Warning`s ⇒ yellow "Minor setup notes"; all
  `Ok` ⇒ strip hidden entirely.
- Each non-`Ok` row shows its message and its single primary action button.
  `Ok` rows are shown only while the strip is already open (so the user sees the
  full picture), and disappear with the strip once everything passes.
- **Re-check** re-runs all probes off-thread and refreshes the strip in place.
  No relaunch, no reconnect.
- Reuse existing brushes: `WarningBrush` (yellow), `SuccessBrush` (green),
  and a blocked/red brush (add `DangerBrush` if absent) — match the
  `ResolveReceiverStatusBrush()` palette (`MainWindow.cs:2486-2501`).
- The dot color logic is unchanged. Preflight `Blocked` does **not** repaint the
  main status dot red — the dot reflects *connection* state; the strip reflects
  *setup* state. They are independent on purpose.

## Lifecycle

```
App launch
  └─ Window_Loaded (MainWindow.cs:304)
       ├─ await _browser.StartAsync()
       ├─ await _airPlayProbe.StartAsync()      // already returns bound-port info
       └─ await StartupDiagnostics.RunAsync()   // NEW: checks 1 + 3, plus reads
            └─ build readiness model             //      bound-port result for check 2
                 └─ render readiness strip (UI thread)

User clicks Re-check
  └─ StartupDiagnostics.RunAsync() again → re-render strip
```

Check 2 depends on the bind result that `AirPlayProbeService.StartAsync()`
already computes, so the diagnostics step must run *after* the probe start and
read its outcome rather than re-binding ports itself (re-binding would race the
live listeners). Checks 1 and 3 are independent and can run in parallel.

## Data model (for the back-end session)

```csharp
internal enum PreflightStatus { Ok, Warning, Blocked }

internal sealed record PreflightCheck(
    string Id,              // "ffmpeg" | "listeners" | "network"
    string Title,           // "FFmpeg", "Firewall / discovery", "Network"
    PreflightStatus Status,
    string Message,         // short, user-facing
    string? Detail);        // optional: local IP, missing port list, ffmpeg path

internal sealed record PreflightReport(
    IReadOnlyList<PreflightCheck> Checks,
    PreflightStatus Worst); // = max severity across Checks
```

`StartupDiagnostics.RunAsync(AirPlayProbeService probe)` returns a
`PreflightReport`. The UI binds to it; it must contain no WPF types so it stays
unit-testable.

## Work split

### This session (front-end / design)
1. This document.
2. Readiness-strip XAML in `MainWindow.xaml` (collapsed by default) and the
   bind/render methods in `MainWindow` that consume a `PreflightReport`:
   per-row visibility, severity → brush, primary-action button wiring, and the
   **Re-check** handler. The strip renders from the model only; it does not
   itself probe anything.

### Hand off to Codex/GPT-5.5 session (back-end) — spec to follow
1. `StartupDiagnostics` service implementing the three probes against the
   existing resolver / `NetworkInterface` APIs, returning `PreflightReport`.
2. Surface the bound-port result out of `AirPlayProbeService.StartAsync()` so
   check 2 reads real state instead of re-binding.
3. Firewall remediation: **guided rule install**. The **Allow in Firewall**
   button launches an elevated PowerShell helper through UAC and creates/enables
   an inbound Windows Firewall program rule for the current `iMirror.exe`.
4. Release packaging: bundle `ffmpeg.exe` under `tools/ffmpeg/bin/` in the zip so
   check 1 passes out of the box (tracked in `release.md`).

## Open questions (resolve before back-end build)

- **FFmpeg bundling:** confirm we have a redistribution-compliant FFmpeg build
  to ship (LGPL/GPL terms) before assuming check 1 is a rare edge.

Resolved: **firewall remediation is a guided UAC action**; the app itself still
runs unelevated during normal mirroring.
