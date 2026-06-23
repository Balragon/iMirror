# First-Run Diagnostics Spec

**Target session:** Codex / GPT-5.5 xhigh  
**Branch:** `claude/magical-faraday-6ex3c7`  
**Language:** C# 11, .NET 8, WPF  
**Design doc:** `docs/first-run-experience.md` — read first for rationale.

## Context

Three setup conditions fail **silently** today and each ends with the user
concluding "it doesn't work":

1. **FFmpeg missing** — resolved lazily at first stream; error buried in `SetStatus()`.
2. **Firewall blocks listeners** — `TryStartListener()` returns `null` silently; app
   advertises nothing, never appears in AirPlay picker.
3. **No usable network** — wrong subnet / VPN / no IPv4; mDNS multicast fails silently.

The fix is a startup preflight that runs once after `AirPlayProbeService.StartAsync()`,
produces a `PreflightReport`, and hands it to the UI to render a "readiness strip" in
the sidebar.

**Do not change:** `ReceiverSettings.cs`, `RenderModeSettings.cs`, any file not
explicitly listed below.  
**Build check:** `dotnet build MacMirrorReceiver.csproj -c Release` must succeed
with no new warnings.

---

## Data model (new file)

**File:** `MacMirrorReceiver/StartupDiagnostics.cs`

Create this file in the `MacMirrorReceiver` namespace:

```csharp
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using MacMirrorReceiver.Video;       // FfmpegDecoder.FindFfmpeg()
using MacMirrorReceiver.Networking;  // AirPlayProbeService

namespace MacMirrorReceiver;

internal enum PreflightStatus { Ok, Warning, Blocked }

internal sealed record PreflightCheck(
    string Id,
    string Title,
    PreflightStatus Status,
    string Message,
    string? Detail = null);

internal sealed record PreflightReport(
    IReadOnlyList<PreflightCheck> Checks,
    PreflightStatus Worst);

internal static class StartupDiagnostics
{
    public static async Task<PreflightReport> RunAsync(AirPlayProbeService probe)
    {
        // Checks 1 and 3 are independent; check 2 reads probe state that is
        // already resolved — run all three in parallel.
        Task<PreflightCheck> ffmpegTask  = Task.Run(CheckFfmpeg);
        Task<PreflightCheck> networkTask = Task.Run(CheckNetwork);
        PreflightCheck listeners = CheckListeners(probe);

        PreflightCheck ffmpeg  = await ffmpegTask;
        PreflightCheck network = await networkTask;

        var checks = new[] { ffmpeg, listeners, network };
        PreflightStatus worst = PreflightStatus.Ok;
        foreach (PreflightCheck c in checks)
        {
            if (c.Status > worst) worst = c.Status;
        }
        return new PreflightReport(checks, worst);
    }

    // ── Check 1: FFmpeg ──────────────────────────────────────────────────────

    private static PreflightCheck CheckFfmpeg()
    {
        string? path = FfmpegDecoder.FindFfmpeg();
        if (path != null)
        {
            return new PreflightCheck("ffmpeg", "FFmpeg", PreflightStatus.Ok,
                "Found.", path);
        }
        return new PreflightCheck("ffmpeg", "FFmpeg", PreflightStatus.Blocked,
            "FFmpeg not found. Audio and software video are disabled.",
            "Place ffmpeg.exe at tools\\ffmpeg\\bin\\ffmpeg.exe or add it to PATH.");
    }

    // ── Check 2: Listeners ───────────────────────────────────────────────────
    // Reads the probe's already-bound state — does NOT re-bind ports.

    private static PreflightCheck CheckListeners(AirPlayProbeService probe)
    {
        bool mdnsBound    = probe.IsMdnsBound;
        bool airPlayBound = probe.IsAirPlayListenerBound;
        bool raopBound    = probe.IsRaopListenerBound;

        if (!mdnsBound || !airPlayBound)
        {
            var missing = new System.Collections.Generic.List<string>();
            if (!mdnsBound)    missing.Add("mDNS UDP 5353");
            if (!airPlayBound) missing.Add("AirPlay TCP 7000");
            return new PreflightCheck("listeners", "Firewall / discovery",
                PreflightStatus.Blocked,
                "Windows Firewall is blocking AirPlay. iMirror is not discoverable.",
                $"Blocked: {string.Join(", ", missing)}. Open Windows Security → " +
                "Firewall & network protection → Allow an app through firewall, " +
                "find iMirror, and check Private networks.");
        }
        if (!raopBound)
        {
            return new PreflightCheck("listeners", "Firewall / discovery",
                PreflightStatus.Warning,
                "Legacy audio port unavailable (TCP 5000). Mirroring still works.",
                null);
        }
        return new PreflightCheck("listeners", "Firewall / discovery",
            PreflightStatus.Ok, "All listeners bound.", null);
    }

    // ── Check 3: Network ─────────────────────────────────────────────────────

    private static PreflightCheck CheckNetwork()
    {
        string? localIp  = null;
        bool hasUsable   = false;
        bool hasVirtualOnly = true;

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up)       continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            if (!nic.SupportsMulticast)                              continue;

            foreach (UnicastIPAddressInformation addr in nic.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                string addrStr = addr.Address.ToString();
                if (addrStr.StartsWith("169.254")) continue; // APIPA / link-local

                bool isVirtual =
                    nic.Description.Contains("Virtual", System.StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("VPN",     System.StringComparison.OrdinalIgnoreCase) ||
                    nic.Description.Contains("Tunnel",  System.StringComparison.OrdinalIgnoreCase) ||
                    nic.Name.Contains("tun",            System.StringComparison.OrdinalIgnoreCase) ||
                    nic.Name.Contains("tap",            System.StringComparison.OrdinalIgnoreCase);

                hasUsable = true;
                if (!isVirtual)
                {
                    hasVirtualOnly = false;
                    localIp ??= $"{addrStr} ({nic.Name})";
                }
                else
                {
                    localIp ??= $"{addrStr} ({nic.Name} — virtual)";
                }
            }
        }

        if (!hasUsable)
        {
            return new PreflightCheck("network", "Network", PreflightStatus.Blocked,
                "No network connection. Connect to the same Wi-Fi as your Mac/iPhone.",
                null);
        }
        if (hasVirtualOnly)
        {
            return new PreflightCheck("network", "Network", PreflightStatus.Warning,
                "Connected via VPN or virtual adapter. Senders on your Wi-Fi may not see iMirror.",
                localIp);
        }
        return new PreflightCheck("network", "Network", PreflightStatus.Ok,
            $"Network OK.", localIp);
    }
}
```

---

## Task 1 — Expose bound-port state on `AirPlayProbeService`

**File:** `MacMirrorReceiver.Networking/AirPlayProbeService.cs`

`StartupDiagnostics.CheckListeners()` must read bind results without re-binding.
Add three read-only properties after the existing listener field declarations
(around line 90, after `_timingListener`):

```csharp
// Expose bind results for startup diagnostics — read after StartAsync().
public bool IsMdnsBound        => _mdnsClient != null;
public bool IsAirPlayListenerBound => _airPlayListener != null;
public bool IsRaopListenerBound    => _raopListener != null;
```

No other change to `AirPlayProbeService`.

**Acceptance:**
- When firewall is open → `IsMdnsBound == true`, `IsAirPlayListenerBound == true`.
- When port 7000 is blocked → `IsAirPlayListenerBound == false`.

---

## Task 2 — Make `FfmpegDecoder.FindFfmpeg` internal and accessible

**File:** `MacMirrorReceiver.Video/FfmpegDecoder.cs`

`FindFfmpeg()` (line ~817) is already declared `internal static`. Verify it is
accessible from `MacMirrorReceiver` (same solution). If the method is `private`
or `private static`, change the access modifier to `internal static`.

No logic change.

---

## Task 3 — Call diagnostics in `Window_Loaded` and render the strip

**File:** `MacMirrorReceiver/MainWindow.cs`

**Current `Window_Loaded` (lines 308–313):**
```csharp
private async void Window_Loaded(object sender, RoutedEventArgs e)
{
    AppLog.Write("MainWindow Loaded entered.");
    await _browser.StartAsync();
    await _airPlayProbe.StartAsync();
}
```

**Replace with:**
```csharp
private async void Window_Loaded(object sender, RoutedEventArgs e)
{
    AppLog.Write("MainWindow Loaded entered.");
    await _browser.StartAsync();
    await _airPlayProbe.StartAsync();
    PreflightReport report = await StartupDiagnostics.RunAsync(_airPlayProbe);
    AppLog.Write($"Preflight: {report.Worst} — " +
        string.Join("; ", System.Linq.Enumerable.Select(report.Checks, c => $"{c.Id}={c.Status}")));
    BindReadinessStrip(report);
}
```

Add `BindReadinessStrip` and `ReadinessRecheckButton_Click` anywhere in the
settings/UI region of `MainWindow.cs`:

```csharp
private void BindReadinessStrip(PreflightReport report)
{
    // Hide the strip entirely when all checks pass.
    if (report.Worst == PreflightStatus.Ok)
    {
        ReadinessStripBorder.Visibility = Visibility.Collapsed;
        return;
    }

    ReadinessStripBorder.Visibility = Visibility.Visible;

    // Strip header color and text.
    bool hasBlocked = report.Worst == PreflightStatus.Blocked;
    ReadinessStripHeaderText.Text = hasBlocked
        ? "Setup needs attention"
        : "Minor setup notes";
    ReadinessStripHeaderText.Foreground = hasBlocked
        ? (System.Windows.Media.Brush)FindResource("DangerBrush")
        : (System.Windows.Media.Brush)FindResource("WarningBrush");

    // Per-row visibility and content — one row per check.
    foreach (PreflightCheck check in report.Checks)
    {
        switch (check.Id)
        {
        case "ffmpeg":
            FfmpegCheckRow.Visibility   = check.Status == PreflightStatus.Ok ? Visibility.Collapsed : Visibility.Visible;
            FfmpegCheckText.Text        = check.Message;
            FfmpegCheckDetail.Text      = check.Detail ?? string.Empty;
            FfmpegCheckDetail.Visibility = check.Detail != null ? Visibility.Visible : Visibility.Collapsed;
            FfmpegCheckRow.Tag          = check.Status; // used for brush lookup below
            break;
        case "listeners":
            ListenersCheckRow.Visibility   = check.Status == PreflightStatus.Ok ? Visibility.Collapsed : Visibility.Visible;
            ListenersCheckText.Text        = check.Message;
            ListenersCheckDetail.Text      = check.Detail ?? string.Empty;
            ListenersCheckDetail.Visibility = check.Detail != null ? Visibility.Visible : Visibility.Collapsed;
            break;
        case "network":
            NetworkCheckRow.Visibility   = check.Status == PreflightStatus.Ok ? Visibility.Collapsed : Visibility.Visible;
            NetworkCheckText.Text        = check.Message;
            NetworkCheckDetail.Text      = check.Detail ?? string.Empty;
            NetworkCheckDetail.Visibility = check.Detail != null ? Visibility.Visible : Visibility.Collapsed;
            break;
        }
    }
}

private async void ReadinessRecheckButton_Click(object sender, RoutedEventArgs e)
{
    ReadinessRecheckButton.IsEnabled = false;
    ReadinessRecheckButton.Content = "Checking…";
    PreflightReport report = await StartupDiagnostics.RunAsync(_airPlayProbe);
    BindReadinessStrip(report);
    ReadinessRecheckButton.IsEnabled = true;
    ReadinessRecheckButton.Content = "Re-check";
}

private void FirewallHelpButton_Click(object sender, RoutedEventArgs e)
{
    // Open Windows Firewall allow-apps page.
    try
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "ms-settings:windowsdefender",
            UseShellExecute = true
        });
    }
    catch
    {
        // Fall back: open the inline detail text (already visible).
    }
}
```

**Acceptance:**
- All checks `Ok` → `ReadinessStripBorder.Visibility == Collapsed`, strip invisible.
- Any `Blocked` → strip visible, header red, `DangerBrush`.
- Re-check button re-runs diagnostics without restart.

---

## Task 4 — Add `DangerBrush` resource (if absent)

**File:** `MainWindow.xaml`

Search for `DangerBrush` in the `<Window.Resources>` section. If it does not exist,
add it adjacent to `WarningBrush`:

```xml
<SolidColorBrush x:Key="DangerBrush" Color="#FF3B30" />
```

No other resource change.

---

## Task 5 — Add readiness strip XAML

**File:** `MainWindow.xaml`

Insert the following Border **immediately after** the closing `</Border>` of the
"Ready to receive" blue card (line 236) and **before** the opening `<Border>` of
the "How to mirror" card (line 238).

The readiness strip is collapsed by default; `BindReadinessStrip` drives it:

```xml
<!-- Readiness strip — collapsed when all preflight checks pass -->
<Border x:Name="ReadinessStripBorder"
        Visibility="Collapsed"
        Background="#FFF9F0"
        BorderBrush="#FFDDA0"
        BorderThickness="1"
        CornerRadius="8"
        Padding="12"
        Margin="0,0,0,16">
    <StackPanel>

        <!-- Header row -->
        <Grid Margin="0,0,0,8">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <TextBlock x:Name="ReadinessStripHeaderText"
                       Text="Setup needs attention"
                       FontWeight="SemiBold"
                       FontSize="13"
                       Foreground="{StaticResource DangerBrush}" />
            <Button x:Name="ReadinessRecheckButton"
                    Grid.Column="1"
                    Content="Re-check"
                    Click="ReadinessRecheckButton_Click"
                    Style="{StaticResource SecondaryButtonStyle}"
                    Padding="8,3"
                    FontSize="11" />
        </Grid>

        <!-- FFmpeg row -->
        <StackPanel x:Name="FfmpegCheckRow" Margin="0,0,0,8" Visibility="Collapsed">
            <TextBlock x:Name="FfmpegCheckText"
                       TextWrapping="Wrap"
                       FontSize="12"
                       Foreground="{StaticResource MutedTextBrush}" />
            <TextBlock x:Name="FfmpegCheckDetail"
                       TextWrapping="Wrap"
                       FontSize="11"
                       Foreground="{StaticResource MutedTextBrush}"
                       Margin="0,3,0,0"
                       Visibility="Collapsed" />
        </StackPanel>

        <!-- Firewall / listeners row -->
        <StackPanel x:Name="ListenersCheckRow" Margin="0,0,0,8" Visibility="Collapsed">
            <TextBlock x:Name="ListenersCheckText"
                       TextWrapping="Wrap"
                       FontSize="12"
                       Foreground="{StaticResource MutedTextBrush}" />
            <TextBlock x:Name="ListenersCheckDetail"
                       TextWrapping="Wrap"
                       FontSize="11"
                       Foreground="{StaticResource MutedTextBrush}"
                       Margin="0,3,0,0"
                       Visibility="Collapsed" />
            <Button x:Name="FirewallHelpButton"
                    Content="Open Windows Firewall settings"
                    Click="FirewallHelpButton_Click"
                    Style="{StaticResource SecondaryButtonStyle}"
                    HorizontalAlignment="Left"
                    Margin="0,6,0,0"
                    Padding="8,3"
                    FontSize="11" />
        </StackPanel>

        <!-- Network row -->
        <StackPanel x:Name="NetworkCheckRow" Visibility="Collapsed">
            <TextBlock x:Name="NetworkCheckText"
                       TextWrapping="Wrap"
                       FontSize="12"
                       Foreground="{StaticResource MutedTextBrush}" />
            <TextBlock x:Name="NetworkCheckDetail"
                       TextWrapping="Wrap"
                       FontSize="11"
                       Foreground="{StaticResource MutedTextBrush}"
                       Margin="0,3,0,0"
                       Visibility="Collapsed" />
        </StackPanel>

    </StackPanel>
</Border>
```

**Named elements added (12 total):**

| Name | Type | Used in |
|---|---|---|
| `ReadinessStripBorder` | Border | `BindReadinessStrip` — outer visibility |
| `ReadinessStripHeaderText` | TextBlock | header text + foreground |
| `ReadinessRecheckButton` | Button | re-run diagnostics |
| `FfmpegCheckRow` | StackPanel | row visibility |
| `FfmpegCheckText` | TextBlock | check message |
| `FfmpegCheckDetail` | TextBlock | detail / path hint |
| `ListenersCheckRow` | StackPanel | row visibility |
| `ListenersCheckText` | TextBlock | check message |
| `ListenersCheckDetail` | TextBlock | firewall instructions |
| `FirewallHelpButton` | Button | open Firewall settings |
| `NetworkCheckRow` | StackPanel | row visibility |
| `NetworkCheckText` | TextBlock | check message |
| `NetworkCheckDetail` | TextBlock | local IP / NIC name |

---

## Completion checklist

- [ ] Task 1 — `IsMdnsBound`, `IsAirPlayListenerBound`, `IsRaopListenerBound` on `AirPlayProbeService`
- [ ] Task 2 — `FfmpegDecoder.FindFfmpeg()` is `internal static` (verify, no logic change)
- [ ] Task 3 — `StartupDiagnostics.cs` created; `Window_Loaded` calls `RunAsync`; `BindReadinessStrip` + two click handlers in `MainWindow.cs`
- [ ] Task 4 — `DangerBrush` resource present in `MainWindow.xaml`
- [ ] Task 5 — Readiness strip XAML inserted at line 237 in `MainWindow.xaml`; all 12 named elements present exactly once
- [ ] Task 6 (Build validation) — `dotnet build MacMirrorReceiver.csproj -c Release` clean; fix any errors before Tasks 1–5, re-run after

---

## Task 6 — Build validation

Run `dotnet build MacMirrorReceiver.csproj -c Release` first. Fix all errors before
making any of the above changes. Known risk areas:

| Risk | Where |
|---|---|
| `FfmpegDecoder` is in `MacMirrorReceiver.Video` assembly — `StartupDiagnostics` in `MacMirrorReceiver` must reference it; confirm project reference exists in `.csproj` | `MacMirrorReceiver.csproj` |
| `NetworkInterface` needs `using System.Net.NetworkInformation` | `StartupDiagnostics.cs` |
| `DangerBrush` key referenced in C# via `FindResource` — must be declared in `Window.Resources` before use | `MainWindow.xaml` |
| `SecondaryButtonStyle` must exist for `ReadinessRecheckButton` and `FirewallHelpButton` | `MainWindow.xaml` resource dictionary |

After Tasks 1–5 build clean, run the build one final time to confirm no regressions.
