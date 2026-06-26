# iMirror v0.4 Lightweight In-App Updater — Design

Companion to `docs/specs/v04-product-surface-roadmap.md` (Phase 2). This is the
detailed design for the auto-update mechanism. Implementation and testing are
**Windows-only** (handed to codex); this document is the buildable spec.

## Goal

Let iMirror notice a newer release and help the user install it, without a
heavyweight update framework. The app checks the GitHub Releases API on startup
(and on demand), and if a newer version exists, downloads the new Inno
`Setup.exe` and runs it. This is the **documented Inno update pattern**, not a
workaround.

Non-goals: delta packages, release channels beyond stable/prerelease, silent
background updates, staged rollouts. Those are Velopack's domain and are
explicitly out of scope (see roadmap "Framework decision" for why).

## What already exists (build on this, don't rebuild)

| Asset | Location | Reuse |
|---|---|---|
| Current version string | `AppVersionInfo.InformationalVersion` | Source of the "current" semver to compare. |
| Releases URL | `AppVersionInfo.ReleasesUrl` | Fallback "open in browser" path. |
| Manual update entry point | `SettingsWindow` "Updates" hyperlink → `AppVersionInfo.OpenReleasesPage()` | Promote into "Check for updates" that runs the new flow; keep browser-open as fallback. |
| Release pipeline | `.github/workflows/release.yml` (tags `v*.*.*`, `prerelease` flag) | The update feed. No new infra. |
| Writable temp/dirs | `MacMirrorReceiver.AppPaths` (`%LOCALAPPDATA%\iMirror`) | Download the Setup to a writable temp dir, never the install folder. |

## Components

### 1. `UpdateInfo` (record)
Result of a check.
```
sealed record UpdateInfo(
    string LatestVersion,      // e.g. "0.4.0"
    bool IsNewer,              // LatestVersion > current
    bool IsPrerelease,
    string SetupAssetUrl,      // browser_download_url of the Setup .exe asset
    long   SetupAssetSize,     // bytes, for verification
    string ReleaseHtmlUrl);    // for "view release notes"
```

### 2. `UpdateService`
Stateless service with two public operations:
- `Task<UpdateInfo?> CheckAsync(bool includePrerelease, CancellationToken)`
  - GET `https://api.github.com/repos/Balragon/iMirror/releases` (or
    `/releases/latest` for stable-only).
  - Pick the newest release honoring the channel policy (skip `prerelease`
    unless opted in).
  - Parse the asset list; select the asset whose name matches the Setup naming
    convention (e.g. `iMirror-<version>-setup.exe`).
  - Compare `tag_name` (stripped `v`) against `AppVersionInfo.InformationalVersion`
    using **semver** comparison (not string compare).
  - Returns `null` on any failure (network, parse, rate-limit) — **never throws
    to the caller**. Failure = "no update offered," app continues normally.
- `Task<string> DownloadSetupAsync(UpdateInfo, IProgress<double>, CancellationToken)`
  - Download `SetupAssetUrl` to `Path.Combine(AppPaths.<TempDir>, fileName)`.
  - Verify the downloaded size equals `SetupAssetSize` (and SHA-256 if the
    release publishes one — see "Integrity" below).
  - Return the local path. Delete partial files on failure.

### 3. `UpdateLauncher`
- `void LaunchAndExit(string setupPath)`
  - `Process.Start` the Setup with flags that let it close+relaunch iMirror via
    Inno's restart manager (see "Apply flow" — typically `/SILENT` or the
    interactive default, plus Inno's `CloseApplications`/`RestartApplications`).
  - Then request a clean app shutdown so files are unlocked for the installer.

### 4. UI hook (WPF, `SettingsWindow` + a startup nudge)
- **Startup:** fire `CheckAsync` in the background after the main window is up
  (never block launch). If `IsNewer`, show a **non-blocking** notice (info bar /
  toast / status line) — "iMirror v0.4.0 is available — Update".
- **Settings:** the existing "Updates" hyperlink becomes "Check for updates,"
  invoking `CheckAsync` immediately and reporting the result inline. Keep
  "open releases page" reachable as a fallback.

## Apply flow (the tricky part: a running app updating itself)

The classic footgun: the installer can't overwrite `iMirror.exe` while it's
running. Inno Setup solves this natively — do **not** hand-roll process killing.

1. User clicks **Update** on the notice.
2. `DownloadSetupAsync` fetches and verifies the new Setup into temp.
3. `UpdateLauncher.LaunchAndExit`:
   - starts the Setup,
   - the Inno script declares `CloseApplications=yes` and
     `RestartApplications=yes` (and `[Setup] AppMutex` matching the app's mutex)
     so Setup's restart manager **asks/closes the running iMirror**, replaces
     files, and **relaunches** it,
   - iMirror also proactively exits shortly after launching Setup as a belt-and-
     suspenders measure.
4. New version starts. Settings (in `%LOCALAPPDATA%\iMirror\Config`) and logs
   are untouched because they live **outside** the install dir (v0.3 dividend).

This requires coordination between the app (define a stable single-instance
**mutex name**) and the Inno script (`AppMutex` = same name). Document the mutex
name as a shared constant.

## Integrity / safety

- **Size check (minimum):** verify the downloaded Setup's byte length equals the
  GitHub asset `size`. Cheap, catches truncated downloads.
- **SHA-256 (recommended):** have `release.yml` publish a `SHA256SUMS` asset (it
  already computes FFmpeg hashes; extend to the Setup). The updater verifies the
  Setup hash before running it. This is the real tamper/corruption guard.
- **HTTPS only**, GitHub API + `*.githubusercontent.com` asset host.
- **Fail safe:** any failure at any step → discard, surface a quiet message,
  leave the user on the current version. Never leave a half-applied state (Inno
  installs are transactional).
- **Signature (later):** once Phase 3 (code signing) lands, the downloaded Setup
  is Authenticode-signed; optionally verify the publisher before running. Until
  then, size+SHA is the guard. Acceptable for a developer audience.

## Behavior policy

| Concern | Decision |
|---|---|
| When to check | Once per app launch, in the background, after UI is up. Plus manual "Check for updates." |
| Throttle | At most one **automatic** notice per day (persist "last notified version + timestamp" in Config). Manual check is never throttled. |
| Dismissal | Notice is dismissible; dismissing a version suppresses re-nagging for that version (until a newer one appears). |
| Channel | **Stable only for v0.4** (resolved). Use `/releases/latest`, which excludes prereleases — no prerelease toggle yet. A prerelease opt-in can be added later without rework. |
| Forced updates | None. Always user-initiated apply. |
| Rate limit | Unauthenticated GitHub API (60/req/hr/IP) is ample for once-per-launch. No token needed. |

## GitHub API specifics

- Endpoint for v0.4: `GET /repos/Balragon/iMirror/releases/latest` — `/latest`
  **excludes** prereleases, which matches the resolved stable-only channel. (The
  full `/releases` list is only needed if a prerelease channel is added later;
  keep `CheckAsync`'s `includePrerelease` param defaulted to `false` as the
  forward-compat hook.)
- Headers: `User-Agent: iMirror-Updater` (GitHub requires a UA),
  `Accept: application/vnd.github+json`.
- Asset selection: match on a stable naming convention emitted by the release
  workflow. **Action item:** the Inno `inst-ci-iscc` task must name the Setup
  deterministically, e.g. `iMirror-<version>-setup.exe`, and upload it as a
  release asset so the updater can find it.

## CI / release pipeline impact

The release workflow (`release.yml`) gains, alongside the existing zip:
1. Compile the Inno `.iss` with `ISCC.exe` → `iMirror-<version>-setup.exe`.
2. Compute and publish `SHA256SUMS` (covering the Setup, and ideally the zip).
3. Upload the Setup (and `SHA256SUMS`) as release assets.

No change to the trigger model — still tag-driven `v*.*.*`.

## Test plan (Windows, codex)

1. **Newer available:** stub/point at a release with a higher tag → notice shows,
   download+verify+launch succeeds, app relaunches on new version, settings/logs
   preserved.
2. **Up to date:** highest release == current → no notice; manual check says
   "you're on the latest."
3. **Prerelease gating:** a prerelease newer than stable → not offered on stable
   channel; offered when prerelease channel is on.
4. **Network failure / rate limit / malformed JSON:** `CheckAsync` returns null,
   app launches and runs normally, no crash, no nag.
5. **Corrupt/truncated download:** size/SHA mismatch → aborted, partial file
   deleted, user stays on current version.
6. **File-lock during apply:** confirm Inno restart manager closes + relaunches
   the running app (mutex coordination) without a manual kill.
7. **Throttle:** second launch same day does not re-notify the same version;
   manual check still works.

## Acceptance criteria (mirror the roadmap Phase 2 table)

- [ ] `UpdateService.CheckAsync` never throws to callers; failures yield no offer.
- [ ] Semver comparison (not string) decides "newer."
- [ ] Channel policy honored (stable excludes prereleases by default).
- [ ] Download verified (size, and SHA-256 once published) before launch.
- [ ] Apply uses Inno restart manager + shared mutex; app relaunches cleanly.
- [ ] All failure paths leave the user on their working version.
- [ ] Settings/logs in `%LOCALAPPDATA%\iMirror` survive the update untouched.

## Open items to resolve with the Inno work (Phase 1)

- **Setup asset name convention** (`iMirror-<version>-setup.exe`) — must match
  between `release.yml`, the `.iss`, and `UpdateService` asset selection.
- **Shared mutex name** — single constant referenced by the app's
  single-instance guard and the Inno `AppMutex`.
- **`SHA256SUMS` format** — decide the file format the updater parses.
