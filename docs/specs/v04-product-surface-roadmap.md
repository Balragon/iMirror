# iMirror v0.4 Product-Surface Roadmap (Installer · Auto-Update · Signing)

## Executive Summary

v0.2 shipped a working public preview and v0.3 finished the **deployment
foundation** (writable paths under `%LOCALAPPDATA%`, third-party compliance,
SBOM, consolidated publish policy, bundled FFmpeg license, soak gate). The
engineering substrate is now release-grade.

What is *not* yet present is the **developer/technical user product surface**
— the part a power user actually experiences day-to-day:

1. There is **no installer** — users unzip a folder and run a loose `.exe`.
2. There is **no auto-update** — only a manual "check for updates" link.
3. The download is **unsigned**, so SmartScreen shows "Windows protected your
   PC" on first run (developer-familiar workaround, but still friction).

For **developer-focused software**, the ranking is different from consumer
products. Developers care about installation experience and update velocity
more than trust signage; they will click through SmartScreen. This roadmap
closes the gaps in actual ROI order for a technical audience.

**Recommended sequence:** Installer → Auto-Update → Signing. Installer comes
first because it is the **entry experience** that shapes daily workflow (Start
Menu, uninstaller, clean uninstall/upgrade). Auto-Update follows because
developers expect to receive fixes without hunting for new zips. Signing comes
last — valuable for trust/reputation-building, but not a blocker for adoption
among technical users.

**Tooling decision:** Installer = **Inno Setup**; Auto-Update = a **lightweight
in-app updater** (not Velopack). Velopack was evaluated and deferred — its
cross-platform and delta-update strengths are both nullified for a Windows-only,
infrequently-updated app, while its coupling/lock-in costs remain. See Phase 1's
"Framework decision" and `docs/specs/v04-updater-design.md` for the full
rationale.

**Timeline constraint:** Issue #17 (.NET 8 EOL, 2026-11-10) competes for the
same v0.4/v0.5 window. Land Installer + Auto-Update **before** the net10
runtime churn, then sequence the net10 bump and re-validate. Signing can land
after reputation stability is proven. See "Interaction with #17" below.

---

## Phase 1: Installer — entry experience, highest impact for developers

**Goal:** Replace "unzip and run a loose exe" with a real install experience:
Start Menu entry, optional desktop shortcut, an uninstaller in Add/Remove
Programs, clean upgrade path, and no elevation needed.

**Key payoff from v0.3:** the writable-path consolidation means iMirror no
longer writes into its own program directory. That unlocks a **per-user install
to `%LOCALAPPDATA%` with no UAC elevation** — the smoothest possible install
flow. This is a direct dividend of the work just merged.

For developers, the install experience is the daily **entry ritual** —
how smooth it is shapes the likelihood they keep using the tool. An installer
directly improves that, while signing does not.

### Framework decision: **Inno Setup** (confirmed)

iMirror uses **Inno Setup**, paired with a lightweight in-app updater (Phase 2).
The alternative, Velopack, was evaluated and **deferred** — see rationale below.

**Why Inno Setup over Velopack for iMirror:** Velopack's two headline features
— unified *cross-platform* packaging and *delta* updates — are both nullified
by iMirror's actual profile:

- **iMirror is Windows-only**, so Velopack's cross-platform model is dead weight.
- **Updates are infrequent**, so Velopack's delta packages have near-zero ROI
  (re-downloading the full ~150MB self-contained package a few times a year is
  fine; the stateful delta-release-repo maintenance is pure overhead).

With both headline benefits stripped, Velopack's remaining costs still apply:
app-lifecycle coupling to its SDK, an imposed `current`-folder install layout,
a version-pinned CLI, and lock-in. For an infrequent-update app the cost timing
is wrong — you pay the coupling tax **continuously** (every build, every startup,
and again during the net10 migration, #17) but collect the "free updater"
benefit only a **few times a year**. Inno + a ~200-line in-app updater inverts
that: a one-time write you barely touch, with no lifecycle coupling and full
reversibility (you can still adopt Velopack later if release cadence rises).

**Revisit Velopack if** release cadence becomes frequent (e.g. monthly, chasing
iOS/AirPlay changes). At that point delta updates over a 150MB package become
genuinely valuable and can justify the coupling. Today they do not.

| Task | Title | Effort | Handoff |
|---|---|---:|---|
| inst-per-user-localappdata | Per-user install to `%LOCALAPPDATA%`, no UAC (`PrivilegesRequired=lowest`) | M | codex-backend |
| inst-shortcuts | Start Menu + optional desktop shortcut | S | codex-backend |
| inst-uninstaller | Uninstaller registered in Add/Remove Programs | S | codex-backend |
| inst-upgrade-path | Clean upgrade from zip → installer; close+restart running app via Inno restart manager | M | codex-backend |
| inst-firewall-handoff | Keep firewall rule as the existing in-app manual flow | S | codex-backend |
| inst-ci-iscc | Compile `.iss` with `ISCC.exe` in the release workflow; emit installer as release asset (keep zip as secondary) | M | codex-backend |
| inst-uninstall-data-policy | Decide: uninstall removes `%LOCALAPPDATA%\iMirror` data or preserves it | S | human-decision |

### Implementation notes
- **No elevation:** per-user install + per-user firewall stays manual (existing
  in-app remediation). Avoid elevated firewall helper — explicitly cut and a
  separate trust/security project.
- **Portable fallback:** keep publishing the zip alongside the installer. Power
  users, CI, and soak workflows still consume the zip. No regression on day one.
- **Data on uninstall:** default to **preserving** `%LOCALAPPDATA%\iMirror`
  (settings/logs). Surprising data deletion feels untrustworthy. Optional
  "also remove my settings" checkbox if desired.
- **Upgrade story:** if a user has the v0.3 zip installed somewhere and
  upgrades to the v0.4 installer, their settings should migrate to
  `%LOCALAPPDATA%\iMirror` seamlessly (already handled by v0.3's
  `MigrateLegacySettingsIfNeeded()`).

### Acceptance criteria
- [ ] Double-click installer → app installed per-user with **no UAC prompt**.
- [ ] Start Menu entry + uninstaller in Apps & Features.
- [ ] Uninstall removes program files; user data preserved per policy.
- [ ] Clean upgrade from earlier zip versions.

---

## Phase 2: Auto-Update — keeps developers on the latest build

**Goal:** Developers get new versions automatically (or one-click) instead of
manually hunting for new zips or GitHub Releases.

**Approach: a lightweight in-app updater** — not a framework. The app checks the
GitHub Releases API on startup, and if a newer release exists, downloads the new
Inno `Setup.exe` and runs it. This is the **documented Inno update pattern**
(app detects/downloads a new Setup and runs it `/SILENT`), not a workaround.
Inno's restart-manager (`CloseApplications`) handles the running-process file
lock and relaunch. Full design lives in **`docs/specs/v04-updater-design.md`**.

| Task | Title | Effort | Handoff |
|---|---|---:|---|
| upd-feed-source | Read latest release via GitHub Releases API (reuses existing tag pipeline) | S | codex-backend |
| upd-check-on-startup | Background check on launch + manual "Check for updates" in Settings | M | codex-backend |
| upd-notify-ux | Non-blocking "vX.Y.Z available" notice; dismissible; throttled (≤1/day) | M | codex-backend |
| upd-download-run | Download new `Setup.exe` to temp, verify size/sha, launch, exit app | M | codex-backend |
| upd-channel-policy | Stable vs prerelease; respect `prerelease` tag flag | S | human-decision |
| upd-failsafe | Any failure (network, corrupt, blocked) leaves user on current version | M | codex-backend |

### Implementation notes
- **Reuse what exists:** v0.2 already has a manual "check for updates" path, and
  the release pipeline publishes GitHub Releases with `v*.*.*` tags and the
  `prerelease` flag — that is a usable update feed with no new infrastructure.
- **UX:** background check + notify on startup; user clicks to update. No forced
  silent updates. A manual Settings button checks immediately on demand.
- **Footgun handling:** the self-replacing-exe and AV-heuristic risks are
  mitigated by Inno's restart manager (clean close/relaunch) and, later, Phase 3
  signing. Low risk for a developer audience; see the design doc for specifics.
- **Reversibility:** this updater is intentionally small and self-contained. If
  release cadence later justifies Velopack's delta model, swapping it in is a
  contained change because nothing else depends on the updater's internals.

### Acceptance criteria
- [ ] App detects a newer GitHub Release on startup (and on manual check).
- [ ] User sees a non-blocking notice + one-click "Update" action.
- [ ] Update downloads the new Setup, verifies it, runs it, and the app relaunches.
- [ ] Any failure path leaves the user on their working version, no half-state.

---

## Phase 3: Code Signing — builds trust when expanding beyond developers

**Goal:** Build platform reputation and prepare for general-audience distribution
once the developer base proves the product.

**This phase is optional for the v0.4 timeline.** Developers will click through
SmartScreen; signing is not a blocker for adoption within that audience.
However, it should land **before** pursuing general users or enterprise
distribution.

**Decision is already made** (`docs/specs/v02-decisions.md` → "Code Signing"):
> Path forward: **Microsoft Trusted Signing** (~$9.99/month, no hardware token,
> CI-compatible) once audience warrants it.

Trusted Signing is the right pick: cloud-based, OIDC-native (no long-lived
secrets), first-class GitHub Actions integration, and Microsoft-operated CA.

| Task | Title | Effort | Handoff |
|---|---|---:|---|
| ts-account-setup | Create Azure + Trusted Signing account, certificate profile | S | human-decision |
| ts-identity-validation | Complete Microsoft identity validation (individual or org) | S–M | human-decision |
| ts-ci-oidc | Wire GitHub OIDC → Azure federated credential | M | codex-backend |
| ts-sign-step | Add signing step to `release.yml` after publish, before zip | M | codex-backend |
| ts-sign-scope | Decide: sign exe only, or all iMirror-authored managed DLLs | S | codex-backend |
| ts-verify-gate | Fail release if `signtool verify /pa` does not pass | S | codex-backend |

### Implementation notes
- **When to do it:** after the developer base is active and regular (2-3 minor
  versions shipped). SmartScreen reputation builds gradually; starting early
  means reputation accrues sooner.
- **Signing scope:** at minimum `iMirror.exe`. Recommended: all iMirror-authored
  managed assemblies (cheap, avoids audit noise). `tools/ffmpeg/bin/ffmpeg.exe`
  is third-party GPL — leave unsigned, note in `THIRD_PARTY_NOTICES.txt`.
- **Honest expectation:** signing does not grant instant SmartScreen trust. It
  builds reputation over download volume + time. Early reputation = earlier
  trust ladder-climbing. Land signing when developer adoption is proven.
- **Installer & updater signing:** if you land Phase 3, re-sign the
  installer/updater artifacts too (they benefit as much as the exe).

### Acceptance criteria
- [ ] Release artifact's `iMirror.exe` passes `signtool verify /pa /v`.
- [ ] CI authenticates to Trusted Signing via OIDC (no stored secret).
- [ ] Release fails if signing or verification fails.
- [ ] Signed binaries begin accumulating SmartScreen reputation.

---

## Interaction with Issue #17 (.NET 8 EOL, 2026-11-10)

The net10 runtime bump and this product-surface work compete for the v0.4/v0.5
window. Recommended ordering:

1. **Phase 1 (Installer)** + **Phase 2 (Auto-Update)** — land these first. Both
   are TFM-independent; the per-user/`%LOCALAPPDATA%` shape does not change with
   net10. Shipping a working installer + updater before the .NET churn gives
   developers a stable install path during net10 re-validation.
2. **net10 bump (#17)** — do the risky D3D11→D3DImage re-validation here, on an
   already-installable/updatable product, well before the 2026-11-10 deadline.
3. **Phase 3 (Code Signing)** — land after net10 is stable and you are confident
   in the developer base. SmartScreen reputation then accrues against a proven,
   LTS-backed runtime.

The hard deadline is **2026-11-10**; everything must complete before it.

---

## Decisions

**Resolved:**
- **Installer framework: Inno Setup** + lightweight in-app updater. Velopack
  deferred (its cross-platform and delta benefits are both nullified for a
  Windows-only, infrequently-updated app; revisit if cadence becomes frequent).

**Still open (need a human call before coding):**
- **Uninstall data policy:** preserve `%LOCALAPPDATA%\iMirror` (settings/logs)
  on uninstall, or remove it (with optional checkbox). *Recommended: preserve.*
- **Update channel policy:** stable-only, or opt-in prerelease channel via tag.
- **Distribution shape:** installer becomes primary asset, or publish both
  installer + zip side by side. *Recommended: both, zip secondary.*
- **Phase 3 timing:** land signing immediately after Phase 2, or wait for 2-3
  minor releases to prove developer adoption first.

## Risk Register

| Risk | Impact | Mitigation |
|---|---|---|
| Self-replacing-exe update footguns (file lock, relaunch) | Update fails mid-apply, app won't restart. | Use Inno's restart manager (`CloseApplications`); the app exits before Setup overwrites files; Setup relaunches it. Covered in the updater design doc. |
| Self-downloaded exe trips AV heuristics | Update download/run flagged as suspicious. | Low risk for a dev audience; mitigated further by Phase 3 signing. Verify size/sha of the downloaded Setup before running. |
| Update cadence rises and full re-download (~150MB) hurts | Slow, wasteful updates if releases get frequent. | Documented Velopack revisit trigger: adopt delta updates if cadence becomes frequent. Until then full download is fine. |
| net10 churn collides with product-surface work | Re-validation thrash, missed EOL deadline. | Fixed ordering: install + auto-update first (TFM-independent), then net10 (risky re-validation), then signing (reputation-building). |
| Elevated firewall helper scope creep | High-effort UAC/security project derails the milestone. | Keep firewall remediation manual/in-app; it is explicitly out of scope. |
| Signing is deferred but then forgotten | End up shipping unsigned forever, never expand beyond developer audience. | Set a trigger (e.g., "after 3 releases" or "when non-dev interest appears") to start Phase 3. Document it in the decision. |

---

## Done When

iMirror is "developer-friendly product" when a technical user can:

1. **Install it with a double-click** (Start Menu entry, uninstaller, no UAC
   friction),
2. **Receive updates automatically** without hunting for new zips or watching
   GitHub Releases,
3. optionally **trust the signed binary** when expanding beyond developer
   audience —

all three with a clear, repeatable release pipeline. That closes the remaining
~30% between the current solid engineering base and a finished product for
developer adoption.

Signing (Phase 3) can land later, when the developer base is proven and you are
ready to pursue general-audience distribution.
