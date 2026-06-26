# Changelog

All notable changes to iMirror are documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/).

For detailed release notes, see [GitHub Releases](https://github.com/Balragon/iMirror/releases).

---

## [Unreleased] (main branch)

Features planned for v0.4 and beyond. See [`docs/specs/v04-product-surface-roadmap.md`](docs/specs/v04-product-surface-roadmap.md) and [`docs/specs/v05-plus-roadmap.md`](docs/specs/v05-plus-roadmap.md).

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

- v0.3 is the last version targeting `net8.0-windows`. v0.5+ will migrate to `net10.0-windows` before .NET 8 EOL (2026-11-10). See [Issue #17](https://github.com/Balragon/iMirror/issues/17).

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

### v0.4 — Installer & Auto-Update (in progress)

See [`docs/specs/v04-product-surface-roadmap.md`](docs/specs/v04-product-surface-roadmap.md):
- **Phase 1:** Inno Setup installer (per-user, no UAC, `%LOCALAPPDATA%`).
- **Phase 2:** Lightweight in-app updater (GitHub Releases API, size/SHA verification, Inno restart manager).
- **Phase 3:** Code signing (deferred pending general-audience expansion decision).

**Timeline:** Q3 2026 (before .NET 8 EOL, 2026-11-10).

### v0.5 — .NET 10 Migration (required)

See [`docs/specs/v05-plus-roadmap.md`](docs/specs/v05-plus-roadmap.md):
- **Scope:** `net10.0-windows` TFM bump; keep SharpDX (GPU binding swap deferred to v0.7).
- **Validation:** CI restore/build/test + real-hardware soak (GPU D3D path, device-loss/restore cycle, 1h soak ≤150ms gate).
- **Deadline:** must complete before 2026-11-10 (net8.0 EOL).

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

iMirror is licensed under the MIT License (see [LICENSE](LICENSE)). Bundled components (FFmpeg, playfair) are GPLv3; see [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) for compliance details.
