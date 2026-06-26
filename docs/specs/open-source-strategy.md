# iMirror Open Source Distribution Strategy

## Executive Summary

iMirror is positioning as an open source Windows developer tool with **transparent development** (public repository, public CI, public roadmap) while maintaining clear boundaries around **audience, contributions, and licensing**. This document establishes the policies governing the public GitHub repository and external engagement.

**Key decision: iMirror adopts MIT license** for the core project, enabling maximum permissiveness for the developer audience while clearly respecting GPLv3 obligations for bundled components (FFmpeg, playfair).

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

## Licensing Decision: MIT for iMirror Core

### iMirror Core License: **MIT**

**License text** (SPDX: MIT):
```
MIT License

Copyright (c) 2024-present Balragon Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

**Action:** Add `LICENSE` file (above text) at repository root.

### Rationale for MIT

- **Permissive:** Allows commercial use, modification, and redistribution without restriction. Aligns with goal of enabling developer tools and derivative projects.
- **Clear:** Simple, non-derivative, no reciprocal (copyleft) obligations. Developers understand it immediately.
- **Copyleft boundary:** GPLv3 dependencies (FFmpeg, playfair) remain under GPL. iMirror's MIT license does **not** "upgrade" them — bundled GPL components stay GPL, documented in `THIRD_PARTY_NOTICES.txt`.
- **Practical:** Enables use in closed-source tools, commercial products, and embedded contexts. Removes friction for teams that can't adopt GPL.

### Bundled Components & Compliance

| Component | License | Role | Bundled | Source |
|---|---|---|---|---|
| **iMirror** | MIT | Core application | Yes (exe, assemblies) | Repository |
| **FFmpeg** (Gyan build) | GPLv3 | Software video fallback, AAC-ELD decode | Yes (`tools/ffmpeg/bin/`) | External release asset (pinned SHA) |
| **playfair** | GPLv3 | FairPlay reference validation | No (source in tree, not bundled in release) | `ThirdParty/playfair/` |

**Compliance flow:**
1. Released packages bundle FFmpeg executables → must include GPLv3 license text in `THIRD_PARTY_NOTICES.txt` (already done). ✅
2. Source tree contains playfair under GPLv3 → source is publicly visible; distributed under GPL. ✅
3. iMirror's MIT license is **not** a license on FFmpeg — FFmpeg users must also accept GPLv3.
4. Release artifact inspection (v0.4 acceptance criteria) confirms both licenses are present.

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

1. **LICENSE** — MIT license (see text above).
2. **CONTRIBUTING.md** — contributor guide (outlines workflow, testing, CoC).
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
- **License:** MIT (set in GitHub UI; populated from LICENSE file).
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
| **iMirror license** | MIT | Create `LICENSE` file (copy text above) |
| **Bundled component compliance** | GPLv3 + MIT | Already documented in `THIRD_PARTY_NOTICES.txt` ✅; no change needed |
| **CONTRIBUTING.md** | Missing | Create (outline contribution workflow, testing, CoC) |
| **CODE_OF_CONDUCT.md** | Missing | Add Contributor Covenant 2.1 template |
| **CHANGELOG.md** | Missing | Create with v0.2/v0.3/v0.4 summaries; update per release |
| **SECURITY.md** | Deferred | Add after Phase 3 (signing) decision to expand to general users |
| **GitHub branch protection** | Unconfigured | Add PR review + CI gate to `main` |
| **Repository topics** | Not set | Add: airplay, mirroring, windows, developer-tools, gpu-video, real-device-testing |

---

## Appendix: MIT License (Full Text)

Placed in `LICENSE` file at repository root:

```
MIT License

Copyright (c) 2024-present Balragon Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
