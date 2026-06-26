# Phase 4 — Codex Handoff: First Public Release (`v0.4.0`)

**Owner:** codex · **Status:** ready to pick up · **Date handed off:** 2026-06-26

This is the execution note for Phase 4 of
`docs/specs/open-source-strategy.md`. Phase 0–2 (licensing, scaffolding,
installer/updater) are done; Phase 3 (repo governance / GitHub settings) is
being applied by the maintainer in the web UI in parallel. Phase 4 is the
"actually publicly distribute" moment and is **yours**.

Two pieces of real work plus the release mechanics:

1. **Code — GPLv3 §5(d) in-app legal notice** (the one open Phase 2 item).
2. **Validation — real-hardware soak + installer/updater E2E** on Windows.
3. **Release — tag `v0.4.0`, verify artifacts, write GPL-compliant notes.**

Do them in that order: the §5(d) notice must be merged *before* the build
that gets tagged, and validation must pass on the exact commit you tag.

---

## 1. GPLv3 §5(d) in-app legal notice  (code — do this first)

### Why
iMirror is an interactive WPF program under GPLv3. GPLv3 §5(d) requires the
program to display "Appropriate Legal Notices": copyright, an explicit
**no-warranty** statement, that it is **GPLv3**, and **where to get the
source**. This is a genuine license obligation, not polish — the public
release should not ship without it.

### Where it goes
`MacMirrorReceiver/SettingsWindow.xaml`, the footer **`StackPanel` at
`Grid.Row="2"`** (lines ~283–356). That footer already holds:
- `VersionTextBlock` (line ~315) — `AppVersionInfo.DisplayText` = `"iMirror <version>"`.
- a "Check" button + a **"Releases" `Hyperlink`** (lines ~331–338) whose
  `RequestNavigate` is wired to `UpdatesHyperlink_RequestNavigate`.

Add the notice **in this same footer**, below the existing version/update grid
(after the `SettingsInstallUpdateButton`, or as a sibling row under the version
line). A short muted `TextBlock` with a `Hyperlink` is enough; or wrap it in an
"About" `Expander` matching the existing "Diagnostics" expander style if you
prefer it collapsed.

### Reuse, don't re-invent
- **Styling:** `Foreground="{StaticResource MutedTextBrush}"`, `FontSize="12"`,
  `TextWrapping="Wrap"` — identical to every other secondary line in this file.
- **Navigation handler:** mirror `UpdatesHyperlink_RequestNavigate`
  (`SettingsWindow.xaml.cs` line ~313): set `e.Handled = true`, open the URL via
  `Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })`,
  catch + `AppLog.Write` on failure. Cleanest: add
  `AppVersionInfo.OpenRepositoryPage()` (and/or `OpenLicensePage()`) next to the
  existing `OpenReleasesPage()` and call it from a new handler.

### ⚠️ Constants — one gap to fix
`AppUpdateConstants` (`MacMirrorReceiver/AppUpdateConstants.cs`) has
`GitHubReleasesUrl` but **no repo-root or LICENSE URL**. The §5(d) notice needs
the *source/repo* link, so:
- Add `public const string GitHubRepoUrl = "https://github.com/Balragon/iMirror";`
  (and optionally a `LICENSE` blob URL) to `AppUpdateConstants`.
- Bonus cleanup: the "Releases" `Hyperlink` in the XAML **hard-codes**
  `https://github.com/Balragon/iMirror/releases` (line ~334) instead of binding
  the existing constant — fine to leave, but if you touch it, point it at
  `GitHubReleasesUrl` to kill the duplication.

### Suggested copy
> iMirror © 2024–present Balragon Contributors. Licensed under GPLv3 — provided
> with **no warranty**. Source and license: github.com/Balragon/iMirror

### Acceptance
- [ ] Settings footer shows: copyright line + explicit **no-warranty** +
      "GPLv3" + a working link to the **repository (source)** and the license.
- [ ] Link opens via the same `Process.Start`/`UseShellExecute` path as the
      Releases link (no crash if no browser; logged on failure).
- [ ] No hard-coded URL that duplicates an `AppUpdateConstants` value
      (add/reuse a constant instead).
- [ ] Wording matches `LICENSE` / `THIRD_PARTY_NOTICES.txt` — **the string
      "MIT" appears nowhere** in the notice.
- [ ] `dotnet build iMirror.sln -c Release` clean; existing tests still pass.

---

## 2. Validation (must pass on the commit you tag)

### 2a. Real-hardware soak
- Run `scripts/soak-gate.ps1` on the build to be released. Procedure and pass
  criteria are in `docs/specs/v05-plus-roadmap.md` — the gate has tooling but
  **must actually run on real hardware** (real Apple sender + real GPU; latency
  ≤150 ms, no decode stalls over the soak window).
- This is the gate that cannot be faked from CI. `windows-latest` runners have
  no GPU decode path and no AirPlay sender.

### 2b. Installer + updater end-to-end (Windows)
Walk the full distributed-product loop:
1. Install `iMirror-0.4.0-setup.exe` → per-user `%LOCALAPPDATA%\Programs\iMirror`,
   no UAC prompt.
2. Run; confirm Settings footer shows the new §5(d) notice and correct version.
3. In-app **"Check"** resolves against `releases/latest`, finds a newer release
   when one exists, and reports cleanly when up to date (never throws).
4. Download path verifies **size + SHA-256** against `SHA256SUMS`
   (classic `<hash>  <filename>` format — see `UpdateService.cs`).
5. **Install update** → restart manager (shared mutex `Local\iMirror.App`)
   closes and relaunches the new build; settings/logs preserved.

---

## 3. Cut the release

### Pre-cut gates (all must be true)
- [ ] §5(d) notice merged to `main`.
- [ ] §2a real-hardware soak passed on the exact commit.
- [ ] §2b installer/updater E2E passed.

### Tag & artifacts
- Tag **`v0.4.0`** on `main`. `.github/workflows/release.yml` builds and uploads:
  - `iMirror-0.4.0-setup.exe` (installer — name must match
    `AppUpdateConstants.SetupAssetNameForVersion`),
  - `iMirror-0.4.0-win-x64.zip` (portable),
  - `SHA256SUMS`,
  - the CycloneDX SBOM (the `SBOM` workflow fires on `v*.*.*` tags).
- After publish, confirm `releases/latest` is the `v0.4.0` release and that the
  in-app updater resolves against it (stable channel; prereleases excluded).

### Release notes (GPLv3 compliance — required)
- State iMirror is **GPLv3** and link the **source** (this repo) and
  `THIRD_PARTY_NOTICES.txt` (the §6 corresponding-source offer for the bundled
  GPL parts travels with the binary).
- Note the build is **unsigned** — SmartScreen warning is expected; acceptable
  for the developer/QA audience (signing is deferred, see Phase 5).
- Summarize v0.4 from `CHANGELOG.md` (installer + in-app auto-update) and bump
  the `CHANGELOG.md` v0.4.0 entry from roadmap to released.

---

## Reference map

| Thing | Location |
|---|---|
| Roadmap / phase context | `docs/specs/open-source-strategy.md` (Phase 4) |
| §5(d) target UI | `MacMirrorReceiver/SettingsWindow.xaml` footer (`Grid.Row="2"`) |
| Nav handler to mirror | `SettingsWindow.xaml.cs` `UpdatesHyperlink_RequestNavigate` (~L313) |
| URL open helper | `AppVersionInfo.OpenReleasesPage()` |
| Constants (add repo URL here) | `AppUpdateConstants.cs` |
| Updater behavior (size/SHA/semver) | `UpdateService.cs`, `SemanticVersion.cs`, `v04-updater-design.md` |
| Installer | `installer/iMirror.iss`, `scripts/build-installer.ps1` |
| Release pipeline | `.github/workflows/release.yml` |
| Soak gate + net10 deadline | `scripts/soak-gate.ps1`, `docs/specs/v05-plus-roadmap.md` |
| CI required check (Phase 3) | workflow `CI`, job name **`Build and Test`** |

**After Phase 4 ships:** Phase 5 standing items (net10 migration before the
**.NET 8 EOL 2026-11-10** deadline; signing/SECURITY.md deferred) — see
`docs/specs/v05-plus-roadmap.md`. Don't let release polish eat net10's
real-hardware re-validation window.
