# Spec: v0.2 Phase 4 — Build, Test, Metadata, and CI

**Handoff:** codex-backend
**Branch:** `claude/magical-faraday-6ex3c7`

## What's already done (do not re-implement)

The following were completed directly before this handoff:

- `iMirror.sln` — created, includes `MacMirrorReceiver.csproj` and
  `MacMirrorReceiver.Tests/MacMirrorReceiver.Tests.csproj`.
- `.github/workflows/ci.yml` — Windows CI: restore → build → test on every push
  to `main` or `claude/**` and on PRs. Uses `iMirror.sln`.
- `.github/workflows/release.yml` — tag-triggered packaging workflow:
  builds, tests, downloads FFmpeg, runs `publish-win-x64.ps1`, computes
  SHA-256, publishes a GitHub Release with the zip and SmartScreen note.
- `Properties/AssemblyInfo.cs` — versions bumped to `0.2.0.0` /
  `AssemblyInformationalVersion("0.2.0")` (Phase 1 decision: v0.2.0 SemVer).

## Tasks for Codex

### Task 1 — `unify-ffmpeg-resolution`: pin FFmpeg with a checksum (M) — DONE

**Completed directly.** `.github/workflows/release.yml` is now pinned to:
- URL: `https://github.com/GyanD/codexffmpeg/releases/download/8.1.1/ffmpeg-8.1.1-essentials_build.zip`
  (GitHub permanent versioned asset, not the floating `ffmpeg-release-essentials.zip`)
- SHA-256: `6F58CE889F59C311410F7D2B18895B33C03456463486F3B1EBC93D97A0F54541`

Verified: the zip contains `ffmpeg-8.1.1-essentials_build/bin/ffmpeg.exe`
(essentials flavor), and the workflow's recursive `ffmpeg.exe` search resolves it.
To upgrade FFmpeg later, bump the URL version and replace the hash.

The original task description (kept for reference):

**File:** `.github/workflows/release.yml`, section "Download Gyan FFmpeg Essentials"

The `$expectedSha256 = ""` placeholder must be filled with a real pinned
release. The floating URL (`ffmpeg-release-essentials.zip`) always points to
the latest Gyan build, which is **not reproducible** — a silent upstream change
could bundle different binaries.

**Steps:**
1. Identify the latest stable Gyan.FFmpeg.Essentials release from
   `https://www.gyan.dev/ffmpeg/builds/` — find a **versioned, permanent URL**
   for a specific release zip (e.g. `ffmpeg-7.x.x-essentials_build.zip` from
   the release folder), NOT the `ffmpeg-release-essentials.zip` floating alias.
2. Download the zip and compute its SHA-256 (PowerShell:
   `(Get-FileHash ffmpeg-*.zip -Algorithm SHA256).Hash`).
3. Update the workflow:
   - Replace `$ffmpegUrl` with the pinned versioned URL.
   - Set `$expectedSha256` to the computed hash (uppercase hex).
4. Verify: extracting the zip must produce an `ffmpeg.exe` that runs
   `ffmpeg -version` and reports `essentials_build` (the publish script's
   `Test-FfmpegBuild` already validates this — it will warn or throw if not).

**Do not change** the rest of the download/extract/copy logic; it is correct.

---

### Task 2 — `manual-update-link`: add a check-for-updates path (S)

**Decision (Phase 1):** v0.2 is manual-update only. No auto-update. Provide
a one-click path to the releases page so users can check themselves.

**Required change:**
- Add a "Check for updates" link (or button) somewhere accessible — reasonable
  locations: the Settings window footer, an About section, or a dedicated About
  dialog triggered from the tray context menu or the Settings window.
- The link opens `https://github.com/Balragon/iMirror/releases` in the default
  browser using `Process.Start(new ProcessStartInfo { FileName = url,
  UseShellExecute = true })`.
- No version comparison, no network call, no auto-download. Just opens the page.
- If placed in `SettingsWindow`, the simplest location is a small hyperlink in
  the footer alongside the restart button row. A tray "Check for updates" menu
  item is also acceptable.

---

### Task 3 — `release-version-metadata`: display version in the UI (S)

Users need to see which version they're running. This matters most when
reporting issues or checking if they have the latest.

**Required change:**
- Read `Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion`
  and display it somewhere visible:
  - Option A (preferred): a small muted version label in the Settings window
    footer, e.g. "iMirror 0.2.0+abc1234".
  - Option B: an About item in the tray context menu showing the version in
    the menu item text (read-only, not clickable).
- Do not add a separate About dialog for v0.2 — that is scope creep.

**CI InformationalVersion stamping:** `.github/workflows/release.yml` already
stamps `AssemblyInformationalVersion` from the git tag + short SHA before
packaging. On non-release builds (CI push, local dev), it remains `"0.2.0"`.
The version display must handle both formats gracefully (with and without `+sha`).

---

### Task 4 — `release-critical-tests`: add remaining pure-logic tests (L)

The existing test suite (`MacMirrorReceiver.Tests`) already covers:
- `CleanupGuards.RunStep` isolation
- Stale generation gate predicate
- `ShouldUseHighResolutionD3DPath` engine gate
- `WindowLifecycleState` hide/exit transitions

Add tests for the remaining pure-logic cases that don't need a GUI or AirPlay
device. **Do not add GUI tests, mock-heavy integration tests, or tests that
require Windows-only APIs at test time if they would prevent `dotnet test` from
running.** Keep the existing pattern: small, focused, no mocks.

High-value additions:
- `ReceiverSettings.NormalizeReceiverName` — truncation, whitespace trim, empty
  → default behavior.
- `ReceiverSettings.ClampAudioOffset` — boundary values (min, max, below min,
  above max).
- `ReceiverSettings.Load()` round-trip — write a temp settings.json, load it,
  verify effective values. (Check if the method is testable with a temp path;
  if Load() hard-codes the path, expose an overload or skip.)
- `StartupDiagnostics` preflight verdict logic — given mock probe state
  (IsAirPlayListenerBound, IsRaopListenerBound, IsMdnsBound, bind errors),
  verify the correct `PreflightStatus` is returned. Requires either an interface
  on `AirPlayProbeService` or a data-only struct; add the minimum needed to make
  it testable without touching the real networking code.
- `AirPlayProbeService.IsMirrorControlLabel` — if it can be made internal/visible
  to tests, add a simple table test.

---

## Build verification

After all tasks:
```powershell
dotnet build iMirror.sln -c Release
dotnet test iMirror.sln -c Release --no-build
```

Both must succeed with no new errors or warnings. Test count must be ≥ 8
(existing 4 + new additions from Task 4).

The release workflow (`release.yml`) is complete and will run automatically on
the next `v*` tag push — no manual action needed to validate CI shape; the
workflow structure is already correct.
