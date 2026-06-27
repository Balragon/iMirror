# v0.7 Vortice Gate B A/B + Default-Flip — Codex Handoff

**Owner:** codex (real-hardware A/B) · **Tracked by:** roadmap v0.7 · **Deadline:** none
(SharpDX works on net10; modernization, not a forcing function).

## What this gates

The Vortice GPU binding is fully ported and merged behind `IMIRROR_GPU_BINDING`
(default SharpDX). Gate A (build/test/publish) is green on `main`, and PR #28 proved
the load-bearing `D3DImage` bridge on real hardware (iPhone, 1h, `receive->present`
p95 `94ms`).

This handoff gates the **default-flip**: prove Vortice is **equivalent to SharpDX**
(not merely "it works") on real hardware, then either flip (cleanup commit drops
SharpDX + the flag) or hold. The decision is hardware-only, so it cannot be done in
CI — same reason as net10 Gate B and the B3 hardening run.

## Fixed parameters (do not drift — the flip judgment depends on stable conditions)

- **Base commit:** `main` `a6b32fe`.
- **SharpDX baseline:** reuse the B3 GPU logs in
  `docs/b3-hardening-evidence-2026-06-27.md` (Mac 2h `worstP95=91ms`/`worstMax=126ms`;
  iPhone p95 `70ms`) **only after confirming the `gpuBinding=SharpDX` line in those
  logs.** If that line is absent or ambiguous, re-measure SharpDX on the same hardware
  in the same session as the Vortice run.
- **Vortice run:** `IMIRROR_GPU_BINDING=vortice` (confirm `gpuBinding=Vortice` in the
  log).
- **A/B principle:** same device, same sender, same resolution, same session window
  wherever possible, so any delta is attributable to the **binding only**.
- **Bisectability boundary:** do **not** pull Issue #3 (A/V sync) or #32 (FFmpeg
  fallback latency) work into this window. Keep a GPU-binding swap isolated from the
  long-run sync and software-fallback tracks so a regression is attributable.

## Targets (run each on Vortice; compare to the SharpDX baseline)

- Mac sender.
- iPhone sender.
- 3x connect / disconnect / reconnect — clean GPU-resource teardown each cycle.
- `D3DImage` front-buffer loss/restore recovers (lock / RDP / fast-user-switch).
- GPU probes (4) pass on Vortice builds: `D3DVideoProcessorProbe`,
  `D3DSharedHandleProbe`, `MediaFoundationH264Probe`, `HighResolutionD3DReplayProbe`.
- Software fallback unaffected (`IMIRROR_FORCE_SOFTWARE_VIDEO=1`).
- >=1h soak (`scripts/soak-gate.ps1`): VRAM/handle no-growth vs the SharpDX baseline.

## PASS criteria (ALL — framed as A/B, not standalone)

- `receive->present` p95 `<=150ms` **and** parity with SharpDX (no material
  regression).
- VRAM/handle: no growth over `>=1h`, comparable to the SharpDX baseline.
- Front-buffer loss/restore recovers; reconnect x3 clean; **no stale
  decoder/surface/device-loss state**.
- GPU probes pass on Vortice; software fallback unaffected.
- Contiguous evidence; no crash / corruption / freeze / keyframe-starvation markers.

## Evidence format

One document modeled on `docs/b3-hardening-evidence-2026-06-27.md`, named
`docs/v07-vortice-gateb-ab-evidence-<yyyy-mm-dd>.md`. For each target, a **side-by-side
SharpDX vs Vortice** row: `worstP95`, `worstMax`, VRAM/handle trend, `clears`,
reconnect notes, probe results. Cite each log filename and its `gpuBinding=` line.

## Result -> action

- **PASS (parity):** cleanup commit — remove `SharpDX.*` package refs, the four
  SharpDX renderer files (`D3D11VideoFrame.cs`,
  `D3D11VideoProcessorD3DImagePresenter.cs`, `D3D11SwapChainVideoPresenter.cs`,
  `MediaFoundationD3D11Decoder.cs`), and the `IMIRROR_GPU_BINDING` selector wiring, so
  Vortice is the only/default path. Confirm NU1701 is gone. `CHANGELOG.md`: v0.7 = GPU
  binding modernization (SharpDX -> Vortice). Tag/release per `release.yml` (it is
  binding-agnostic). **This cleanup commit IS the default-flip.**
- **FAIL (regression / instability):** keep the flag (default stays SharpDX), file a
  GitHub issue with the failing A/B evidence, and hold the default-flip. Do **not**
  flip on "it mostly works."

## Sequence

1. This handoff doc. (done)
2. Codex / Windows: Vortice Gate B A/B on Mac + iPhone vs the SharpDX baseline.
3. Record the evidence doc.
4. PASS -> default-flip cleanup commit.
5. FAIL -> hold; keep the flag; file an issue.

## Reference

| Thing | Location |
|---|---|
| Full port plan, per-file API map, commit sequence | `docs/specs/v07-vortice-migration.md` |
| Guardrails / why-codex | `docs/specs/v07-vortice-codex-handoff.md` |
| Gate B checklist source | `docs/specs/v07-vortice-migration.md` §5 |
| SharpDX baseline evidence | `docs/b3-hardening-evidence-2026-06-27.md` |
| Soak gate + latency report | `scripts/soak-gate.ps1`, `docs/soak-gate.md`, `tools/LatencyAcceptanceReport` |
| Load-bearing bridge (Vortice) | `MacMirrorReceiver.Video/VorticeD3D11VideoProcessorD3DImagePresenter.cs` (+ the SharpDX twin for A/B) |
| Binding selector | `MacMirrorReceiver.Video/D3DGpuBindingSelector.cs` |
