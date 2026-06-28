# Security Policy

iMirror is an AirPlay mirroring receiver: it opens network listeners, parses
untrusted protocol input from senders on the local network, handles FairPlay
key material, and ships a self-updating installer. We take security reports
seriously and appreciate responsible, coordinated disclosure.

## Supported Versions

Security fixes are provided for the latest released version line only. Please
update to the most recent release before reporting.

| Version        | Supported |
| -------------- | --------- |
| 0.7.x (latest) | ✅        |
| < 0.7.0        | ❌        |

The current release is on the [Releases page](https://github.com/Balragon/iMirror/releases).

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues,
pull requests, or discussions.**

Instead, use GitHub's private vulnerability reporting:

1. Open the repository's **Security** tab.
2. Click **Report a vulnerability** (privately report a vulnerability).
3. Provide the details listed below.

This opens an advisory visible only to you and the maintainers.

Please include:

- A description of the vulnerability and its impact.
- The iMirror version (and Windows version / GPU if relevant).
- Step-by-step reproduction, ideally with a minimal proof of concept.
- Any relevant logs — but **redact private screen content, key material, and
  session data first** (see "Sensitive data" below).

## What to Expect

- **Acknowledgement:** we aim to acknowledge a report within 7 days.
- **Assessment:** we confirm the issue, determine the affected versions, and
  keep you updated on remediation progress.
- **Fix and disclosure:** once a fix is ready we coordinate a release and public
  disclosure, and are glad to credit reporters who wish to be named.

iMirror is an unsigned, GPLv3, developer/QA project maintained on a best-effort
basis, so timelines depend on severity and maintainer availability.

## Scope

Areas most relevant to security:

- **Network input parsing** — AirPlay RTSP/RAOP control, mDNS, mirror data, and
  audio RTP paths process input from devices on the local network.
- **FairPlay / cryptographic handling** — pairing, pair-verify, and key setup.
- **Auto-update** — the in-app updater verifies a published `SHA256SUMS` and
  **fails closed** when a checksum is missing, unreachable, or does not match.
  Reports that bypass this verification are in scope.
- **Local diagnostic artifacts** — logs, H.264/audio dumps, and snapshots can
  contain private screen content or key material.

Out of scope:

- SmartScreen / "unknown publisher" warnings: release builds are intentionally
  **unsigned** (developer/QA distribution). Code signing is a tracked product
  decision, not a vulnerability.
- Issues that require a privileged local attacker who already controls the
  machine running iMirror.

## Sensitive Data

iMirror's logs and diagnostic dumps may contain private screen content, audio,
or FairPlay key material. These files are git-ignored and must never be
committed or attached to public issues. Redact or trim them before sharing
evidence in a private report.

## Disclosure Policy

We follow coordinated disclosure: please give us a reasonable opportunity to
ship a fix before any public disclosure. We will work with you on timing and are
glad to credit you in the release notes and advisory.
