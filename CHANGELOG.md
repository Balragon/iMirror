# Changelog

All notable changes to iMirror are documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/).

For detailed release notes, see [GitHub Releases](https://github.com/Balragon/iMirror/releases).

---

## [Unreleased] (main branch)

Features planned for v0.6 and beyond. See [`docs/specs/v05-plus-roadmap.md`](docs/specs/v05-plus-roadmap.md).

---

## [0.5.0] - 2026-06-27

### Changed

- Migrated the Windows app, tests, diagnostic probes, CI, release, and SBOM workflows to .NET 10 (`net10.0-windows` / `net10.0`).
- Kept SharpDX as the GPU binding for the validated .NET 10 baseline; the Vortice.Windows swap remains deferred to v0.7.

### Fixed

- Preserved queued Media Foundation/D3D11 GOP data during iPhone startup bursts instead of flushing the first keyframe sequence and waiting indefinitely for a later keyframe.
- Updated the high-resolution D3D replay probe so decoded frames are presented on the STA/WPF thread under .NET 10.

### Validation

- GitHub CI restore/build/test and self-contained publish checks passed for the .NET 10 migration.
- Gate B real-device validation confirmed live iPhone video/audio on the Media Foundation/D3D11 path with stable decoder/render stats over a 10+ minute hardware session.

---

## [0.4.0] - 2026-06-26

### Added

- **Inno Setup installer** - per-user install under `%LOCALAPPDATA%\Programs\iMirror`, Start Menu shortcut, optional desktop shortcut, and Add/Remove Programs uninstaller.
- **Lightweight in-app updater** - stable-channel GitHub Releases check, non-blocking update notice, one-click installer download, size/SHA-256 verification, and Inno restart-manager relaunch.
- **GPLv3 in-app legal notice** - Settings footer now shows copyright, GPLv3/no-warranty notice, and source/license links.

### Changed

- Release pipeline now publishes both `iMirror-<version>-setup.exe` and the portable `iMirror-<version>-win-x64.zip`, plus `SHA256SUMS`.
- The portable zip remains available for soak testing, CI, and power users; the installer is the primary distribution artifact.

### Fixed

- Reduced live GPU backlog during Media Foundation/D3D11 stalls by bounding decoder input queue growth and dropping stale GPU frames instead of presenting seconds-late video.

### Notes

- Builds remain unsigned. SmartScreen warnings are expected for this developer/QA release; code signing remains deferred.
- iMirror is GPLv3. Source, license, and third-party notices are included in the repository and release artifacts.

---

## [0.3.0] — 2024-06-26

### Added

- **Writable-paths consolidation** — all user data (logs, settings, diagnostics) now written to `%LOCALAPPDATA%\iMirror`, enabling clean per-user installer support in v0.4.
- **Third-party compliance** — SBOM generation (CycloneDX), third-party notices, and bundled FFmpeg license documentation.
- **Soak-gate automation** — `scripts/soak-gate.ps1` and `tools/LatencyAcceptanceReport` for 1-hour stability validation with latency gating (≤150ms) and crash detection.
- **Shared SBOM workflow** — `.github/workflows/sbom.yml` generates and publishes CycloneDX JSON for supply-chain transparency.

### Changed

- App logging, settings, and temp files migrated from `./` (loose exe directory) to `%LOCALAPPDATA%\iMirror` (writable, non-admin).
- Legacy settings (`appdata.xml` in old location) auto-migrate on first run.
- FFmpeg and diagnostic dumps moved to writable paths; no longer require write access to install directory.

### Fixed

- File-not-found errors on clean run (settings now created in writable location).
- Firewall rule registration now works with per-user paths.

### Notes

- v0.3 is the last version targeting `net8.0-windows`. v0.5.0 migrated iMirror to `net10.0-windows` before .NET 8 EOL (2026-11-10). See [Issue #17](https://github.com/Balragon/iMirror/issues/17).

---

## [0.2.1] — 2024-03-15

### Fixed

- Latency regression under sustained high-frame-rate mirroring.
- Audio drop-outs on reconnect (RTP timestamp drift).

### Added

- Detailed latency instrumentation (frame timings logged per frame).

---

## [0.2.0] — 2024-02-01

### Added

- **Public preview release** on GitHub Releases.
- WPF GUI with real-time mirroring display.
- Hardware video decode (Media Foundation / D3D11) with software fallback (FFmpeg).
- AirPlay control, pair-verify, FairPlay, mirror setup.
- Diagnostic logging and dump tools (`LatencyAcceptanceReport`, H.264/audio dump).
- Bundled Gyan FFmpeg essentials (GPLv3).

### Notes

- Loose .exe zip package (no installer).
- Unsigned build (SmartScreen warning on first run).

---

## [0.1.0] — 2023-12-10

### Added

- Initial implementation: AirPlay receiver protocol, H.264 decoding, WPF host, WASAPI audio output.
- Internal testing and validation.

---

## Roadmap

### v0.5 — .NET 10 Migration (completed in 0.5.0)

See [`docs/specs/v05-plus-roadmap.md`](docs/specs/v05-plus-roadmap.md):
- **Scope:** `net10.0-windows` TFM bump; kept SharpDX (GPU binding swap deferred to v0.7).
- **Validation:** CI restore/build/test + real-hardware GPU Gate B.
- **Deadline:** completed before 2026-11-10 (net8.0 EOL).

### v0.7 — SharpDX → Vortice.Windows (GPU Modernization)

- Swap deprecated SharpDX for actively-maintained Vortice.Windows (1:1 API for D3D11/DXGI/D3D9).
- Separate from v0.5 to isolate GPU-regression risk from runtime upgrade.
- Requires v0.5 soak-validated baseline.

### Later (Deferred)

- **Code signing** — un-defer only if product decision to target general-audience expansion is made.
- **Additional platforms** — Windows-only by design; not planned.

---

## How to Report Issues

- **Bug reports:** use [GitHub Issues](https://github.com/Balragon/iMirror/issues). Include Windows version, GPU, device info, and reproduction steps.
- **Security:** see [SECURITY.md](SECURITY.md) (when available) or contact maintainers privately.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for contribution guidelines, testing requirements, and real-device validation procedures.

---

## License

iMirror is licensed under the **GNU General Public License v3** (see [LICENSE](LICENSE)). The license is GPLv3 because iMirror combines required GPLv3 components — the playfair FairPlay sources (read at runtime; mandatory for real-device mirroring) and a bundled GPLv3 FFmpeg build. See [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) for per-component provenance and [docs/specs/open-source-strategy.md](docs/specs/open-source-strategy.md) for the licensing rationale.
