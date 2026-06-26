# iMirror Public Distribution Strategy

This document records the public-distribution decisions for iMirror: license,
redistribution obligations, repository readiness, and the first stable release
gate.

## Current Status

| Area | Status |
|---|---|
| License baseline | Done |
| Third-party notices | Done |
| Community files | Done |
| Installer and updater | Done |
| In-app GPLv3 notice | Done |
| Real-hardware 60-minute soak | Done on `0.4.0+261e5f2` |
| Repository visibility | Pending maintainer action |
| Updater public E2E | Blocked while the repository is private |
| `v0.4.0` tag | Pending public updater access |

## License Decision

iMirror is distributed under GPLv3. This is required by the distribution shape:

- `ThirdParty/playfair` is GPLv3 and is required for real-device AirPlay
  mirroring.
- Release packages bundle a GPLv3 FFmpeg build from Gyan FFmpeg Essentials.
- MIT-compatible dependencies remain listed in `THIRD_PARTY_NOTICES.txt`.

The root `LICENSE`, `ThirdParty/playfair/LICENSE.md`, README license section,
and `THIRD_PARTY_NOTICES.txt` are the source of truth for public licensing.

## Public Repository Readiness

Before switching repository visibility to public:

- Keep generated binaries, logs, dumps, FFmpeg binaries, and local build output
  out of git.
- Keep local automation/workflow scratch files out of git.
- Confirm the GitHub license detector sees GPLv3.
- Set repository description and topics.
- Keep branch protection enabled for `main`.

The repository should be public before the first stable release because the
in-app updater reads unauthenticated GitHub Releases API endpoints.

## First Stable Release Gate

The `v0.4.0` tag should be created only after all of these are true:

- `main` is at the intended release commit.
- CI is green.
- The installer and portable zip are produced with `SHA256SUMS`.
- Real hardware soak passes with the high-resolution D3D path required.
- The repository or update feed is publicly readable.
- The in-app updater can resolve `releases/latest` without authentication.

For the current release candidate:

- Candidate commit: `261e5f2`
- Installed version: `0.4.0+261e5f2`
- 60-minute soak: PASS
- Remaining blocker: unauthenticated GitHub API returns 404 while the repository
  is private.

## Post-Release Work

After `v0.4.0`, the important follow-ups are:

- .NET 10 migration before .NET 8 EOL on 2026-11-10.
- Long-run memory/audio soak expansion across more devices.
- Security policy and private disclosure channel when the audience expands.
- Code signing if iMirror moves beyond a developer-focused audience.
