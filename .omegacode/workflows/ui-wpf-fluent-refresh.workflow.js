// ─────────────────────────────────────────────────────────────────────────────
// PRODUCT BRANCH WORKFLOW — modernize the existing WPF app with WPF-UI (Fluent).
//
// BEFORE RUNNING (do this yourself; this workflow does NOT touch git):
//   git checkout -b ui/wpf-fluent-refresh
//
// This is a real product-improvement branch. It keeps the existing WPF app and
// restyles its shell with the WPF-UI library (lepoco/wpfui): Mica window, a
// Fluent TitleBar, a polished empty state, and a Card/ToggleSwitch settings page.
//
// RENDERING GUARDRAILS (hard constraints — every agent is told these):
//   - Stable path:  WriteableBitmap -> WPF Image control. Mica/WPF-UI compatible.
//   - High-res path: D3D11SwapChainVideoPresenter : HwndHost (HIGH_RESOLUTION_D3D,
//                    Quality mode + >1080p). Airspace-sensitive.
//   - Mica/Acrylic/blur/transparency: ONLY on TitleBar, EmptyState, SettingsWindow,
//     and ordinary cards/panels. NEVER on the live video surface.
//   - During mirroring VideoStage MUST be opaque black. It may be transparent ONLY
//     while disconnected (empty state) so Mica shows behind the empty-state art.
//   - Do NOT touch: FFmpeg decode, TCP receive, frame timing, decoder fallback,
//     D3D presenter behavior, or D3D11SwapChainVideoPresenter.cs.
//   - Do NOT add any WPF overlay element as a child of, or on top of, the HwndHost.
//
// App bootstrap note: there is NO App.xaml. App.cs is a code-only Application with
// a manual Main(). WPF-UI theme dictionaries are therefore merged in App.cs CODE,
// not in XAML.
// ─────────────────────────────────────────────────────────────────────────────

export const meta = {
  name: 'ui-wpf-fluent-refresh',
  description: 'Modernize the existing WPF app with WPF-UI (Mica, Fluent TitleBar, empty state, settings) without touching the video pipeline',
  phases: [
    { title: 'Preflight', detail: 'Report git branch / working-tree state and confirm guardrail-sensitive files' },
    { title: 'Scout', detail: 'Map MainWindow, SettingsWindow, App.cs bootstrap, and off-limits rendering files' },
    { title: 'Foundation', detail: 'Add WPF-UI NuGet and merge theme dictionaries in App.cs (code-only, no App.xaml)' },
    { title: 'Implement', detail: 'Parallel: MainWindow Fluent shell, SettingsWindow Fluent redesign, guardrails doc' },
    { title: 'Verify', detail: 'dotnet build + test, then write the verification checklist doc' },
  ],
}

// ── Phase 1: Preflight ───────────────────────────────────────────────────────
phase('Preflight')
log('Checking git branch and working-tree state (no git mutations performed)...')

const pre = await agent(
  `Inspect the repository state and report it. Run read-only git commands only; do NOT
checkout, create branches, stash, or modify anything.

1. Current branch:        git rev-parse --abbrev-ref HEAD
2. Working tree dirty?:    git status --porcelain  (non-empty output => dirty)
3. Confirm these guardrail-sensitive files exist (so later agents know what to avoid):
   - MacMirrorReceiver.Video/D3D11SwapChainVideoPresenter.cs
   - MacMirrorReceiver/MainWindow.cs
   - MacMirrorReceiver/App.cs

Return JSON.`,
  {
    sandbox: 'read-only',
    schema: {
      type: 'object',
      properties: {
        currentBranch: { type: 'string' },
        dirty: { type: 'boolean' },
        guardrailFilesPresent: { type: 'boolean' },
        notes: { type: 'string' },
      },
      required: ['currentBranch', 'dirty', 'guardrailFilesPresent', 'notes'],
    },
  }
)

log(`Branch: ${pre.currentBranch} | dirty: ${pre.dirty}`)
if (pre.currentBranch !== 'ui/wpf-fluent-refresh') {
  log(`WARNING: expected branch 'ui/wpf-fluent-refresh' but on '${pre.currentBranch}'. ` +
      `Stop and run: git checkout -b ui/wpf-fluent-refresh  — then re-run this workflow.`)
}
if (!pre.guardrailFilesPresent) {
  log('WARNING: a guardrail-sensitive file is missing; the repo layout may have changed.')
}

// ── Phase 2: Scout ───────────────────────────────────────────────────────────
phase('Scout')
log('Mapping the UI surface and the off-limits rendering code...')

const scout = await agent(
  `Read these files in full and return a structured map. READ-ONLY — do not edit anything.

  1. MainWindow.xaml                              (project root)
  2. MacMirrorReceiver/MainWindow.cs              (code-behind, very large)
  3. MacMirrorReceiver/SettingsWindow.xaml
  4. MacMirrorReceiver/SettingsWindow.xaml.cs
  5. MacMirrorReceiver/App.cs                     (code-only Application, no App.xaml)
  6. MacMirrorReceiver/SharedResources.xaml
  7. MacMirrorReceiver.csproj

Return:
  - rootElementMain: the root element tag of MainWindow.xaml (e.g. "Window")
  - mainWindowClassDecl: the exact C# class declaration line in MainWindow.cs (e.g. "public partial class MainWindow : Window, ISettingsHost")
  - settingsWindowClassDecl: the exact C# class declaration line in SettingsWindow.xaml.cs
  - videoStageBackground: the current Background value of the VideoStage Grid
  - emptyStateNames: x:Name values inside EmptyStatePanel
  - statusPillName: the x:Name of the floating status chip border (the frosted pill at top-center)
  - settingsControls: a list of {type, xName} for every CheckBox, RadioButton, TextBox, Slider, Button, Expander in SettingsWindow.xaml that is a candidate for a Fluent equivalent
  - appBootstrap: 2-3 sentences on HOW App.cs constructs/merges resources today (constructor, OnStartup, MainWindow creation) so we know where to merge WPF-UI dictionaries
  - offLimitsSymbols: the names of the rendering symbols in MainWindow.cs that must NOT be modified (decoder/presenter/timing fields and methods, e.g. _highResolutionD3DPresenter, _mediaFoundationD3DDecoder, StartFreshDecoder, PresentFrameToWriteableBitmap, QueueD3DFrameForPresentation, TryStartHighResolutionD3DPath)
  - connectDisconnectHooks: where in MainWindow.cs EmptyStatePanel.Visibility is toggled on connect vs disconnect (method names + line context) — this is where a SAFE VideoStage background swap can be added`,
  {
    sandbox: 'read-only',
    schema: {
      type: 'object',
      properties: {
        rootElementMain: { type: 'string' },
        mainWindowClassDecl: { type: 'string' },
        settingsWindowClassDecl: { type: 'string' },
        videoStageBackground: { type: 'string' },
        emptyStateNames: { type: 'array', items: { type: 'string' } },
        statusPillName: { type: 'string' },
        settingsControls: {
          type: 'array',
          items: {
            type: 'object',
            properties: { type: { type: 'string' }, xName: { type: 'string' } },
            required: ['type', 'xName'],
          },
        },
        appBootstrap: { type: 'string' },
        offLimitsSymbols: { type: 'array', items: { type: 'string' } },
        connectDisconnectHooks: { type: 'string' },
      },
      required: [
        'rootElementMain', 'mainWindowClassDecl', 'settingsWindowClassDecl',
        'videoStageBackground', 'emptyStateNames', 'statusPillName',
        'settingsControls', 'appBootstrap', 'offLimitsSymbols', 'connectDisconnectHooks',
      ],
    },
  }
)

log(`MainWindow root: <${scout.rootElementMain}>  | class: ${scout.mainWindowClassDecl}`)
log(`Settings controls to convert: ${scout.settingsControls.map(c => `${c.type}:${c.xName}`).join(', ')}`)
log(`Off-limits symbols: ${scout.offLimitsSymbols.join(', ')}`)

// ── Phase 3: Foundation (single agent — must finish before any UI edit) ───────
phase('Foundation')
log('Adding WPF-UI NuGet and merging theme dictionaries in App.cs...')

await agent(
  `Add the WPF-UI library and initialize its theme — code-only, because this project has NO App.xaml.

CONTEXT
  - csproj: MacMirrorReceiver.csproj (net8.0-windows, x64, UseWPF=True, UseWindowsForms=True)
  - App bootstrap today: ${scout.appBootstrap}
  - App.cs is "public class App : Application" with a manual static Main() that does "new App().Run()".

TASKS (edit ONLY MacMirrorReceiver.csproj and MacMirrorReceiver/App.cs):

1. In MacMirrorReceiver.csproj, add a PackageReference to the WPF-UI library:
       <PackageReference Include="WPF-UI" Version="3.0.5" />
   Add it inside an existing <ItemGroup> that holds PackageReferences (or a new one).
   Do NOT change any other package, the TargetFramework, PlatformTarget, DefineConstants,
   or the HIGH_RESOLUTION_D3D constant.

2. In MacMirrorReceiver/App.cs, merge the WPF-UI control + theme dictionaries into the
   Application's resources in CODE (there is no App.xaml). Do this once at startup —
   the cleanest place is the App constructor AFTER the existing AppLog.Write("App constructed.")
   line, or at the very top of OnStartup BEFORE the MainWindow is created. Use a dark theme
   with Mica:

       Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());
       Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary
       {
           Theme = Wpf.Ui.Appearance.ApplicationTheme.Dark,
       });

   Add the necessary using or use fully-qualified names. Keep ShutdownMode,
   the SharpDX assembly resolver, the unhandled-exception handlers, the
   HighResolutionPipelineProbe call, and the MainWindow creation/show logic EXACTLY as-is.

ABSOLUTELY DO NOT:
  - touch any file other than the two named above
  - modify any decoder, TCP, timing, or D3D code
  - change how MainWindow is constructed or shown

Report what you changed in 3-4 lines.`,
  { sandbox: 'workspace-write' }
)

// ── Phase 4: Implement (3 parallel agents, disjoint file sets) ────────────────
phase('Implement')
log('Restyling MainWindow + SettingsWindow and writing the guardrails doc in parallel...')

await parallel([

  // ── A. MainWindow Fluent shell ──────────────────────────────────────────────
  () => agent(
    `Convert MainWindow into a WPF-UI FluentWindow with a Fluent TitleBar, Mica backdrop,
and a polished empty state — WITHOUT disturbing the video pipeline.

You may edit ONLY: MainWindow.xaml (project root) and MacMirrorReceiver/MainWindow.cs.

CURRENT STATE
  - MainWindow.xaml root: <${scout.rootElementMain}>
  - Code-behind class: ${scout.mainWindowClassDecl}
  - VideoStage Background today: ${scout.videoStageBackground}
  - Empty-state element names: ${scout.emptyStateNames.join(', ')}
  - Floating status pill name: ${scout.statusPillName}
  - Connect/disconnect hooks (where empty state is toggled): ${scout.connectDisconnectHooks}

HARD RENDERING GUARDRAILS — violating any of these fails the task:
  - Do NOT modify these symbols or any decode/timing/present logic in MainWindow.cs:
    ${scout.offLimitsSymbols.join(', ')}
  - Do NOT add Mica/Acrylic/blur/transparency to VideoStage, VideoImage, or the HwndHost.
  - Do NOT add any new child element to VideoStage that sits over the video, except the
    EmptyStatePanel and status pill that already exist.
  - During mirroring VideoStage MUST be opaque black (#000000).

REQUIRED CHANGES

1. XAML root -> FluentWindow:
   - Add xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml"
   - Change the root element from Window to ui:FluentWindow.
   - On the root set: ExtendsContentIntoTitleBar="True" and WindowBackdropType="Mica".
   - Keep x:Class, Title, Width/Height/MinWidth/MinHeight, WindowStartupLocation,
     FontFamily, all event handlers (Loaded/Closing/SizeChanged/KeyDown),
     UseLayoutRounding, SnapsToDevicePixels exactly as they are.
   - Keep the merged SharedResources.xaml dictionary and the MainWindow-only styles.

2. Code-behind:
   - Change the base type in the class declaration from Window to
     Wpf.Ui.Controls.FluentWindow (keep ISettingsHost and any other interfaces).
     Current line: ${scout.mainWindowClassDecl}
   - Add "using Wpf.Ui.Controls;" if helpful, or fully-qualify.
   - In the constructor, after InitializeComponent(), call
     Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this); so the Mica backdrop is applied.
   - Do NOT change anything else in the code-behind except, in #4 below, the SAFE
     background swap at the existing connect/disconnect hooks.

3. Layout + TitleBar:
   - Wrap the content in a Grid with two rows: Row 0 = Auto (the title bar), Row 1 = * (content).
   - Row 0: a <ui:TitleBar Title="iMirror" Grid.Row="0"/> (it provides drag + minimize/
     maximize/close that match the Mica chrome). Put the existing root content Grid into Row 1.
   - The video stack (VideoStage with VideoImage, RemoteCursorLayer, EmptyStatePanel) and the
     status pill stay in Row 1, unchanged in structure.

4. Mica-behind-empty-state (the ONLY allowed VideoStage background change):
   - VideoStage must be transparent ONLY while disconnected (empty state visible) so Mica
     shows through behind the empty-state art, and opaque black while mirroring.
   - Implement by toggling VideoStage.Background right where EmptyStatePanel.Visibility is
     already toggled (see the connect/disconnect hooks above):
       * On connect / stream start (EmptyStatePanel -> Collapsed):
             VideoStage.Background = Brushes.Black;   // opaque, covers Mica, safe for HwndHost
       * On disconnect / empty (EmptyStatePanel -> Visible):
             VideoStage.Background = Brushes.Transparent;  // Mica shows behind empty-state art
   - Also set the XAML VideoStage Background to Transparent for the initial empty state.
   - Do NOT touch any decode/present code while doing this — only the background assignment
     next to the already-existing visibility toggle.

5. Empty-state polish (XAML only, inside EmptyStatePanel):
   - Keep all existing x:Name elements and their bindings/handlers.
   - Improve spacing/typography to read cleanly on a Mica surface (the text was tuned for a
     black background). Keep it tasteful and subtle; do not add heavy animations.

6. Status pill (${scout.statusPillName}): leave its show/hide logic and its
   "collapsed during mirroring" behavior intact. You may only retune its colors/corner radius
   to match the Fluent look. Do not change when it appears.

Report a concise list of the edits you made.`,
    { sandbox: 'workspace-write' }
  ),

  // ── B. SettingsWindow Fluent redesign ───────────────────────────────────────
  () => agent(
    `Redesign SettingsWindow with WPF-UI Fluent controls. No video/decoder code is involved here.

You may edit ONLY: MacMirrorReceiver/SettingsWindow.xaml and
MacMirrorReceiver/SettingsWindow.xaml.cs.

CURRENT STATE
  - Code-behind class: ${scout.settingsWindowClassDecl}
  - Controls present (convert these): ${scout.settingsControls.map(c => `${c.type}:${c.xName}`).join(', ')}

REQUIRED CHANGES

1. XAML root -> FluentWindow:
   - Add xmlns:ui="http://schemas.lepo.co/wpfui/2022/xaml".
   - Change root from Window to ui:FluentWindow with ExtendsContentIntoTitleBar="True"
     and WindowBackdropType="Mica".
   - Add a <ui:TitleBar Title="Settings"/> in a top Auto row; move the existing content below it.
   - Keep x:Class, SizeToContent, Width, MaxHeight, WindowStartupLocation, ShowInTaskbar,
     all event handlers, FocusManager, UseLayoutRounding, SnapsToDevicePixels.
   - Keep the merged SharedResources.xaml dictionary.

2. Code-behind:
   - Change base type from Window to Wpf.Ui.Controls.FluentWindow (keep any interfaces).
     Current line: ${scout.settingsWindowClassDecl}
   - In the constructor after InitializeComponent(), call
     Wpf.Ui.Appearance.ApplicationThemeManager.Apply(this);
   - Preserve EVERY existing x:Name and EVERY event-handler wiring. The code-behind reads/writes
     these controls (e.g. ReceiverNameTextBox, AudioSyncOffsetSlider, the render-mode radios,
     the audio + diagnostics checkboxes). If you change a control's TYPE, update the matching
     handler signatures/casts accordingly, but keep the exact same x:Name and behavior.

3. Fluent control mapping (keep x:Name, keep AutomationProperties.Name, keep handlers):
   - CheckBox  -> ui:ToggleSwitch  (Checked/Unchecked handlers still apply)
   - TextBox   -> ui:TextBox
   - Button    -> ui:Button (use Appearance="Primary" for the primary/restart action)
   - Group related sections (GENERAL / VIDEO / AUDIO / Advanced) into <ui:Card> containers
     with consistent padding and spacing.
   - RadioButton and Slider: keep as-is (or use the WPF-UI styled versions if drop-in), but do
     NOT change their x:Name, GroupName, ranges, tick settings, or handlers.
   - Keep the warning banners, the restart panel, version text, and the "Check for updates"
     hyperlink working.

Report a concise list of the controls you converted and any handler-signature changes.`,
    { sandbox: 'workspace-write' }
  ),

  // ── C. Rendering guardrails doc (new file — no conflict) ─────────────────────
  () => agent(
    `Create docs/ui-rendering-guardrails.md — the canonical, durable rules that any future UI
work on iMirror must follow so the video pipeline is never destabilized by cosmetics.

Base it on these established facts:
  - Stable render path: WriteableBitmap -> WPF Image control (VideoImage). WPF-UI/Mica compatible.
  - High-res render path: D3D11SwapChainVideoPresenter : HwndHost, enabled under the
    HIGH_RESOLUTION_D3D compile constant (always defined) for Quality mode + streams above 1080p.
    HwndHost is an airspace surface: WPF cannot composite over it reliably.
  - Off-limits code (cosmetic changes must never touch): FFmpeg decode, TCP receive, frame timing,
    decoder fallback, D3D presenter behavior, and MacMirrorReceiver.Video/D3D11SwapChainVideoPresenter.cs.
  - Off-limits symbols in MainWindow.cs: ${scout.offLimitsSymbols.join(', ')}

The document MUST contain these sections:
  1. Two render paths — what each is, when it activates, and the file/symbols involved.
  2. Where Mica / Acrylic / blur / transparency IS allowed: TitleBar, EmptyState, SettingsWindow,
     ordinary cards/panels.
  3. Where it is FORBIDDEN: the live video surface (VideoStage during mirroring, VideoImage,
     and anything on top of the HwndHost).
  4. The VideoStage background rule: transparent ONLY while disconnected (empty state) so Mica
     shows behind the empty-state art; opaque black (#000000) during mirroring.
  5. A "Never modify for cosmetic reasons" list (the off-limits code + symbols above).
  6. A short checklist a reviewer runs before approving any UI PR.

Keep it under ~90 lines. Write ONLY docs/ui-rendering-guardrails.md.`,
    { sandbox: 'workspace-write' }
  ),

])

// ── Phase 5: Verify ──────────────────────────────────────────────────────────
phase('Verify')
log('Restoring + building, running tests, then writing the verification checklist...')

const build = await agent(
  `Restore and build the product, then run the tests, and report results.

Run from the repo root:
  1. dotnet build MacMirrorReceiver.csproj -c Release --no-incremental 2>&1
  2. If the build succeeds, also run:
     dotnet test MacMirrorReceiver.Tests/MacMirrorReceiver.Tests.csproj -c Release 2>&1

Return JSON:
  - buildSuccess: boolean
  - testSuccess: boolean (false if build failed or any test failed; true if all passed)
  - errorLines: array of error lines (compiler errors, missing resource keys, or failed tests)
  - warnCount: number of build warnings
  - summary: one sentence`,
  {
    sandbox: 'workspace-write',
    schema: {
      type: 'object',
      properties: {
        buildSuccess: { type: 'boolean' },
        testSuccess: { type: 'boolean' },
        errorLines: { type: 'array', items: { type: 'string' } },
        warnCount: { type: 'number' },
        summary: { type: 'string' },
      },
      required: ['buildSuccess', 'testSuccess', 'errorLines', 'warnCount', 'summary'],
    },
  }
)

log(build.summary)
if (!build.buildSuccess) log('Build errors: ' + build.errorLines.join(' | '))

await agent(
  `Create docs/ui-fluent-refresh-verification.md — the manual verification checklist for the
ui/wpf-fluent-refresh branch. A human runs this against a real build with real Apple devices.

Record the automated result at the top:
  - Build success: ${build.buildSuccess}
  - Tests success: ${build.testSuccess}
  - Build warnings: ${build.warnCount}
  - Summary: ${build.summary}

Then include checklists with - [ ] items:

  ## Automated (already run)
  - [ ] dotnet build -c Release succeeds  (result above)
  - [ ] dotnet test passes                (result above)

  ## Rendering integrity (the whole point of the guardrails)
  - [ ] STABLE path: connect an iPhone (1080p) — video renders via WriteableBitmap/Image, no flicker
  - [ ] HIGH-RES path: Quality mode + a >1080p source — D3D11/HwndHost video still renders, no black box,
        no airspace artifact over the video
  - [ ] During mirroring the area behind the video is opaque black (no Mica bleed onto the video)
  - [ ] While disconnected, the empty state shows Mica behind the art (transparent VideoStage)
  - [ ] Disconnect returns to empty state cleanly; reconnect works; no hang/crash

  ## Fluent shell
  - [ ] TitleBar: drag to move, minimize, maximize/restore, close all work
  - [ ] Window resize + Windows snap (Win+Arrow / drag to edge) behave normally
  - [ ] Mica backdrop renders on the title bar and empty state

  ## SettingsWindow
  - [ ] Opens and closes without freezing video
  - [ ] Receiver name edit persists / prompts restart as before
  - [ ] Render-mode radios still switch and persist
  - [ ] Audio toggle + audio-sync slider save and restore correctly
  - [ ] Diagnostics toggles + warning banners + "Check for updates" link still work

  ## Accessibility
  - [ ] Keyboard focus visuals present; Tab order logical
  - [ ] High-contrast theme still legible

Write ONLY docs/ui-fluent-refresh-verification.md.`,
  { sandbox: 'workspace-write' }
)

log('Done. Review the diff, then run the manual checklist in docs/ui-fluent-refresh-verification.md.')
return build
