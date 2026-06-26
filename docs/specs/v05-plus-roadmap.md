# iMirror Post-v0.4 Roadmap (v0.5+)

Forward plan for the work **after** v0.4 (installer + auto-update, currently in
codex's hands). This consolidates items that until now were scattered across
Issue #17, `docs/dotnet-strategy.md`, and the product-surface roadmap, so the
sequence, dependencies, and the one hard deadline are in a single place.

For v0.4 itself, see `docs/specs/v04-product-surface-roadmap.md` and
`docs/specs/v04-updater-design.md`.

## The forcing function: .NET 8 EOL — 2026-11-10

Everything below is sequenced around one immovable date. **.NET 8 reaches end of
support on 2026-11-10** (Microsoft LTS policy, confirmed in
`docs/dotnet-strategy.md`). Shipping a distributed product on an unsupported
runtime past that date is not acceptable, so the **net10 migration must land
well before it**, with buffer for the risky GPU-path re-validation.

This deadline — not feature ambition — drives the order.

## Sequence at a glance

```
v0.4 (codex, now) ─ Installer (Inno) + Auto-update (in-app)      [TFM-independent]
   ↓
v0.5 ───────────── net10 migration (#17) + real-hardware soak    [REQUIRED before 2026-11-10]
   ↓
v0.7 ───────────── SharpDX → Vortice.Windows (GPU binding)       [decoupled from net10]

Standing (every release / not version-gated):
   • real-hardware soak run as a release gate
   • long-run A/V sync hardening (#3)

On hold (NOT a scheduled milestone — see "Deferred"):
   • Code signing / general-audience expansion
```

---

## v0.5 — net10 runtime migration (REQUIRED)

**Tracked by Issue #17.** This is the priority immediately after v0.4 and the
only milestone with a hard external deadline. Ready-to-run execution note (exact
TFM/CI edits, gates, sequencing): `docs/specs/v05-net10-migration.md`.

- **Scope:** bump `TargetFramework` `net8.0-windows` → `net10.0-windows` on
  `MacMirrorReceiver.csproj` and the `tools/*` probe projects. **Keep SharpDX**
  (the DirectX binding swap is a separate, later milestone — see v0.7).
- **Why net10:** it is the current LTS (GA 2025-11-11, supported to 2028-11-14),
  the correct eventual target.
- **The real risk** is not the TFM bump itself but the highest-risk code path it
  re-validates: D3D11 → D3D9 shared-surface → WPF `D3DImage` present. SharpDX
  4.2.0 resolves on net10 via the netstandard1.1 shim (expected), but
  "restores" ≠ "works" — this path must be exercised on real hardware.
- **Validation checklist** (from `docs/dotnet-strategy.md`): restore / build /
  test, publish-artifact inspection (confirm all WPF assemblies present in the
  self-contained output — the known net9+ self-contained drop), then on real
  hardware: GPU keyframe render, `D3DImage` device-loss/restore cycle, 10-min
  soak, latency ≤150ms gate.

### Acceptance criteria (mirrors Issue #17)
- [ ] `net10.0-windows` builds and passes CI (restore/build/test).
- [ ] Published self-contained artifact includes all WPF assemblies.
- [ ] GPU D3D11 present path renders a keyframe; `D3DImage` interop survives a
      device-loss/restore cycle on real hardware.
- [ ] 1-hour soak gate (`scripts/soak-gate.ps1`) passes on net10.
- [ ] Completed before 2026-11-10.

---

## v0.5 release gate — real-hardware soak (must actually RUN)

The soak **gate tooling** shipped in v0.3 (`scripts/soak-gate.ps1`,
`tools/LatencyAcceptanceReport` with crash detection), but **no real 1-hour
device session has ever been run against it.** A gate that has never been
exercised is not evidence.

- Before releasing the net10 build (and any release claiming stability), run a
  real ≥60-minute mirroring session from a physical device and pass
  `scripts/soak-gate.ps1` (see `docs/soak-gate.md`).
- This is a **release activity, not a feature** — it cannot be CI-automated
  (needs a real AirPlay sender + GPU).
- The net10 migration in particular **must** be validated by an actual soak,
  because that is exactly where the D3D-path risk would surface.

---

## v0.7 — SharpDX → Vortice.Windows (GPU binding modernization)

**Deliberately separated from the net10 bump** so any GPU regression is
bisectable: was it the runtime change, or the binding swap? Validating them
independently is the whole reason this stays at v0.7.

- **Why:** SharpDX is archived/unmaintained; Vortice.Windows is actively
  maintained and a near 1:1 API match for the D3D11 + DXGI + D3D9 surface iMirror
  uses.
- **Effort:** MEDIUM — ~4 renderer files (per `docs/dotnet-strategy.md`).
- **Precondition:** net10 must already be stable and soak-validated, so the
  Vortice swap is measured against a known-good baseline. **Met:** v0.5.0 (net10 +
  SharpDX) shipped and soak-validated — v0.7 is now unblocked.
- **Ready-to-run execution note** (package swap, per-file API mapping,
  feature-flag, Gate A/B): `docs/specs/v07-vortice-migration.md`.

---

## Standing tracks (not version-gated)

- **Real-hardware soak per release** — repeat the soak gate for every release,
  not just net10. It is the only check on runtime A/V behaviour CI cannot do.
- **Long-run A/V sync hardening (Issue #3)** — 30m/1h/2h sessions: confirm no
  audio drift/mute/stutter/over-buffer, clean reconnect, and promote a stable
  default `IMIRROR_AUDIO_SYNC_OFFSET_MS`; add RTP/timestamp drift correction if
  cumulative drift appears. Field hardening, ongoing. **Measurement-first
  execution note** (observation protocol, conditional drift-correction decision
  tree): `docs/specs/v03-av-sync-hardening.md`. Runs serial *after* v0.7 Gate B to
  keep a GPU-binding swap and an audio-sync change independently bisectable.

---

## Deferred — NOT a scheduled milestone

### Code signing / general-audience expansion — ON HOLD

Phase 3 of the product-surface roadmap (Microsoft Trusted Signing) was written
assuming iMirror would eventually pursue **general (non-developer) users**.
**That expansion is not a confirmed direction** — it is currently closer to
*deferred* than *planned*.

Consequences:
- **iMirror's confirmed audience is technical/developer users**, who click
  through SmartScreen. Code signing is **not on the critical path** and is **not
  scheduled** into v0.5/v0.6/v0.7.
- Signing remains a **conditional** item: revisit **only if and when** the
  decision to target general-audience or enterprise distribution is actually
  made. The implementation plan in `v04-product-surface-roadmap.md` (Phase 3)
  stays valid as a ready-to-pull-in spec, but it does not own a release slot.
- Nothing else depends on it. Deferring signing does not block net10, soak, or
  Vortice. (It is also EOL-deadline-independent — a safety valve if the calendar
  gets tight: if v0.5 schedule is squeezed, signing is the thing that was never
  scheduled anyway.)

**Trigger to un-defer:** an explicit product decision to expand beyond the
developer audience. Until that decision exists, treat signing as out of scope.

---

## Calendar risk (read this)

Roughly **4.5 months** (from 2026-06-26) must contain **both** (a) finishing the
v0.4 installer + auto-update and (b) the net10 migration with its real-hardware
re-validation and soak. net10 is the risky half and needs buffer. If v0.4 slips,
it eats the net10 buffer, not the deadline. Keep v0.4 tight; protect net10's
validation time. The deferred signing work is the intentional release-valve if
the schedule tightens.
