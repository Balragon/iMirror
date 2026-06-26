# v0.5 Gate B — net10 GPU Re-validation: Codex Handoff

**Owner:** codex (real-hardware batch) · **Tracked by:** Issue #17 · **Hard
deadline:** finish before **.NET 8 EOL 2026-11-10**, with buffer.

Gate A is done and merged: `main` is on `net10.0-windows`, CI is green
(restore/build/test, 43 tests, warning-clean), and the self-contained publish
ships all WPF assemblies. The full context, exact edits, and gate definitions are
in `docs/specs/v05-net10-migration.md` — read it once; this note does not repeat
it. **This handoff is only the part CI cannot do: validate the GPU present path on
real hardware, then tag `v0.5`.**

---

## Why this needs a human + real hardware (not CI)

net10's only real risk is the highest-risk runtime path, which no hosted runner
has a GPU to exercise: **D3D11 decode → DXGI shared handle → D3D9 → WPF
`D3DImage` present.** "Restores/builds/tests green" does not prove this path
still presents live video or survives device loss. That is exactly what Gate B
checks. Keep it on the **same** real device + GPU used for the v0.4.0 soak.

**Start state:** branch from current `main` (net10 already in). No code change is
expected — Gate B is validation. If something fails, fix narrowly on a branch and
re-validate; do **not** pull in the Vortice swap (v0.7) or signing (deferred).

---

## Gate B checklist (real device + GPU; must actually RUN)

From `docs/dotnet-strategy.md` Part B (steps 7–14); load-bearing ones:

- [ ] Keyframe renders via the default GPU engine
      (`D3D11VideoProcessorD3DImagePresenter` actually presents).
- [ ] DXGI shared-handle → D3D9 → `D3DImage` bridge shows live, updating video
      (not black/frozen).
- [ ] **`D3DImage` front-buffer loss/restore:** lock workstation / RDP reconnect /
      fast-user-switch → presenter recovers. *Most likely thing to break on a
      runtime change.*
- [ ] GPU probes pass on net10: `D3DVideoProcessorProbe`, `D3DSharedHandleProbe`,
      `MediaFoundationH264Probe`, `HighResolutionD3DReplayProbe`.
- [ ] Software fallback unaffected (`IMIRROR_FORCE_SOFTWARE_VIDEO=1`).
- [ ] **≥1-hour real-hardware soak** via `scripts/soak-gate.ps1` (see
      `docs/soak-gate.md`): no VRAM/handle growth, no `D3DImage` stalls, no crash.
- [ ] **Latency gate:** `tools/LatencyAcceptanceReport` p95 ≤ **150 ms**.
- [ ] 3× connect/disconnect/reconnect: clean state reset, no leaked GPU resources.

The probe projects are net10 now (Gate A), so they build/run against the same
runtime the product uses — no separate setup.

---

## After Gate B passes — tag and close

- [ ] Update `CHANGELOG.md`: **v0.5 = net10 runtime migration**.
- [ ] Tag `v0.5.0` on the validated `main` commit. The release workflow
      (`.github/workflows/release.yml`, already net10) builds the installer +
      portable zip with `SHA256SUMS` and the GPLv3 release-notes section.
- [ ] Confirm the published self-contained package **launches on real hardware**
      (not just that assemblies are present — that is the CI half).
- [ ] Close Issue #17.

Then **v0.7** (SharpDX → Vortice) is unblocked against a known-good net10 baseline.

---

## Acceptance (mirrors Issue #17)

- [x] `net10.0-windows` builds and passes CI (restore/build/test).  ← Gate A, done
- [x] Published self-contained artifact includes all WPF assemblies.  ← Gate A, done
- [ ] GPU D3D11 present path renders a keyframe; `D3DImage` survives a
      device-loss/restore cycle on real hardware.
- [ ] 1-hour soak gate passes on net10.
- [ ] Merged and released as **v0.5** comfortably before **2026-11-10**.

---

## Reference map

| Thing | Location |
|---|---|
| Full migration execution note (Gate A + B) | `docs/specs/v05-net10-migration.md` |
| Deep analysis / risk / sources | `docs/dotnet-strategy.md` |
| Highest-risk code path | `MacMirrorReceiver.Video/D3D11VideoProcessorD3DImagePresenter.cs` |
| Soak gate | `scripts/soak-gate.ps1`, `docs/soak-gate.md` |
| Real-device E2E + latency conventions | `docs/windows-e2e-validation.md` |
| Roadmap context (sequence, deadline) | `docs/specs/v05-plus-roadmap.md` |
| Issue tracker | #17 |

**Boundary reminder:** Gate B validates the runtime bump only. A GPU regression
here is a net10 problem to fix narrowly — not a reason to pull the Vortice binding
swap (v0.7) or signing (deferred) into v0.5.
