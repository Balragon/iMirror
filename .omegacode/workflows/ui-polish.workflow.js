export const meta = {
  name: 'ui-polish',
  description: 'Fluent icons, empty state, firewall feedback, keyboard accessibility',
  phases: [
    { title: 'Scout', detail: 'Read current XAML and C# to understand state machine and diagnostics' },
    { title: 'Implement', detail: 'Four parallel agents — icons, empty state, firewall UI, accessibility' },
    { title: 'Verify', detail: 'dotnet build to confirm no regressions' },
  ],
}

// ── Phase 1: Scout ─────────────────────────────────────────────────────────
phase('Scout')
log('Reading XAML and startup diagnostics code...')

const scout = await agent(
  `Read these files in full and return a structured summary:

1. MainWindow.xaml            — the main WPF window
2. MacMirrorReceiver/MainWindow.cs     — the code-behind
3. MacMirrorReceiver/StartupDiagnostics.cs  — startup checks (firewall, FFmpeg, network)
4. MacMirrorReceiver/WindowLifecycleState.cs — lifecycle state enum/class

Return:
- currentIcons: list of {name, unicode, fontFamily} for every icon used in MainWindow.xaml buttons
- emptyStateElements: list of x:Name values in the EmptyState panel and how they are updated in MainWindow.cs
- diagnosticFields: list of public properties/methods on StartupDiagnostics that surface check results
- legacyBindingsHostNames: list of all x:Name values inside LegacyBindingsHost that relate to diagnostics (firewall, FFmpeg, listeners, network, readiness)
- lifecycleStates: list of enum values or state names in WindowLifecycleState`,
  {
    sandbox: 'read-only',
    schema: {
      type: 'object',
      properties: {
        currentIcons: {
          type: 'array',
          items: {
            type: 'object',
            properties: {
              name: { type: 'string' },
              unicode: { type: 'string' },
              fontFamily: { type: 'string' },
            },
            required: ['name', 'unicode', 'fontFamily'],
          },
        },
        emptyStateElements: { type: 'array', items: { type: 'string' } },
        diagnosticFields: { type: 'array', items: { type: 'string' } },
        legacyBindingsHostNames: { type: 'array', items: { type: 'string' } },
        lifecycleStates: { type: 'array', items: { type: 'string' } },
      },
      required: ['currentIcons', 'emptyStateElements', 'diagnosticFields', 'legacyBindingsHostNames', 'lifecycleStates'],
    },
  }
)

log(`Icons found: ${scout.currentIcons.map(i => i.name).join(', ')}`)
log(`Diagnostic fields: ${scout.diagnosticFields.join(', ')}`)

// ── Phase 2: Implement (4 parallel agents) ─────────────────────────────────
phase('Implement')
log('Running four implementation agents in parallel...')

await parallel([

  // ── 1. Fluent icon upgrade ──────────────────────────────────────────────
  () => agent(
    `Upgrade the toolbar button icons in MainWindow.xaml from Segoe MDL2 Assets to Segoe Fluent Icons (the Windows 11 icon font).

Current icons in the file:
${JSON.stringify(scout.currentIcons, null, 2)}

Segoe Fluent Icons replacements to use:
- Settings gear (&#xE713; MDL2) → &#xE713; is the same in Fluent Icons (Settings)
  Actually use &#xF8B0; which is the filled Settings gear in Segoe Fluent Icons
- Minimize (&#xE921; MDL2) → &#xE949; in Segoe Fluent Icons (ChromeMinimize)
- Close/X (&#xE8BB; MDL2) → &#xE8BB; is same, but use &#xE711; (ChromeClose) for consistency

For each button that currently has FontFamily="Segoe MDL2 Assets":
1. Change FontFamily to "Segoe Fluent Icons, Segoe MDL2 Assets" (fallback chain so it still works on Windows 10)
2. Update the Content unicode to the Fluent Icons equivalent

Edit only MainWindow.xaml. Do not touch any other file.`,
    { sandbox: 'workspace-write' }
  ),

  // ── 2. Empty state improvement ─────────────────────────────────────────
  () => agent(
    `Improve the EmptyStatePanel in MainWindow.xaml.

Current empty state elements:
${scout.emptyStateElements.join('\n')}

Goals:
1. The phone illustration currently shows a static iPhone silhouette. Replace it with a simpler, more Windows-native visual — a rounded rectangle (like a phone outline) using Path or Border shapes, keeping the same size (132×176). Use subtle gradient or layered borders with colors from SharedResources (#101014, #34343A, #303037) to keep the dark aesthetic.

2. Add a diagnostic status row BELOW the main "Ready to mirror" text. This row should show:
   - An x:Name="DiagStatusPanel" StackPanel (Orientation=Horizontal, HorizontalAlignment=Center, Margin="0,16,0,0")
   - Inside: an x:Name="DiagStatusDot" Ellipse (Width=8, Height=8, Fill=WarningBrush, VerticalAlignment=Center)
   - Then: an x:Name="DiagStatusText" TextBlock (Text="Checking…", Foreground="#B9B9C0", FontSize=13, Margin="8,0,0,0", VerticalAlignment=Center)
   This gives us named elements to wire up diagnostic results later.

3. Keep EmptyStateTextBlock ("Ready to mirror") and EmptyStateDetailTextBlock ("Open Control Center…") exactly as-is.

Edit only MainWindow.xaml. Do not touch any other file.`,
    { sandbox: 'workspace-write' }
  ),

  // ── 3. Firewall / diagnostic feedback ──────────────────────────────────
  () => agent(
    `Wire up startup diagnostic feedback in MainWindow.cs.

Context:
- StartupDiagnostics runs at startup and checks FFmpeg, firewall/listeners, and network.
- Results are currently only shown in the hidden LegacyBindingsHost panel.
- We added DiagStatusDot (Ellipse) and DiagStatusText (TextBlock) to the EmptyStatePanel in MainWindow.xaml.
- These elements need to be updated from MainWindow.cs to show real diagnostic status.

Diagnostic fields/methods available on StartupDiagnostics:
${scout.diagnosticFields.join('\n')}

Task: In MainWindow.cs, find where StartupDiagnostics results are applied (look for the method that reads diagnostic results and updates the legacy UI). After that existing code, also update the new elements:

1. If all checks pass: DiagStatusDot.Fill = SuccessBrush (from resources), DiagStatusText.Text = "Ready on this network"
2. If FFmpeg missing: DiagStatusDot.Fill = DangerBrush, DiagStatusText.Text = "FFmpeg not found — video decode unavailable"
3. If listeners/firewall blocked: DiagStatusDot.Fill = WarningBrush, DiagStatusText.Text = "Firewall may be blocking AirPlay — check Windows Firewall"
4. If network issue: DiagStatusDot.Fill = WarningBrush, DiagStatusText.Text = "No suitable network interface found"
5. Default/unknown: DiagStatusDot.Fill = WarningBrush, DiagStatusText.Text = "Checking network…"

For the brush, retrieve from window resources: (SolidColorBrush)FindResource("SuccessBrush") etc.

Edit only MacMirrorReceiver/MainWindow.cs. Do not touch any other file.`,
    { sandbox: 'workspace-write' }
  ),

  // ── 4. Keyboard accessibility ───────────────────────────────────────────
  () => agent(
    `Add missing AutomationProperties.Name values to interactive elements in MainWindow.xaml.

The overlay control buttons (Settings, Minimize, Close) already have ToolTip but may be missing AutomationProperties.Name. Screen readers use AutomationProperties.Name when it differs from ToolTip.

Tasks:
1. For every Button that has a ToolTip but NO AutomationProperties.Name, add AutomationProperties.Name matching the ToolTip text.
2. For the ReceiverCardBorder status area (the floating status chip at top-center), add AutomationProperties.LiveSetting="Polite" to the StackPanel inside it, so screen readers announce status changes.
3. For EmptyStateTextBlock and EmptyStateDetailTextBlock, add AutomationProperties.LiveSetting="Polite" so connection status is announced.
4. Confirm the three overlay buttons (Settings, Minimize, Close) each have TabIndex set: Settings=1, Minimize=2, Close=3 so Tab order is logical.

Edit only MainWindow.xaml. Do not touch any other file.`,
    { sandbox: 'workspace-write' }
  ),

])

// ── Phase 3: Verify ─────────────────────────────────────────────────────────
phase('Verify')
log('Building to verify all changes compile...')

const buildResult = await agent(
  `Run this command and report the result:

  dotnet build MacMirrorReceiver.csproj -c Release --no-incremental 2>&1

Return:
- success: true/false
- errorLines: array of error lines (empty if success)
- warnCount: number of warnings
- summary: one sentence`,
  {
    sandbox: 'workspace-write',
    schema: {
      type: 'object',
      properties: {
        success: { type: 'boolean' },
        errorLines: { type: 'array', items: { type: 'string' } },
        warnCount: { type: 'number' },
        summary: { type: 'string' },
      },
      required: ['success', 'errorLines', 'warnCount', 'summary'],
    },
  }
)

log(buildResult.summary)
if (!buildResult.success) {
  log('Build errors: ' + buildResult.errorLines.join(' | '))
}
return buildResult
