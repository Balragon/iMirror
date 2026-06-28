# Changelog

All notable changes to iMirror are documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/).

For detailed release notes, see [GitHub Releases](https://github.com/Balragon/iMirror/releases).

---

## [Unreleased] (main branch)

### Fixed

- Reinitialize the WASAPI output after render endpoint changes or playback invalidation, so display-audio endpoint power-cycles can recover without an AirPlay reconnect.

---

## [0.7.0] - 2026-06-28

### Security

- Auto-update now fails closed: `DownloadSetupAsync` aborts the install when the release SHA-256 cannot be verified (missing, unreachable, or unparseable `SHA256SUMS`, or a hash mismatch) instead of running the unsigned setup unverified. Verified end-to-end against the real `v0.5.1` release plus four negative cases.

### Changed

- Made Vortice.Windows the default and only high-resolution D3D GPU binding after Gate B A/B parity; removed the SharpDX packages and `IMIRROR_GPU_BINDING` feature flag.

### Fixed

- AirPlay receiver startup no longer wedges on a partial mDNS start: the mDNS socket is published only after bind+join succeed, and partial state is cleaned up, so a transiently held UDP 5353 can be retried.
- `AirPlayAudioReceiver.Start` tears down sockets/CTS/dump on a partial-start failure so resources do not leak and `IsRunning` no longer reports a false positive.
- H.264 stream gate retains the latest SPS and PPS independently, so parameter sets that arrive in separate packets are both prepended (SPS then PPS) to the next IDR.
- Fixed mangled `??` separators in the readiness and log strings.

### Known limitations

- The FFmpeg software-decode fallback exceeds the 150ms latency gate (real-device B3: Mac p95 ~228ms, iPhone p95 ~174ms) while remaining functional; the GPU path meets the gate. See [`docs/b3-hardening-evidence-2026-06-27.md`](docs/b3-hardening-evidence-2026-06-27.md) and [#32](https://github.com/Balragon/iMirror/issues/32).

### Validation

- B3 real-device gate: GPU path PASS (Mac 2h `worstP95=91ms`; iPhone smoke/reconnect), auto-update fail-closed PASS, FFmpeg fallback functional but latency-failing (tracked in #32).
- v0.7 Vortice Gate B A/B: Mac same-session SharpDX 30m PASS (`worstP95=38ms`, `worstMax=70ms`) vs Vortice 30m PASS (`worstP95=63ms`, `worstMax=68ms`); iPhone Vortice GPU, reconnect, probes, and software fallback checks passed. Monitor power-cycle audio loss is tracked separately as binding-independent #35.

---

## [0.5.1] - 2026-06-27

### Fixed

- Serialized AirPlay stream-config reset/restart handling so SPS/PPS parameter sets seed the fresh H.264 gate before the next IDR is processed, preventing long-run Mac mirroring from freezing video while audio continues.
- Added H.264 gate tests for buffered SPS/PPS being prepended to the next keyframe.
- Updated the soak acceptance report to fail when audio continues after video decode/render progress stalls, or when the H.264 gate remains stuck waiting for SPS/PPS.

### Validation

- GitHub CI passed on PR #30 and PR #31.
- Built and installed a main-based `0.5.1-rc.2+446b1a4` candidate with bundled FFmpeg.
- Synthetic soak-gate checks now fail the v0.5.0-style video-freeze/audio-continues pattern and pass a healthy A/V-progress pattern.
- Real-device iPhone smoke passed the 30-minute soak gate (`00:31:20` evidence, no crash/corruption/video-audio-freeze/keyframe-starvation markers).
- Real-device Mac AirPlay smoke passed the 30-minute soak gate (`00:30:00` evidence, `worstP95=26ms`, `worstMax=52ms`, no crash/corruption/video-audio-freeze/keyframe-starvation markers).

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

### v0.7 - Vortice.Windows GPU Modernization (completed in 0.7.0)

- Replaced deprecated SharpDX with actively-maintained Vortice.Windows for D3D11/DXGI/D3D9.
- Vortice.Windows is now the default and only high-resolution D3D GPU binding; FFmpeg remains the software fallback path.
- Completed after Gate B A/B validation against the v0.5 soak-validated SharpDX baseline.

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
