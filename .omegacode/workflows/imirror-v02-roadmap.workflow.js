export const meta = {
  name: 'imirror-v02-roadmap',
  description: 'Investigate iMirror across four product dimensions in parallel, break each into sequenced tasks, and synthesize a v0.2 roadmap toward public distribution.',
  phases: [
    { title: 'Investigate', detail: 'one deep reader per product dimension' },
    { title: 'Break down', detail: 'turn each dimension into concrete, estimated tasks' },
    { title: 'Synthesize', detail: 'merge into one sequenced roadmap with cross-area dependencies' },
  ],
}

// ─────────────────────────────────────────────────────────────────────────────
// iMirror is a WPF/.NET 8 (x64) Windows app: an AirPlay receiver that mirrors a
// Mac/iPhone screen onto Windows. v0.1.0 shipped; the in-app Settings UI and a
// first-run preflight diagnostics strip have since landed on main. The goal of
// this cycle is to raise the app to a public-distribution quality bar.
//
// This workflow does NOT write code. It produces a roadmap. Run it from the repo
// root so the agents can read the actual source. Save the returned markdown to
// docs/specs/v02-roadmap.md.
//
// Known starting facts (verify, don't trust blindly):
//   - Settings overlay uses Panel.ZIndex=100 + Grid.ColumnSpan=2, but the D3D11
//     swap-chain presents in an HWND child layer OUTSIDE WPF composition, so the
//     overlay can be occluded by live video on the HIGH_RESOLUTION_D3D path.
//   - FirewallHelpButton_Click has an empty catch block (silent failure).
//   - Window_Closing awaits DisconnectAsync and calls Dispose() with no try-catch.
//   - No system tray; closing the window exits the app entirely.
//   - No CI (.github/workflows absent) and no unit-test projects.
//   - No code signing (no signtool, no SignAssembly); SmartScreen will warn.
//   - Design system exists (resource brushes/styles) but the empty-state
//     illustration hardcodes dark-theme colors; spacing is inconsistent.
// ─────────────────────────────────────────────────────────────────────────────

const AREAS = [
  {
    key: 'ui-ux',
    title: 'UI / UX design quality',
    prompt: `Investigate iMirror's UI/UX design quality for a public release.
Read MainWindow.xaml (resources, sidebar, video stage, Settings overlay, empty
state) and the relevant MainWindow.cs handlers. Assess: the overall visual
language vs a modern Windows 11 / Fluent bar; the Settings overlay layering
problem when video is mirroring (D3D11 HWND layer occlusion — is the overlay
actually visible over live video, and what are the real fix options); hardcoded
colors vs resource brushes; spacing/padding inconsistency; the empty-state
illustration; and any accessibility gaps (focus, keyboard nav, contrast).
Report current state, concrete findings with file:line, and the highest-impact
design risks for a first-time public user.`,
  },
  {
    key: 'stability',
    title: 'Stability & bug fixes',
    prompt: `Investigate iMirror's stability and correctness gaps. Confirm and
characterize these three known issues, then hunt for more: (1) Settings overlay
occluded by live video on the D3D11 path; (2) FirewallHelpButton_Click empty
catch swallowing failures with no user feedback; (3) Window_Closing disposing
services with no exception guard. Also review the Settings event handlers
(receiver name, audio-sync slider, diagnostics checkboxes), the preflight
BindReadinessStrip path, reconnect handling, and resource disposal on session
end. Report each issue with file:line, a reproduction sketch, severity, and a
proposed fix approach (not full code).`,
  },
  {
    key: 'infra',
    title: 'Build infra: CI, tests, code signing',
    prompt: `Investigate what build/release infrastructure iMirror needs for
trustworthy public distribution. There is currently no CI, no automated tests,
and no code signing. Read MacMirrorReceiver.csproj, scripts/publish-win-x64.ps1,
docs/release.md, and the tools/ projects. Propose: a GitHub Actions pipeline
(build, package, release-on-tag) given the app needs Windows + a bundled
FFmpeg.Essentials; a pragmatic first test layer (what is unit-testable without a
Windows GUI/AirPlay device — settings precedence, preflight verdict logic,
FFmpeg path resolution); and a code-signing path (cert options, signtool
integration into the publish script, SmartScreen reputation). Report concrete
steps, rough effort, and what blocks each (e.g. cert acquisition is a human/$$
decision).`,
  },
  {
    key: 'features',
    title: 'New features',
    prompt: `Investigate high-value new features for iMirror toward public
distribution, and judge each by user impact vs implementation cost. Candidates:
system tray icon with minimize-to-tray / background receiving (today closing the
window exits the app); one-click firewall remediation (elevated netsh helper,
deferred earlier from the first-run design — see docs/first-run-experience.md);
auto-update via GitHub Releases. Read App.cs, MainWindow.cs window lifecycle,
and docs/first-run-experience.md. For each feature report: the user problem it
solves, an implementation sketch with the key technical risk, rough effort, and
a recommendation on whether it belongs in v0.2 or later.`,
  },
]

const INVESTIGATION_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['area', 'currentState', 'findings', 'topRisks'],
  properties: {
    area: { type: 'string' },
    currentState: { type: 'string', description: 'how this dimension stands today' },
    findings: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: false,
        required: ['title', 'detail', 'severity', 'evidence'],
        properties: {
          title: { type: 'string' },
          detail: { type: 'string' },
          severity: { type: 'string', enum: ['low', 'medium', 'high'] },
          evidence: { type: 'array', items: { type: 'string' }, description: 'file:line references' },
        },
      },
    },
    topRisks: { type: 'array', items: { type: 'string' } },
  },
}

const BREAKDOWN_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  required: ['area', 'tasks'],
  properties: {
    area: { type: 'string' },
    tasks: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: false,
        required: ['id', 'title', 'description', 'effort', 'priority', 'dependencies', 'handoff'],
        properties: {
          id: { type: 'string', description: 'short stable id, e.g. ui-overlay-fix' },
          title: { type: 'string' },
          description: { type: 'string' },
          effort: { type: 'string', enum: ['S', 'M', 'L', 'XL'] },
          priority: { type: 'string', enum: ['P0', 'P1', 'P2', 'P3'] },
          dependencies: { type: 'array', items: { type: 'string' }, description: 'task ids or external blockers' },
          handoff: { type: 'string', enum: ['frontend-design', 'codex-backend', 'human-decision'] },
        },
      },
    },
  },
}

phase('Investigate')
log(`Investigating ${AREAS.length} product dimensions in parallel.`)

// Each area: deep investigation, then task breakdown — pipelined so an area's
// breakdown starts as soon as its investigation lands (no cross-area barrier yet).
const areaResults = await pipeline(
  AREAS,
  (area) =>
    agent(area.prompt, {
      label: `investigate:${area.key}`,
      phase: 'Investigate',
      schema: INVESTIGATION_SCHEMA,
    }),
  (investigation, area) => {
    phase('Break down')
    return agent(
      `Turn this investigation of iMirror's "${area.title}" into a concrete, ` +
        `sequenced task list for a v0.2 cycle aimed at public distribution. ` +
        `Each task needs a clear scope, an effort size (S/M/L/XL), a priority ` +
        `(P0 = blocks release, P3 = nice-to-have), explicit dependencies, and a ` +
        `handoff target: "frontend-design" (WPF/XAML + judgement work suited to a ` +
        `planning session), "codex-backend" (well-specified implementation), or ` +
        `"human-decision" (needs an owner choice, e.g. buying a cert).\n\n` +
        `Investigation:\n${JSON.stringify(investigation, null, 2)}`,
      { label: `breakdown:${area.key}`, phase: 'Break down', schema: BREAKDOWN_SCHEMA },
    )
  },
)

const breakdowns = areaResults.filter(Boolean)
log(`Got task breakdowns for ${breakdowns.length}/${AREAS.length} areas.`)

phase('Synthesize')

// Final synthesis genuinely needs ALL areas at once to sequence cross-area
// dependencies (e.g. CI before a signed release; bug fixes before UI rework).
const roadmap = await agent(
  `You are the lead planner for iMirror. Merge these four per-area task ` +
    `breakdowns into ONE sequenced v0.2 roadmap toward public distribution.\n\n` +
    `Produce a Markdown document with:\n` +
    `1. A short executive summary (where the product is, what v0.2 must achieve).\n` +
    `2. A phased plan (Phase 1..N). For each phase: a goal, the tasks in it ` +
    `(by id + title + effort + handoff), and the rationale for its position in ` +
    `the sequence. Respect cross-area dependencies — e.g. P0 stability bugs ` +
    `before UI rework; CI before a signed public release.\n` +
    `3. A "Parallelizable now" callout: which tasks can run concurrently across ` +
    `the frontend-design session and codex-backend sessions.\n` +
    `4. An "Open decisions" section listing every human-decision item (cert ` +
    `purchase, distribution format, scope cuts) that must be resolved.\n` +
    `5. A risk register: the few things most likely to derail the cycle.\n\n` +
    `Be decisive about ordering and call out anything you would cut from v0.2. ` +
    `Return only the Markdown document — it will be saved to ` +
    `docs/specs/v02-roadmap.md.\n\n` +
    `Per-area breakdowns:\n${JSON.stringify(breakdowns, null, 2)}`,
  { label: 'synthesize:roadmap', phase: 'Synthesize' },
)

log('Roadmap synthesized. Save the returned Markdown to docs/specs/v02-roadmap.md.')
return roadmap
