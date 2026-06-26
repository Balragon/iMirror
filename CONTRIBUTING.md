# Contributing to iMirror

Thank you for your interest in contributing to iMirror! This document outlines how to get started, what kinds of contributions we accept, and our development process.

## Code of Conduct

iMirror adopts the [Contributor Covenant 2.1](CODE_OF_CONDUCT.md). Please review it before participating. We expect respectful, inclusive discourse; violations are taken seriously.

## What We Welcome

### Bug Fixes
Issues affecting AirPlay protocol handling, video/audio decode, UI responsiveness, or latency are good candidates. Include:
- Reproducible steps or video.
- Device/OS details (Windows 10/11, GPU model, driver version).
- Logs (`IMIRROR_WRITE_DIAGNOSTICS=1`).

### Performance Improvements
Latency, CPU, GPU, or memory optimizations. Changes to the video/audio stack should include:
- Benchmark before/after (use `tools/LatencyAcceptanceReport` for latency).
- Real-device testing results (device, driver, outcome).

### Documentation
README expansions, architecture clarifications, validation step improvements. Low barrier to entry and valuable for new developers.

### Testing & Validation
Additional tests, edge-case coverage, or real-device validation reports are always helpful. See `docs/validation.md` for the acceptance framework.

## What We're Selective About

- **Platform changes:** iMirror is Windows-only by design. Abstraction layers for cross-platform support are out of scope.
- **New rendering backends:** GPU code is high-risk and high-maintenance. Discuss major rendering changes in an issue first.
- **Dependency upgrades:** SharpDX, Media Foundation, WASAPI are core to the GPU path. Changes require real-device re-validation.
- **Protocol extensions:** new AirPlay modes should be discussed in an issue before PR, to align with roadmap and confirm GPU/latency impact.

## Getting Started

### Prerequisites

- **Windows 10+** (x64).
- **Visual Studio 2022** or **dotnet 8.0 SDK** CLI.
- **FFmpeg:** [Gyan essentials build](https://github.com/GyanD/codexffmpeg/releases) or `winget install Gyan.FFmpeg.Essentials`.
- For GPU testing: NVIDIA, Intel, or AMD GPU with hardware video decode support.

### Build

```powershell
dotnet build .\MacMirrorReceiver.csproj -c Release
```

### Run

```powershell
.\bin\Release\net8.0-windows\iMirror.exe
```

### Test

Unit tests:
```powershell
dotnet test .\MacMirrorReceiver.Tests.csproj -c Release
```

Real-device testing (v0.3+):
```powershell
# From an iPhone/Mac, send screen mirroring to iMirror
# Then validate latency and stability:
dotnet run --project .\tools\LatencyAcceptanceReport\LatencyAcceptanceReport.csproj -c Release -- .\iMirror.log 150 10
```

## Contribution Workflow

### 1. Fork & Branch

```bash
git clone https://github.com/YOUR_FORK/iMirror.git
cd iMirror
git checkout -b fix/your-issue-title
```

### 2. Make Changes

Follow the existing code style (C# conventions, LINQ over loops where readable, explicit null-coalescing). No major refactoring outside the scope of the fix.

### 3. Test Locally

- **Build:** `dotnet build -c Release` must pass.
- **Unit tests:** `dotnet test -c Release` must pass.
- **GPU path changes:** test on real hardware (see "Real-Device Testing" below).

### 4. Commit & Push

```bash
git commit -m "Fix: [summary]

[Detailed explanation if needed.]"
git push origin fix/your-issue-title
```

### 5. Create a Pull Request

Open a PR against `main`. Reference related issues (e.g., "Fixes #42"). Describe:
- What the change does.
- Why it's needed (bug report reference, performance gain, etc.).
- Testing done (real device? which one?).

### 6. Review & Merge

Maintainers will review for correctness, test coverage, and GPU/latency impact. If real-device testing is needed but you lack hardware, mention it in the PR; maintainers will test on their machines.

Once approved, the PR is squashed and merged to `main`.

## Real-Device Testing

The GPU video path (D3D11 hardware decode → `D3DImage` WPF present) is the highest-risk code path and **must be tested on real hardware** before merge if changed.

### What to Test

- **GPU keyframe render:** start a mirroring session; confirm video appears and is smooth (no stutter).
- **Device-loss/restore:** during mirroring, unplug/replug the GPU (or trigger a device reset); confirm iMirror recovers without crash.
- **Latency gate:** measure frame-arrival latency with `tools/LatencyAcceptanceReport` or in-app latency display (Settings → Diagnostics); confirm ≤150ms.
- **1-hour soak:** run for at least 1 hour without audio mute, stutter, or mismatch; log must show no crash.

### Report Template

Include in your PR if you tested on real hardware:

```markdown
### Real-Device Testing
- **Device:** iPhone 14 Pro
- **Sender OS:** iOS 17.2
- **Windows:** Windows 11 23H2
- **GPU:** NVIDIA RTX 4060
- **Driver:** 552.12
- **Test results:**
  - ✅ Keyframe render: smooth, no stutter
  - ✅ Device loss: recovered without crash
  - ✅ Latency: 120ms (LatencyAcceptanceReport gate passed)
  - ✅ 1-hour soak: no audio mute, no crash
```

If you **don't have access to real hardware**, mention it clearly:

```markdown
### Real-Device Testing
- **Hardware available:** None. Requesting maintainer validation before merge.
```

Maintainers will test on their machines; this is expected for external contributors.

## Coding Conventions

- **C# style:** follow Microsoft's C# coding conventions (implicit `this`, LINQ for sequences, explicit nullability).
- **Naming:** `PascalCase` for public/internal; `_camelCase` for private fields.
- **Comments:** only when the *why* is non-obvious; don't repeat what the code already says.
- **Tests:** new logic gets a corresponding test; use xUnit (existing test suite).

## Reporting Issues

Use [GitHub Issues](https://github.com/Balragon/iMirror/issues). For **GPU/latency bugs**, include:
- Windows version.
- GPU model and driver version.
- Device sending mirroring (iPhone model/iOS, Mac model/macOS).
- Reproduction steps.
- `iMirror.log` or diagnostic dumps (if privacy permits).

## Roadmap & Future Work

See [`docs/specs/v04-product-surface-roadmap.md`](docs/specs/v04-product-surface-roadmap.md) and [`docs/specs/v05-plus-roadmap.md`](docs/specs/v05-plus-roadmap.md) for planned work. Major architectural changes or new features should align with these milestones.

## Questions?

- Open a [GitHub Discussion](https://github.com/Balragon/iMirror) (if enabled) or a [GitHub Issue](https://github.com/Balragon/iMirror/issues) to ask.
- For private inquiries, email maintainers (contact info in README).

---

**Thank you for contributing to iMirror!** Your work helps make screen mirroring better for developers and QA teams.
