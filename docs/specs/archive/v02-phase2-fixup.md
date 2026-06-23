# Spec: v0.2 Phase 2 Fixup — TEARDOWN Session End

**Handoff:** codex-backend
**Branch:** `claude/magical-faraday-6ex3c7`
**Scope:** One focused fix to complete Task 1 (`airplay-session-ended`).

## Background

Phase 2 stability work (`5057ecf`) correctly implemented `MirrorSessionEnded`,
`ResetMirrorSessionState`, `HandleAirPlayMirrorSessionEnded`, and all wiring.
Tasks 2–7 are verified complete.

A subsequent fix (`fcb2ac7`) removed the TEARDOWN trigger from `BuildResponse`
to prevent RAOP 5000 TEARDOWN from falsely ending an active mirror session.
That was a valid observation but an overly broad fix: the TEARDOWN trigger was
removed from **all** connections instead of being scoped to AirPlay 7000 only.

The connection-close path (`HandleProbeClientAsync` lines 334–338, 352–355,
363–365) already uses `IsMirrorControlLabel(label)` to gate session-end on
AirPlay 7000 only. TEARDOWN needs the same treatment.

## The Problem with the Current State

When a Mac sender stops mirroring it sends TEARDOWN on AirPlay TCP 7000, then
closes the TCP connection. Currently:

1. TEARDOWN arrives → `BuildResponse` returns 200 OK, no session reset.
2. Loop calls `ReadRequestAsync` again (waiting for more requests).
3. Sender closes the TCP connection → `ReadRequestAsync` returns null.
4. `HandleProbeClientAsync` fires `EndMirrorSessionIfAnnounced` via the
   connection-close path.

Step 4 works, but the delay between step 1 and step 4 can be several seconds
(sender-dependent). During that window the UI shows a frozen last frame with
audio still running, appearing broken to the user.

## Fix

**File:** `MacMirrorReceiver.Networking/AirPlayProbeService.cs`
**Method:** `HandleProbeClientAsync` (around line 346)

After writing the response, check for TEARDOWN on a mirror control connection
and end the session immediately:

```csharp
byte[] response = BuildResponse(request, client.Client.RemoteEndPoint);
await stream.WriteAsync(response, _cts.Token);

// End the mirror session immediately on AirPlay TEARDOWN; do not wait for
// the TCP connection to close. RAOP TEARDOWN (label != "AirPlay") is ignored
// here — some senders send it mid-session as an audio-stream reset.
if (request.Method == "TEARDOWN" && IsMirrorControlLabel(label))
{
    EndMirrorSessionIfAnnounced("TEARDOWN");
    break;
}
```

The `break` exits the request loop, letting the `using (client)` block close
the TCP connection cleanly. Double-fire is already guarded: `ResetMirrorSessionState`
checks `hadAnnouncedSession` under `_mirrorKeyGate` and returns false if the
session was already reset by the subsequent connection-close path.

**Do not change `BuildResponse`.** The fix belongs in `HandleProbeClientAsync`
where `label` is available.

## Verification

**Automated:**
- Existing tests in `MacMirrorReceiver.Tests` cover `CleanupGuards`,
  `StaleGenerationTests`, `VideoEngineGateTests`. No new unit tests needed for
  this change (it requires a live AirPlay sender to exercise the TEARDOWN path).

**Build:**
```powershell
dotnet build .\MacMirrorReceiver.csproj -c Release
```
Must succeed with no new warnings.

**Manual (Windows, real Mac/iPhone):**
1. Start mirroring from Mac → verify video appears.
2. Stop mirroring from Mac Control Center (sends TEARDOWN).
3. iMirror should return to the empty state within ~1 second, audio stops.
4. Start mirroring again → verify clean second session (no stale state).
5. Force-close the sender mid-session (no TEARDOWN) → connection-close path
   should also return to empty state.
6. Confirm RAOP-only operations (audio restart, pause) do not accidentally end
   the session while video is active.

## What NOT to change

- `BuildResponse`: keep TEARDOWN as `200 OK` only (no session logic here).
- `FLUSH`: still a no-op, no session end.
- Tasks 2–7: already complete, do not re-implement or refactor.
- `cf6c63e` (Settings Escape-to-close, sidebar expand): keep as-is.
  The full Settings-as-top-level-Window approach is Phase 3.
