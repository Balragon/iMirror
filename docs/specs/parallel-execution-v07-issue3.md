# Parallel Execution: v0.7 Vortice + Issue #3 A/V-Sync — Handoff to codex + measurement team

**Owner:** codex (v0.7 code + Gate A) + measurement team (Issue #3 Phase-1) · **Structure:** Parallel Phase-1 work (non-conflicting surfaces) → Serial Phase-2+ (single real device) · **Timeline:** Phase 1 starts immediately after v0.5.0 release (2026-06-27); Phase 2 after v0.7 Gate B passes.

---

## Overview: Why this structure works

v0.5.0 (net10 + SharpDX) is now the **soak-validated baseline** on real hardware. Two independent tracks must now run:

1. **v0.7 Vortice porting (codex):** swap SharpDX GPU binding to Vortice.Windows  
   - **Surface:** csproj edits, C# namespaces/API calls, build system  
   - **Gate A validation:** hosted CI (restore/build/test/publish)  
   - **Hardware requirement:** None for Gate A  

2. **Issue #3 A/V-sync hardening (measurement team):** run 30m/1h/2h soaks on v0.5.0, gather latency/drift data  
   - **Surface:** observation (logging), Settings slider exercise, WASAPI buffer monitoring  
   - **Gate B validation:** real device + GPU (measurements on known-good v0.5.0 baseline)  
   - **Compute requirement:** real AirPlay sender, real GPU, Windows device  

**These do NOT conflict:** CI (Gate A) is CPU-only on windows-latest; measurement (Phase-1) is device-only. Run in parallel, both starting now. They converge at Phase-2 when a single real device becomes the bottleneck.

---

## Timeline at a glance

```
NOW (2026-06-27, post-v0.5.0)
   ↓
┌──────────────────────────────┬──────────────────────────────┐
│ PHASE 1a (parallel, Week 1-3) │ PHASE 1b (parallel, Week 1-3) │
│ codex: Vortice port + Gate A  │ measurement: Issue #3 soaks    │
│ ✓ Add Vortice pkgs            │ ✓ 30m session                  │
│ ✓ Port 4 renderer files       │ ✓ 1h session                   │
│ ✓ Gate A (CI restore/build)   │ ✓ 2h session + reconnect       │
│ ✓ Publish/WPF verify          │ ✓ Gather drift data            │
│ ✗ Do NOT touch GPU code path  │ ✓ Propose stable default       │
│   until Gate A green          │                                │
└──────────────────────────────┴──────────────────────────────┘
         ↓                                ↓
   [Gate A passes]                  [Drift data collected]
         ↓                                ↓
         └────────────────┬───────────────┘
                          ↓
         PHASE 2 (serial, single device)
         v0.7 Gate B: real GPU A/B vs SharpDX
         Measure: latency p95 ≤ 150ms, VRAM stable
         Baseline: v0.5.0 SharpDX
                    ↓
         [Gate B passes, Vortice merged]
                    ↓
         PHASE 3 (conditional, serial)
         [Only if Phase-1b showed cumulative drift]
         Issue #3 Phase-3: add drift-correction code
         Re-validate on v0.7 Vortice baseline
```

---

## Phase 1a — v0.7 Vortice Porting + Gate A (codex responsibility)

**Owner:** codex  
**Start:** immediately (2026-06-27)  
**Duration:** ~2–3 weeks  
**Gate A location:** `.github/workflows/ci.yml` (CI runs on PR/push to branch)  
**Full spec:** `docs/specs/v07-vortice-migration.md`  

### Scope

- [ ] Add Vortice NuGet packages (3 packages: Direct3D11, Direct3D9, DXGI + Mathematics) to 5 csprojs alongside SharpDX.
- [ ] Port 4 renderer files + 2 tool probes, applying confirmed API mappings from `v07-vortice-migration.md` (§2–3).
- [ ] Feature-flag the Vortice presenter via `IMIRROR_GPU_BINDING=vortice` env var; default = SharpDX path.
- [ ] Resolve C# namespace / call-style differences (factory functions vs constructors, `out` params vs returns).

### Gate A checklist (CI-automated, no hardware)

- [ ] `dotnet restore iMirror.sln` succeeds; NU1701 (SharpDX netstandard1.1) still expected during transition.
- [ ] `dotnet build iMirror.sln -c Release` succeeds; all 4 renderer files compile.
- [ ] `dotnet test` passes (existing tests are binding-agnostic).
- [ ] `publish-win-x64.ps1 -AllowMissingFfmpeg -NoZip` succeeds; WPF assembly check passes (PresentationFramework.dll, PresentationCore.dll, WindowsBase.dll all present).

**What Gate A proves:** The code compiles, packages, and passes CI. **It does NOT prove the GPU bridge works or latency is acceptable.**

### Commit sequence (suggested from v07-vortice-migration.md §Suggested commit sequence)

1. Add Vortice packages alongside SharpDX (Gate A still green on unchanged code).
2. Port `D3D11VideoFrame.cs` (trivial shared type) — keep building.
3. **Add the Vortice `D3D11VideoProcessorD3DImagePresenter` behind the flag.** This is the load-bearing bridge you will test in Phase 2.
4. Port `D3D11SwapChainVideoPresenter.cs` and `MediaFoundationD3D11Decoder.cs`.
5. Port the two `tools/*` probes.
6. **Do NOT merge to main yet.** Wait for Phase-2 Gate B (real-hardware A/B) before the cleanup commit (drop SharpDX).

### Key constraint: No live hardware testing before Phase 2

Do **not** run `scripts/soak-gate.ps1` or any real-device testing in Phase 1a. The Vortice path validation is **gated on Phase 2 Gate B**, which runs after the code is ready and measurement team has collected baseline drift data. This boundary keeps GPU changes independently bisectable.

---

## Phase 1b — Issue #3 A/V-Sync Measurement (measurement team responsibility)

**Owner:** measurement team  
**Start:** immediately (2026-06-27), in parallel with Phase 1a  
**Duration:** ~2–3 weeks  
**Baseline:** v0.5.0 (net10 + SharpDX), confirmed working  
**Full spec:** `docs/specs/v03-av-sync-hardening.md`  

### Scope

Run long soaks on the v0.5.0 baseline to gather audio-sync stability data. No code changes yet — this is observation only.

- [ ] **30-minute** session: WASAPI buffer depth, sync-drop count, subjective lip-sync at start / 15m / 30m.
- [ ] **1-hour** session: same measurements, confirm no audio mute/stutter/over-buffer.
- [ ] **2-hour** session: same; this is where cumulative drift surfaces if it exists.
- [ ] **Reconnect cycles:** 3× disconnect/reconnect mid-session; confirm audio restores cleanly.
- [ ] **Repeat 1-hour run at 2–3 distinct `IMIRROR_AUDIO_SYNC_OFFSET_MS` values** (e.g., 120ms, 170ms, 220ms) to find the subjective sweet spot.

### Instrumentation (mostly already logged in v0.5.0)

- [ ] WASAPI buffer status lines (existing WasapiAudioOutput logging).
- [ ] Sync-drop and latency-trim events.
- [ ] RTP timestamp vs. video `SourceTimestampNanos` delta over time — **key:** is this delta growing monotonically (cumulative drift) or staying bounded (jitter only)?

### Deliverable from Phase 1b

A **drift report** answering:
- Does the RTP/video-timestamp delta grow monotonically over the 1h/2h runs?
- Which `IMIRROR_AUDIO_SYNC_OFFSET_MS` value held lip-sync best?
- Did any reconnect cycle break audio?

**If drift report shows NO cumulative drift:** Phase 3 is **skipped**. Proceed directly to Phase 2 merge (Vortice on main), then promote the best-performing offset as the stable default (Phase-2 only).

**If drift report shows cumulative drift:** Phase 3 is triggered after v0.7 Gate B passes (Phase-2 code → drift-correction logic).

---

## Phase 2 — v0.7 Gate B + Conditional Phase-3 Trigger (serial, single device)

**Owner:** codex (Gate B) + measurement team (Phase-3 decision)  
**Start:** after Phase 1a Code-ready + Phase 1b drift report  
**Duration:** ~1 week (Gate B) + ~1 week (Phase-3 if needed)  
**Location:** real device + GPU (same device used for Phase-1b soaks)  

### Gate B checklist (v0.7 Vortice validation against v0.5.0 SharpDX baseline)

**Prerequisite:** Phase-1b drift data is in hand; v0.7 code is Gate A green.

Run the exact same validation as net10 Gate B (from `docs/dotnet-strategy.md` Part B), but **A/B'd via `IMIRROR_GPU_BINDING`**:

- [ ] **Prototype gate (do this first):** Vortice `IDirect3DSurface9.NativePointer` → `D3DImage.SetBackBuffer` shows live, updating video. **If this fails, stop and report — the whole swap hinges on it.**
- [ ] Keyframe renders via the Vortice `D3D11VideoProcessorD3DImagePresenter`.
- [ ] `D3DImage` front-buffer loss/restore recovers (lock/RDP/fast-user-switch).
- [ ] GPU probes pass on Vortice builds: `D3DVideoProcessorProbe`, `D3DSharedHandleProbe`, `MediaFoundationH264Probe`, `HighResolutionD3DReplayProbe`.
- [ ] Software fallback unaffected (`IMIRROR_FORCE_SOFTWARE_VIDEO=1`).
- [ ] **≥1-hour soak (`scripts/soak-gate.ps1`):** compare Vortice vs. SharpDX  
  - No VRAM/handle growth vs. the SharpDX baseline.  
  - No stalls, no crash.  
  - **Latency p95 ≤ 150 ms** (parity with SharpDX baseline from Phase-1b).  
- [ ] **3× connect/disconnect/reconnect:** clean GPU-resource teardown on both bindings.

**Passing Gate B means:** Vortice is latency-equivalent to SharpDX, stable over 1h, and the D3DImage bridge works on real hardware.

### Merge and cleanup (after Gate B passes)

- [ ] Remove SharpDX package references and the `IMIRROR_GPU_BINDING` flag; make Vortice the only path.
- [ ] `CHANGELOG.md`: v0.7 = GPU binding modernization (SharpDX → Vortice).
- [ ] Confirm NU1701 is gone.
- [ ] Tag/release `v0.7.0` per `release.yml` (builds installer + portable zip automatically).

---

## Phase 3 — Issue #3 Drift-Correction Code (conditional, serial)

**Owner:** measurement team → codex (if triggered)  
**Start:** only after Phase-2 (v0.7 Gate B passed and merged)  
**Trigger:** Phase-1b drift report shows cumulative RTP/video-timestamp delta growth  
**Duration:** ~1–2 weeks  
**Baseline:** v0.7 Vortice (not v0.5.0 SharpDX)  

### What Phase-3 is (if it runs)

Add RTP-timestamp / video-timestamp drift-correction logic (slow-rate resampling or periodic latency re-target toward the 120–220ms window) rather than the one-shot offset from Phase-2.

### What Phase-3 is NOT

- If Phase-1b shows **no cumulative drift** (only bounded jitter), Phase-3 is **skipped entirely**. The Phase-2 stable default is sufficient.
- No Phase-3 feature flag. It either lands as code (if drift exists) or does not land at all (if drift does not exist).

### If Phase-3 runs

- [ ] Implement drift-correction in `MacMirrorReceiver.Audio` (references in `v03-av-sync-hardening.md`).
- [ ] Re-validate with a 2-hour soak on v0.7 baseline; confirm RTP/video delta stays bounded.
- [ ] Close Issue #3 (enhancement → completed).
- [ ] Tag/release `v0.8.0` or patch `v0.7.x` per cadence.

---

## GitHub tracking and closure

### Issue #17 (net10 migration)

- Status: **CLOSED** as of v0.5.0 release (2026-06-27).
- All acceptance criteria from `docs/specs/v05-net10-migration.md` met.

### v0.7 Vortice (roadmap, not an issue number yet)

- Tracked by: `docs/specs/v05-plus-roadmap.md` (v0.7 section).
- Status: Code + Gate A (Phase 1a) → Gate B (Phase 2) → Merge + release v0.7.0.
- When Phase 2 Gate B passes: merge PR, tag `v0.7.0`, update `docs/specs/v05-plus-roadmap.md` (mark v0.7 complete).

### Issue #3 (A/V-sync hardening)

- Tracked by: GitHub Issue #3 (enhancement, standing track).
- Status: Measurement phase active (Phase 1b) → Phase-2 default promotion → Conditional Phase-3 (drift-correction, only if data shows it).
- When Phase 1b drift report is in: comment on Issue #3 with findings (drift observed Y/N, best offset, reconnect behavior).
- When Phase-3 completes (if triggered): close Issue #3 with final acceptance from `v03-av-sync-hardening.md`.
- If Phase-3 is not triggered (no drift): close Issue #3 after Phase-2 default is promoted, noting "measurement shows bounded jitter only, no drift-correction needed."

---

## Reference map

| Phase | Document | Key action |
|-------|----------|-----------|
| 1a (codex) | `docs/specs/v07-vortice-migration.md` | Port 4 files + add packages, Gate A (CI) |
| 1b (measurement) | `docs/specs/v03-av-sync-hardening.md` (Phase 1) | Soaks on v0.5.0, gather drift data |
| 2 (codex + measurement) | `docs/specs/v07-vortice-codex-handoff.md` (guardrails) + `v03-av-sync-hardening.md` (Phase 2) | Gate B A/B, merge v0.7, promote stable offset |
| 3 (conditional) | `docs/specs/v03-av-sync-hardening.md` (Phase 3) | Drift-correction code (only if Phase-1b drift observed) |

---

## Guardrails (do not deviate)

1. **Phase 1a and 1b are parallel, not sequential.** They start simultaneously and run independently. Do not wait for one to finish before starting the other.

2. **Phase 1a must not touch the live GPU code path.** No real-device testing, no soak runs in Phase 1a. Gate A proves compilation; Gate B (Phase 2) proves GPU correctness on real hardware.

3. **Phase 2 serializes on a single real device.** You cannot run Phase 2 Gate B (v0.7 GPU validation) and Issue #3 Phase-2+ code changes concurrently on the same device. v0.7 Gate B runs first; Issue #3 Phase-3 (if triggered) runs after v0.7 merges.

4. **A/V-sync and GPU changes must stay bisectable.** Do not fold a drift-correction code change into the same window as the Vortice GPU swap. If a latency regression appears after Phase-2, was it the Vortice swap or the sync change? Keep them separate in time.

5. **Measure against the v0.5.0 baseline, not the Vortice under-development code.** Phase 1b soaks are all on v0.5.0 (known-good SharpDX). Phase 2 Gate B A/B's Vortice against the same v0.5.0 baseline. Do not run Phase-1b soaks on the Vortice branch while it's under development.

6. **Feature-flag is mandatory during Phase 1a porting.** Keep SharpDX in the tree throughout Phase 1a and Phase 2. Both presenters must coexist so Gate B can A/B them on the same device in one session. Remove SharpDX only in the cleanup commit after Gate B passes.

---

## Success criteria (end-of-phase checkpoints)

### After Phase 1a (Gate A passes)
- Vortice code is on a PR, Gate A green (build/test/publish succeed).
- SharpDX still in tree, feature-flag in place.
- Feature flag defaults to SharpDX (unchanged v0.5.0 behavior).

### After Phase 1b (drift report in)
- Measurement team has 30m, 1h, 2h soak data on v0.5.0.
- RTP/video timestamp drift analysis complete (monotonic growth Y/N).
- Best-performing offset identified.
- Issue #3 has a comment with findings.

### After Phase 2 (Gate B passes + v0.7 merged)
- Vortice latency p95 ≤ 150ms, matches SharpDX baseline.
- VRAM/handle stable over 1h soak vs. SharpDX A/B.
- D3DImage bridge prototyped and working on real hardware.
- SharpDX packages removed, `IMIRROR_GPU_BINDING` flag removed.
- `v0.7.0` tagged and released (installer + portable zip).
- Stable default offset promoted in code (Phase-2 code commit).

### After Phase 3 (if triggered, drift correction merged)
- RTP/video delta stays bounded on v0.7 baseline over 2h re-validation.
- Issue #3 closed with Phase-3 acceptance criteria met.
- `v0.8.0` or patch release tagged (drift-correction shipped).

### After Phase 3 (if NOT triggered, Issue #3 closed with no drift-correction)
- Phase-1b data shows no cumulative drift (only bounded jitter).
- Phase-2 stable default is permanent, slider retained for override.
- Issue #3 closed with note: "Field measurement shows bounded jitter only, drift-correction not needed."

---

## Next steps (immediate)

1. **codex:** Start Phase 1a (v0.7 code + Gate A). Use `docs/specs/v07-vortice-migration.md` as your detailed spec. PR to `claude/dreamy-bohr-nmvvzp` branch (do not merge to main until Phase 2 completes).

2. **measurement team:** Start Phase 1b (Issue #3 soaks on v0.5.0). Use `docs/specs/v03-av-sync-hardening.md` Phase-1 section. Collect RTP/video-timestamp delta data; prepare drift report.

3. **Both teams (asynchronously):** Report status weekly or when milestones hit. Phase 2 gates are unlocked when codex reports "Gate A green" + measurement team reports "drift data in hand."

4. **After Phase 1a + 1b complete:** Start Phase 2 (Gate B real-hardware validation on single device). v0.7 GPU A/B happens first. If no drift observed in Phase-1b, immediately promote stable offset in code and merge v0.7. If drift observed, queue Phase-3 after v0.7 merges.

---

## Summary (tl;dr)

| Timeline | Phase | What | Who | Dependency |
|----------|-------|------|-----|-----------|
| Week 1–3 (now) | 1a | Vortice code + Gate A (CI) | codex | None; start now |
| Week 1–3 (now) | 1b | Issue #3 soaks on v0.5.0 | measurement | None; start now (parallel to 1a) |
| Week 3–4 | 2 | v0.7 Gate B (real GPU A/B) + Phase-2 offset promotion | codex + measurement | Both Phase 1a + 1b complete |
| Week 4–5 (conditional) | 3 | Drift-correction code (only if Phase-1b shows drift) | codex | Phase 2 merged, drift data positive |
| Ongoing | Cleanup | v0.5.0 + v0.7.0 (+ v0.8.0 if Phase-3) releases | Release workflow | All phases complete |
