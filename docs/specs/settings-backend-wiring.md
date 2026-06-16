# Settings Back-End Wiring Spec

**Target session:** Codex / GPT-5.5 xhigh  
**Branch:** `claude/magical-faraday-6ex3c7`  
**Language:** C# 11, .NET 8, WPF

## Context (read first)

An in-app Settings overlay was added to the front end.
The model is `MacMirrorReceiver/ReceiverSettings.cs` (`ReceiverSettings.Load()` returns
a `ReceiverSettingsSnapshot` with `.Persisted`, `.Effective`, and `.Overrides` per field).

The settings are already loaded at startup:

```csharp
// MacMirrorReceiver/MainWindow.cs
private static readonly ReceiverSettingsSnapshot StartupReceiverSettings = ReceiverSettings.Load();
```

**Rule that must hold for every item below:**

```
effective value = IMIRROR_* env override (when present and parseable)
               > persisted value from %AppData%/iMirror/settings.json
               > built-in default
```

The `ReceiverSettings.Load()` call already resolves this for each field.
Your job is to make the *runtime consumer* of each option read the resolved
`.Effective` value instead of calling `Environment.GetEnvironmentVariable` directly.

**Do not change:** `MainWindow.cs` Settings UI methods, `ReceiverSettings.cs`,
`RenderModeSettings.cs`, or any file that is not explicitly listed below.  
**Build check:** `dotnet build MacMirrorReceiver.csproj -c Release` must succeed with no new warnings.

---

## Task 1 — Receiver name in `AirPlayProbeService`

**File:** `MacMirrorReceiver.Networking/AirPlayProbeService.cs`

**Current behaviour (line 116–123):**
```csharp
public AirPlayProbeService(string displayName)
{
    _displayName = SanitizeLabel(displayName);
    ...
    _airPlayInstanceName = _displayName + "." + AirPlayServiceType;
    _raopInstanceName = _deviceId.Replace(":", "", ...) + "@" + _displayName + "." + RaopServiceType;
```

The caller (`MacMirrorReceiver/MainWindow.cs`) passes the hardcoded string `"iMirror"`:
```csharp
private readonly AirPlayProbeService _airPlayProbe = new AirPlayProbeService("iMirror");
```

**Change in `MainWindow.cs` (field initializer, line ~101):**
```csharp
// before
private readonly AirPlayProbeService _airPlayProbe = new AirPlayProbeService("iMirror");

// after
private readonly AirPlayProbeService _airPlayProbe = new AirPlayProbeService(
    StartupReceiverSettings.Effective.ReceiverName);
```

No change needed inside `AirPlayProbeService` itself — it already accepts any name.

**Acceptance:**
- `settings.json` with `"receiverName": "MyDevice"` → sender sees "MyDevice" in the AirPlay picker.
- `IMIRROR_RENDER_MODE` env unrelated to this; removing the hardcoded "iMirror" must not affect other init.
- The Settings overlay's name field already persists the value via `ReceiverSettings.UpdateDto`.

---

## Task 2 — Audio advertise gate in `AirPlayProbeService`

**File:** `MacMirrorReceiver.Networking/AirPlayProbeService.cs`

**Current behaviour (lines 47–56):**
```csharp
private const bool AdvertiseAudioCapabilities = true;

private static readonly bool AudioDiscoveryEnabled = string.Equals(
    Environment.GetEnvironmentVariable("IMIRROR_AUDIO_DISCOVERY"), "1", StringComparison.OrdinalIgnoreCase);

private static bool AudioAdvertised => AdvertiseAudioCapabilities || AudioDiscoveryEnabled;
```

`IMIRROR_AUDIO_DISCOVERY=1` is currently a *verbose logging* toggle, not an audio on/off.
The Settings UI exposes "Receive audio from senders" as a true enable/disable switch persisted
to `settings.json` under `audioEnabled`.

**Changes:**

1. Remove the env-var-only meaning of `AudioDiscoveryEnabled` from the advertise gate.
2. Replace `AudioAdvertised` to read the resolved setting. Because `AirPlayProbeService` is
   constructed before `Start()` is called, read the snapshot at construction:

```csharp
// replace lines 47-56 with:
private const bool AdvertiseAudioCapabilitiesDefault = true;

// Optional verbose discovery logging — meaning unchanged.
private static readonly bool AudioDiscoveryLogging = string.Equals(
    Environment.GetEnvironmentVariable("IMIRROR_AUDIO_DISCOVERY"), "1", StringComparison.OrdinalIgnoreCase);

private readonly bool _audioAdvertised;
```

Pass the audio-enabled flag in the constructor:

```csharp
// constructor signature
public AirPlayProbeService(string displayName, bool advertiseAudio = true)
{
    ...
    _audioAdvertised = advertiseAudio;
```

Update the one call site in `MainWindow.cs`:

```csharp
private readonly AirPlayProbeService _airPlayProbe = new AirPlayProbeService(
    StartupReceiverSettings.Effective.ReceiverName,
    StartupReceiverSettings.Effective.AudioEnabled);
```

Replace every use of the old `AudioAdvertised` static property with `_audioAdvertised`.
Leave `AudioDiscoveryLogging` wired to the same log calls where `AudioDiscoveryEnabled`
was used (lines ~429, ~1323) — only the advertise gate changes.

**Acceptance:**
- `audioEnabled: false` in `settings.json` → AirPlay picker does not list audio; no RAOP advertisement.
- `IMIRROR_AUDIO_DISCOVERY=1` still triggers verbose logging but does not force audio on.
- `audioEnabled: true` (default) → behaves identically to today.

---

## Task 3 — `ShouldWriteMirrorDiagnostics` in `AirPlayProbeService`

**File:** `MacMirrorReceiver.Networking/AirPlayProbeService.cs`

**Current behaviour (lines 742–746):**
```csharp
private static bool ShouldWriteMirrorDiagnostics()
{
    string? setting = Environment.GetEnvironmentVariable("IMIRROR_WRITE_DIAGNOSTICS");
    return setting == "1" || string.Equals(setting, "true", StringComparison.OrdinalIgnoreCase);
}
```

**Change:** Replace the env-var read with the resolved setting. Because
`AirPlayProbeService` already receives the resolved values via the constructor,
add a field:

```csharp
private readonly bool _writeDiagnostics;

public AirPlayProbeService(string displayName, bool advertiseAudio = true, bool writeDiagnostics = false)
{
    ...
    _writeDiagnostics = writeDiagnostics;
```

Update `ShouldWriteMirrorDiagnostics` to return the field (make it non-static):

```csharp
private bool ShouldWriteMirrorDiagnostics() => _writeDiagnostics;
```

Update the call site in `MainWindow.cs`:

```csharp
private readonly AirPlayProbeService _airPlayProbe = new AirPlayProbeService(
    StartupReceiverSettings.Effective.ReceiverName,
    StartupReceiverSettings.Effective.AudioEnabled,
    StartupReceiverSettings.Effective.WriteDiagnostics);
```

Keep the `AppLog.Write(... "set IMIRROR_WRITE_DIAGNOSTICS=1 ...")` skip message but
update the text to also mention the Settings overlay:

```csharp
AppLog.Write("AirPlay mirror diagnostic snapshot skipped; enable Write diagnostics in Settings or set IMIRROR_WRITE_DIAGNOSTICS=1.");
```

**Acceptance:**
- Settings overlay → Advanced → "Write diagnostics" checked → restart → `ShouldWriteMirrorDiagnostics()` returns `true`.
- `IMIRROR_WRITE_DIAGNOSTICS=1` env var still works and overrides the persisted value (the Settings UI already disables the checkbox when the env var is set).

---

## Task 4 — `TryOpenH264Dump` in `MediaFoundationD3D11Decoder` and `FfmpegDecoder`

**Files:**
- `MacMirrorReceiver.Video/MediaFoundationD3D11Decoder.cs` — `TryOpenH264Dump()` at line ~125
- `MacMirrorReceiver.Video/FfmpegDecoder.cs` — `TryOpenH264Dump()` at line ~232

Both have the same pattern:
```csharp
string? setting = Environment.GetEnvironmentVariable("IMIRROR_DUMP_H264");
if (string.IsNullOrWhiteSpace(setting)) { return; }
```

`TryOpenH264Dump` is called at decoder start (not a static initializer), so the value
can be passed in at call time.

**Pattern for both decoders — add a `bool dumpH264` parameter:**

For `MediaFoundationD3D11Decoder`, find the class constructor or the method that calls
`TryOpenH264Dump`. Add a property or constructor parameter:

```csharp
// Property injected by MainWindow before Start() / first use
public bool DumpH264Enabled { get; set; }
```

Inside `TryOpenH264Dump`, prepend:
```csharp
// Effective gate: persisted setting OR env var (env var already captured in ReceiverSettings)
if (!DumpH264Enabled && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("IMIRROR_DUMP_H264")))
{
    return;
}
```

This keeps backward compat (env var still works alone) while also honoring the UI toggle.

In `MainWindow.cs`, after constructing each decoder, set:
```csharp
decoder.DumpH264Enabled = StartupReceiverSettings.Effective.DumpH264;
```

Do the same for `FfmpegDecoder.TryOpenH264Dump`.

**Acceptance:**
- Settings → Advanced → "Dump H.264" checked → restart → decoder writes `.h264` file next to the exe.
- `IMIRROR_DUMP_H264=1` env var still triggers the dump regardless of the UI setting.
- When neither is set, no dump file is created.

---

## Task 5 — `AudioDumpWriter.TryCreate` in `AirPlayAudioReceiver`

**File:** `MacMirrorReceiver.Networking/AirPlayAudioReceiver.cs`

**Current behaviour (line ~373):**
```csharp
public static AudioDumpWriter? TryCreate(...)
{
    string? setting = Environment.GetEnvironmentVariable("IMIRROR_DUMP_AUDIO");
    if (string.IsNullOrWhiteSpace(setting)) { return null; }
```

`TryCreate` is called at `AirPlayAudioReceiver.Start` time (line ~98). Add a `bool dumpAudio`
parameter:

```csharp
public static AudioDumpWriter? TryCreate(AirPlayAudioStreamInfo info, AirPlayAudioCrypto? crypto,
    int macControlPort, int dataPort, int controlPort, bool dumpAudio = false)
{
    bool envEnabled = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("IMIRROR_DUMP_AUDIO"));
    if (!dumpAudio && !envEnabled) { return null; }
```

The call site in `AirPlayAudioReceiver` (line ~98) passes `dumpAudio` into `Start()`.
Add `bool dumpAudio = false` to `AirPlayAudioReceiver.Start()` and forward it.

In `MainWindow.cs`, where `_airPlayProbe.AudioStreamStarted` fires and the audio receiver
starts, pass `StartupReceiverSettings.Effective.DumpAudio`. (If `AirPlayAudioReceiver`
is owned by `AirPlayProbeService`, thread the value through the `AirPlayProbeService`
constructor similarly to Task 3.)

**Acceptance:**
- Settings → Advanced → "Dump audio" checked → restart → three dump files (rtp, control, clear) written next to exe.
- `IMIRROR_DUMP_AUDIO=1` still works regardless of UI setting.

---

## Task 6 — Video engine (`ForceSoftwareVideoRequested`) wiring

**File:** `MacMirrorReceiver/RenderModeSettings.cs`

**Current behaviour (lines 68–80):**
```csharp
public const string ForceSoftwareVideoEnvironmentVariableName = "IMIRROR_FORCE_SOFTWARE_VIDEO";

public static bool ForceSoftwareVideoRequested => string.Equals(
    Environment.GetEnvironmentVariable(ForceSoftwareVideoEnvironmentVariableName),
    "1",
    StringComparison.OrdinalIgnoreCase);

public static bool GpuVideoEngineEnabled =>
    Video.HighResolutionPipelineProbe.IsHardwareDecodeAvailable && !ForceSoftwareVideoRequested;
```

**Change:** `ForceSoftwareVideoRequested` should return `true` when *either* the env var is
set or the persisted `videoEngine` is `"software"`:

```csharp
public static bool ForceSoftwareVideoRequested
{
    get
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(ForceSoftwareVideoEnvironmentVariableName),
                "1",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // Fall through to persisted setting (env takes priority, already handled above)
        return ReceiverSettings.Load().Effective.VideoEngine == ReceiverVideoEngineSetting.Software;
    }
}
```

`GpuVideoEngineEnabled` already reads `ForceSoftwareVideoRequested`, so no further change.

> **Note:** `ForceSoftwareVideoRequested` is called at startup from static field initializers
> in `MainWindow` and `AirPlayProbeService`. `ReceiverSettings.Load()` reads the file — this
> is the same one read by `MainWindow`'s own `StartupReceiverSettings`. The extra file read
> at startup is a known cost (< 1 ms); do not cache across restarts.

**Acceptance:**
- Settings → Video engine → "Force software" → restart → `GpuVideoEngineEnabled` returns `false`; decoder uses FFmpeg.
- `IMIRROR_FORCE_SOFTWARE_VIDEO=1` still forces software regardless of UI.
- `IMIRROR_FORCE_SOFTWARE_VIDEO` unset + UI "Auto" → GPU path when hardware is available.

---

## Completion checklist

- [ ] Task 1 — Receiver name read from `StartupReceiverSettings.Effective.ReceiverName`
- [ ] Task 2 — `_audioAdvertised` field driven by settings + constructor param
- [ ] Task 3 — `ShouldWriteMirrorDiagnostics()` reads `_writeDiagnostics` field
- [ ] Task 4 — Both `TryOpenH264Dump()` callers honor `DumpH264Enabled` property
- [ ] Task 5 — `AudioDumpWriter.TryCreate` accepts `bool dumpAudio` parameter
- [ ] Task 6 — `ForceSoftwareVideoRequested` falls through to `ReceiverSettings`
- [ ] Task 7 (Build validation) — see section below
- [ ] Audio sync offset: **already wired live in this session — no action needed**

---

## Task 7 — Build validation and compile-error fixes

The front-end was written without a Windows SDK in the build environment, so it
has not been compiled yet. Your first step is to build the project as-is and fix
every error before doing any of Tasks 1–6.

**Build command (Windows x64, from repo root):**
```powershell
dotnet build .\MacMirrorReceiver.csproj -c Release
```

**Known areas to check (front-end changes that could produce errors):**

| Risk | Where to look |
|---|---|
| `Volatile.Read(ref _audioSyncOffsetMilliseconds)` — `_audioSyncOffsetMilliseconds` is an instance field but `Volatile.Read` needs a `ref` to a field, not a property | `MacMirrorReceiver/MainWindow.cs` — `ResolveAudioSyncTargetLatencyMilliseconds()` |
| `ReceiverSettingsSnapshot`, `ReceiverSettingsValues`, `ReceiverSettingsOverrides` types used in `MainWindow.cs` must be visible (same namespace) | `MacMirrorReceiver/ReceiverSettings.cs` — all `internal` in `MacMirrorReceiver` namespace ✓ |
| `ReceiverDiagnosticsDto` used in `SettingsRestartButton_Click` must be accessible | Same file |
| XAML named elements (`ReceiverNameTextBox`, `AudioSyncOffsetSlider`, etc.) used in `InitializeReceiverSettingsUi` must exist in `MainWindow.xaml` — element count was verified statically but XAML parser may surface type-mismatch errors | `MainWindow.xaml` overlay section |
| `RoutedPropertyChangedEventArgs<double>` in `AudioSyncOffsetSlider_ValueChanged` | Check `using` namespace in `MainWindow.cs` — should already be present via `System.Windows.Controls` |
| `TextChangedEventArgs` in `ReceiverNameTextBox_TextChanged` | Same |
| `InitializeReceiverSettingsUi()` is called after `InitializeRenderModeSettingsUi()` in constructor — verify ordering does not depend on fields set by the render-mode initializer | `MainWindow.cs` constructor ~line 273 |
| `WasapiAudioOutput` usage in `AudioSyncOffsetSlider_ValueChanged` — `_audioOutput` field type and `SetSyncTargetLatencyMilliseconds` exist on it | `MacMirrorReceiver.Audio/WasapiAudioOutput.cs` line 76 ✓ |

**Fix approach:**
1. Run the build. Collect all CS errors.
2. Fix each error in the file(s) where it occurs. Do not change logic, only fix
   type/namespace/accessibility issues.
3. Re-run until the build is clean. Then proceed to Tasks 1–6.
4. Run the build one final time after Tasks 1–6 to confirm no regressions.

**Warnings:** Treat new `CS8600`/`CS8602` nullable warnings as errors (the project
has `<Nullable>enable</Nullable>`). Fix nullability rather than suppressing.

**Do not change:** test tools under `tools/` — they are excluded from the main project
(`<Compile Remove="tools\**\*.cs" />`).

