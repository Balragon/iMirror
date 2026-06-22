# iMirror v0.2 Public Distribution Roadmap

## Executive Summary

iMirror is close to a usable public Windows preview, but v0.2 cannot ship until the core receiver lifecycle, D3D/live-video Settings behavior, packaging pipeline, and distribution trust story are release-safe.

v0.2 must achieve a narrow public release: a self-contained Windows build with repeatable CI, tests, packaging, signing posture, release metadata, manual hardware validation, clear firewall/tray/update guidance, and no known P0 stability or accessibility blockers. Installer, auto-update, elevated firewall helper, and hardware automation should be cut from v0.2.

## Phased Plan

## Phase 1: Release Policy And Scope Lock

**Goal:** Resolve the human decisions that determine implementation shape before engineering work fans out.

| Task | Title | Effort | Handoff |
|---|---:|---:|---|
| distribution-policy | Decide public distribution policy | S | human-decision |
| release-posture | Choose v0.2 distribution posture | S | human-decision |
| distribution-trust-decision | Decide Public Distribution Trust Path | M | human-decision |
| signing-path | Choose code signing service | M | human-decision |
| version-policy | Choose release and version policy | S | human-decision |
| ffmpeg-source | Choose FFmpeg redistribution source | S | human-decision |
| audio-override-decision | Decide audio discovery override semantics | M | human-decision |
| settings-modal-strategy | Decide Settings Modal Strategy | M | human-decision |
| settings-airspace-design | Choose Settings overlay airspace strategy | L | frontend-design |
| lifecycle-ux-spec | Define tray and lifecycle behavior | S | frontend-design |
| windows-visual-direction | Define Windows 11 Visual Direction | M | frontend-design |

**Rationale:** These choices affect binary trust, release naming, FFmpeg packaging, runtime defaults, Settings architecture, tray behavior, and visual implementation. Starting coding before these are locked risks rework in the most expensive areas.

## Phase 2: P0 Runtime Stability Foundation

**Goal:** Make receiver lifecycle, shutdown, disconnect, software fallback, and stale-session behavior deterministic before UI polish or packaging.

| Task | Title | Effort | Handoff |
|---|---:|---:|---|
| airplay-session-ended | Add guarded AirPlay session lifecycle | L | codex-backend |
| disconnect-cleanup-hardening | Harden DisconnectAsync cleanup | M | codex-backend |
| window-closing-hardening | Harden Window_Closing cleanup | M | codex-backend |
| force-software-runtime | Honor Force software at runtime | M | codex-backend |
| stale-callback-guards | Gate stale reconnect callbacks | M | codex-backend |
| airplay-preflight-ports | Expand AirPlay preflight readiness | M | codex-backend |
| audio-override-implementation | Implement audio override semantics | S | codex-backend |

**Rationale:** Public distribution fails hardest on lifecycle bugs: abandoned sessions, broken shutdown, incorrect GPU opt-out, stale reconnect callbacks, and misleading readiness state. These must precede Settings rework and validation.

## Phase 3: Settings, Live Video, And Tray Lifecycle

**Goal:** Fix the core public UX failure modes around live video airspace, Settings modality, background receiving, and explicit exit.

| Task | Title | Effort | Handoff |
|---|---:|---:|---|
| settings-above-video | Implement Settings Above Live Video | L | codex-backend |
| settings-airspace-implementation | Implement Settings overlay fix | L | codex-backend |
| settings-modal-accessibility | Add Settings Modal Semantics | M | codex-backend |
| tray-background-receiving | Add tray icon and background receiving | M | codex-backend |
| explicit-service-shutdown | Restrict receiver teardown to explicit exit | S | codex-backend |
| active-session-hide-policy | Implement active mirroring hide policy | M | codex-backend |
| windows-lifecycle-e2e | Run Windows lifecycle E2E validation | M | codex-backend |
| gpu-settings-release-qa | Verify GPU Path Is Release-Safe | M | codex-backend |

**Rationale:** Settings cannot be occluded by the D3D child HWND, and closing/minimizing must not accidentally kill discovery or sessions. These are user-visible release blockers and should land before broader visual rework.

## Phase 4: Build, Test, Metadata, And CI

**Goal:** Create the repeatable engineering release spine: solution, tests, metadata, CI, and FFmpeg resolution.

| Task | Title | Effort | Handoff |
|---|---:|---:|---|
| solution-test-scaffold | Add solution and test project scaffold | M | codex-backend |
| release-critical-tests | Test release-critical pure logic | L | codex-backend |
| release-metadata | Stamp release metadata and provenance | S | codex-backend |
| manual-update-link | Add manual check-for-updates path | S | codex-backend |
| release-version-metadata | Make version metadata release-driven | S | codex-backend |
| unify-ffmpeg-resolution | Unify FFmpeg resolution | M | codex-backend |
| ci-build-test | Add Windows CI build and test workflow | M | codex-backend |

**Rationale:** CI and tests must exist before signed packaging and GitHub release automation. Version metadata also needs to be consistent before user-facing docs, About/version display, and public artifacts are finalized.

## Phase 5: Accessibility, Windows Visual System, And Release UX

**Goal:** Bring the app up to a credible Windows public-preview standard without expanding scope.

| Task | Title | Effort | Handoff |
|---|---:|---:|---|
| semantic-color-tokens | Replace Apple-Like Color Tokens | M | frontend-design |
| centralize-xaml-colors | Centralize Hardcoded XAML Colors | L | codex-backend |
| contrast-aa-pass | Fix Contrast Failures | M | frontend-design |
| keyboard-focus-visuals | Add Keyboard Focus Visuals | M | codex-backend |
| screen-reader-names | Add Screen Reader Names | S | codex-backend |
| settings-copy-review | Normalize Settings Interaction Copy | S | frontend-design |
| spacing-density-system | Tokenize Spacing And Density | M | frontend-design |
| empty-state-redesign | Redesign Empty State | L | frontend-design |
| empty-state-implementation | Implement Empty State Redesign | M | codex-backend |
| fluent-icon-pass | Run Fluent Icon Pass | M | frontend-design |
| theme-contrast-sanity | Check Theme And High Contrast Behavior | M | frontend-design |
| tray-lifecycle-polish | Polish tray lifecycle UX | S | frontend-design |
| manual-firewall-flow | Keep firewall remediation manual for v0.2 | S | frontend-design |
| firewall-feedback | Show firewall launch failure feedback | S | codex-backend |
| rerun-startup-diagnostics | Re-run diagnostics after firewall changes | S | codex-backend |
| public-release-copy | Review public download and trust copy | S | frontend-design |

**Rationale:** Visual polish comes after P0 runtime behavior so design work does not hide or rework unstable flows. Accessibility and contrast are not optional for public distribution, but larger visual enhancements should remain bounded.

## Phase 6: Packaging, Signing, Docs, And Release Automation

**Goal:** Produce a reproducible public artifact with provenance, checksums, signing behavior, docs, and GitHub release publication.

| Task | Title | Effort | Handoff |
|---|---:|---:|---|
| tagged-release-packaging | Add tagged release packaging workflow | L | codex-backend |
| publish-script-signing | Integrate code signing into publish script | M | codex-backend |
| github-release-publication | Publish GitHub Release from CI | M | codex-backend |
| manual-hardware-gate | Document manual hardware release gate | S | codex-backend |
| public-zip-release-docs | Update public zip release docs | S | codex-backend |
| public-release-smoke-checklist | Formalize public release smoke checklist | M | codex-backend |
| release-validation-checklist | Create v0.2 release validation checklist | M | codex-backend |

**Rationale:** Signed release automation depends on CI, metadata, FFmpeg resolution, and the signing decision. Docs and smoke checklists should be generated against the actual packaging shape, not a guessed one.

## Phase 7: Release Candidate Validation And Go/No-Go

**Goal:** Validate the release candidate on real Windows hardware and make the final public-release decision.

| Task | Title | Effort | Handoff |
|---|---:|---:|---|
| release-validation-run | Run v0.2 release validation | L | human-decision |
| rc-validation | Run v0.2 release candidate validation | M | human-decision |
| v02-ux-release-review | Run v0.2 UX Release Review | M | human-decision |
| public-package-v02 | Package public v0.2 build | M | human-decision |

**Rationale:** The final package should only be produced after manual hardware validation, signature/checksum verification, lifecycle validation, Settings/live-video QA, accessibility review, and public copy review have all passed.

## Cut From v0.2

| Task | Title | Effort | Handoff | Reason |
|---|---:|---:|---|---|
| updater-signing-strategy | Decide updater and signing path for v0.3 | M | human-decision | Keep v0.2 manual-update only. Decide updater strategy after public feedback. |
| elevated-firewall-helper-design | Design future elevated firewall helper | M | codex-backend | v0.2 should keep firewall remediation manual and avoid UAC/helper scope. |
| self-hosted-validation-runner | Add optional self-hosted validation runner | XL | codex-backend | Valuable later, but manual hardware validation is enough for v0.2. |
| settings-save-design | Separate Save from Restart in Settings | M | frontend-design | Cut unless current Settings persistence blocks release validation. |
| settings-save-implementation | Implement unified settings persistence | M | codex-backend | Cut unless validation proves data loss in common release paths. |
| audio-sync-close-save | Persist live audio sync on close | S | codex-backend | Cut unless current behavior is a confirmed release-blocking regression. |

## Parallelizable Now

Frontend-design can start immediately on:

- `windows-visual-direction` once Phase 1 scope is confirmed.
- `settings-airspace-design` in parallel with backend lifecycle hardening.
- `lifecycle-ux-spec` in parallel with backend lifecycle hardening.
- `semantic-color-tokens`, `spacing-density-system`, and `empty-state-redesign` after `windows-visual-direction`.
- `manual-firewall-flow` and `public-release-copy` once distribution policy is settled.

Codex-backend can start immediately on:

- `airplay-session-ended`
- `force-software-runtime`
- `solution-test-scaffold` after `version-policy`
- `release-metadata` after `version-policy`
- `screen-reader-names`
- `firewall-feedback`
- `manual-update-link` after `distribution-policy`

Backend work that should wait:

- `settings-above-video` / `settings-airspace-implementation` should wait for `settings-modal-strategy` and `settings-airspace-design`.
- `publish-script-signing` should wait for `signing-path`, `release-metadata`, and tagged packaging.
- `github-release-publication` should wait for packaging and signing verification.
- `release-validation-run`, `rc-validation`, and `public-package-v02` should wait for all P0 runtime, CI, packaging, and UX gates.

## Open Decisions

- Whether v0.2 ships as a self-contained public zip only, with installer and auto-update explicitly deferred.
- Whether to buy/use a public code signing certificate now, use Microsoft Trusted Signing/Azure Artifact Signing, ship unsigned, or ship self-signed.
- Whether third-party binaries such as FFmpeg remain unsigned.
- Which minimum Windows version is officially supported.
- Whether the GPU/D3D11 AirPlay path ships enabled by default.
- Which Settings-over-video strategy is selected: owned top-level Settings window, suspend/hide child video HWND, or switch presentation mode while overlays are open.
- Exact tray lifecycle behavior: close, minimize, restore, Exit, active mirroring while hidden, incoming sessions while hidden, and first-close messaging.
- Active-session hide policy: block hide, pause rendering, or allow hide with resource checks.
- Version policy: tag format, assembly/file version mapping, informational version, and commit SHA inclusion.
- FFmpeg redistribution source: pinned Gyan Essentials artifact, checksum, flavor verification, and license/notice packaging.
- `IMIRROR_AUDIO_DISCOVERY` semantics: logging-only or real audio-enabled override.
- Whether v0.2 officially supports theme switching or only avoids broken light/dark/high-contrast behavior.
- Whether Settings save/restart persistence issues are release blockers or deferred to v0.3.
- Whether v0.2 ships with manual hardware validation only or requires a self-hosted hardware runner.

## Risk Register

| Risk | Why It Can Derail v0.2 | Mitigation |
|---|---|---|
| D3D/WPF airspace blocks Settings | If Settings cannot reliably appear above live video, the app feels broken during the primary use case. | Decide strategy in Phase 1, implement in Phase 3, validate on active GPU mirroring before visual polish. |
| Receiver lifecycle cleanup remains non-idempotent | Disconnect, reconnect, sender stop, and app close bugs can leave ports, UI, audio, or GPU resources in bad states. | Prioritize session-ended, guarded cleanup, shutdown hardening, and stale callback gates before packaging. |
| Signing and trust decision slips | Public distribution cannot be finalized without knowing signed/unsigned posture, release copy, and SmartScreen expectations. | Force the decision in Phase 1 and make packaging fail clearly when expected signatures are missing. |
| CI arrives too late | Without tests and Windows CI, release automation may package unverified regressions. | Build solution/test scaffold and CI before tagged release packaging. |
| Manual hardware validation is unavailable | AirPlay, GPU decode, firewall behavior, and tray lifecycle cannot be trusted from unit tests alone. | Define the manual hardware gate early and reserve time for at least one GPU path and one software fallback run. |
| Scope creep into installer, updater, or firewall helper | These are high-effort trust/security projects that can consume the v0.2 cycle. | Cut installer, auto-update, elevated firewall helper, and hardware automation from v0.2 unless the owner explicitly re-scopes the release. |
