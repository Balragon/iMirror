# Architecture

iMirror is organized around a small WPF host, AirPlay networking services, and separate video/audio media pipelines.

## Session Flow

1. `AirPlayProbeService` advertises the receiver over mDNS and handles AirPlay/RAOP control traffic.
2. Pairing, pair-verify, FairPlay setup, mirror `SETUP`, and `RECORD` establish the encrypted media session.
3. Mirror video payloads are decrypted, normalized to Annex B H.264, and passed through `H264AnnexBStreamGate`.
4. The preferred video path decodes H.264 with `VorticeMediaFoundationD3D11Decoder` and presents GPU frames with `VorticeD3D11SwapChainVideoPresenter`.
5. If the GPU path cannot start or faults at runtime, the app falls back to `FfmpegDecoder` and WPF bitmap presentation.
6. AirPlay audio RTP packets are received by `AirPlayAudioReceiver`, decrypted, decoded by `FfmpegAudioDecoder`, and submitted to `WasapiAudioOutput`.

## Video Policy

- Prefer GPU-native decode and presentation.
- Keep compressed H.264 input GOP-consistent; do not randomly drop P-frames.
- Drop only decoded frames at the presentation boundary when the UI cannot keep up.
- Keep FFmpeg as the compatibility fallback, capped to a manageable output size for high-resolution streams.
- Record fixed-window receive-to-present latency so backlog is visible.

## Audio Policy

- Audio is advertised by default because the receive/decode/output path exists.
- Decryption and decoding failures should degrade audio without interrupting video.
- `IMIRROR_AUDIO_SYNC_OFFSET_MS` tunes audio target latency relative to measured video latency.
- WASAPI buffering may drop late PCM frames to stay near the sync target.

## Privacy

Diagnostic captures and media dumps are local-only artifacts. They may include private screen content, audio, session keys, encrypted RTP, or decrypted elementary streams.
