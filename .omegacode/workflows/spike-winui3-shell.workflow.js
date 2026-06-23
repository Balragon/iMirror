// ─────────────────────────────────────────────────────────────────────────────
// SPIKE / RESEARCH WORKFLOW — investigate whether WinUI 3 is a viable future shell.
//
// BEFORE RUNNING (do this yourself; this workflow does NOT touch git):
//   git checkout main
//   git checkout -b spike/winui3-shell        # cut from main, NOT from ui/wpf-fluent-refresh
//
// This is an EXPERIMENT branch. Its only deliverable is findings. It is NOT a
// migration and is NOT meant to be merged.
//
//   - Do NOT delete or replace the existing WPF app.
//   - Do NOT change the production entry point (App.cs / Main / MainWindow).
//   - Do NOT do a full UI migration or port the settings window.
//   - Do NOT polish design.
//   - All prototype code lives ONLY under experiments/winui3-shell/.
//   - Do NOT add the prototype to the main .sln or modify MacMirrorReceiver.csproj.
//   - The ONLY question that matters: can the existing VIDEO interop survive in WinUI 3?
//   - Even if the spike "works", do NOT merge it. Leave findings only.
// ─────────────────────────────────────────────────────────────────────────────

export const meta = {
  name: 'spike-winui3-shell',
  description: 'Throwaway spike: can iMirror video interop (WriteableBitmap + HwndHost/D3D swap-chain) survive a WinUI 3 shell? Findings only — do not merge.',
  phases: [
    { title: 'Preflight', detail: 'Confirm branch is spike/winui3-shell cut from main (read-only)' },
    { title: 'Scout', detail: 'Extract the exact video interop architecture that would need to migrate' },
    { title: 'Spike', detail: 'Minimal WinUI 3 prototype under experiments/winui3-shell/ + interop feasibility probe' },
    { title: 'Document', detail: 'Write docs/winui3-migration-spike.md (tried/worked/failed/unknown/risks/preconditions)' },
  ],
}

// ── Phase 1: Preflight ───────────────────────────────────────────────────────
phase('Preflight')
log('Confirming branch is the spike branch cut from main (read-only git only)...')

const pre = await agent(
  `Inspect repository state with READ-ONLY git commands. Do NOT checkout, branch, stash, or edit.

  1. Current branch:            git rev-parse --abbrev-ref HEAD
  2. Is the spike a descendant of main (cut from main)?:
        git merge-base --is-ancestor main HEAD && echo yes || echo no
  3. Working tree dirty?:       git status --porcelain (non-empty => dirty)

Return JSON.`,
  {
    sandbox: 'read-only',
    schema: {
      type: 'object',
      properties: {
        currentBranch: { type: 'string' },
        cutFromMain: { type: 'boolean' },
        dirty: { type: 'boolean' },
      },
      required: ['currentBranch', 'cutFromMain', 'dirty'],
    },
  }
)

log(`Branch: ${pre.currentBranch} | cut from main: ${pre.cutFromMain} | dirty: ${pre.dirty}`)
if (pre.currentBranch !== 'spike/winui3-shell') {
  throw new Error(`Run this workflow only on spike/winui3-shell; current branch is ${pre.currentBranch}.`)
}
if (pre.dirty) {
  throw new Error('Working tree must be clean before running spike-winui3-shell.')
}
if (!pre.cutFromMain) {
  throw new Error('The spike branch must be cut from main.')
}

// ── Phase 2: Scout ───────────────────────────────────────────────────────────
phase('Scout')
log('Extracting the video interop architecture that any WinUI 3 shell would have to reproduce...')

const scout = await agent(
  `Read these files and return a precise architectural map of the VIDEO INTEROP. READ-ONLY.

  1. MacMirrorReceiver/MainWindow.cs   (focus on the rendering members only)
  2. MacMirrorReceiver.Video/D3D11SwapChainVideoPresenter.cs
  3. MacMirrorReceiver.csproj
  4. MacMirrorReceiver/App.cs

Return:
  - stablePath: how the software/stable path puts pixels on screen (WriteableBitmap details:
    pixel format, how VideoImage.Source is set, on which thread/dispatcher) — 3-5 sentences
  - highResPath: how the high-res path works (MediaFoundation D3D11 decode -> NV12 ->
    D3D11SwapChainVideoPresenter as an HwndHost child HWND with a DXGI flip-model swap chain) —
    3-5 sentences
  - hwndHostMechanics: exactly what D3D11SwapChainVideoPresenter overrides from HwndHost
    (BuildWindowCore/DestroyWindowCore, the child HWND creation, WS_CHILD style) — be specific
  - sharpDxUsage: which SharpDX packages/types are used (Direct3D11, DXGI, video processor) and
    the SharpDX assembly-resolver shim in App.cs
  - wpfCouplings: every WPF-specific type the interop depends on (HwndHost, WriteableBitmap,
    Dispatcher, PixelFormats, etc.) — these are the things that have no 1:1 WinUI 3 equivalent
  - interopRiskNotes: where the migration risk concentrates (1-3 sentences)`,
  {
    sandbox: 'read-only',
    schema: {
      type: 'object',
      properties: {
        stablePath: { type: 'string' },
        highResPath: { type: 'string' },
        hwndHostMechanics: { type: 'string' },
        sharpDxUsage: { type: 'string' },
        wpfCouplings: { type: 'array', items: { type: 'string' } },
        interopRiskNotes: { type: 'string' },
      },
      required: ['stablePath', 'highResPath', 'hwndHostMechanics', 'sharpDxUsage', 'wpfCouplings', 'interopRiskNotes'],
    },
  }
)

log(`WPF couplings to replace in WinUI 3: ${scout.wpfCouplings.join(', ')}`)
log(`Interop risk: ${scout.interopRiskNotes}`)

// ── Phase 3: Spike (strictly sandboxed to experiments/winui3-shell/) ──────────
phase('Spike')
log('Attempting a minimal WinUI 3 prototype + interop feasibility probe (isolated folder only)...')

const spike = await agent(
  `Build a MINIMAL WinUI 3 spike to test ONLY video-interop feasibility. This is throwaway.

ABSOLUTE CONSTRAINTS — violating any of these fails the task:
  - Create files ONLY under experiments/winui3-shell/. Create that directory if needed.
  - Do NOT modify ANY existing file: not MacMirrorReceiver.csproj, not App.cs, not MainWindow.*,
    not the .sln, not SharedResources.xaml, nothing outside experiments/winui3-shell/.
  - Do NOT add the prototype project to the main solution.
  - Do NOT touch FFmpeg/TCP/timing/decoder/D3D production code.
  - This is NOT a migration. Do NOT port the settings window or do any design polish.

CONTEXT — the production video interop you are testing the feasibility of (do not copy its
production code; reason about whether WinUI 3 can host an equivalent):
  - Stable path: ${scout.stablePath}
  - High-res path: ${scout.highResPath}
  - HwndHost mechanics: ${scout.hwndHostMechanics}
  - SharpDX usage: ${scout.sharpDxUsage}
  - WPF couplings with no obvious WinUI 3 equivalent: ${scout.wpfCouplings.join(', ')}

WHAT TO ATTEMPT (best-effort; if tooling is missing, that is itself a finding — record it,
do not fail the whole task):
  1. Check tooling: is the Windows App SDK / WinUI 3 template available?
     Run: dotnet new list 2>&1   (look for a WinUI / Windows App SDK template)
     Record the exact availability result.
  2. If a WinUI 3 template IS available, scaffold the smallest possible WinUI 3 app INSIDE
     experiments/winui3-shell/ (e.g. dotnet new <winui-template> -o experiments/winui3-shell).
     Do NOT add it to the main .sln. Then assess (build it only if it is self-contained and safe):
        a. Is there a SwapChainPanel (Microsoft.UI.Xaml.Controls.SwapChainPanel) that can host a
           DXGI flip-model swap chain equivalent to the current HwndHost presenter?
        b. Is there a WriteableBitmap equivalent (Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap /
           SoftwareBitmapSource) for the software path?
        c. Can a native child HWND be hosted (e.g. via Microsoft.UI.Content / DesktopWindowXamlSource
           or HWND interop), or must the HwndHost approach be redesigned around SwapChainPanel?
     If the template is NOT available, do the assessment as a documentation-only feasibility
     analysis grounded in the production architecture above — clearly labeled as "not built".
  3. Add a experiments/winui3-shell/README.md noting this is a throwaway spike, not for merge.

Return JSON findings (be honest; "unknown" is a valid value):
  - toolingAvailable: boolean (WinUI 3 / Windows App SDK template present)
  - toolingDetail: string (what dotnet new list showed)
  - scaffolded: boolean (did you actually create a WinUI 3 project)
  - builtSuccessfully: string ("yes" | "no" | "not-attempted")
  - swapChainPanelViable: string ("yes" | "no" | "unknown") with a one-line reason inline
  - writeableBitmapEquivalent: string ("yes" | "no" | "unknown") with a one-line reason inline
  - nativeChildHwndHosting: string ("yes" | "no" | "needs-redesign" | "unknown") with reason inline
  - presenterPortability: string — can D3D11SwapChainVideoPresenter:HwndHost be carried over
    as-is, or must it be redesigned around SwapChainPanel? Explain in 2-4 sentences.
  - tried: array of strings (concrete things you attempted)
  - worked: array of strings
  - failed: array of strings
  - unknown: array of strings (open questions the spike could not resolve)
  - filesCreated: array of paths created under experiments/winui3-shell/`,
  {
    sandbox: 'workspace-write',
    schema: {
      type: 'object',
      properties: {
        toolingAvailable: { type: 'boolean' },
        toolingDetail: { type: 'string' },
        scaffolded: { type: 'boolean' },
        builtSuccessfully: { type: 'string' },
        swapChainPanelViable: { type: 'string' },
        writeableBitmapEquivalent: { type: 'string' },
        nativeChildHwndHosting: { type: 'string' },
        presenterPortability: { type: 'string' },
        tried: { type: 'array', items: { type: 'string' } },
        worked: { type: 'array', items: { type: 'string' } },
        failed: { type: 'array', items: { type: 'string' } },
        unknown: { type: 'array', items: { type: 'string' } },
        filesCreated: { type: 'array', items: { type: 'string' } },
      },
      required: [
        'toolingAvailable', 'toolingDetail', 'scaffolded', 'builtSuccessfully',
        'swapChainPanelViable', 'writeableBitmapEquivalent', 'nativeChildHwndHosting',
        'presenterPortability', 'tried', 'worked', 'failed', 'unknown', 'filesCreated',
      ],
    },
  }
)

log(`Tooling available: ${spike.toolingAvailable} | scaffolded: ${spike.scaffolded} | built: ${spike.builtSuccessfully}`)
log(`SwapChainPanel: ${spike.swapChainPanelViable} | WriteableBitmap eq: ${spike.writeableBitmapEquivalent} | child HWND: ${spike.nativeChildHwndHosting}`)

// ── Phase 4: Document ────────────────────────────────────────────────────────
phase('Document')
log('Writing docs/winui3-migration-spike.md (findings only — do not merge)...')

await agent(
  `Create docs/winui3-migration-spike.md — the findings report for the WinUI 3 shell spike.

Use these inputs verbatim where relevant.

ARCHITECTURE UNDER TEST (from scout):
  - Stable path: ${scout.stablePath}
  - High-res path: ${scout.highResPath}
  - HwndHost mechanics: ${scout.hwndHostMechanics}
  - SharpDX usage: ${scout.sharpDxUsage}
  - WPF couplings: ${scout.wpfCouplings.join(', ')}
  - Interop risk: ${scout.interopRiskNotes}

SPIKE RESULTS (from the spike phase):
  - Tooling available: ${spike.toolingAvailable} — ${spike.toolingDetail}
  - Scaffolded a WinUI 3 project: ${spike.scaffolded}
  - Built successfully: ${spike.builtSuccessfully}
  - SwapChainPanel viable: ${spike.swapChainPanelViable}
  - WriteableBitmap equivalent: ${spike.writeableBitmapEquivalent}
  - Native child HWND hosting: ${spike.nativeChildHwndHosting}
  - Presenter portability: ${spike.presenterPortability}
  - Tried: ${spike.tried.join('; ')}
  - Worked: ${spike.worked.join('; ')}
  - Failed: ${spike.failed.join('; ')}
  - Unknown: ${spike.unknown.join('; ')}
  - Files created: ${spike.filesCreated.join(', ')}

The document MUST have these sections, in this order:
  1. **Status banner** — a bold line at the very top:
     "> SPIKE / RESEARCH ONLY. Do not merge directly; use findings only."
  2. **Goal** — what question this spike answers (can iMirror's video interop survive WinUI 3?).
  3. **What was tried**
  4. **What worked**
  5. **What failed**
  6. **What is still unknown**
  7. **Video interop risk** — the central risk: HwndHost + DXGI swap chain + SharpDX, and the
     WPF couplings that have no 1:1 WinUI 3 equivalent. Address the four key questions explicitly:
       - Can the existing video output structure be kept inside a WinUI 3 shell?
       - Is a WriteableBitmap-equivalent path available?
       - Is HWND / native child window hosting possible?
       - Can D3D11SwapChainVideoPresenter:HwndHost be carried over as-is, or is a SwapChainPanel
         redesign required?
  8. **Preconditions for an actual migration** — the concrete things that must be PROVEN before
     anyone commits to a real WinUI 3 migration (e.g. a working SwapChainPanel present path at
     full frame rate, a verified software path, parity on latency).
  9. **Recommendation** — given the WPF-UI refresh already addresses the product UI goals, state
     whether WinUI 3 is worth pursuing now, later, or not at all, and why.
  10. **Policy** — restate: "Do not merge directly; use findings only." Explain that the
      experiments/winui3-shell/ folder is a throwaway prototype, never wired into production.

Keep it focused and honest — clearly mark anything that was reasoned about but not actually built.
Write ONLY docs/winui3-migration-spike.md.`,
  { sandbox: 'workspace-write' }
)

log('Spike complete. Read docs/winui3-migration-spike.md. Do NOT merge this branch — findings only.')
return spike
