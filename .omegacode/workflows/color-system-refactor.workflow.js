export const meta = {
  name: 'color-system-refactor',
  description: 'Centralize XAML resources and apply Windows 11 system accent color',
  phases: [
    { title: 'Scout', detail: 'Read both XAML files and classify all resources' },
    { title: 'Create', detail: 'Create MacMirrorReceiver/SharedResources.xaml' },
    { title: 'Update', detail: 'Strip duplicates from MainWindow.xaml and SettingsWindow.xaml' },
    { title: 'Verify', detail: 'Confirm build compiles and no resources are missing' },
  ],
}

// ── Phase 1: Scout ─────────────────────────────────────────────────────────
phase('Scout')
log('Reading MainWindow.xaml and SettingsWindow.xaml to classify resources...')

const scout = await agent(
  `You are inspecting a WPF project to prepare for resource centralisation.

Read these two files in full:
  1. MainWindow.xaml                         (project root)
  2. MacMirrorReceiver/SettingsWindow.xaml   (subfolder)

For each file, extract every entry inside <Window.Resources> (or inside a nested
<ResourceDictionary> if one exists there).

Classify each resource as:
  - "shared"   — x:Key appears in BOTH files with the same or equivalent definition
  - "main"     — x:Key appears only in MainWindow.xaml
  - "settings" — x:Key appears only in SettingsWindow.xaml

Return the raw XAML fragments grouped by classification.
Do NOT paraphrase — return verbatim XAML snippets so they can be pasted directly.
Return the full list of x:Key names under each category as well.`,
  {
    sandbox: 'read-only',
    schema: {
      type: 'object',
      properties: {
        sharedXaml: {
          type: 'string',
          description: 'Verbatim XAML for every resource that appears in both files',
        },
        mainOnlyXaml: {
          type: 'string',
          description: 'Verbatim XAML for resources only in MainWindow.xaml',
        },
        settingsOnlyXaml: {
          type: 'string',
          description: 'Verbatim XAML for resources only in SettingsWindow.xaml',
        },
        sharedKeys: { type: 'array', items: { type: 'string' } },
        mainOnlyKeys: { type: 'array', items: { type: 'string' } },
        settingsOnlyKeys: { type: 'array', items: { type: 'string' } },
      },
      required: [
        'sharedXaml', 'mainOnlyXaml', 'settingsOnlyXaml',
        'sharedKeys', 'mainOnlyKeys', 'settingsOnlyKeys',
      ],
    },
  }
)

log(`Shared keys: ${scout.sharedKeys.join(', ')}`)
log(`MainWindow-only keys: ${scout.mainOnlyKeys.join(', ')}`)
log(`SettingsWindow-only keys: ${scout.settingsOnlyKeys.join(', ')}`)

// ── Phase 2: Create SharedResources.xaml ───────────────────────────────────
phase('Create')
log('Creating MacMirrorReceiver/SharedResources.xaml...')

await agent(
  `Create the file MacMirrorReceiver/SharedResources.xaml.

This file is a standalone WPF ResourceDictionary that consolidates shared brushes
and styles previously duplicated across MainWindow.xaml and SettingsWindow.xaml.

=== Shared resources to include (verbatim from scout) ===
${scout.sharedXaml}

=== Changes to apply on top of those verbatim resources ===

1. Replace the AccentBrush definition so it tracks the Windows 11 system accent colour:
   OLD: <SolidColorBrush x:Key="AccentBrush" Color="#007AFF" />
   NEW: <SolidColorBrush x:Key="AccentBrush"
            Color="{DynamicResource {x:Static SystemColors.HighlightColorKey}}" />

2. Inside BaseButtonStyle, add a proper FocusVisualStyle so keyboard focus is visible.
   After the existing <Setter Property="Cursor" Value="Hand" /> line add:
   <Setter Property="FocusVisualStyle">
       <Setter.Value>
           <Style>
               <Setter Property="Control.Template">
                   <Setter.Value>
                       <ControlTemplate>
                           <Rectangle Margin="-2"
                                      StrokeThickness="2"
                                      Stroke="{DynamicResource {x:Static SystemColors.HighlightBrushKey}}"
                                      StrokeDashArray="1 2"
                                      RadiusX="9"
                                      RadiusY="9" />
                       </ControlTemplate>
                   </Setter.Value>
               </Style>
           </Setter>
       </Style>
   </Setter.Value>
   </Setter>

=== File skeleton ===
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- paste all shared resources here with the two changes above applied -->
</ResourceDictionary>

Write only that file. Do not touch any other file.`,
  { sandbox: 'workspace-write' }
)

// ── Phase 3: Update both XAML windows in parallel ──────────────────────────
phase('Update')
log('Updating MainWindow.xaml and SettingsWindow.xaml in parallel...')

await parallel([
  () => agent(
    `Update MainWindow.xaml (at the project root).

Goal: replace its <Window.Resources> section so it merges SharedResources.xaml
instead of re-declaring the shared brushes/styles.

=== Keys that are now ONLY in SharedResources.xaml (remove these from MainWindow) ===
${scout.sharedKeys.map(k => '  ' + k).join('\n')}

=== Keys that must STAY in MainWindow.xaml (not shared) ===
${scout.mainOnlyKeys.map(k => '  ' + k).join('\n')}

MainWindow-only XAML to keep:
${scout.mainOnlyXaml}

=== Required Window.Resources structure ===
<Window.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="/MacMirrorReceiver/SharedResources.xaml" />
        </ResourceDictionary.MergedDictionaries>
        <!-- paste MainWindow-only resources here -->
    </ResourceDictionary>
</Window.Resources>

Rules:
- Do NOT remove any x:Key that appears in the mainOnlyKeys list.
- Do NOT add or change anything outside <Window.Resources>.
- Do NOT alter the AccentBrush reference style in MainWindow — it no longer needs
  to define it (SharedResources now owns it).
- Preserve every AutomationProperties, event handler, and control definition
  in the rest of the file exactly as-is.`,
    { sandbox: 'workspace-write' }
  ),

  () => agent(
    `Update MacMirrorReceiver/SettingsWindow.xaml.

Goal: replace its <Window.Resources> section so it merges SharedResources.xaml
instead of re-declaring the shared brushes/styles.

=== Keys that are now ONLY in SharedResources.xaml (remove these from SettingsWindow) ===
${scout.sharedKeys.map(k => '  ' + k).join('\n')}

=== Keys that must STAY in SettingsWindow.xaml (not shared) ===
${scout.settingsOnlyKeys.map(k => '  ' + k).join('\n')}

SettingsWindow-only XAML to keep:
${scout.settingsOnlyXaml}

=== Required Window.Resources structure ===
<Window.Resources>
    <ResourceDictionary>
        <ResourceDictionary.MergedDictionaries>
            <ResourceDictionary Source="/MacMirrorReceiver/SharedResources.xaml" />
        </ResourceDictionary.MergedDictionaries>
        <!-- paste SettingsWindow-only resources here -->
    </ResourceDictionary>
</Window.Resources>

Rules:
- Do NOT remove any x:Key that appears in the settingsOnlyKeys list.
- Do NOT add or change anything outside <Window.Resources>.
- Preserve every AutomationProperties, event handler, and control definition
  in the rest of the file exactly as-is.`,
    { sandbox: 'workspace-write' }
  ),
])

// ── Phase 4: Verify build ──────────────────────────────────────────────────
phase('Verify')
log('Running dotnet build to confirm nothing is broken...')

const buildResult = await agent(
  `Run this command from the project root and report the result:

  dotnet build MacMirrorReceiver.csproj -c Release --no-incremental 2>&1

Return:
- Whether the build succeeded or failed
- Any error or warning lines (full text)
- A one-sentence summary

If the build fails due to a missing StaticResource key (e.g. "Cannot find resource named X"),
list every missing key name clearly — that tells us which keys were accidentally removed.`,
  { sandbox: 'workspace-write' }
)

log(buildResult)
return buildResult
