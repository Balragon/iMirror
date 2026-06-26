# iMirror GitHub Public Distribution Roadmap

How iMirror becomes a properly-distributed open source project on GitHub:
license, community scaffolding, a distributable product surface, repo
governance, and the first public release — sequenced as phases with live status.

This is the roadmap form of what was previously a flat strategy doc. It is
companion to `docs/specs/v04-product-surface-roadmap.md` (the installer/updater
that makes the product *distributable*) and `docs/specs/v05-plus-roadmap.md`
(what happens after the first public release).

## Where we are

```
Phase 0  Licensing & compliance baseline ............. DONE
Phase 1  Community & contribution scaffolding ........ DONE (templates optional)
Phase 2  Distributable product surface (codex v0.4) .. DONE — 1 follow-up item (§5(d) notice)
Phase 3  Repo governance & metadata .................. DONE (settings applied; GPL-3.0 detection fix in flight)
Phase 4  First public release cut (v0.4.0 tag) ....... TODO (gated on Phase 2 item + real-HW validation)
Phase 5  Post-distribution / ongoing ................. STANDING (net10, signing-deferred, security-deferred)
```

The hard external constraint behind everything past Phase 4 is **.NET 8 EOL,
2026-11-10** — see `docs/specs/v05-plus-roadmap.md`. Get the public release out,
then protect net10's validation window.

---

## Phase 0 — Licensing & compliance baseline ✅ DONE

The foundation: you cannot publicly distribute until the license is correct.
For iMirror that license is **GPLv3**, and it is *forced*, not chosen.

### iMirror is licensed under **GPLv3** (SPDX `GPL-3.0-or-later`)

`LICENSE` at the repo root is the **verbatim** GNU GPL v3 text — kept canonical so
GitHub's `licensee` matcher auto-detects **GPL-3.0**. iMirror's own copyright/grant
notice and the combined-work statement live in `NOTICE`; per-component provenance
and source offers are in `THIRD_PARTY_NOTICES.txt`.

### Why GPLv3, and why MIT/permissive is not available

MIT was the initial instinct (permissive, developer-friendly). It does not
survive contact with the codebase. Two GPLv3 components are **structurally
required** by every shipped build:

1. **playfair (GPLv3) — the decisive one.** `MacMirrorReceiver.csproj` ships the
   playfair `.c`/`.h` files into the publish output, and `AirPlayPlayFair.cs`
   **reads them at runtime** to extract the FairPlay constant tables (S-boxes,
   keys, IVs), throwing `FileNotFoundException` if they are absent. FairPlay is
   mandatory for real-device AirPlay mirroring, so iMirror is **non-functional
   without playfair**. That makes the distributed work a combined/derivative
   work, and GPL is one-directional: you may pull MIT *into* GPL, but you cannot
   wrap a required GPLv3 component in an MIT product and call the whole MIT.
   playfair is a FairPlay reverse-engineering project published GPLv3-only —
   **there is no LGPL or permissive alternative**.

2. **FFmpeg (Gyan "essentials", GPLv3) — bundled, but not the blocker.** iMirror
   invokes `ffmpeg.exe` as a *separate child process* (documented in the notices:
   "not statically or dynamically linked"). That arm's-length call does **not**
   propagate copyleft into iMirror's own code; it only imposes redistribution
   duties on the bundled binary (license text + source offer), already satisfied.
   **An LGPL FFmpeg build would therefore not unlock MIT** — it would relax a
   bundled-binary obligation that is already met, while playfair (#1) keeps the
   work GPL regardless.

**Conclusion:** GPLv3 is the only honest license for the distributed work.

### Consequences of GPLv3 for users and contributors

- **Source availability:** anyone who receives an iMirror binary is entitled to
  the complete corresponding source (the public repo + the bundled GPL component
  sources). Addressed by the public repository and `THIRD_PARTY_NOTICES.txt` §6
  source offers. Phase 4 also surfaces this in the release notes.
- **Derivatives stay GPL:** forks/redistributions must remain GPLv3; iMirror
  cannot be embedded in closed-source products.
- **Contributions are GPLv3:** by submitting a PR, contributors agree their
  changes are GPLv3 (inbound = outbound). Stated in `CONTRIBUTING.md`.
- **Appropriate Legal Notices (GPLv3 §5(d)):** because iMirror has an interactive
  WPF UI, an About/Settings notice must display copyright + "no warranty" + a
  license/source pointer. **This is the one open Phase 2 item — handed to codex
  (see Phase 2).**

### Bundled / combined components & compliance

| Component | License | Coupling | In release? | Source obligation |
|---|---|---|---|---|
| **iMirror** (first-party) | GPLv3 | — | Yes (exe, assemblies) | This repository |
| **playfair** | GPLv3 | **Combined** — sources shipped, read at runtime; required | Yes (`ThirdParty/playfair/`) | Source shipped in-tree ✅ |
| **FFmpeg** (Gyan essentials) | GPLv3 | **Aggregated** — separate child process | Yes (`tools/ffmpeg/bin/ffmpeg.exe`) | `THIRD_PARTY_NOTICES.txt` §6(d) offer ✅ |
| .NET runtime / BCL | MIT | Bundled (self-contained) | Yes | MIT, compatible into GPL |
| BouncyCastle / NAudio / WPF-UI / SharpDX | MIT | Linked | Yes | MIT, compatible into GPL |

**Compatibility note:** MIT → GPLv3 is fine (MIT is GPL-compatible; those
components keep their MIT notices in `THIRD_PARTY_NOTICES.txt` while the combined
work is GPLv3). The reverse — GPL → MIT — is what is impossible here.

### Path back to permissive (if ever wanted)

The *only* way iMirror could become permissive is to **remove the playfair
dependency** (re-implement or drop FairPlay) **and** swap FFmpeg to an LGPL
build. Removing FairPlay would break real-device AirPlay mirroring, so this is
not a near-term option. Until then, GPLv3 is correct and final. Documented so the
question is not re-litigated.

**Status:** `LICENSE` (verbatim GPLv3), `NOTICE` (iMirror copyright + combined-work
statement), `THIRD_PARTY_NOTICES.txt` (playfair combined-work note resolved), and
README License section are all in place. ✅

---

## Phase 1 — Community & contribution scaffolding ✅ DONE

The files a public contributor expects to find, and the contribution rules.

| File | Status | Purpose |
|---|---|---|
| `LICENSE` | ✅ | Verbatim GPLv3 (canonical for GitHub detection) |
| `NOTICE` | ✅ | iMirror copyright + grant + combined-work statement |
| `CONTRIBUTING.md` | ✅ | Workflow, testing, real-device template, inbound=GPLv3 |
| `CODE_OF_CONDUCT.md` | ✅ | Contributor Covenant 2.1 |
| `CHANGELOG.md` | ✅ | v0.2/v0.3/v0.4 history + roadmap; update per release |
| `README.md` License + Contributing sections | ✅ | Discoverability |
| `.github/ISSUE_TEMPLATE/*`, `PULL_REQUEST_TEMPLATE.md` | ⚪ Optional | Nudge GPU/device info into bug reports; defer until inbound issue volume justifies |

### Contribution model (the rules these files encode)

- **Open to external PRs** — anyone with a GitHub account; fork → branch → PR
  against `main`; CI must pass; maintainer review.
- **Inbound = outbound GPLv3** — stated in `CONTRIBUTING.md`.
- **Scope boundaries:**
  - *In scope:* bug fixes, performance, documentation, test coverage.
  - *Out of scope (maintainer-led):* non-Windows platform support, new rendering
    backends, GPU-stack dependency swaps (SharpDX/MediaFoundation/WASAPI), public
    API/CLI redesign. These fragment the GPU-path testing surface.
- **Real-device testing** — GPU-path changes must be validated on real hardware
  (device, driver, latency ≤150ms, soak); the `CONTRIBUTING.md` template captures
  this. Contributors without hardware say so; maintainers validate before merge.

**Status:** scaffolding complete; issue/PR templates intentionally deferred.

---

## Phase 2 — Distributable product surface (codex v0.4) ✅ DONE · 1 follow-up

"Open source on GitHub" is not just source — it is something a user can install
and keep updated. v0.4 (codex, commit `f36b238`) delivered exactly that:

| Delivered | Where |
|---|---|
| Inno Setup installer, per-user `%LOCALAPPDATA%\Programs\iMirror`, no UAC | `installer/iMirror.iss`, `scripts/build-installer.ps1` |
| Restart-manager close+relaunch; shared mutex `Local\iMirror.App` | `.iss` `AppMutex` ↔ `AppUpdateConstants.ApplicationMutexName` |
| Lightweight in-app updater (GitHub Releases API, semver, size/SHA verify) | `UpdateService.cs`, `UpdateInfo.cs`, `UpdateLauncher.cs`, `SemanticVersion.cs` |
| Deterministic Setup asset name `iMirror-<version>-setup.exe` | `AppUpdateConstants.SetupAssetNameForVersion` ↔ `.iss` `OutputBaseFilename` |
| Update UI: startup notice + Settings "Check"/"Install update" | `MainWindow.*`, `SettingsWindow.xaml(.cs)` |
| Release pipeline emits installer + `SHA256SUMS` alongside the zip | `.github/workflows/release.yml` |
| Updater tests | `MacMirrorReceiver.Tests/UpdateServiceTests.cs` |

This matches `docs/specs/v04-updater-design.md`; the shared constants
(mutex name, asset naming) are consistent across app and installer. ✅

### ▶ Open codex item: GPLv3 §5(d) in-app legal notice

**Handed to the v0.4 codex batch** (codex owns the WPF Settings surface). Small,
self-contained, and a genuine GPLv3 obligation for an interactive-UI program.

- **What:** display "Appropriate Legal Notices" in the app — copyright line, an
  explicit **no-warranty** statement, that it is licensed under **GPLv3**, and
  **where to get the source** (the GitHub repo) and the license.
- **Where:** the Settings window footer already shows `VersionTextBlock` next to
  the "Check"/"Releases" controls (`SettingsWindow.xaml`). Add the notice there
  (e.g., a short muted `TextBlock` under the version line, with a `Hyperlink` to
  the repo/LICENSE), or an "About" expander in the same footer. Reuse the
  existing `MutedTextBrush`/`Hyperlink` styling and the
  `UpdatesHyperlink_RequestNavigate` pattern already in `SettingsWindow.xaml.cs`.
- **Suggested copy:**
  > iMirror © 2024–present Balragon Contributors. Licensed under GPLv3 — provided
  > with **no warranty**. Source and license: github.com/Balragon/iMirror
- **Constants:** `AppUpdateConstants.GitHubReleasesUrl` / repo URL already exist;
  reuse rather than hard-code.
- **Acceptance:**
  - [ ] Settings shows copyright + no-warranty + "GPLv3" + a working link to the
        repository (source) and the license.
  - [ ] No new strings hard-coded that duplicate existing `AppUpdateConstants`.
  - [ ] Wording matches `LICENSE`/`THIRD_PARTY_NOTICES.txt` (no "MIT" anywhere).

**Why it's in this phase:** it is the last piece of the *distributed product*
that the license requires; Phase 4 (public release) should not ship without it.

---

## Phase 3 — Repo governance & metadata ✅ DONE (settings applied)

Not code — GitHub repository configuration the maintainer applies once. Applied
via the GitHub web UI.

- **Branch protection on `main`:** ✅ require a PR before merging + CI
  (build/test) green; admins may bypass for emergency releases.
- **Repository metadata:**
  - *Description:* ✅ "Windows AirPlay mirroring receiver for developers and QA teams."
  - *Topics:* ✅ `airplay`, `mirroring`, `windows`, `developer-tools`, `gpu-video`,
    `real-device-testing`.
  - *License:* ⚠️ GitHub initially detected `LICENSE` as "Other" (NOASSERTION)
    because the iMirror preamble prepended to the GPL text dropped the `licensee`
    match below threshold. **Fix:** `LICENSE` reduced to verbatim GPLv3, notice
    moved to `NOTICE` — once that lands on `main`, detection should resolve to
    **GPL-3.0**. Confirm in the About sidebar after merge.
  - *Discussions:* off for now (issues only). *Wikis:* off (use `docs/`).
- **Collaborator roles:** maintainers merge/administer; external contributors via
  forks + PRs (no direct push); add a triage role only if issue volume warrants.
- **Visibility:** repository is public; CI/SBOM/release logs are public. No
  change — just confirm.

**Not set up (intentionally):** project board (use GitHub Milestones linked to
the `docs/specs/*roadmap.md` files), GitHub Sponsors (no donation model).

---

## Phase 4 — First public release cut (v0.4.0 tag) 🔲 TODO · codex

The actual "publicly distribute" moment. Gated on the Phase 2 §5(d) item and a
real-hardware validation pass. **Handed to codex** — full execution note (file
pointers, §5(d) UI placement, validation loop, release-notes checklist) in
`docs/specs/v04-phase4-codex-handoff.md`.

1. **Pre-cut gates:**
   - Phase 2 §5(d) in-app notice merged.
   - Real-hardware soak passes on the build to be released
     (`scripts/soak-gate.ps1`; see `docs/specs/v05-plus-roadmap.md` — the gate
     has tooling but must actually *run*).
   - Installer + updater end-to-end validated on Windows: install → run →
     in-app "Check" finds a newer release → download verifies size/SHA → restart
     manager relaunches the new build → settings/logs preserved.
2. **Tag `v0.4.0`** on `main`. `release.yml` builds and uploads:
   - `iMirror-0.4.0-setup.exe` (installer),
   - `iMirror-0.4.0-win-x64.zip` (portable),
   - `SHA256SUMS`, and the CycloneDX SBOM.
3. **Release notes** must include the **GPLv3 source/license statement** and link
   `THIRD_PARTY_NOTICES.txt` (the §6 corresponding-source offer travels with the
   binary). Note the build is **unsigned** (SmartScreen warning expected for the
   developer audience).
4. **Post-cut:** verify the published `releases/latest` is what the in-app updater
   resolves against (stable channel, prereleases excluded).

**Output:** iMirror is installable, self-updating, and license-compliant from a
public GitHub Release — the definition of "distributed open source" for this app.

---

## Phase 5 — Post-distribution / ongoing ⏩ STANDING

Sequenced in `docs/specs/v05-plus-roadmap.md`; summarized here for the public-
distribution lens.

- **Release cadence:** stable `v0.X.Y` on `main`, ~1–2/quarter to v1.0;
  prereleases `v0.X.Y-preN` flagged on GitHub and excluded from the updater's
  stable channel. No breaking changes within a `0.X` series without release-note
  callout.
- **net10 migration (v0.5, REQUIRED before 2026-11-10):** the only milestone with
  a hard deadline. Protect its real-hardware re-validation window; do not let
  Phase 4 polish eat it.
- **Code signing — DEFERRED.** iMirror's confirmed audience is technical/developer
  users who click through SmartScreen, so unsigned public releases are acceptable.
  Signing is **not scheduled**; un-defer only on an explicit decision to target a
  general (non-developer) audience. See `v04-product-surface-roadmap.md` Phase 3.
  Repository visibility and distribution do not depend on it.
- **SECURITY.md — DEFERRED (tracks with signing).** AirPlay surface is mDNS
  discovery + paired-device only (low risk). Add a disclosure policy (supported
  versions, private reporting channel, response SLA) when the audience expands
  beyond developers. Until then: report security issues privately to maintainers.
- **Transparency:** issues, Milestones (→ roadmap docs), and `CHANGELOG.md` stay
  public and current.

---

## Status dashboard

| Item | Phase | Status | Owner |
|---|---|---|---|
| GPLv3 `LICENSE` + notices + README license | 0 | ✅ Done | — |
| `CONTRIBUTING` / `CODE_OF_CONDUCT` / `CHANGELOG` | 1 | ✅ Done | — |
| Issue/PR templates | 1 | ⚪ Optional/deferred | maintainer |
| Installer + in-app updater (v0.4) | 2 | ✅ Done | codex (`f36b238`) |
| **GPLv3 §5(d) in-app notice** | 2 | 🔲 **Open — codex** | codex |
| Branch protection on `main` | 3 | ✅ Done | maintainer (GitHub) |
| Repo description / topics | 3 | ✅ Done | maintainer (GitHub) |
| GPL-3.0 license auto-detection (LICENSE/NOTICE split) | 3 | 🔧 In flight | — |
| Real-hardware soak + updater E2E validation | 4 | 🔲 TODO | codex (Windows + device) |
| Cut `v0.4.0` public release (GPL notes) | 4 | 🔲 TODO | codex |
| net10 migration (before EOL) | 5 | ⏩ Scheduled | — |
| Code signing / SECURITY.md | 5 | ⏸ Deferred | — |

---

## Appendix: license files

- **`LICENSE`** (repository root) — the **verbatim** GNU GPL v3 text, kept
  canonical so GitHub auto-detects the license. SPDX: `GPL-3.0-or-later`.
- **`NOTICE`** (repository root) — iMirror's copyright, the GPLv3 grant, the
  no-warranty statement, and the combined-work explanation (playfair + bundled
  FFmpeg) that was previously prepended to `LICENSE`. Points to `LICENSE` and
  `THIRD_PARTY_NOTICES.txt`.
- **`ThirdParty/playfair/LICENSE.md`** — the same GPLv3 text as shipped with the
  playfair component (verbatim, markdown); the root `LICENSE` is derived from it.
- **`THIRD_PARTY_NOTICES.txt`** — per-component provenance, coupling, and GPLv3
  §6 corresponding-source offers for the combined/bundled GPL parts, plus MIT
  notices for the permissively-licensed dependencies.
