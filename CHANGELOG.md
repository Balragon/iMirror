# Changelog

All notable changes to iMirror are documented in this file. The format follows [Keep a Changelog](https://keepachangelog.com/).

For detailed release notes, see [GitHub Releases](https://github.com/Balragon/iMirror/releases).

---

## [Unreleased] (main branch)

---

## [0.8.0] - 2026-10-27

Hardening release from the 2026-10-26 full code review: network-input limits, sender checks, lifecycle and deadlock fixes, firewall detection, and a large internal restructuring of the receiver window with new tests. Updating from 0.7.5: if the in-app update does not finish (a 0.7.5 handoff issue fixed in this release), download `iMirror-0.8.0-setup.exe` from this release and run it.

### Security

- Harden binary plist parsing against malformed SETUP bodies: reference cycles, excessive nesting and truncated or overflowing integers are rejected instead of hanging or crashing the receiver (#52).
- Bound RTSP control requests: header and body size caps (oversized `Content-Length` is refused and the connection closed), a per-connection read timeout and a cap on concurrent control connections (#53).
- Bound the mirror decryptor candidate sweep to once per session and fix the data-stream buffer cap (#54).
- Accept the mirror data connection and audio RTP only from the host that performed SETUP; packets and connections from any other LAN host are rejected (#55).
- Run the Windows Firewall rule script inline (`-EncodedCommand`) with PowerShell resolved by absolute path, instead of an elevated script file in `%TEMP%` that another process could swap before the UAC prompt was accepted (#56).
- Release workflows pass the tag through `env:`, pin every action to a commit SHA and use least-privilege tokens (#58).
- Git-ignore audio/H.264 dump files, which can carry session key material (#59).

### Fixed

- End the mirror session only from the control connection that set it up. Mac senders run the whole session on the RAOP port, so stopping mirroring on the Mac now really ends the session (ffmpeg stops, the window returns to idle), and another host opening and dropping a connection on port 7000 can no longer reset an active session. An audio-only TEARDOWN keeps the session (#79).
- When another device starts mirroring, the previous sender is disconnected and shows mirroring as ended instead of staying "connected" with no picture (#86).
- The readiness check detects a blocking Windows Firewall (no inbound allow rule for this iMirror on the current network, or a block rule) instead of reporting "Ready" because the ports could bind; the Allow in Firewall button now appears in that case (#80).
- Allow in Firewall also disables the inbound block rules Windows creates when its own "allow access" prompt is cancelled, which previously overrode the new allow rule (#81).
- Fix a WASAPI output deadlock between `Stop()` and `PlaybackStopped` that could freeze audio and the window when the output device changed or disappeared; repeated default-device notifications no longer restart the output twice (#63).
- Back off the audio pipeline start after a failure (5 s → 60 s cap) instead of retrying on every packet, and keep a timing-port bind failure out of SETUP (#61).
- Guard every decoder callback by decoder identity and stop `FfmpegDecoder` from respawning during `Dispose`, so a late event from a replaced decoder can no longer touch the new one or leave a zombie ffmpeg (#60).
- Shutdown-safe session end, a guarded `Window_Loaded`, and download stall and size guards for the updater (#62).
- Release the single-instance mutex before launching the update installer, so a silent update no longer aborts with "application is running" and leaves neither the old nor the new version (#57).
- Keep the Media Foundation output sample alive until its frame is disposed (#78); keep the MF overflow policy but stop the per-packet "input unavailable" log spam and size the queue for the session-start burst (#66).
- Keep the queued GOP on FFmpeg decoder input overflow instead of flushing to wait for a keyframe Mac senders never resend (#67).
- Cut the mirror decryptor warm-up from seconds to tens of milliseconds (#74), and cut software-path latency and stale-frame freezes (#76).
- Read audio diagnostics without the audio start lock (#77).
- Decrypt audio with one-shot AES-CBC; keep and, after an idle gap, realign the written-frame timing so the audio latency estimate no longer jumps to several seconds when sound resumes (#83, #87).
- Answer mDNS queries only for iMirror's own names, at most once per second, with cached addresses, instead of re-announcing for every `.local` query on the LAN (#82).
- Rotate the app log (5 MB × 3) and write it from a background thread; crash handlers flush it (#72).
- Small fixes: UTF-16 plist strings, async DNS, atomic settings write, HTTPS checksums, no working-directory ffmpeg probe, a single in-flight install (#71).
- Close iMirror automatically after launching the silent update installer (#48); clean up leftover update downloads and cancel in-flight update work on shutdown (#51); make the settings "Save & Restart" handoff wait for the exiting instance (#50).

### Changed

- New app icon; window and tray icons follow the Windows light/dark theme live (#64, #65).
- The FairPlay tables are embedded into the executable at build time instead of being parsed from the `ThirdParty\playfair` files next to it; the source files are still shipped for GPLv3 compliance (#85).
- Removed the NTP timing client, whose replies were never read; UDP 7102 is no longer opened (#88).
- New `IMIRROR_DATA_DIR` environment variable: an absolute path that replaces `%LOCALAPPDATA%\iMirror` for logs, settings, diagnostics, dumps and update downloads (#94).
- Local/dev builds report the latest release version, so an untagged build no longer always sees an update as available (#84).
- The FFmpeg decoder reports a stopped stderr reader, a failing H.264 dump and a failing WinGet ffmpeg probe instead of swallowing them (#84, #87).
- Docs and packaging: stale docs synced, playfair provenance recorded, `LICENSE` and `NOTICE` shipped with the app (#73).

### Internal

- `MainWindow` split into focused pieces with no behaviour change: `UpdateController`, `PendingVideoBuffer`, `ReceiverChromeText`, `NativeWindowFrame`, `TrayIconHost`, `AudioSessionHost`, `LatestFrameMailbox`, `ThrottledLogGate` and `VideoEngineHost` (3,786 → 2,500 lines) (#89, #90, #91, #92).
- Dead code removed (#70, #84).
- New fixture tests for protocol parsing, both decryptors, the latency window, fMP4, RTP and the audio receiver's data path (#69, #93), the session-ownership rules (#79, #86), the firewall readiness check (#80, #81) and the GPU-fault fallback (#92); the tests no longer touch fixed ports, the real settings file or the real log (#68, #94).

### Validation

- `dotnet test .\iMirror.sln -c Release`: 280 tests passed.
- Windows 11 device checks with a Mac and an iPhone on the GPU and software engines: discovery, mirroring with audio, Mac stop → idle, reconnect, Mac → iPhone takeover, audio-only TEARDOWN cycles, output-device removal, firewall block/allow, tray exit and Save & Restart.
- Release gate on the packaged `iMirror-0.8.0-win-x64.zip` extracted outside the repository (local package with FFmpeg full_build; the published package is rebuilt by CI with the pinned Essentials build): smoke test with Mac and iPhone; reconnect gate with three disconnect/reconnect cycles (GPU path and audio restarted cleanly each time); latency gate on one continuous 20 min 21 s active-motion Mac session: `PASS`, 122 windows, worst p95 58 ms, worst max 78 ms, 0 windows ≥ 150 ms, `contiguousEvidence=True`, `corruption=pass`, `highResolutionD3D=pass`.

---

## [0.7.5] - 2026-07-01

### Added

- Add iMirror app icon assets and wire them into the Windows executable, WPF window, and installer.

### Validation

- Local `dotnet build .\iMirror.sln -c Release` passed with zero warnings and zero errors.
- Local `dotnet test .\MacMirrorReceiver.Tests\MacMirrorReceiver.Tests.csproj -c Release` passed: 65 tests.

---

## [0.7.4] - 2026-06-29

### Fixed

- Suppress the delayed audio Windows Firewall warning when iMirror already has an enabled inbound allow rule for the current executable, so the app does not keep prompting after the user has allowed audio.

### Validation

- Local `dotnet build .\iMirror.sln -c Release --no-restore -p:OutDir=artifacts\verify\out\` passed with zero warnings and zero errors.
- Local `dotnet test .\MacMirrorReceiver.Tests\MacMirrorReceiver.Tests.csproj -c Release --no-restore -p:OutDir=artifacts\verify\testout\` passed: 65 tests.
- GitHub CI passed on PR #45.

---

## [0.7.3] - 2026-06-29

### Fixed

- Correct the Vortice D3D11 video processor color range by explicitly treating NV12 input as BT.709 full range and RGB output as full range, reducing the over-bright whites/highlights seen on the GPU path.

### Changed

- Add `IMIRROR_D3D_COLOR_SPACE` as a runtime diagnostic override for the Vortice GPU color conversion path (`default`, `bt709-full`, or `bt709-studio`).

### Validation

- Local `dotnet build .\iMirror.sln -c Release --no-restore` passed with zero warnings and zero errors.
- Local `dotnet test .\MacMirrorReceiver.Tests\MacMirrorReceiver.Tests.csproj -c Release --no-restore` passed: 63 tests.
- Real-device Mac sender visual validation passed on the Vortice GPU path; runtime logs confirmed `gpuBinding=Vortice`, `ffmpegPipeline=0`, and `D3D11 video processor color space: mode=bt709-full`.

---

## [0.7.2] - 2026-06-29

### Added

- Added `SECURITY.md` with supported-version and vulnerability-reporting guidance.

### Changed

- Document the release latency posture as a two-tier gate: GPU/high-resolution D3D remains `p95 < 150ms`; intentional FFmpeg software fallback uses a compatibility tier of `p95 < 250ms`.
- Reduce FFmpeg software-decoder frame-thread depth for small phone-sized fallback streams to lower steady-state presentation latency while keeping 1080p fallback parallelism.

### Fixed

- Drop stale FFmpeg software-decoded frames older than 240ms instead of presenting them and contaminating latency windows after startup/reconnect or transient stalls.
- Make F11/double-click fullscreen use true borderless monitor fullscreen, hide app/window chrome, and fill the display without side bars while keeping the normal maximize button behavior unchanged.
- Clear the audio firewall warning immediately after the Windows Firewall allow rule succeeds, so the app does not keep prompting after the user has allowed iMirror.
- Find bundled `ffmpeg.exe` from local build output/repository-relative `tools\ffmpeg\bin` paths, reducing false "FFmpeg not found" diagnostics in development builds.
- Reconcile historical changelog dates and release history for the public release line.

### Validation

- iPhone FFmpeg fallback fresh validation passed on PR #39 with the 240ms stale-frame cutoff: 10m `worstP95=108ms`/`worstMax=209ms`; 30m `worstP95=157ms`/`worstMax=224ms`; both with zero breach windows and `severeMax=pass`.
- Mac FFmpeg fallback fresh 30m validation passed on PR #39: `worstP95=229ms`, `worstMax=241ms`, zero breach windows, `severeMax=pass`, and no recurrence of the earlier 10.2s startup max spike.
- Mac FFmpeg fallback same-process reconnect validation passed on PR #39: five fresh stream sessions were observed in one iMirror process, each starting a new FFmpeg video decoder with `-threads 4 -thread_type frame`, with no exceptions or multi-second latency spikes.
- Local `dotnet build -c Release --no-restore` and `dotnet test -c Release --no-restore` passed after the v0.7.2 hotfixes.
- Fullscreen smoke validation confirmed the iMirror window enters monitor-covering borderless fullscreen with native caption/frame bits removed.

---

## [0.7.1] - 2026-06-28

### Fixed

- Reinitialize the WASAPI output after render endpoint changes or playback invalidation, so display-audio endpoint power-cycles can recover without an AirPlay reconnect.

### Validation

- GitHub CI passed on PR #38, and local `dotnet build -c Release` plus `dotnet test` passed after merge.
- Real-device display-audio monitor power-cycle validation passed: WASAPI output restarted after endpoint invalidation/default endpoint changes, and audio recovered without an AirPlay reconnect. See #35.

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

## [0.3.0] - 2026-06-26

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
- 0.3.0 was not published as a standalone GitHub release; these changes shipped as part of the [v0.4.0](https://github.com/Balragon/iMirror/releases/tag/v0.4.0) installer release.

---

## [0.2.2] – [0.2.6] - 2026-06-24 to 2026-06-25

### Changed

- Incremental packaging and fix iterations on the 0.2.x preview line, tagged for distribution but not individually documented here. See the per-tag [GitHub Releases](https://github.com/Balragon/iMirror/releases) for details.

---

## [0.2.1] - 2026-06-24

### Fixed

- Latency regression under sustained high-frame-rate mirroring.
- Audio drop-outs on reconnect (RTP timestamp drift).

### Added

- Detailed latency instrumentation (frame timings logged per frame).

---

## [0.2.0] - 2026-06-24

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

## [0.1.0] - 2026-06-16

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
- **Security:** see [SECURITY.md](SECURITY.md) for the private vulnerability reporting process.

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for contribution guidelines, testing requirements, and real-device validation procedures.

---

## License

iMirror is licensed under the **GNU General Public License v3** (see [LICENSE](LICENSE)). The license is GPLv3 because iMirror combines required GPLv3 components — the playfair FairPlay sources (read at runtime; mandatory for real-device mirroring) and a bundled GPLv3 FFmpeg build. See [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) for per-component provenance and [docs/specs/open-source-strategy.md](docs/specs/open-source-strategy.md) for the licensing rationale.
