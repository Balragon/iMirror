# iMirror Open Source Distribution Strategy

## Executive Summary

iMirror is positioning as an open source Windows developer tool with **transparent development** (public repository, public CI, public roadmap) while maintaining clear boundaries around **audience, contributions, and licensing**. This document establishes the policies governing the public GitHub repository and external engagement.

**Key decision: iMirror is licensed under GPLv3.** This is not a preference — it is *forced* by the codebase. iMirror combines two GPLv3 components that it cannot ship without: the **playfair** FairPlay sources (read at runtime; FairPlay is required for real-device mirroring) and a bundled **GPLv3 FFmpeg** build. A required GPLv3 component makes the distributed work a GPLv3 combined work; a permissive (MIT) license on the whole would misrepresent that. See "Licensing Decision" below for the full analysis, including why an LGPL-FFmpeg swap would *not* unlock MIT.

---

## Repository Visibility & Access Model

### Public Repository
- **Repository:** `github.com/Balragon/iMirror` is **publicly visible** (anyone can clone, fork, browse code).
- **Rationale:** Transparency attracts developers, enables feedback loops, and demonstrates active development. Windows-only tooling carries minimal supply-chain risk, and the technical-audience positioning self-selects against drive-by issues.
- **Current CI:** GitHub Actions workflows (CI, SBOM, release) are publicly logged and visible to anyone.
- **Branches:** `main` is the stable/released branch; feature branches are development-in-progress; releases are tagged.

### Read Access
- Fully public; no authentication needed.

### Write Access (Contributions)
- **Core maintainers** (Balragon team) have direct push and merge rights on `main`.
- **External contributors** submit PRs; see "Contribution Model" below.

---

## Licensing Decision: GPLv3 for iMirror

### iMirror License: **GPLv3** (forced by required GPL components)

**Action:** `LICENSE` at the repository root is the full GNU General Public
License v3 text, prefixed with iMirror's copyright/grant notice and a pointer to
`THIRD_PARTY_NOTICES.txt`. SPDX: `GPL-3.0-or-later`.

### Why GPLv3, and why MIT is not available

MIT was the initial instinct (permissive, developer-friendly). It does not
survive contact with the codebase. Two GPLv3 components are **structurally
required** by every shipped build:

1. **playfair (GPLv3) — the decisive one.** `MacMirrorReceiver.csproj` ships the
   playfair `.c`/`.h` files into the publish output, and
   `AirPlayPlayFair.cs` **reads them at runtime** to extract the FairPlay
   constant tables (S-boxes, keys, IVs), throwing `FileNotFoundException` if they
   are absent. FairPlay is mandatory for real-device AirPlay mirroring, so iMirror
   is **non-functional without playfair**. That is a combined/derivative work, and
   GPL is one-directional: you may pull MIT *into* GPL, but you cannot wrap a
   required GPLv3 component in an MIT product and call the whole MIT. playfair is a
   FairPlay reverse-engineering project published GPLv3-only — **there is no LGPL
   or permissive alternative**.

2. **FFmpeg (Gyan "essentials", GPLv3) — bundled, but not the blocker.** iMirror
   invokes `ffmpeg.exe` as a *separate child process* (documented in the notices:
   "not statically or dynamically linked"). That arm's-length call does **not**
   propagate copyleft into iMirror's own code; it only imposes redistribution
   duties on the bundled binary (license text + source offer), already satisfied.
   **An LGPL FFmpeg build would therefore not unlock MIT** — it would relax a
   bundled-binary obligation that is already met, while playfair (#1) keeps the
   work GPL regardless.

**Conclusion:** GPLv3 is the only honest license for the distributed work. A bare
MIT `LICENSE` would assert freedoms the playfair dependency does not grant. The
"permissive developer tool" positioning is simply unavailable unless FairPlay is
re-implemented without playfair — see "Path back to permissive" below.

### Consequences of GPLv3 for users and contributors

- **Source availability:** anyone who receives an iMirror binary is entitled to
  the complete corresponding source (the public repo + the bundled GPL component
  sources). Already addressed by the public repository and `THIRD_PARTY_NOTICES.txt`
  §6 source offers.
- **Derivatives stay GPL:** forks/redistributions of iMirror (or its binaries)
  must remain GPLv3. iMirror cannot be embedded in closed-source products.
- **Contributions are GPLv3:** by submitting a PR, contributors agree their
  changes are licensed GPLv3 (inbound = outbound). Stated in `CONTRIBUTING.md`.
- **Appropriate Legal Notices (GPLv3 §5(d)):** because iMirror has an interactive
  WPF UI, an About/Settings notice should display copyright + "no warranty" + a
  pointer to the license. The existing Settings/Updates surface is the natural
  home; track as a small follow-up.

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

The *only* way iMirror could become MIT/permissive is to **remove the playfair
dependency** (re-implement or drop FairPlay) **and** swap FFmpeg to an LGPL
build. Removing FairPlay would break real-device AirPlay mirroring, so this is
not a near-term option. Until then, GPLv3 is correct and final. Documented so the
question is not re-litigated.

---

## Contribution Model

### Philosophy

iMirror is **open to external contributions** with a clear scope: bug fixes, performance improvements, and documentation. The bar for acceptance is higher for architectural changes that conflict with the Windows/GPU focus or demand platform-specific re-testing.

### Who Can Contribute

- **External developers:** anyone with a GitHub account. No prior approval or membership required.
- **Pull requests:** fork → branch → PR against `main`.
- **Expectations:** (see "Code of Conduct" below).

### Contribution Types & Acceptance Criteria

| Type | Bar | Examples | Acceptance |
|---|---|---|---|
| **Bug fixes** | Medium | Crash on D3D loss, latency regression, FFmpeg fallback issue | Reviewed for correctness; must include test or repro; CI must pass |
| **Performance** | Medium | Reduce frame-copy overhead, lower latency, memory efficiency | Benchmark required (see `tools/LatencyAcceptanceReport`); real-device validation on branch before merge |
| **Documentation** | Low | Expand README, add architecture notes, clarify validation steps | Reviewed for accuracy; should link to source code locations |
| **Architectural change** | High | Platform abstraction, new rendering backend, redesigned audio stack | Discussion in issue first; Windows-only focus must be preserved; GPU-path testing overhead scoped |
| **Feature request** | Case-by-case | New protocol support, alternative codecs, AirPlay 2 | Discuss in issue; scope against roadmap (v0.4/v0.5 milestones); GPU implications assessed |

### Contribution Workflow

1. **Open an issue** (for bugs, features, or design discussion) or **fork & branch**.
2. **Create a PR** against `main`. Reference related issues.
3. **CI runs automatically** (build, test, SBOM). Must pass before review.
4. **Code review** by maintainers. For GPU/video changes, author may be asked for real-device test results.
5. **Approval & merge** — typically `squash and rebase` to keep history clean.

### Pre-Contribution Discussion

For **major changes** (protocol changes, new rendering modes, removal of FFmpeg fallback), open an issue first. This avoids wasted effort on PRs that may not align with the product direction.

### Testing Requirements for Contributors

- **Build:** `dotnet build` must pass.
- **Unit tests:** run existing test suite; add tests for new logic.
- **GPU-path changes:** author should validate on real hardware (Windows 10+, NVIDIA or Intel iGPU preferred). Report: device model, driver version, test result.

---

## Code of Conduct

iMirror adopts the **Contributor Covenant 2.1** (standard in-community CoC). Key expectations:

- **Respectful:** critique ideas, not people. Assume good intent.
- **Inclusive:** welcome contributors from all backgrounds.
- **On-topic:** discussions stay focused on iMirror development; off-topic arguments are closed.
- **Violations:** reported to maintainers; enforcement may include warning, temporary block, or permanent ban.

**File:** `CODE_OF_CONDUCT.md` (add to repository root; standard Contributor Covenant template).

---

## Public Release & Version Management

### Release Cadence

- **Stable releases:** tagged `v0.X.Y` on `main`. Infrequent (~1-2 per quarter until reach v1.0).
- **Prerelease:** tagged `v0.X.Y-preN`. Marked as prerelease on GitHub; excluded from auto-update stable channel (v0.4+).
- **No breaking changes** within a `0.X` series unless documented in release notes.

### Public Release Artifacts

GitHub Releases (per `.github/workflows/release.yml`):
1. **iMirror-v0.X.Y-setup.exe** — Inno Setup installer (v0.4+). Requires approval on SmartScreen for now.
2. **iMirror-v0.X.Y.zip** — portable self-contained package. Always available (v0.3+).
3. **SBOM (CycloneDX JSON)** — published for supply-chain transparency (v0.3+).
4. **SHA256SUMS** — integrity hashes for installer and zip (v0.4+).

### Development Transparency

- **Issues:** tracked publicly (GitHub Issues). Open issues visible to anyone; includes planned work (v0.4, v0.5, future).
- **Milestones:** roadmap milestones on GitHub Milestones link to docs/specs/v04-product-surface-roadmap.md etc.
- **Release notes:** GitHub Releases page + CHANGELOG (manual).

---

## External Contributor Considerations

### Scope Boundaries

**In scope:**
- Bug fixes to existing features (AirPlay protocol, video/audio decode, UI).
- Performance optimization (latency, CPU/GPU efficiency).
- Documentation improvements.
- Test coverage expansion.

**Out of scope (likely require maintainer involvement):**
- New rendering backends (would fragment GPU-path testing).
- Non-Windows platform support (project charter is Windows).
- Dependency changes that affect GPU stack (SharpDX, Media Foundation, WASAPI).
- CLI restructure or public API design (product surface is WPF GUI).

### Hardware Requirements for Contributors

Testing GPU-path changes requires:
- Windows 10 or later.
- GPU with hardware video decode (NVIDIA, Intel iGPU; AMD also supported).
- Real AirPlay sender (iPhone, Mac, iPad).
- Validation against soak gate (if perf/latency claims made).

For contributors without access to real hardware, maintainers will test on their machines before merge. Clearly state "hardware available: none" in PR description if so.

---

## Security & Vulnerability Disclosure

### No Security.md Yet (Add Later)

When iMirror reaches general-audience distribution or handles untrusted network data (AirPlay is mDNS discovery + paired device only, low risk), add a `SECURITY.md` file with:
- **Supported versions:** only latest `0.X.Y` branch.
- **Reporting:** private disclosure to maintainers (email, not public issue).
- **Response SLA:** 30 days to fix or public notification.

**For now (v0.3/v0.4):** no formal security policy; report security issues directly in a private discussion or email.

---

## Contributing Guidelines & Documentation

### Files to Add (Immediate)

1. **LICENSE** — GPLv3 full text + iMirror notice (done; see "Licensing Decision").
2. **CONTRIBUTING.md** — contributor guide (workflow, testing, CoC, inbound=GPLv3).
3. **CODE_OF_CONDUCT.md** — Contributor Covenant 2.1 standard template.
4. **CHANGELOG.md** — releases (v0.2, v0.3, v0.4 summaries); updated per release.

### Files to Add (When General-Audience Expansion is Decided)

- **SECURITY.md** — vulnerability disclosure policy (un-defer with Phase 3 signing).

### Existing Documentation (Keep Updated)

- **README.md** — user quick-start (already good; extend with "Contributing" link).
- **docs/architecture.md** — developer reference for internals (already exists; refresh per major version).
- **docs/validation.md** — acceptance & soak procedures (already exists; reference in CONTRIBUTING).
- **docs/release.md** — release packaging (already exists; update for new installer v0.4).

---

## Access Control & Branch Protection

### `main` Branch Protection

Enforce:
- Require PR review (≥1 approval from maintainer).
- Require CI (build, test) to pass.
- Require up-to-date with `main` before merge.
- Allow admins to bypass (for emergency releases).

### Collaborator Roles

- **Maintainers** (core team): can merge PRs, push directly to branches, administer repository.
- **Triage** (future, if needed): can label/close issues, cannot merge.
- **External contributors:** create forks, submit PRs; no direct push access.

---

## Repository Metadata & Discoverability

### GitHub Repository Settings

- **Description:** "Windows AirPlay mirroring receiver for developers and QA teams."
- **URL:** https://github.com/Balragon/iMirror
- **Topics:** `airplay`, `mirroring`, `windows`, `developer-tools`, `gpu-video`, `real-device-testing`.
- **License:** GPL-3.0 (GitHub auto-detects from the LICENSE file).
- **Visibility:** Public.
- **Discussions:** disabled (issues only for now).
- **Wikis:** disabled (use docs/ folder).

---

## Not Public (Yet)

- **Private discussions:** none; all roadmap/planning is in issues or docs/.
- **Board:** no public project board; reference GitHub Milestones instead.
- **Sponsors:** no GitHub Sponsors setup (no donation model yet).

---

## Interaction with v0.4 Signing Decision

**Code signing** (v0.4 Phase 3) is currently **deferred** pending a product decision to expand beyond technical users. The open source strategy **does not depend on signing**:

- **Signing un-defer trigger:** explicit decision to target general-audience distribution (not developer-only).
- **Until then:** iMirror remains unsigned on public GitHub Releases. Technical users click through SmartScreen.
- **Repository visibility:** unaffected; repository stays public regardless of signing status.

---

## Summary & Next Steps

| Item | Status | Action |
|---|---|---|
| **Repository visibility** | Public | No change; confirm in GitHub Settings |
| **iMirror license** | GPLv3 | `LICENSE` (GPLv3 + notice) added ✅ |
| **Bundled/combined compliance** | GPLv3 (playfair combined, FFmpeg aggregated) + MIT deps | Documented in `THIRD_PARTY_NOTICES.txt`; playfair combined-work note updated ✅ |
| **CONTRIBUTING.md** | Added ✅ | Includes inbound=GPLv3 (DCO-style) statement |
| **CODE_OF_CONDUCT.md** | Added ✅ | Contributor Covenant 2.1 |
| **CHANGELOG.md** | Added ✅ | v0.2/v0.3/v0.4 summaries; update per release |
| **GPLv3 §5(d) in-app notice** | Follow-up | Add copyright + no-warranty + license pointer to Settings/About |
| **SECURITY.md** | Deferred | Add after general-audience decision (tracks with signing) |
| **GitHub branch protection** | Unconfigured | Add PR review + CI gate to `main` |
| **Repository topics** | Not set | airplay, mirroring, windows, developer-tools, gpu-video, real-device-testing |

---

## Appendix: License Files

- **`LICENSE`** (repository root) — the full GNU General Public License v3 text,
  prefixed with iMirror's copyright/grant notice and a pointer to
  `THIRD_PARTY_NOTICES.txt`. SPDX identifier: `GPL-3.0-or-later`.
- **`ThirdParty/playfair/LICENSE.md`** — the same GPLv3 text as shipped with the
  playfair component (verbatim, markdown-formatted); the root `LICENSE` is derived
  from it.
- **`THIRD_PARTY_NOTICES.txt`** — per-component provenance, coupling, and GPLv3
  §6 corresponding-source offers for the combined/bundled GPL parts, plus the MIT
  notices for the permissively-licensed dependencies.
