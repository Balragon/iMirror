# v0.7 — SharpDX → Vortice.Windows: Codex Handoff

**Owner:** codex (compile + real-hardware batch) · **Tracked by:** roadmap v0.7 ·
**Deadline:** none (SharpDX works on net10; modernization, not a forcing function).

The full plan — package swap, confirmed per-file API mapping, feature-flag design,
Gate A/B, commit sequence — is in **`docs/specs/v07-vortice-migration.md`**. Read it;
this handoff does not repeat it. This note is the **why-codex** and the **guardrails**.

---

## Why this goes to codex (not the planning agent)

Two hard reasons, both the same as net10 Gate B:

1. **No local compile.** The planning was done in a Linux environment that cannot
   build `net10.0-windows` + WPF + D3D. The Vortice port is real COM-interop
   refactoring (~2,200 lines across 4 renderer files), not string edits — it needs
   an environment that actually compiles it, with fast iteration.
2. **The load-bearing path is hardware-only.** `D3DImage.SetBackBuffer(IntPtr)` fed
   by a Vortice `IDirect3DSurface9.NativePointer` has **no official Vortice sample**.
   It must be proven on a real GPU before the rest of the port is worth doing. CI
   cannot fake this.

The shared types (`ID3D11Texture2D` etc.) flow through presenters + decoder, so the
port cannot be landed file-by-file as green CI increments — it is a cohesive unit.
That is another reason it belongs in a compile+hardware batch, not blind CI cycles.

---

## Guardrails (do not deviate without flagging)

1. **Prototype the D3DImage bridge FIRST.** Before porting `D3D11SwapChainVideoPresenter`,
   `MediaFoundationD3D11Decoder`, or the probes, get a Vortice
   `IDirect3DSurface9.NativePointer` → `D3DImage` showing live updating video on real
   hardware. If that single bridge does not work, stop and report — the whole swap
   hinges on it.
2. **Feature-flag, keep SharpDX in-tree during transition.** `IMIRROR_GPU_BINDING=vortice`
   selects the Vortice presenter; unset = today's SharpDX path. This is mandatory so
   Gate B can **A/B the two bindings on the same device in one session** — the only
   way to attribute a regression to the swap. Remove SharpDX + the flag only in the
   final cleanup commit, after Gate B passes.
3. **Measure against the v0.5.0 baseline.** v0.5.0 (net10 + SharpDX) is the known-good
   reference for latency p95, VRAM/handle stability, and front-buffer recovery.
   Vortice must match it, not just "work."
4. **GPU binding ONLY — bisectability boundary.** Do **not** pull Issue #3 (A/V sync,
   `docs/specs/v03-av-sync-hardening.md`) into this validation window. Those soak runs
   are deliberately serial *after* v0.7 merges, so a GPU-binding swap and an
   audio-sync change never share a validation window.

---

## Gates (full detail in v07-vortice-migration.md)

- **Gate A — hosted CI:** restore (NU1701 from SharpDX still expected during
  transition), build, test, `publish-win-x64.ps1 -AllowMissingFfmpeg -NoZip` WPF
  check. Proves it compiles/packages; does NOT prove the GPU bridge.
- **Gate B — real hardware, A/B vs SharpDX:** prototype-bridge gate first, then
  keyframe render, front-buffer loss/restore, GPU probes on Vortice, software
  fallback unaffected, ≥1h soak (no growth vs. baseline), latency p95 ≤ 150 ms,
  3× reconnect clean.

## Done = 

- [ ] Gate A green on the Vortice build.
- [ ] Gate B green, A/B'd against SharpDX on the same hardware.
- [ ] Cleanup commit: SharpDX packages + `IMIRROR_GPU_BINDING` removed, NU1701 gone.
- [ ] `CHANGELOG.md`: v0.7 = GPU binding modernization (SharpDX → Vortice).
- [ ] Tag/release per the established flow (release.yml is binding-agnostic).

---

## Reference map

| Thing | Location |
|---|---|
| Full execution note (this handoff's source) | `docs/specs/v07-vortice-migration.md` |
| Deep analysis / API-coverage table | `docs/dotnet-strategy.md` §Vortice |
| Load-bearing bridge | `MacMirrorReceiver.Video/D3D11VideoProcessorD3DImagePresenter.cs:281-322` |
| Front-buffer loss/restore | same file `:185-212, :331` |
| Soak gate / E2E | `scripts/soak-gate.ps1`, `docs/windows-e2e-validation.md` |
| Deferred sibling track (serial, not now) | `docs/specs/v03-av-sync-hardening.md` (Issue #3) |
