# iMirror v0.4 Product-Surface Roadmap (Signing · Installer · Auto-Update)

## Executive Summary

v0.2 shipped a working public preview and v0.3 finished the **deployment
foundation** (writable paths under `%LOCALAPPDATA%`, third-party compliance,
SBOM, consolidated publish policy, bundled FFmpeg license, soak gate). The
engineering substrate is now release-grade.

What is *not* yet present is the **consumer product surface** — the part a
non-technical user actually experiences:

1. The download is **unsigned**, so SmartScreen shows "Windows protected your
   PC" on first run.
2. There is **no installer** — users unzip a folder and run a loose `.exe`.
3. There is **no auto-update** — only a manual "check for updates" link.

These three are the bulk of the gap between "a tool that works" and "a product
that feels finished." This roadmap closes them in ROI order.

**Recommended sequence:** Signing → Installer → Auto-Update. Signing comes first
because every later artifact (the installer, every update payload) should ship
signed; an *unsigned installer* is the worst case — it is the very first thing
a user executes.

**Timeline constraint:** Issue #17 (.NET 8 EOL, 2026-11-10) competes for the
same v0.4/v0.5 window. Land Signing + Installer **before** the net10 runtime
churn so SmartScreen reputation starts accruing against a stable binary, then
sequence the net10 bump and re-validate. See "Interaction with #17" below.

---

## Phase 1: Code Signing — highest ROI, least effort

**Goal:** Ship signed binaries so SmartScreen stops treating iMirror as an
unknown publisher, and so the installer/updater in later phases are trusted.

**Decision is already made** (`docs/specs/v02-decisions.md` → "Code Signing"):
> Path forward: **Microsoft Trusted Signing** (~$9.99/month, no hardware token,
> CI-compatible) for v0.3 once audience warrants it.

Trusted Signing is the right pick: cloud-based (no USB HSM), first-class GitHub
Actions support via the official signing action, OIDC auth (no long-lived
secrets in the repo), and it is a Microsoft-operated CA so it chains cleanly.

| Task | Title | Effort | Handoff |
|---|---|---:|---|
| ts-account-setup | Create Azure + Trusted Signing account, certificate profile | S | human-decision |
| ts-identity-validation | Complete Microsoft identity validation (individual or org) | S–M | human-decision |
| ts-ci-oidc | Wire GitHub OIDC → Azure federated credential (no stored secret) | M | codex-backend |
| ts-sign-step | Add signing step to `release.yml` before packaging | M | codex-backend |
| ts-sign-scope | Decide & implement what gets signed (exe only vs all managed DLLs) | M | codex-backend |
| ts-verify-gate | Fail the release if `signtool verify /pa` does not pass | S | codex-backend |
| ts-release-notes | Remove "unsigned / SmartScreen warning" copy from release body | S | codex-backend |

### Implementation notes
- **Where it hooks in:** a new step in `.github/workflows/release.yml` *after*
  "Publish Windows x64 package" produces the publish output but *before* "Verify
  package zip" / zipping. Sign the binaries in the publish directory, then zip.
- **Signing scope:** at minimum `iMirror.exe`. Recommended: sign all
  iMirror-authored managed assemblies in the publish dir too (cheap, and avoids
  "some DLLs unsigned" audit noise). `tools/ffmpeg/bin/ffmpeg.exe` is
  third-party GPL — leave it unsigned and note it in `THIRD_PARTY_NOTICES.txt`.
- **Auth:** use `azure/login` + `azure/trusted-signing-action` (or
  `dotnet sign` with the Trusted Signing dlib) with GitHub OIDC federated
  credentials. Do **not** put a client secret in repo secrets.
- **Honest expectation:** signing does not grant *instant* SmartScreen trust.
  Standard certificates build reputation over download volume + time. The
  scary red warning becomes a milder/none-state as reputation accrues. The win
  is real but gradual — which is exactly why this should land first.

### Acceptance criteria
- [ ] Release artifact's `iMirror.exe` passes `signtool verify /pa /v`.
- [ ] CI release job authenticates to Trusted Signing via OIDC (no stored secret).
- [ ] Release fails closed if signing or verification fails.
- [ ] Release notes no longer warn about unsigned binaries.

---

## Phase 2: Installer — turns a folder into an installed app

**Goal:** Replace "unzip and run a loose exe" with a real install experience:
Start Menu entry, optional desktop shortcut, an uninstaller in
Add/Remove Programs, and (optionally) run-on-login.

**Key payoff from v0.3:** the writable-path consolidation means iMirror no
longer writes into its own program directory. That unlocks a **per-user install
to `%LOCALAPPDATA%` with no UAC elevation** — the smoothest possible install
flow. This is a direct dividend of the work just merged.

### Recommended approach — decide between two tracks

**Track A (recommended): Velopack** — installer *and* auto-updater in one.
Velopack (the maintained successor to Squirrel/Clowd.Squirrel) produces a
per-user installer, an uninstaller, and delta auto-updates served straight from
GitHub Releases. Choosing it here **collapses Phase 3 into Phase 2** — the
updater comes essentially for free, and both share one signed release feed.

**Track B: Inno Setup (or WiX/MSI)** — a classic, well-understood installer
with full control over shortcuts/uninstall, but **no built-in updater** (Phase 3
becomes a separate custom build). Pick this only if a self-contained,
dependency-free installer toolchain is preferred over adding an update framework.

| Task | Title | Effort | Handoff |
|---|---|---:|---|
| inst-framework-decision | Choose Velopack vs Inno/WiX (drives whether P3 is free) | M | human-decision |
| inst-per-user-localappdata | Per-user install to `%LOCALAPPDATA%`, no UAC | M | codex-backend |
| inst-shortcuts | Start Menu + optional desktop shortcut | S | codex-backend |
| inst-uninstaller | Uninstaller registered in Add/Remove Programs | S | codex-backend |
| inst-signed-installer | Sign the installer/bootstrapper (depends on Phase 1) | S | codex-backend |
| inst-firewall-handoff | Keep firewall rule as the existing in-app manual flow | S | codex-backend |
| inst-ci-artifact | Emit installer as a release asset alongside (or instead of) the zip | M | codex-backend |
| inst-uninstall-data-policy | Decide whether uninstall removes `%LOCALAPPDATA%\iMirror` data | S | human-decision |

### Implementation notes
- **No elevation:** per-user install + per-user firewall stays manual (the
  existing in-app remediation). Avoid bundling an elevated firewall helper —
  that was explicitly cut and is a separate trust/security project.
- **Keep the zip too (initially):** advanced users and the soak workflow can
  keep consuming the portable zip; add the installer as an *additional* asset
  so nothing regresses on day one.
- **Data on uninstall:** default to **leaving** `%LOCALAPPDATA%\iMirror`
  (settings/logs) on uninstall, with an optional "also remove my settings"
  checkbox. Surprise data deletion feels less trustworthy, not more.

### Acceptance criteria
- [ ] Double-click installer → app installed per-user with **no UAC prompt**.
- [ ] Start Menu entry launches iMirror; uninstaller appears in Apps & Features.
- [ ] Installer/bootstrapper is signed (Phase 1) and passes SmartScreen the
      same way the exe does.
- [ ] Uninstall removes program files; user data handled per the decided policy.

---

## Phase 3: Auto-Update — keeps users current without re-downloading

**Goal:** Users get new versions automatically (or one-click) instead of
manually re-downloading a zip.

**If Track A (Velopack) was chosen in Phase 2, this phase is ~80% done** — it
becomes "wire the update check into app startup + a Settings button, choose the
channel policy, and test the upgrade path." If Track B was chosen, this is a
from-scratch updater build.

| Task | Title | Effort | Handoff |
|---|---|---:|---|
| upd-feed-source | Use GitHub Releases as the update feed (reuses release pipeline) | S | codex-backend |
| upd-check-on-startup | Background check on launch + manual "Check for updates" in Settings | M | codex-backend |
| upd-apply-flow | Download, verify signature, stage, apply on next restart | M | codex-backend |
| upd-channel-policy | Stable vs prerelease channel; respect `prerelease` tag flag | S | human-decision |
| upd-signature-pin | Only apply updates whose binaries pass signature verification | M | codex-backend |
| upd-rollback-safety | Safe failure if an update is corrupt/blocked (stay on current) | M | codex-backend |

### Implementation notes
- **Reuse what exists:** v0.2 already added a manual "check for updates" path
  and the release pipeline already publishes GitHub Releases with a consistent
  `v*.*.*` tag scheme and `prerelease` flag — that *is* a usable update feed.
- **Trust chain:** an updater is a code-execution path. Only apply payloads that
  pass signature verification (Phase 1). This is why signing is the prerequisite
  for the *whole* roadmap, not just the first download.
- **No silent surprises:** default to "notify + one-click apply on restart"
  rather than forced silent updates, at least until reputation is established.

### Acceptance criteria
- [ ] App detects a newer GitHub Release and offers to update.
- [ ] Update payload signature is verified before it is applied.
- [ ] A corrupt/blocked update leaves the user on their working version.
- [ ] Channel policy (stable vs prerelease) is honored.

---

## Interaction with Issue #17 (.NET 8 EOL, 2026-11-10)

The net10 runtime bump and this product-surface work compete for the v0.4/v0.5
window. Recommended ordering:

1. **Phase 1 (Signing)** — independent of TFM; land it first so SmartScreen
   reputation starts accruing as early as possible.
2. **Phase 2 (Installer)** — also TFM-independent; the per-user/`%LOCALAPPDATA%`
   shape does not change with net10.
3. **net10 bump (#17)** — do the risky D3D11→D3DImage re-validation here, on an
   already-signed/installable product, well before the 2026-11-10 deadline.
4. **Phase 3 (Auto-Update)** — ideally after net10 so the first auto-delivered
   build is already on the supported runtime (avoids auto-shipping a soon-EOL
   binary).

The hard deadline is **2026-11-10**; everything above must fit before it.

---

## Open Decisions (need a human call before coding)

- **Identity validation type** for Trusted Signing: individual vs organization
  (affects the "Signed by" name users see and the validation lead time).
- **Signing scope:** main exe only, or all iMirror-authored assemblies.
- **Installer framework:** Velopack (installer + updater unified, recommended)
  vs Inno/WiX (installer only, separate updater).
- **Uninstall data policy:** keep or remove `%LOCALAPPDATA%\iMirror` on uninstall.
- **Update channel policy:** stable-only, or opt-in prerelease channel.
- **Distribution shape:** installer replaces the zip, or both ship side by side.

## Risk Register

| Risk | Impact | Mitigation |
|---|---|---|
| Trusted Signing identity validation slips | Blocks all of Phase 1 (and the trusted installer). | Start `ts-identity-validation` **first**; it is lead-time, not engineering. |
| Expecting instant SmartScreen trust from signing | Disappointment when the warning lingers. | Set expectation: reputation accrues over time/volume; signing is necessary, not instant. |
| Installer framework also owns updates (Velopack) but is a new dependency | Lock-in / learning curve. | If avoiding the dependency matters more than free updates, take Track B knowingly. |
| Auto-updater as an unverified code path | A compromised/corrupt update is a code-exec risk. | Signature-verify every payload (`upd-signature-pin`); fail safe to current version. |
| net10 churn collides with product-surface work | Re-validation thrash, missed EOL deadline. | Fixed ordering above: sign + install first, then net10, then auto-update. |
| Elevated firewall helper scope creep | High-effort UAC/security project derails the milestone. | Keep firewall remediation manual/in-app; it stays explicitly out of scope. |

---

## Done When

iMirror is "product-like" on the consumer surface when a non-technical user can:

1. Download an installer that **does not trip a scary SmartScreen warning**,
2. **Install it with a double-click** (Start Menu entry, uninstaller, no UAC),
3. and **receive updates automatically** without hunting for a new zip —

all three backed by a signed, verifiable trust chain. That is the remaining
~30% between the current solid engineering base and a finished product.
