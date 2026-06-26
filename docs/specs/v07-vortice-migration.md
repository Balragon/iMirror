# v0.7 — SharpDX → Vortice.Windows: Execution Note

**Tracked by:** roadmap `docs/specs/v05-plus-roadmap.md` (v0.7) · **Status:**
prepped · **Deadline:** none (SharpDX works on net10; this is modernization, not a
forcing function).

The *analysis* (why Vortice, maintenance status, full API-coverage table, risk
surface) is in `docs/dotnet-strategy.md` §"Vortice.Windows replacement assessment"
— read it once; this note does not repeat it. Here: the order, the exact per-file
edits, the feature-flag strategy, and the gates.

---

## Non-negotiable approach (from dotnet-strategy.md)

1. **Validated against a known-good net10 baseline.** v0.5.0 (net10 + SharpDX) is
   soak-validated. Every Vortice change is measured against it, so any GPU
   regression is attributable to the binding swap and nothing else.
2. **Prototype the load-bearing bridge FIRST.** `D3DImage.SetBackBuffer` takes a
   raw `IntPtr` to an `IDirect3DSurface9`. Vortice COM wrappers expose
   `.NativePointer` (`IntPtr`) just like SharpDX, so the bridge *should* port — but
   there is no official Vortice `D3DImage` sample. **Prove this one call path on
   hardware before porting anything else.**
3. **Feature-flag one presenter, validate, then convert the rest.** Keep SharpDX
   in the tree during the transition; select the Vortice presenter via env var so
   the two implementations can be A/B'd on the same hardware in one session.
4. **Bisectability boundary.** This change swaps the GPU binding ONLY. Do **not**
   fold A/V-sync work (Issue #3) into the same validation window — a Vortice GPU
   regression and an audio-sync change must stay independently bisectable.

---

## 1. Package swap (3 csproj package groups)

`SharpDX.*` 4.2.0 → `Vortice.*` (latest stable, currently **3.8.3**, targets
net8/9/10 — confirmed in dotnet-strategy.md):

| SharpDX package | Vortice package |
|---|---|
| `SharpDX.Direct3D11` | `Vortice.Direct3D11` |
| `SharpDX.Direct3D9` | `Vortice.Direct3D9` |
| `SharpDX.DXGI` | `Vortice.DXGI` |
| `SharpDX.Mathematics` (`RawRectangle`) | `Vortice.Mathematics` (`RawRect`/`Rectangle`) |

Affected csprojs: `MacMirrorReceiver.csproj`, `tools/D3DSharedHandleProbe`,
`tools/D3DVideoProcessorProbe`, `tools/HighResolutionD3DReplayProbe`,
`tools/MediaFoundationH264Probe`. **During the feature-flag phase, both SharpDX and
Vortice packages are referenced** so the two presenters coexist; SharpDX is removed
only in the final cleanup commit once the Vortice path is hardware-validated.

NU1701 (SharpDX netstandard1.1 fallback) disappears once SharpDX is gone — a
secondary benefit.

---

## 2. The four renderer files (concentration of work)

`grep` confirms SharpDX is used in exactly these product files (plus 2 probes):

| File | Lines | SharpDX surface | Port difficulty |
|---|---|---|---|
| `D3D11VideoFrame.cs` | 37 | `D3D11.Texture2D` type only | trivial (`ID3D11Texture2D`) |
| `D3D11VideoProcessorD3DImagePresenter.cs` | 365 | **the load-bearing one** | HIGH — prototype first |
| `D3D11SwapChainVideoPresenter.cs` | 364 | D3D11 + DXGI swapchain | MEDIUM |
| `MediaFoundationD3D11Decoder.cs` | 1435 | D3D11 device/texture + MF interop | MEDIUM (mostly type renames) |

### Confirmed API mapping for the load-bearing presenter

From `D3D11VideoProcessorD3DImagePresenter.cs` (the exact calls to port):

| SharpDX (current) | Vortice equivalent |
|---|---|
| `new D3D11.Device(DriverType.Hardware, flags)` | `D3D11.D3D11CreateDevice(...)` → `ID3D11Device` (factory function, not ctor) |
| `device.QueryInterface<D3D11.VideoDevice>()` | `device.QueryInterface<ID3D11VideoDevice>()` |
| `device.ImmediateContext.QueryInterface<D3D11.VideoContext>()` | `context.QueryInterface<ID3D11VideoContext>()` |
| `device.QueryInterface<D3D11.Multithread>()` | `ID3D11Multithread` |
| `new D3D9.Direct3DEx()` / `new D3D9.DeviceEx(...)` | `D3D9.Direct3D9.Direct3DCreate9Ex()` → `IDirect3D9Ex` / `IDirect3DDevice9Ex` |
| `new D3D11.Texture2D(device, desc)` | `device.CreateTexture2D(desc)` → `ID3D11Texture2D` |
| `texture.QueryInterface<DXGI.Resource>().SharedHandle` | `texture.QueryInterface<IDXGIResource>().SharedHandle` |
| `new D3D9.Texture(device, w, h, 1, Usage.RenderTarget, Format.A8R8G8B8, Pool.Default, ref handle)` | `device.CreateTexture(...)` → `IDirect3DTexture9` (out/ref shared-handle param) |
| `d3d9Texture.GetSurfaceLevel(0)` | `IDirect3DTexture9.GetSurfaceLevel(0)` → `IDirect3DSurface9` |
| `surface.NativePointer` → `ImageSource.SetBackBuffer(D3DResourceType.IDirect3DSurface9, ptr)` | Vortice `.NativePointer` — **identical IntPtr contract; this is the prototype target** |
| `RawRectangle(...)` | `Vortice.Mathematics.RawRect` / `Rectangle` |
| video processor: `CreateVideoProcessorEnumerator`/`CreateVideoProcessor`/`VideoProcessorBlt`, input/output views, `VideoProcessorStream` | same names on `ID3D11VideoDevice`/`ID3D11VideoContext`; structs move to `Vortice.Direct3D11` namespace |

**Call-style differences to watch:** Vortice uses factory *functions*
(`D3D11CreateDevice`, `Direct3DCreate9Ex`) instead of constructors; many `out`
parameters become return values; COM objects are `ID*` interfaces. Enum/struct
names are near-identical but live in Vortice namespaces.

---

## 3. Feature flag

Add an env-gated selector where the presenter is constructed (engine wiring in
`MacMirrorReceiver.Video`):

```
IMIRROR_GPU_BINDING=vortice   # opt into the Vortice presenter
# default/unset = SharpDX presenter (unchanged v0.5.0 behaviour)
```

This lets Gate B A/B the two bindings on the *same* device in one session, which is
the only way to attribute a regression to the swap. Remove the flag (and SharpDX)
in the final cleanup commit.

---

## 4. Gate A — build/restore (hosted `windows-latest`)

- [ ] `dotnet restore iMirror.sln` succeeds with Vortice added; **NU1701 for
      SharpDX still present** (both packages referenced during transition).
- [ ] `dotnet build iMirror.sln -c Release` succeeds; resolve Vortice
      namespace/call-style differences until clean.
- [ ] `dotnet test` green (existing tests are binding-agnostic).
- [ ] `publish-win-x64.ps1 -AllowMissingFfmpeg -NoZip` passes the WPF-assembly
      check (already CI-enforced in `ci.yml`).

**Gate A proves it compiles and packages — it does NOT prove the GPU bridge works.**

## 5. Gate B — GPU present path (REQUIRES real device + GPU)

Run the *same* checklist as net10 Gate B (`docs/dotnet-strategy.md` Part B), but
**A/B against the SharpDX presenter via `IMIRROR_GPU_BINDING`**:

- [ ] **Prototype gate (do this before porting the other 3 files):** Vortice
      `IDirect3DSurface9.NativePointer` → `D3DImage.SetBackBuffer` shows live,
      updating video.
- [ ] Keyframe renders via the Vortice `D3D11VideoProcessorD3DImagePresenter`.
- [ ] `D3DImage` front-buffer loss/restore recovers (lock/RDP/fast-user-switch).
- [ ] GPU probes pass on Vortice builds: `D3DVideoProcessorProbe`,
      `D3DSharedHandleProbe`, `MediaFoundationH264Probe`,
      `HighResolutionD3DReplayProbe`.
- [ ] Software fallback unaffected (`IMIRROR_FORCE_SOFTWARE_VIDEO=1`).
- [ ] ≥1-hour soak (`scripts/soak-gate.ps1`): no VRAM/handle growth vs. the SharpDX
      baseline, no stalls, no crash.
- [ ] Latency p95 ≤ 150 ms (`tools/LatencyAcceptanceReport`), parity with SharpDX.
- [ ] 3× connect/disconnect/reconnect: clean GPU-resource teardown.

## 6. Cleanup (only after Gate B passes)

- [ ] Remove SharpDX package references and the `IMIRROR_GPU_BINDING` flag; make
      Vortice the only path.
- [ ] `CHANGELOG.md`: v0.7 = GPU binding modernization (SharpDX → Vortice).
- [ ] Confirm NU1701 is gone.

---

## Suggested commit sequence

1. Add Vortice packages alongside SharpDX (Gate A still green on unchanged code).
2. Port `D3D11VideoFrame.cs` (trivial shared type) — keep building.
3. Add the Vortice `D3D11VideoProcessorD3DImagePresenter` behind the flag.
   **→ Gate B prototype gate here.**
4. Port `D3D11SwapChainVideoPresenter.cs` and `MediaFoundationD3D11Decoder.cs`.
5. Port the two `tools/*` probes.
6. Full Gate B, then the cleanup commit (drop SharpDX).

---

## Reference map

| Thing | Location |
|---|---|
| Deep analysis / API-coverage table / risk | `docs/dotnet-strategy.md` §Vortice |
| Load-bearing bridge | `MacMirrorReceiver.Video/D3D11VideoProcessorD3DImagePresenter.cs:281-322` (`AttachOutputToD3DImage`) |
| Front-buffer loss/restore | same file `:185-212, :331` |
| Soak gate | `scripts/soak-gate.ps1`, `docs/soak-gate.md` |
| Real-device E2E + latency | `docs/windows-e2e-validation.md` |

**Boundary reminder:** GPU binding only. Keep Issue #3 (A/V sync) out of this
validation window so a GPU regression stays bisectable.
