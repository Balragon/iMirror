# B3 Hardening Evidence - 2026-06-27

Branch: `claude/imirror-code-review-ibnu5q`

Code under test: `9374ab8` (`fix H264 SPS/PPS separate-packet retention; make mDNS bind port test-injectable`)

Local evidence root: `C:\Users\User\Documents\Codex\2026-06-27\ha\outputs\b3-hardening-9374ab8\logs`

## Verdict

B3 is not a clean release PASS.

The GPU path passed the measured gates, and auto-update fail-closed behavior passed. FFmpeg fallback remained functional for the tested runs, but it exceeded the B3 latency target (`p95 <= 150ms`) on both iPhone and Mac. Treat this branch as merge-ready only if FFmpeg fallback latency is accepted as a known release limitation; otherwise hold release for fallback latency work.

## Build and Unit Evidence

- `dotnet build -c Release`: previously verified on this branch after `9374ab8` with 0 errors / 0 warnings.
- `dotnet test MacMirrorReceiver.Tests/MacMirrorReceiver.Tests.csproj -c Release`: previously verified with 58 passed.
- UpdateService focused VSTest evidence: `auto-update-fail-closed-vstest-20260627.log`
  - 13 `UpdateServiceTests` passed.
  - Includes the four fail-closed tests:
    - `DownloadSetupAsync_FailsClosed_WhenNoChecksumAssetAvailable`
    - `DownloadSetupAsync_FailsClosed_WhenChecksumDownloadFails`
    - `DownloadSetupAsync_FailsClosed_WhenChecksumOmitsTheSetupAsset`
    - `DownloadSetupAsync_FailsClosed_WhenHashDoesNotMatch`

## Mac Sender, GPU Path

Evidence:

- `mac-gpu-reconnect-10m-20260627-175154.log`
- `mac-gpu-reconnect-20m-20260627-175154.log`
- `mac-gpu-reconnect-30m-20260627-175154.log`
- `mac-gpu-reconnect-1h-20260627-175154.log`
- `mac-gpu-reconnect-2h-20260627-175154.log`

Result: PASS.

2h report summary:

- `evidenceDuration=02:01:50`
- `worstP95=91ms`
- `worstMax=126ms`
- `contiguousEvidence=True`
- `duration=pass`
- `p95=pass`
- `severeMax=pass`
- `corruption=pass`
- `crash=pass`
- `videoAudioLiveness=pass`
- `keyframeStarvation=pass`
- `clears=0`

## iPhone Sender, GPU Path

Evidence:

- `iphone-gpu-10m-20260627-200509.log`
- `iphone-gpu-reconnect-20260627-201631.log`

Result: PASS for smoke and reconnect.

10m report summary:

- `evidenceDuration=00:10:20`
- `worstP95=70ms`
- `worstMax=96ms`
- `contiguousEvidence=True`
- `duration=pass`
- `p95=pass`
- `severeMax=pass`
- `corruption=pass`
- `crash=pass`
- `videoAudioLiveness=pass`
- `keyframeStarvation=pass`
- `clears=0`

Reconnect summary:

- Disconnect at `2026-06-27T20:16:31+09:00`.
- New session at `2026-06-27T20:16:47+09:00`.
- Fresh stream config: `666x1440 @ 60 codec=h264-annexb`.
- D3D11 decoder restarted and produced the first NV12 texture.
- Audio pipeline, FFmpeg audio decoder, first AAC, first PCM, and WASAPI restarted.

## iPhone Sender, FFmpeg Fallback

Evidence:

- `iphone-ffmpeg-10m-20260627-201830.log`

Result: functional PASS, latency FAIL.

10m report summary:

- `evidenceDuration=00:10:30`
- `worstP95=174ms`
- `worstMax=327ms`
- `p95BreachWindows=59 of 63`
- `severeMaxWindows=1`
- `contiguousEvidence=True`
- `duration=pass`
- `p95=fail`
- `severeMax=fail`
- `corruption=pass`
- `crash=pass`
- `videoAudioLiveness=pass`
- `keyframeStarvation=pass`
- `clears=0`
- FFmpeg software decoder path was active.

## Mac Sender, FFmpeg Fallback

Evidence:

- `mac-ffmpeg-10m-strict-20260627-203214.log`
- `mac-ffmpeg-10m-postwarmup-20260627-203222.log`
- `mac-ffmpeg-20m-strict-20260627-203214.log`
- `mac-ffmpeg-30m-strict-20260627-203214.log`
- `mac-ffmpeg-1h-strict-20260627-203214.log`

Result: functional 1h soak PASS, latency FAIL.

1h strict report summary:

- `evidenceDuration=01:00:40`
- `worstP95=228ms`
- `worstMax=10256ms`
- `p95BreachWindows=361 of 364`
- `severeMaxWindows=1`
- `contiguousEvidence=True`
- `duration=pass`
- `p95=fail`
- `severeMax=fail`
- `corruption=pass`
- `crash=pass`
- `videoAudioLiveness=pass`
- `keyframeStarvation=pass`
- `videoProgressLines=3,639`
- `audioActivityLines=3,522`
- Latest health near the end showed active FFmpeg pipeline, audio frames increasing, `clears=0`, `h264 dropped=0`, and no stdin stalls.

Notes:

- Video stream start: `2026-06-27T20:31:47+09:00`.
- Audio start: `2026-06-27T20:32:14+09:00`.
- Report baseline used audio start for conservative A/V coverage.
- Repeated RAOP `TEARDOWN`/`SETUP` events occurred, but audio recovered and continued without clears or freeze markers.

## Auto-Update Fail-Closed

Evidence:

- `auto-update-fail-closed-vstest-20260627.log`
- `test-results-auto-update-vstest-20260627\auto-update-fail-closed.trx`
- `auto-update-actual-setup-fail-closed-20260627-214336.json`

Result: PASS.

Actual setup evidence:

- Latest GitHub release: `v0.5.1`
- Release URL: `https://github.com/Balragon/iMirror/releases/tag/v0.5.1`
- Setup asset: `iMirror-0.5.1-setup.exe`
- Setup asset size: `84,466,915` bytes
- Setup URL used by the harness: `https://github.com/Balragon/iMirror/releases/download/v0.5.1/iMirror-0.5.1-setup.exe`

The harness invoked `DownloadSetupAsync` against the built `iMirror.dll`. The setup bytes were requested from the real GitHub release setup URL. SHA responses were overridden only for the negative cases so public GitHub releases did not need to be mutated.

Four negative cases all ended in `install-blocked`, with no final setup file and no `.download` file left behind:

- no SHA asset
- SHA fetch failure
- hash mismatch
- wrong asset name in SHA file

## Release Decision

- GPU path: acceptable based on Mac 2h and iPhone smoke/reconnect evidence.
- Auto-update fail-closed: acceptable.
- FFmpeg fallback: not latency-accepted. It is usable as a functional fallback in these logs, but it does not satisfy the current B3 `p95 <= 150ms` quantitative gate.
