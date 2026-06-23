# Real-Device Validation Results

Recorded results from actual real-device mirroring runs. This is the evidence log
(what was observed), distinct from `windows-e2e-validation.md` (the checklist template)
and `validation.md` (the procedure).

Source of truth for each run is the local `iMirror.log` produced during the session.
Logs are gitignored (they can contain private screen content), so only the distilled,
non-sensitive results are recorded here.

---

## Run: 2026-06-23

**Setup**

- Receiver: iMirror Release build, WPF render tier 2 (full hardware), on `192.168.0.13`.
- Sender: real device at `192.168.0.10` (1440p source; H.264 / AAC AirPlay).
- Duration: 6 app launches between 16:58 and 22:10 (~5 hours of sessions).
- FFmpeg: bundled `tools\ffmpeg\bin\ffmpeg.exe`.

**Results**

| Area | Result | Evidence (from iMirror.log) |
|---|---|---|
| AirPlay discovery / advertise | PASS | Listeners up on :7000 (AirPlay) and :5000 (RAOP); advertised as "iMirror". |
| Pairing / mirror setup | PASS | Mirror SPS/PPS received, stream config `2560x1440@30 h264-annexb` accepted. |
| **GPU video path (Media Foundation / D3D11)** | **PASS** | MF D3D11 decoder started at `2560x1440@30` (NV12 texture); **decoded 617 / rendered 506 frames** in one session. |
| Session lifecycle (connect → stream → teardown) | PASS | Clean 126s data stream, normal TEARDOWN, RAOP control closed by peer, no hang. |
| **Software video fallback (FFmpeg)** | **FAIL** | At 19:52 and 21:57, FFmpeg software path produced no decoded frame within 12s and fell through software → d3d11va → dxva2. See issue below. |
| Audio playback (WASAPI / AAC-ELD) | NOT EVIDENCED | RAOP audio traffic present, but no audio-output confirmation captured in this log. Needs explicit check. |

**Open issue found in this run**

- **FFmpeg software fallback cannot open its output pipe.** FFmpeg logs
  `[out#0/rawvideo] Output file does not contain any stream` →
  `Error opening output file pipe:1` → `Error opening output files: Invalid argument`,
  before any frame is decoded, while the gate reports
  `waiting for SPS/PPS keyframe; saw NAL 1`. The GPU path succeeds because it logs
  `prepended buffered SPS/PPS to keyframe`; the software path appears not to get a
  usable SPS/PPS-bearing keyframe when entered mid-stream. Tracked separately as the
  software-fallback investigation.

**Not covered by this run (still require manual confirmation)**

- First-run diagnostics strip (missing FFmpeg / firewall blocked / mDNS UDP 5353 occupied / VPN-only / Re-check).
- Settings UI: receiver-name change + restart, render-mode persistence, audio-sync slider live apply.
- Audio output actually heard, and audio-sync offset behavior.
- Tray lifecycle: close-to-tray, restore, explicit Exit, incoming session while hidden.
- Accessibility: keyboard focus visuals, high-contrast legibility, screen-reader names.

**Summary:** The primary product path — GPU (MF/D3D11) mirroring of a real 1440p
sender with clean session teardown — is validated on real hardware. The software
FFmpeg fallback is currently broken and is the main release-blocking finding from
this run. Remaining checklist items above were not exercised in a way this log can prove.

---

## Run: 2026-06-24 (forced-software, instrumented)

**Setup**

- Receiver: instrumented Release build (`IMIRROR_FORCE_SOFTWARE_VIDEO=1`,
  `IMIRROR_WRITE_DIAGNOSTICS=1`), WPF render tier 2.
- Senders: an iPhone (`192.168.0.22`) and a Mac (`192.168.0.10`), connected and
  switched repeatedly, including overlapping (both connected at once).

**Results**

| Area | Result | Evidence |
|---|---|---|
| Software (FFmpeg) path, single sender | PASS | Four single-sender connections (1920x1080 and 500x1080, 60fps) all decoded and rendered cleanly; `first decodable video frame: keyframe(IDR)=True` each time. |
| Decryptor warm-up keyframe survival | PASS (self-healing) | One connection logged `droppedBeforeFirst=7, decryptorWarmup=539ms` yet still recovered to a keyframe — packets dropped during warm-up self-heal when a keyframe follows. |
| First-frame-timeout guard (decoder fallback) | PASS (verified) | When a session was fed no keyframe for 12s, the build logged a single `no input written ... awaiting keyframe (no decoder fallback)` line instead of cascading through d3d11va/dxva2 with misleading `Output file does not contain any stream` errors. |
| **Two senders connected at once** | **FAIL** | iPhone session `3049745444710008145` was still active when the Mac SETUP `15475132918413936160` arrived (~12s overlap before the iPhone teardown). The single shared mirror pipeline (one decryptor keyed to one streamConnectionID, one gate, one decoder) is overwritten by the second SETUP, so packets decrypt against the wrong key, the gate never gets a clean keyframe, and the decoder starves. |

**Root cause (revised)**

The earlier "decryptor warm-up keyframe loss" framing was a symptom. The real
trigger is **concurrent senders**: iMirror runs a single mirror pipeline, so a
second SETUP arriving while another session is live tangles decryption/gate state
and starves the decoder. Single-sender mirroring (GPU or software, iPhone or Mac)
is sound.

**Follow-up**

- Implement last-connected-sender arbitration: on a new mirror SETUP, tear down
  the existing session and adopt the new sender (reset the gate to await its
  keyframe). Tracked as a separate change; not yet implemented.
- The first-frame-timeout guard and warm-up instrumentation landed in
  `fix/software-fallback-keyframe` and are verified above; they remain useful as a
  safety net and will capture any future keyframe-starvation automatically.
