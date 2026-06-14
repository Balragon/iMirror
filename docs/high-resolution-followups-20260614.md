# High-Resolution Follow-ups - 2026-06-14

These items are not blockers for the real-device acceptance bundle captured in
`real-device-acceptance-evidence-20260614.md`. They remain useful follow-up
work before promoting the experimental high-resolution paths beyond their
current gates.

## HRD3D Latency Spikes

The final-code HRD3D 2048x1152 30-minute run passed duration, corruption, and
high-resolution D3D marker gates, but strict p95/maxTrend latency gates failed
on isolated spikes.

Follow-up:

- Inspect GPU/driver scheduling stalls around the worst window.
- Keep `dropped=0`, no queue accumulation, and no fault/corruption markers as
  the primary correctness signal.
- Do not change the latest-frame-wins render policy unless user-visible stutter
  is reproduced with matching log evidence.

## Product Replay Probe

The direct `MediaFoundationH264Probe` accepted the main submitted dump and
decoded CPU/D3D11 output, but `HighResolutionD3DReplayProbe` failed with
`ProcessOutput failed: 0xC00D6D61` when fed from the same submitted dump.

Follow-up:

- Compare replay input pacing, access-unit boundaries, and decoder startup
  sequence against the live AirPlay path.
- Treat this as a probe/tooling mismatch until it reproduces in live rendering.
- Keep the submitted dump byte/path validation and direct MF/D3D11 probe as the
  current accepted evidence.

## Stable 1080 Post-Window Spikes

The bounded stable 1080 active-motion window passed 4 minutes 30 seconds with
FFmpeg software decode at 1920x1080 and no HRD3D/MF leakage. Later unbounded
logging showed intermittent latency spikes.

Follow-up:

- Reopen only if the user reports visible stable-path stutter.
- If reopened, collect a bounded log around the visible event and compare it
  against `stdinWrite`, `decoderQueue`, and payload cadence.
- Do not treat post-window spikes as an acceptance blocker without matching
  user-visible symptoms or sustained queue/drop evidence.
