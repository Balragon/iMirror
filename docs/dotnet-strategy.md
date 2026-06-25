# .NET Runtime & DirectX Binding Strategy (v0.3 Decision Spike)

**Status:** Decision document (Phase 3, v0.3 release-readiness). No code changed.
**Author context:** Desk research performed on a Linux container with no dotnet SDK
(the app targets `net8.0-windows` + WPF, so nothing here was built or run locally).
Every claim below is tagged **CONFIRMED** (verified against a primary source or the
repo) or **UNCERTAIN** (inference that must be validated on Windows CI — see the
checklist).
**Date:** 2026-06-25

---

## Decision (recommended)

**Recommended: (b) — Stay on `net8.0-windows` for v0.3, and open an EOL-transition
blocker issue that schedules the `net10.0-windows` bump for an early v0.4/v0.5
window, decoupled from the SharpDX→Vortice replacement (which stays deferred to
v0.7).**

### Reasoning

1. **No forcing function in the v0.3 window.** .NET 8 is itself an LTS release;
   its support runs to **2026-11-10** (CONFIRMED — Microsoft support policy). v0.3
   is a *release-readiness* phase, not a runtime-modernization phase. A TFM bump
   touches the single riskiest subsystem in the product — the D3D11 video-processor
   → D3D9 shared-surface → WPF `D3DImage` present path — and that risk buys nothing
   the v0.3 release needs.

2. **The migration is real work, not a one-line TFM edit.** The app does *not*
   merely reference SharpDX; it drives a fragile cross-API GPU interop bridge
   (D3D11 `VideoProcessorBlt` → DXGI shared handle → D3D9 `Texture`/`Surface` →
   `D3DImage.SetBackBuffer(IDirect3DSurface9, NativePointer)`; see
   `MacMirrorReceiver.Video/D3D11VideoProcessorD3DImagePresenter.cs:288-316`).
   SharpDX targets only `netstandard1.1`/`net45` (CONFIRMED, below), so on **any**
   modern TFM it resolves via the netstandard1.1 compatibility shim and is expected
   to raise **NU1701** ("restored using a different framework"). That warning is
   *already latent today on net8* — the repo has no `Directory.Build.props`, no
   `NoWarn`, and CI runs `dotnet build` without `-warnaserror` (CONFIRMED:
   `.github/workflows/ci.yml`), so it currently passes despite the warning. The
   net8→net10 bump does not remove this; it only re-rolls the dice on the unsupported
   runtime combination one more time.

3. **Separating the two changes is the safe sequencing.** Bumping the TFM *and*
   swapping the DirectX binding in the same change would make any GPU-path
   regression impossible to bisect. Keeping them as two ordered, independently
   validated steps (TFM bump first under the *same* SharpDX, Vortice swap later) is
   strictly safer. (b) preserves that ordering and keeps v0.3 shippable.

4. **Why not (a) — bump to net10 now, keep SharpDX:** Defensible, and net10 is the
   current LTS (CONFIRMED, support to **2028-11-14**), so it is the right *eventual*
   target. But doing it *inside* v0.3 spends the release's stability budget
   validating a runtime change with no v0.3 user benefit, on the highest-risk code
   path, against an unsupported (netstandard1.1-on-net10) binding. If the roadmap
   wants net10 in v0.3 regardless, (a) is acceptable **only if** the full Windows CI
   checklist below (including the 10-minute soak and the `D3DImage` front-buffer
   loss/restore test) is run and green first.

5. **Why not (c) — net10 + fast-track Vortice now:** Highest risk, lowest urgency.
   It collapses two hard changes into one un-bisectable change during a
   release-readiness phase and pulls a v0.7 item forward with no v0.3 justification.
   Reject for v0.3.

**Net:** ship v0.3 on net8; file the blocker issue so the net10 bump is *scheduled
and owned* rather than forgotten; keep Vortice at v0.7. This is the option that
treats the EOL clock honestly without gambling the release on the GPU present path.

---

## SharpDX 4.2.0 on .NET 10

**CONFIRMED — declared target frameworks (from the NuGet package nuspec):**
`SharpDX.Direct3D11` 4.2.0 declares: **.NET Framework 4.0**, **.NET Framework 4.5**,
**.NET Standard 1.1**, and **UAP 10.0**. Published **2018-08-24**.
(Source: nuget.org package page for SharpDX.Direct3D11 4.2.0.)
`SharpDX.DXGI` and `SharpDX.Direct3D9` 4.2.0 follow the same pattern (same release
train; CONFIRMED via nuget.org listing). SharpDX itself is **archived/unmaintained**
(last release 2019).

**Restore behaviour on `net10.0-windows` — UNCERTAIN (high-confidence inference):**
A `netstandard1.1` asset *is* compatible with net10 under the .NET Standard
compatibility rules, so `dotnet restore` is expected to **succeed**. Because the
nearest compatible asset is netstandard1.1 (not a `net*`-specific asset), the SDK is
expected to emit **NU1701** ("package was restored using `.NETStandard,Version=v1.1`
instead of the project target framework"). This warning already applies on net8 and
is non-fatal in this repo (CI has no `-warnaserror`). MUST be confirmed on CI.

**Runtime risk on net10 — the real concern.** "Restores" ≠ "works." Specific risks,
all UNCERTAIN until validated on Windows:

- **P/Invoke / native COM marshalling.** SharpDX is a thin hand-written COM/P-Invoke
  layer built in 2018 against the .NET Framework marshaller. .NET 5+ uses
  `DllImportGenerator`-era marshalling and has tightened COM interop behaviour across
  releases. The hot path here passes raw `NativePointer` `IntPtr`s into
  `D3DImage.SetBackBuffer` and into D3D9 `CreateTexture(..., ref sharedHandle)`
  (`D3D11VideoProcessorD3DImagePresenter.cs:296-316`). This is exactly the kind of
  manual marshalling most sensitive to runtime changes.
- **WPF `D3DImage` interop.** The present path depends on `D3DImage` +
  `IDirect3DSurface9` + DXGI shared-handle bridging. This is WPF-internal milcore
  interop; it has historically been stable but is also exactly where net9/net10 WPF
  composition changes would surface. No specific net10 regression is documented
  against this path (UNCERTAIN — must be run).
- **Trimming / AOT / JIT.** Self-contained or trimmed publish + reflection-heavy,
  unsupported-TFM binding = elevated risk of trimmed-away types or JIT differences.
  The repo publishes via `scripts/publish-win-x64.ps1` (referenced in
  `docs/windows-e2e-validation.md`); whether that uses trimming/self-contained must
  be confirmed before relying on it on net10.

**Bottom line:** SharpDX 4.2.0 will almost certainly *restore* on net10 and *probably*
runs, but it is an unsupported binding on an unsupported-by-it runtime. This is
acceptable as a bridge but is not a place to be casual — every TFM bump must
re-validate the GPU present path end-to-end on real hardware.

---

## WPF `net8.0-windows` → `net10.0-windows` migration notes

**.NET 10 status:** **LTS**, released **2025-11-11**, supported to **2028-11-14**
(CONFIRMED — Microsoft .NET support policy / Announcing .NET 10).

**Mechanical migration steps:**

1. Bump `<TargetFramework>net8.0-windows</TargetFramework>` →
   `net10.0-windows` in **all** affected projects (CONFIRMED list, all currently
   net8.0-windows):
   - `MacMirrorReceiver.csproj`
   - `MacMirrorReceiver.Tests/MacMirrorReceiver.Tests.csproj`
   - `tools/D3DSharedHandleProbe`, `tools/D3DVideoProcessorProbe`,
     `tools/HighResolutionD3DReplayProbe`, `tools/HighResolutionProbeReport`,
     `tools/MediaFoundationH264Probe`, `tools/LatencyAcceptanceReport`
     (LatencyAcceptanceReport's TFM should be confirmed; the other tools are
     net8.0-windows CONFIRMED).
2. Keep `UseWPF`/`UseWindowsForms` as-is (both used by the main project).
3. Update CI: `actions/setup-dotnet` `dotnet-version: '10.0.x'` (currently `8.0.x`
   — CONFIRMED `.github/workflows/ci.yml:24`). The `windows-latest` runner already
   used is fine.
4. `LangVersion` is pinned to `11.0` in the csprojs; net10 ships a newer C#, but
   pinning 11.0 is harmless. No action required unless newer language features are
   wanted.
5. Re-check `scripts/publish-win-x64.ps1` and `release.yml` for any hard-coded
   `net8.0-windows` path segments in publish output folders (UNCERTAIN — grep the
   scripts on Windows; publish output paths embed the TFM).

**WPF-specific net9/net10 changes to be aware of:**

- WPF in net9/net10 adds **Fluent theme** styling and ongoing rendering/perf work
  (CONFIRMED — .NET 10 "what's new"). These are additive, but theme/visual changes
  can alter default control rendering; a visual smoke test of the existing UI is
  warranted.
- **Self-contained / trimmed publish regression risk:** a documented class of issue
  exists where adding certain NuGet packages caused WPF assemblies to be omitted
  from self-contained publish output starting at net9 (UNCERTAIN as to whether it
  affects this app's package set; flagged because the app ships self-contained-style
  zips). MUST verify the published zip on net10 actually launches and contains the
  WPF assemblies — do not trust a green `dotnet build` alone.
- **General .NET 10 breaking changes:** consult Microsoft's "Breaking changes in
  .NET 10" page during the bump; nothing in it is known to specifically break this
  app, but the SharpDX P/Invoke surface (above) is the place to watch.

**Self-contained publish implication:** the runtime pack changes from the .NET 8 to
the .NET 10 Windows Desktop pack; size/output and any per-TFM publish paths change
accordingly. Validate the produced artifact end-to-end, not just the build.

---

## Vortice.Windows replacement assessment (for v0.7)

**Maintenance status — CONFIRMED, strongly positive.** `Vortice.Windows`
(amerkoleci) is actively maintained. `Vortice.Direct3D11` latest stable **3.8.3
published 2026-03-04**, with a steady recent cadence (3.8.2 2026-01, 3.8.1 2025-12).
It explicitly targets **net8.0 / net9.0 / net10.0** (CONFIRMED — nuget.org). This is
the de-facto modern successor to SharpDX and is recommended by the community for
exactly this migration.

**API surface coverage — CONFIRMED it covers everything this app uses:**
Vortice provides `Vortice.Direct3D11`, `Vortice.DXGI`, **and** `Vortice.Direct3D9` —
a 1:1 match for the three SharpDX packages referenced here. The specific surface this
app depends on:

| This app uses (SharpDX) | Vortice equivalent | Notes |
|---|---|---|
| `D3D11.Device`, `ImmediateContext`, `Multithread` | `ID3D11Device`/`ID3D11DeviceContext`/`ID3D11Multithread` | Vortice exposes COM as `ID*` interfaces |
| `D3D11.VideoDevice`/`VideoContext`/`VideoProcessor*` (`VideoProcessorBlt`, input/output views, enumerator) | `ID3D11VideoDevice`/`ID3D11VideoContext`/`ID3D11VideoProcessor*` | Video APIs are present in Vortice.Direct3D11 |
| `DXGI.SwapChain1`, `Factory`, `Resource.SharedHandle`, `Format`, `Rational` | `IDXGISwapChain1`, `IDXGIFactory*`, `IDXGIResource.SharedHandle`, `Format`, `Rational` | |
| `D3D9.Direct3DEx`/`DeviceEx`/`Texture`/`Surface`, `CreateTexture(..., ref sharedHandle)`, `GetSurfaceLevel` | `Vortice.Direct3D9` `IDirect3D9Ex`/`IDirect3DDevice9Ex`/`IDirect3DTexture9`/`IDirect3DSurface9` | |
| `surface.NativePointer` → `D3DImage.SetBackBuffer(IDirect3DSurface9, IntPtr)` | Vortice COM objects expose `.NativePointer` (`IntPtr`) | This is the load-bearing interop and the thing to prototype first |

**WPF `D3DImage` interop — UNCERTAIN but expected to work.** `D3DImage.SetBackBuffer`
takes a raw `IntPtr` to an `IDirect3DSurface9`; it does not care which binding
produced the pointer. Vortice COM wrappers expose a `NativePointer`/`IntPtr` handle
analogous to SharpDX's, so the same pattern should port. There is no
Vortice-published "D3DImage sample," so this MUST be prototyped, but there is no
structural reason it cannot work, and the Avalonia/community discussions show people
doing equivalent D3D11/D3D9 interop with Vortice.

**Migration effort — qualitative: MEDIUM (focused, not sprawling).** Only four
renderer files use SharpDX (`D3D11VideoProcessorD3DImagePresenter.cs`,
`D3D11SwapChainVideoPresenter.cs`, `D3D11VideoFrame.cs`,
`MediaFoundationD3D11Decoder.cs`), plus the `tools/*` probes. The work is mostly
mechanical renaming (`D3D11.Device` → `ID3D11Device`, factory-method call-style
differences, enum/struct namespace moves) concentrated in a small, well-isolated
surface. The genuine risk is *not* breadth — it is the two non-mechanical hot spots:
(1) the DXGI-shared-handle → D3D9 texture → `D3DImage` bridge, and (2) the video
processor (`VideoProcessorBlt`) path. Both must be re-validated on hardware. The
right v0.7 approach: port one presenter behind a feature flag, validate the GPU
present path with the existing probes/soak, then convert the rest. Estimate: a
contained multi-day effort dominated by hardware validation, not by line count.

---

## CI validation checklist (run on Windows to confirm the decision)

This is the gate that converts the **UNCERTAIN** items above into **CONFIRMED**. It
must run on a Windows runner/dev box with a real GPU (the GitHub `windows-latest`
hosted runner is headless/limited-GPU and is sufficient for restore/build/test, but
**not** for the GPU-present and soak steps — those need real-hardware E2E, consistent
with `docs/windows-e2e-validation.md`).

### Part A — Build/restore confirmation (can run on hosted `windows-latest`)

For the option being validated, set `TargetFramework` to the target (`net10.0-windows`
for option (a), or simply re-run as-is for (b)) on `MacMirrorReceiver.csproj`, the
test project, and all `tools/*` projects. Then:

1. [ ] `dotnet --info` — confirm the intended SDK (10.0.x for a net10 bump).
2. [ ] `dotnet restore iMirror.sln` — **MUST succeed.** Capture and inspect every
       **NU1701** warning; confirm it is *only* the expected SharpDX
       netstandard1.1 fallback and nothing new/fatal.
3. [ ] `dotnet build iMirror.sln -c Release --no-restore` — **MUST succeed.**
       Record the full warning list; decide explicitly whether to suppress NU1701
       via `NoWarn` or leave it visible (do **not** silently add `-warnaserror`).
4. [ ] `dotnet test iMirror.sln -c Release --no-build` — all existing tests green.
5. [ ] Run `scripts/publish-win-x64.ps1` and **inspect the produced zip**: confirm
       it launches AND that WPF runtime assemblies (e.g. `PresentationFramework`,
       `PresentationCore`, `WindowsBase`) are present in the output. This catches
       the self-contained/trimmed WPF-omission regression class.
6. [ ] Grep the publish/release scripts for hard-coded `net8.0-windows` path
       segments and fix any that the TFM bump invalidated.

### Part B — GPU present-path confirmation (REQUIRES real GPU; cannot be hosted-runner)

7. [ ] Launch the app; start an AirPlay mirroring session from a Mac/iPhone; confirm
       **a keyframe renders** via the default GPU (MediaFoundation/D3D11) engine —
       i.e. the `D3D11VideoProcessorD3DImagePresenter` path actually presents.
8. [ ] Confirm the **DXGI shared-handle → D3D9 → `D3DImage`** bridge works: video is
       visible and updating (not a black/frozen `D3DImage`).
9. [ ] **`D3DImage` front-buffer loss/restore test:** trigger a front-buffer-loss
       event (lock workstation / RDP disconnect-reconnect / fast user switch) and
       confirm the presenter recovers (`IsFrontBufferAvailable` handling and
       surface reattach at `D3D11VideoProcessorD3DImagePresenter.cs:190-211,331`).
       This path is the most likely to break on a runtime/binding change.
10. [ ] Run the existing GPU probes against the target TFM:
        `tools/D3DVideoProcessorProbe`, `tools/D3DSharedHandleProbe`,
        `tools/MediaFoundationH264Probe`, `tools/HighResolutionD3DReplayProbe` —
        confirm each still passes (these isolate the exact interop primitives at risk).
11. [ ] Exercise the **software FFmpeg fallback** (`IMIRROR_FORCE_SOFTWARE_VIDEO=1`)
        to confirm the non-GPU path is unaffected.
12. [ ] **10-minute soak:** continuous mirroring for ≥10 min; watch for GPU-memory
        growth, leaked D3D9/D3D11 surfaces, `D3DImage` stalls, or crash. Confirm no
        unbounded handle/VRAM growth (the present path allocates/reattaches surfaces
        on resize — see `AttachOutputToD3DImage`).
13. [ ] **Latency gate:** run `tools/LatencyAcceptanceReport` against a fresh session
        log; confirm p95 ≤ 150 ms (consistent with `docs/windows-e2e-validation.md`).
14. [ ] Three connect/disconnect/reconnect cycles; confirm session/surface state
        resets cleanly with no leaked GPU resources.

Only when **Part A + Part B** are all green is a TFM bump (option (a)) confirmed safe
to merge. For option (b), Part A on net8 is already implicitly green in current CI;
the value of the checklist is as the *acceptance gate for the scheduled net10 bump*.

---

## Risks & open questions

1. **NU1701 on net10 is inferred, not observed (UNCERTAIN).** SharpDX's
   netstandard1.1 target *should* restore on net10 and *should* warn NU1701; both
   must be confirmed by Part A step 2/3. There is a small chance a future SDK
   tightens netstandard1.x fallback into an error — unlikely, but it is the one thing
   that could turn the "easy" TFM bump into a hard blocker, which is itself an
   argument for scheduling it deliberately (option (b)) rather than rushing it.
2. **GPU present path runtime behaviour on net10 is unverified (UNCERTAIN).** The
   D3D11→D3D9→`D3DImage` `NativePointer` marshalling is the single highest-risk
   surface for any runtime change. No documented net10 regression targets it, but
   "no documented regression" is not "tested." Part B is mandatory before trusting it.
3. **Self-contained/trimmed WPF publish regression (UNCERTAIN).** A documented
   net9+ class of issue can drop WPF assemblies from self-contained publish when
   certain packages are present. Must verify the *published artifact*, not just the
   build (Part A step 5). Whether this app even publishes self-contained/trimmed
   needs confirming by reading `scripts/publish-win-x64.ps1` on Windows.
4. **Vortice `D3DImage` interop unproven for this exact pattern (UNCERTAIN).** No
   official Vortice `D3DImage` sample exists. Structurally it should work
   (`NativePointer`/`IntPtr` is binding-agnostic), but the v0.7 migration MUST start
   by prototyping that single bridge before committing to the full swap.
5. **Hosted runner cannot fully validate.** GitHub `windows-latest` is sufficient for
   restore/build/test but not for GPU present + soak + latency. The decision's
   confidence is bounded by whether a real-hardware E2E run is actually performed
   (same constraint already acknowledged in `docs/windows-e2e-validation.md`).
6. **net8 EOL clock.** .NET 8 support ends 2026-11-10. The blocker issue from option
   (b) must land the net10 bump comfortably before that date, or the EOL risk
   re-materializes. The bump should not slip past v0.5.

---

## Sources

- SharpDX.Direct3D11 4.2.0 (declared TFMs, deps, 2018-08-24): https://www.nuget.org/packages/SharpDX.Direct3D11/4.2.0
- SharpDX.DXGI 4.2.0: https://www.nuget.org/packages/SharpDX.DXGI/
- SharpDX (core, archived/unmaintained): https://www.nuget.org/packages/SharpDX
- NU1701 warning semantics (restore-via-different-framework): https://learn.microsoft.com/en-us/nuget/reference/errors-and-warnings/nu1701
- .NET / .NET Core official support policy (net8 EOL 2026-11-10; net10 LTS to 2028-11-14): https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- Announcing .NET 10 (LTS, GA 2025-11-11): https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/
- Breaking changes in .NET 10: https://learn.microsoft.com/en-us/dotnet/core/compatibility/10
- What's new in .NET 10 (WPF Fluent styles, perf): https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview
- Upgrade a WPF app guidance (TFM bump pattern): https://learn.microsoft.com/en-us/dotnet/desktop/wpf/migration/
- Self-contained publish WPF-assembly omission regression (net9 class of issue): https://github.com/dotnet/sdk/issues/43461
- Vortice.Windows (repo, active maintenance, API list): https://github.com/amerkoleci/Vortice.Windows
- Vortice.Direct3D11 3.8.3 (net8/net9/net10 TFMs, 2026-03-04): https://www.nuget.org/packages/Vortice.Direct3D11/
- Vortice.Direct3D9: https://www.nuget.org/packages/Vortice.Direct3D9
- Vortice.DXGI: https://www.nuget.org/packages/Vortice.DXGI/
- Community: replacing SharpDX with Vortice.Windows: https://github.com/amerkoleci/Vortice.Windows/discussions/465
- D3DImage.SetBackBuffer (IDirect3DSurface9, IntPtr) API: https://learn.microsoft.com/en-us/dotnet/api/system.windows.interop.d3dimage.setbackbuffer

### Repo files reviewed
- `MacMirrorReceiver.csproj` (net8.0-windows, SharpDX 4.2.0 x3, UseWPF/UseWindowsForms)
- `MacMirrorReceiver.Video/D3D11VideoProcessorD3DImagePresenter.cs` (D3D11→D3D9→D3DImage bridge; shared handle; SetBackBuffer/NativePointer; front-buffer loss handling)
- `MacMirrorReceiver.Video/D3D11SwapChainVideoPresenter.cs` (VideoProcessorBlt path)
- `MacMirrorReceiver.Video/MediaFoundationD3D11Decoder.cs`, `D3D11VideoFrame.cs`
- `tools/*` probe csprojs (all net8.0-windows, SharpDX 4.2.0)
- `.github/workflows/ci.yml` (windows-latest, setup-dotnet 8.0.x, restore/build/test iMirror.sln, no -warnaserror)
- `docs/windows-e2e-validation.md` (existing real-device E2E + latency-gate conventions)
