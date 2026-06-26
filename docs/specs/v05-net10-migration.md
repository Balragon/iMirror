# v0.5 — net10 Runtime Migration: Execution Note

**Tracked by:** Issue #17 · **Hard deadline:** land well before **.NET 8 EOL
2026-11-10**, with buffer for GPU re-validation.

**Status (2026-06-26):**
- **Gate A — DONE.** TFM bump (8 projects) + workflows + `LangVersion 12.0` landed
  in PR #25; CI green on net10 (restore/build/test, 43 tests, warning-clean), and
  the self-contained publish ships all WPF assemblies (the net9+ regression did
  **not** occur). `v0.4.0` shipped first, as designed.
- **Gate B — handed to codex.** Real-hardware GPU present-path re-validation
  (§3 below) runs in the codex batch on a real device + GPU, before the `v0.5`
  tag. See `docs/specs/v05-gateb-codex-handoff.md`.

This is the execution layer for the net10 bump. The *analysis* (why net10, SharpDX
behaviour, risk surface, full CI checklist) is in `docs/dotnet-strategy.md` — read
it once; this note does not repeat it. Here: the order, the exact edits, and the
gates.

---

## Why this is gated on `v0.4.0` (do not start the TFM bump early)

1. **v0.4.0 ships on net8 by design.** The installer + in-app updater are
   TFM-independent; v0.4 deliberately does *not* carry a runtime change. net10 is
   the *next* version (v0.5).
2. **Shared real-hardware validation window.** net10's real risk is the
   D3D11→D3D9→`D3DImage` present path, which must be re-validated on a real
   device + GPU — the *same* soak gate codex is running for v0.4. Two builds
   cannot compete for that one un-automatable resource.
3. **Bisectability.** Keep the TFM bump as its own validated step (same SharpDX).
   The SharpDX→Vortice swap stays at **v0.7** so any GPU regression is isolable.

**Start trigger:** codex has tagged `v0.4.0` and the release is published. Then
branch from the post-release `main`.

---

## 1. Mechanical bump (the easy half — code)

### TFM edits (8 projects, confirmed)

`net8.0-windows` → `net10.0-windows` (7):
- `MacMirrorReceiver.csproj`
- `MacMirrorReceiver.Tests/MacMirrorReceiver.Tests.csproj`
- `tools/D3DSharedHandleProbe/D3DSharedHandleProbe.csproj`
- `tools/D3DVideoProcessorProbe/D3DVideoProcessorProbe.csproj`
- `tools/HighResolutionD3DReplayProbe/HighResolutionD3DReplayProbe.csproj`
- `tools/HighResolutionProbeReport/HighResolutionProbeReport.csproj`
- `tools/MediaFoundationH264Probe/MediaFoundationH264Probe.csproj`

⚠️ **One exception** — `tools/LatencyAcceptanceReport/LatencyAcceptanceReport.csproj`
is `net8.0` (**no `-windows`**). Bump it to **`net10.0`**, not `net10.0-windows`.

### CI / pipeline edits

`actions/setup-dotnet` `dotnet-version: '8.0.x'` → `'10.0.x'` in **all three**
workflows (the `windows-latest` runner stays):
- `.github/workflows/ci.yml:24`
- `.github/workflows/release.yml:25`
- `.github/workflows/sbom.yml:25`

### Required alongside the TFM bump
- **`LangVersion` `11.0` → `12.0`** (all 8 projects). **Not optional on net10:**
  net10's `Marshal.QueryInterface(IntPtr, in Guid, out IntPtr)` takes the IID by
  `in` (it was effectively `ref` on net8). The existing `ref dxgiBufferIid` call
  site (`MediaFoundationD3D11Decoder.cs`) is `error CS9194` under C# 11 —
  "may not be passed with the 'ref' keyword in language version 11.0". C# 12 is
  the minimum that permits `ref`→`in`. Bumping to 12.0 fixes this class of error
  without touching call sites. (Earlier drafts of this note wrongly called the
  `11.0` pin "harmless on net10" — it blocks the build.)

### Leave alone
- `UseWPF` / `UseWindowsForms` — keep as-is.
- **Keep SharpDX 4.2.0.** No binding swap here (that's v0.7 / Vortice).
- Hard-coded `net8.0` path segments in scripts/workflows: **pre-checked, none
  found** — but re-confirm after the bump (publish output paths embed the TFM).

---

## 2. Gate A — restore/build/test (hosted `windows-latest` is enough)

Per `docs/dotnet-strategy.md` §"CI validation checklist", Part A:

- [ ] `dotnet --info` shows **10.0.x**.
- [ ] `dotnet restore iMirror.sln` succeeds; the **only** new warning is the
      expected SharpDX **NU1701** (netstandard1.1 fallback). Nothing else fatal.
- [ ] `dotnet build iMirror.sln -c Release --no-restore` succeeds. **Decide
      explicitly**: suppress NU1701 via `NoWarn`, or leave it visible — do **not**
      silently add `-warnaserror`.
- [ ] `dotnet test iMirror.sln -c Release --no-build` — all existing tests green.
- [x] **WPF-assembly presence now CI-enforced.** `publish-win-x64.ps1`'s
      `$requiredFiles` asserts `PresentationFramework`/`PresentationCore`/
      `WindowsBase`, and `ci.yml` runs `publish-win-x64.ps1 -AllowMissingFfmpeg
      -NoZip` on every PR/push — so the net9+ self-contained WPF-omission
      regression is a hard CI failure, not a manual zip inspection. A green build
      alone still does not prove launch on real hardware (that is Gate B).

## 3. Gate B — GPU present path (REQUIRES real device + GPU; not hosted)

This is the gate that cannot be faked from CI. Full steps in
`docs/dotnet-strategy.md` Part B (steps 7–14); the load-bearing ones:

- [ ] Keyframe renders via the default GPU engine
      (`D3D11VideoProcessorD3DImagePresenter` actually presents).
- [ ] DXGI shared-handle → D3D9 → `D3DImage` bridge shows live, updating video
      (not black/frozen).
- [ ] **`D3DImage` front-buffer loss/restore:** lock workstation / RDP
      reconnect / fast-user-switch → presenter recovers. *Most likely thing to
      break on a runtime change.*
- [ ] GPU probes pass on net10: `D3DVideoProcessorProbe`, `D3DSharedHandleProbe`,
      `MediaFoundationH264Probe`, `HighResolutionD3DReplayProbe`.
- [ ] Software fallback unaffected (`IMIRROR_FORCE_SOFTWARE_VIDEO=1`).
- [ ] **≥1-hour real-hardware soak** via `scripts/soak-gate.ps1` (see
      `docs/soak-gate.md`): no VRAM/handle growth, no `D3DImage` stalls, no crash.
- [ ] **Latency gate:** `tools/LatencyAcceptanceReport` p95 ≤ **150 ms**.
- [ ] 3× connect/disconnect/reconnect: clean state reset, no leaked GPU resources.

---

## 4. Acceptance (mirrors Issue #17)

- [ ] `net10.0-windows` builds and passes CI (restore/build/test).
- [ ] Published self-contained artifact includes all WPF assemblies.
- [ ] GPU D3D11 present path renders a keyframe; `D3DImage` survives a
      device-loss/restore cycle on real hardware.
- [ ] 1-hour soak gate passes on net10.
- [ ] Merged and released as **v0.5** comfortably before **2026-11-10**.

After merge: update `CHANGELOG.md` (v0.5 = net10 runtime) and close Issue #17.
Then **v0.7** (SharpDX → Vortice) is unblocked against a known-good net10 baseline.

---

## Reference map

| Thing | Location |
|---|---|
| Deep analysis / risk / sources | `docs/dotnet-strategy.md` |
| Roadmap context (sequence, deadline) | `docs/specs/v05-plus-roadmap.md` |
| Highest-risk code path | `MacMirrorReceiver.Video/D3D11VideoProcessorD3DImagePresenter.cs` |
| Soak gate | `scripts/soak-gate.ps1`, `docs/soak-gate.md` |
| Real-device E2E + latency conventions | `docs/windows-e2e-validation.md` |
| Issue tracker | #17 |

**Boundary reminder:** this note bumps the runtime only. Do **not** pull the
Vortice binding swap (v0.7) or signing (deferred) into the net10 change — both
would make a GPU regression un-bisectable or spend the EOL buffer on unscheduled
work.
