export const meta = {
  name: 'phase5-finish',
  description: 'Finish Phase 5: contrast fixes, high contrast, re-diagnose button, settings copy',
  phases: [
    { title: 'Scout', detail: 'Read XAML and C# to map current state' },
    { title: 'Implement', detail: 'Four parallel agents' },
    { title: 'Verify', detail: 'dotnet build' },
  ],
}

phase('Scout')
log('Reading current XAML and C# state...')

const scout = await agent(
  `Read these files in full:
1. MainWindow.xaml
2. MacMirrorReceiver/SettingsWindow.xaml
3. MacMirrorReceiver/SharedResources.xaml
4. MacMirrorReceiver/MainWindow.cs
5. MacMirrorReceiver/SettingsWindow.xaml.cs

Return:
- hardcodedTextColors: list of {element, color, context} for every hardcoded foreground/text color in MainWindow.xaml and SettingsWindow.xaml that is NOT using a StaticResource (e.g. Foreground="#B9B9C0", Foreground="#F5F5F7", Color="#6B4A00" etc.)
- warningBannerColors: any hardcoded colors used in warning/info Banner Borders (like #FFF8E8, #FFE0A8, #6B4A00 in SettingsWindow)
- recheckButtonExists: true/false — does a "Re-check" or "Recheck" button exist in MainWindow.cs that re-runs startup diagnostics
- recheckButtonXamlName: the x:Name of the re-check button if it exists, else null
- settingsTextSamples: list of {label, currentText} for every TextBlock label/hint in SettingsWindow.xaml (section headers and description lines)`,
  {
    sandbox: 'read-only',
    schema: {
      type: 'object',
      properties: {
        hardcodedTextColors: {
          type: 'array',
          items: {
            type: 'object',
            properties: {
              element: { type: 'string' },
              color: { type: 'string' },
              context: { type: 'string' },
            },
            required: ['element', 'color', 'context'],
          },
        },
        warningBannerColors: { type: 'array', items: { type: 'string' } },
        recheckButtonExists: { type: 'boolean' },
        recheckButtonXamlName: { type: ['string', 'null'] },
        settingsTextSamples: {
          type: 'array',
          items: {
            type: 'object',
            properties: {
              label: { type: 'string' },
              currentText: { type: 'string' },
            },
            required: ['label', 'currentText'],
          },
        },
      },
      required: ['hardcodedTextColors', 'warningBannerColors', 'recheckButtonExists', 'recheckButtonXamlName', 'settingsTextSamples'],
    },
  }
)

log(`Hardcoded colors: ${scout.hardcodedTextColors.length}, recheck button: ${scout.recheckButtonExists}`)

phase('Implement')
log('Running four parallel agents...')

await parallel([

  // ── 1. Contrast + hardcoded color fixes ────────────────────────────────
  () => agent(
    `Fix hardcoded text colors in MainWindow.xaml and SettingsWindow.xaml for WCAG AA compliance and Windows High Contrast support.

Hardcoded colors found by the scout:
${JSON.stringify(scout.hardcodedTextColors, null, 2)}

Warning banner hardcoded colors:
${JSON.stringify(scout.warningBannerColors, null, 2)}

Rules:
1. In SharedResources.xaml, add two new brushes after the existing ones:
   <SolidColorBrush x:Key="WarnBannerBackBrush"  Color="#FFF8E8" />
   <SolidColorBrush x:Key="WarnBannerBorderBrush" Color="#FFE0A8" />
   <SolidColorBrush x:Key="WarnBannerTextBrush"   Color="#6B4A00" />

2. In SettingsWindow.xaml, replace every hardcoded #FFF8E8 / #FFE0A8 / #6B4A00 occurrence with the new named resources.

3. In MainWindow.xaml and SettingsWindow.xaml:
   - Replace Foreground="#B9B9C0" with Foreground="{StaticResource MutedTextBrush}" (the closest mapped token)
   - Replace Foreground="#F5F5F7" (light text on dark background) with a new resource. Add to SharedResources.xaml:
     <SolidColorBrush x:Key="LightTextBrush" Color="#F5F5F7" />
   - Replace any Foreground="#F5F5F7" occurrences with Foreground="{StaticResource LightTextBrush}"

4. Do NOT change layout, sizes, or any non-color attributes.

Edit SharedResources.xaml, MainWindow.xaml, and SettingsWindow.xaml as needed.`,
    { sandbox: 'workspace-write' }
  ),

  // ── 2. Windows High Contrast support ───────────────────────────────────
  () => agent(
    `Add Windows High Contrast mode support to SharedResources.xaml.

In WPF, High Contrast is handled with SystemColors triggers. Add a SystemParameters.HighContrast-aware ResourceDictionary style override at the bottom of SharedResources.xaml.

Add this block just before the closing </ResourceDictionary>:

<!-- High Contrast overrides ─────────────────────────────────────────── -->
<Style x:Key="HCButtonFocusVisualStyle">
    <Setter Property="Control.Template">
        <Setter.Value>
            <ControlTemplate>
                <Rectangle Margin="-2"
                           StrokeThickness="2"
                           Stroke="{DynamicResource {x:Static SystemColors.HighlightBrushKey}}"
                           RadiusX="9"
                           RadiusY="9" />
            </ControlTemplate>
        </Setter.Value>
    </Setter>
</Style>

Also in BaseButtonStyle, add a DataTrigger that switches to SystemColors when High Contrast is active:
After the existing ControlTemplate.Triggers in BaseButtonStyle, add to the Style.Triggers (not ControlTemplate.Triggers):
<Style.Triggers>
    <DataTrigger Binding="{Binding Source={x:Static SystemParameters.HighContrast}}" Value="True">
        <Setter Property="Background"   Value="{DynamicResource {x:Static SystemColors.ControlBrushKey}}" />
        <Setter Property="Foreground"   Value="{DynamicResource {x:Static SystemColors.ControlTextBrushKey}}" />
        <Setter Property="BorderBrush"  Value="{DynamicResource {x:Static SystemColors.ControlTextBrushKey}}" />
        <Setter Property="FocusVisualStyle" Value="{StaticResource HCButtonFocusVisualStyle}" />
    </DataTrigger>
</Style.Triggers>

Edit only MacMirrorReceiver/SharedResources.xaml.`,
    { sandbox: 'workspace-write' }
  ),

  // ── 3. Re-run diagnostics after firewall change ─────────────────────────
  () => agent(
    `Wire up the "Re-check" / firewall help flow in MainWindow.cs so that after the user opens Windows Firewall and returns, the empty-state diagnostic status updates automatically.

Scout result:
- recheckButtonExists: ${scout.recheckButtonExists}
- recheckButtonXamlName: ${scout.recheckButtonXamlName}

Task:
In MacMirrorReceiver/MainWindow.cs, find the FirewallHelpButton_Click handler (it opens Windows Firewall settings). After the existing code that opens the firewall dialog, add:

1. Show a brief "Re-checking…" state on DiagStatusText.
2. Call the existing preflight / startup diagnostics re-run method (look for ReadinessRecheckButton_Click or a similar method that re-runs StartupDiagnostics) on a short delay (500ms) so the user has time to return from the Firewall window.
3. If no existing re-run method is found, call BindEmptyStateDiagnosticStatus with a stub report that shows "Re-checking…" — do not invent new diagnostics logic.

Also: in the DiagStatusText area (in MainWindow.xaml), if DiagStatusPanel does not already have a Button for re-checking, add a small hyperlink-style TextBlock BELOW DiagStatusPanel with:
  x:Name="RecheckDiagLink"
  Text="Re-check"
  Foreground="{StaticResource AccentBrush}"
  FontSize="12"
  Cursor="Hand"
  HorizontalAlignment="Center"
  Margin="0,6,0,0"
  MouseLeftButtonUp="RecheckDiagLink_Click"

And in MainWindow.cs add the RecheckDiagLink_Click handler that triggers a re-run of the preflight checks (re-use the existing recheck logic).

Edit MacMirrorReceiver/MainWindow.cs and MainWindow.xaml.`,
    { sandbox: 'workspace-write' }
  ),

  // ── 4. Settings copy review ─────────────────────────────────────────────
  () => agent(
    `Review and normalize the copy (text) in MacMirrorReceiver/SettingsWindow.xaml.

Current settings text found by the scout:
${JSON.stringify(scout.settingsTextSamples, null, 2)}

Goals — apply these normalizations:
1. Section headers (GENERAL, VIDEO, AUDIO): keep uppercase, they're fine.
2. "Experimental high-res testing only." → "Native GPU path. May not work on all hardware."
   (This is more honest than "testing only" for a public release.)
3. "Restart required. AirPlay connections will disconnect." → "Restart required to apply — active sessions will end."
   (Clearer cause and effect.)
4. "Shown to senders on the network. Restart required." → "Name shown to nearby Apple devices. Requires restart."
5. "Higher values delay audio to match video. Applies immediately." → "Delays audio relative to video. Takes effect immediately."
6. "These files can contain private screen content and key material. Keep them local."
   → "Diagnostic files may contain screen content or session keys. Never share them."
7. The "Check for updates" hyperlink label: keep as-is.

Edit only MacMirrorReceiver/SettingsWindow.xaml.`,
    { sandbox: 'workspace-write' }
  ),

])

phase('Verify')
log('Building...')

const build = await agent(
  `Run: dotnet build MacMirrorReceiver.csproj -c Release --no-incremental 2>&1
Return JSON: { success: boolean, errorLines: string[], warnCount: number, summary: string }`,
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

log(build.summary)
if (!build.success) log('Errors: ' + build.errorLines.join(' | '))
return build
