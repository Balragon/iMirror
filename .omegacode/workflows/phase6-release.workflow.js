export const meta = {
  name: 'phase6-release',
  description: 'Phase 6: tagged release CI, GitHub Release automation, public docs, v0.2 checklist',
  phases: [
    { title: 'Scout', detail: 'Read existing CI workflows and publish scripts' },
    { title: 'Implement', detail: 'Four parallel agents — CI, GitHub Release, docs, checklist' },
    { title: 'Verify', detail: 'Validate YAML syntax and check all referenced paths exist' },
  ],
}

phase('Scout')
log('Reading existing CI and packaging setup...')

const scout = await agent(
  `Read these files and return a structured summary:
1. .github/workflows/ — list every workflow file and its trigger/purpose (one line each)
2. scripts/publish-win-x64.ps1 — full content
3. MacMirrorReceiver/AppVersionInfo.cs — full content
4. docs/release.md — full content
5. README.md — the Build and Publish sections only

Return:
- existingWorkflows: list of {filename, trigger, purpose}
- publishScriptSummary: what publish-win-x64.ps1 does (2-3 sentences)
- currentVersion: the version string from AppVersionInfo.cs
- releaseMdExists: true/false
- artifactOutputPath: the artifacts output directory mentioned in the publish script`,
  {
    sandbox: 'read-only',
    schema: {
      type: 'object',
      properties: {
        existingWorkflows: {
          type: 'array',
          items: {
            type: 'object',
            properties: {
              filename: { type: 'string' },
              trigger: { type: 'string' },
              purpose: { type: 'string' },
            },
            required: ['filename', 'trigger', 'purpose'],
          },
        },
        publishScriptSummary: { type: 'string' },
        currentVersion: { type: 'string' },
        releaseMdExists: { type: 'boolean' },
        artifactOutputPath: { type: 'string' },
      },
      required: ['existingWorkflows', 'publishScriptSummary', 'currentVersion', 'releaseMdExists', 'artifactOutputPath'],
    },
  }
)

log(`Current version: ${scout.currentVersion}`)
log(`Existing workflows: ${scout.existingWorkflows.map(w => w.filename).join(', ')}`)
log(`Artifact output: ${scout.artifactOutputPath}`)

phase('Implement')
log('Running four parallel agents...')

await parallel([

  // ── 1. Tagged release packaging workflow ───────────────────────────────
  () => agent(
    `Create .github/workflows/release.yml — a GitHub Actions workflow that builds and packages iMirror on every version tag push.

Context:
- Project: .NET 8 WPF, target net8.0-windows, x64
- Publish script: scripts/publish-win-x64.ps1
- Artifact output path: ${scout.artifactOutputPath}
- Current version: ${scout.currentVersion}
- Existing workflows: ${scout.existingWorkflows.map(w => w.filename).join(', ')}
- Signing: UNSIGNED for v0.2 (no code signing step needed)
- FFmpeg: NOT bundled — README tells users to install via winget

The workflow must:
1. Trigger on: push to tags matching v*.*.* (e.g. v0.2.0)
2. Run on: windows-latest
3. Steps:
   a. Checkout (full history)
   b. Setup .NET 8
   c. Run dotnet test MacMirrorReceiver.Tests/MacMirrorReceiver.Tests.csproj -c Release
   d. Run scripts/publish-win-x64.ps1 via PowerShell
   e. Upload the resulting zip from the artifacts directory as a GitHub Actions artifact named "iMirror-windows-x64"
   f. Create a GitHub Release using the tag name as the release title
      - Mark as pre-release if the tag contains "-" (e.g. v0.2.0-rc1)
      - Upload the zip to the release
      - Release body: auto-generated from the tag annotation, or a default template if no annotation

Write only .github/workflows/release.yml.`,
    { sandbox: 'workspace-write' }
  ),

  // ── 2. GitHub Release publication notes ────────────────────────────────
  () => agent(
    `Create .github/RELEASE_TEMPLATE.md — a reusable release notes template for iMirror v0.2 public releases.

Context:
- App: Windows AirPlay mirroring receiver
- Distribution: unsigned self-contained zip (no installer)
- FFmpeg: user installs separately via winget install Gyan.FFmpeg.Essentials
- SmartScreen: will warn on first launch (unsigned)
- Runtime: .NET 8 Windows Desktop Runtime required (or self-contained)

The template should include:
1. ## What's new  (placeholder: <!-- CHANGES -->)
2. ## Requirements
   - Windows 10 22H2 or Windows 11
   - .NET 8 Desktop Runtime (link to dotnet.microsoft.com)
   - FFmpeg Essentials (winget command)
3. ## Installation
   - Download the zip, extract, run iMirror.exe
   - Windows SmartScreen: click "More info" → "Run anyway" (unsigned build)
   - Firewall prompt: allow both Private and Public network access
4. ## Known limitations (v0.2)
   - No auto-update — check this page for new releases
   - Unsigned build — SmartScreen warning is expected
   - GPU decode requires a D3D11-capable GPU; software fallback is automatic
5. ## Checksums  (placeholder: <!-- SHA256 -->)

Write only .github/RELEASE_TEMPLATE.md.`,
    { sandbox: 'workspace-write' }
  ),

  // ── 3. Update docs/release.md ──────────────────────────────────────────
  () => agent(
    `Update docs/release.md to reflect the current v0.2 public release process.

Current state: ${scout.releaseMdExists ? 'file exists' : 'file does not exist — create it'}
Publish script does: ${scout.publishScriptSummary}

The updated docs/release.md must cover:
1. **Overview** — what the release artifact is (self-contained zip, unsigned, no installer)
2. **Prerequisites** — .NET 8 SDK, FFmpeg Essentials location
3. **Building the release zip**
   \`\`\`powershell
   powershell -ExecutionPolicy Bypass -File .\\scripts\\publish-win-x64.ps1
   \`\`\`
   Output: artifacts\\ directory
4. **Tagging a release** — git tag v0.2.0 -m "v0.2.0", git push origin v0.2.0 → CI publishes automatically
5. **Manual smoke test checklist** (reference docs/validation.md)
6. **SmartScreen / trust** — expected behavior, no action needed from publisher
7. **FFmpeg note** — not bundled, users install via winget
8. **Checksums** — how to generate SHA-256 after packaging

Keep it concise (under 120 lines). Write only docs/release.md.`,
    { sandbox: 'workspace-write' }
  ),

  // ── 4. v0.2 release validation checklist ───────────────────────────────
  () => agent(
    `Create docs/release-checklist-v02.md — a manual release validation checklist for the v0.2 public release of iMirror.

This checklist is filled out by the developer before publishing a GitHub Release.
It must be a markdown document with checkboxes (- [ ] items).

Sections:
1. **Build & CI**
   - [ ] CI build passes on Windows runner (green on the release tag)
   - [ ] dotnet test passes with 0 failures
   - [ ] Zip artifact produced in artifacts\\ directory
   - [ ] SHA-256 checksum recorded

2. **Smoke test — AirPlay mirroring**
   - [ ] App launches on a clean Windows 11 machine (no prior install)
   - [ ] mDNS advertisement visible: iMirror appears in iOS/macOS Screen Mirroring picker
   - [ ] Mac screen mirroring connects and displays video
   - [ ] iPhone screen mirroring connects and displays video
   - [ ] Audio plays through Windows speakers when audio is enabled
   - [ ] Disconnect from sender — app returns to empty state cleanly
   - [ ] Reconnect after disconnect — no hang or crash
   - [ ] App exits cleanly from tray icon

3. **Settings & lifecycle**
   - [ ] Settings window opens and closes without freezing video
   - [ ] Receiver name change prompts restart
   - [ ] Render mode switch (Stable ↔ High quality) persists after restart
   - [ ] Audio sync offset saves and restores correctly
   - [ ] Minimize to tray works; double-click tray restores window

4. **Diagnostic & firewall**
   - [ ] Empty state shows correct status dot (green = ready, warning = firewall/FFmpeg issue)
   - [ ] "Re-check" link re-runs diagnostics after user fixes firewall
   - [ ] FFmpeg missing → orange dot + "FFmpeg not found" message shown
   - [ ] Windows Firewall prompt appears on first launch (allow both network types)

5. **GPU & video engine**
   - [ ] GPU path (Media Foundation/D3D11) activates when hardware decode is available
   - [ ] Force software override (IMIRROR_FORCE_SOFTWARE_VIDEO=1) works
   - [ ] No GPU decode → software fallback activates silently

6. **Trust & distribution**
   - [ ] SmartScreen shows "More info" → "Run anyway" on unsigned build (expected)
   - [ ] SHA-256 of published zip matches recorded checksum
   - [ ] Release notes posted with correct version, requirements, and installation steps

7. **Go / No-Go**
   - [ ] All P0 items above checked
   - [ ] No known crash or data-loss regression
   - [ ] Release notes reviewed and published
   - Approver: ________________  Date: ________________

Write only docs/release-checklist-v02.md.`,
    { sandbox: 'workspace-write' }
  ),

])

phase('Verify')
log('Validating CI YAML and checking referenced paths...')

const verify = await agent(
  `Verify the newly created .github/workflows/release.yml:
1. Check YAML syntax by reading the file carefully — look for indentation errors, missing colons, wrong types
2. Verify that every file path referenced in the workflow exists:
   - scripts/publish-win-x64.ps1
   - MacMirrorReceiver.Tests/ directory
3. Check that the workflow uses actions/checkout, actions/setup-dotnet, and softprops/action-gh-release (or similar) with pinned versions
4. Confirm the trigger fires on v*.*.* tags

Return JSON: { valid: boolean, issues: string[], summary: string }`,
  {
    sandbox: 'read-only',
    schema: {
      type: 'object',
      properties: {
        valid: { type: 'boolean' },
        issues: { type: 'array', items: { type: 'string' } },
        summary: { type: 'string' },
      },
      required: ['valid', 'issues', 'summary'],
    },
  }
)

log(verify.summary)
if (!verify.valid) log('Issues: ' + verify.issues.join(' | '))
return verify
