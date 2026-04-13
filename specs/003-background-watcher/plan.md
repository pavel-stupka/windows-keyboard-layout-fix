# Implementation Plan: Background Watcher, Autostart, and Slim Binary

**Branch**: `003-background-watcher` | **Date**: 2026-04-13 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/003-background-watcher/spec.md`

## Summary

Extend `KbFix` with a per-user background watcher that repeatedly re-applies the existing reconciliation pipeline every few seconds, so keyboard layouts injected by Remote Desktop (or anything else) disappear within a few seconds without user action. Add `--install`, `--uninstall`, and `--status` CLI commands that stage the binary into `%LOCALAPPDATA%\KbFix\`, register a per-user `HKCU\Run` autostart entry, manage the watcher's lifecycle via a named mutex + named event, and report installed state. In the same release, shrink the published binary from 80+ MB to a target of ≤20 MB (ideally <15 MB) by enabling full trimming and a size-knob suite on the existing .NET 8 self-contained single-file publish. All changes respect the existing constitution: per-user, no admin, idempotent, reversible, no new third-party dependencies.

## Technical Context

**Language/Version**: C# 12 on .NET 8 (LTS), `net8.0-windows10.0.17763.0`, unchanged from features 001/002.
**Primary Dependencies**: existing Win32 P/Invoke (`user32.dll` — `GetKeyboardLayoutList`, `LoadKeyboardLayout`, `UnloadKeyboardLayout`, `ActivateKeyboardLayout`); existing TSF COM interop (`ITfInputProcessorProfileMgr`, `ITfInputProcessorProfiles`); `Microsoft.Win32.Registry` (already referenced); `System.Threading.Mutex` / `EventWaitHandle` (BCL); `System.Diagnostics.Process` (BCL). **No new NuGet packages.**
**Storage**: Per-user filesystem under `%LOCALAPPDATA%\KbFix\` (staged binary, PID file, rolling log ≤64 KB). Per-user registry value `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\KbFixWatcher`. Two named kernel objects scoped to `Local\`: `Local\KbFixWatcher.Instance` (mutex), `Local\KbFixWatcher.StopEvent` (manual-reset event). Reads only from the existing persisted-config sources (`HKCU\Control Panel\International\User Profile`, fallback `HKCU\Keyboard Layout\Preload`) — no new read surfaces.
**Testing**: xUnit unit tests under `tests/KbFix.Tests` (unchanged project). New pure-logic units: `InstallDecision`, `FlapDetector`, `WatcherLoop` (over an `ISessionReconciler` seam). Manual verification per constitution Dev Workflow gate for the Registry / Mutex / Event / process-detach code paths. See `quickstart.md` §§2–7 for the manual-verification script.
**Target Platform**: Windows 10 1809 and Windows 11 desktop, including RDP sessions and fast-user-switching. Single user session at a time; multi-user hosts get one independent watcher per session via `Local\` scoping.
**Project Type**: Desktop CLI utility (single project — `src/KbFix/` producing `kbfix.exe`). Unchanged from features 001/002.
**Performance Goals**:
- Watcher reacts to a layout injection within ~2 s (one `PollInterval`); SC-001, SC-002.
- Watcher CPU indistinguishable from zero at a 1-minute average when idle (SC-003). Achieved by `GetKeyboardLayoutList` being sub-millisecond and the idle backoff stretching `PollInterval` to 10 s after prolonged no-ops.
- `--install`, `--uninstall` each complete in <5 s (SC-004). `--status` completes in <1 s (SC-005).
**Constraints**:
- No administrator elevation at any point (FR-003, FR-009, FR-010). Hard constraint.
- No new third-party NuGet packages (FR-019, constitution § Technical Constraints).
- Published binary ≤20 MB, ideally <15 MB (SC-006). Hard constraint for the release build.
- All existing one-shot behavior must remain byte-for-byte identical (FR-018, SC-008).
- No changes to write surfaces beyond the three identified (Run key, `%LOCALAPPDATA%\KbFix\`, named kernel objects). In particular, no writes to `HKCU\Keyboard Layout\Preload` or `HKCU\Control Panel\International\User Profile`.
**Scale/Scope**: Single-user, single-machine. Peak state under management per user: ≤20 keyboard layouts, ≤1 watcher process, ≤2 log files, 1 registry value. This is a micro-utility; scale concerns do not apply.

All unknowns resolved in Phase 0 — see [research.md](./research.md). No `NEEDS CLARIFICATION` markers remain.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-checked after Phase 1 design.*

The project constitution is `.specify/memory/constitution.md` v1.0.0. Each of its five Core Principles is evaluated against this feature.

### I. Single Purpose & Simplicity

**Status: PASS, with explicit note on v1 exclusion.**

The constitution's Principle I says "Phase 2 (resident watcher) is explicitly out of scope for v1 and MUST NOT influence v1 architectural decisions beyond leaving a clean seam." This feature **is** the Phase 2 resident watcher. It is being delivered as a *follow-up* feature on top of the existing v1, not by broadening v1 itself. The constitution does not forbid the work; it forbade it from leaking into v1's scope.

The feature still honors the single-purpose spirit:

- The watcher does exactly one thing — re-run the existing reconciliation loop on a timer. No new functional concerns.
- No GUI, no tray icon, no configuration UI, no hotkeys, no telemetry, no plugin system.
- `--install` / `--uninstall` / `--status` are the minimum machinery required to make the watcher durable across reboots and discoverable for troubleshooting. They are not general-purpose "keyboard manager" features.
- The binary-size work (Story 4) is purely a publish-settings change; it touches no business logic.

**No constitutional amendment is required.** This feature is a new feature past v1, explicitly permitted by the governance section.

### II. User Configuration Is the Source of Truth

**Status: PASS.**

The watcher reuses `PersistedConfigReader.ReadRaw` verbatim every poll cycle. It does not cache, infer, or guess layouts. It does not modify `HKCU\Control Panel\International\User Profile`, `HKCU\Keyboard Layout\Preload`, or any other persisted language setting. A user who changes their language settings in Windows Settings while the watcher is running sees the watcher adopt the new set on the next poll cycle (because `ReadRaw` is re-invoked each iteration).

### III. Safe & Reversible Operations

**Status: PASS.**

Each invariant from Principle III is addressed explicitly:

- *Never remove or alter persisted user settings.* The watcher has no code path that writes to persisted-config registry keys. `--install` writes only the Run key and the staging directory. `--uninstall` removes only those.
- *Never leave the session with zero usable input layouts.* The existing `ReconciliationPlan.Build` already produces `Refused` when the plan would empty the session (feature 001). The watcher treats `Refused` as a no-op for that cycle and logs `session-empty-refused`. FR-007's flap detector provides a second line of defense against pathological loops.
- *Best-effort rollback / fail-closed on errors during reconciliation.* The watcher uses the same per-action failure handling as the one-shot code path (`gateway.Apply` returns an `AppliedAction` with `Succeeded: false` on failure), and the next poll cycle re-evaluates from scratch — no stale state carries forward.
- *Idempotent.* Running `--install` twice produces the same state (data-model §3 `InstallDecision` table). Running the watcher for 10 minutes vs. 10 hours against a quiescent session produces identical session state. The new commands are idempotent by construction.
- *Registry edits only when no API path exists.* `HKCU\Run` is the supported per-user autostart API — there is no higher-level managed API for it. This is documented in `research.md` §R2.

### IV. Native Windows Integration

**Status: PASS.**

All new Win32 surface area uses documented Microsoft APIs: `CreateMutexW` (via `System.Threading.Mutex`), `CreateEventW` (via `EventWaitHandle`), `CreateProcessW` (via `System.Diagnostics.Process`), `FreeConsole` (one defensive P/Invoke). No undocumented APIs. No brittle registry hacks — the one registry value written is the well-known `HKCU\Run` key explicitly documented by Microsoft for per-user autostart.

No third-party runtime added. The binary remains self-contained (actually becomes *more* self-contained with trimming, since trimming reduces it to only the BCL surface the code actually needs).

### V. Observability & Diagnosability

**Status: PASS (requires §R7 log implementation).**

A long-lived background process without output is explicitly the failure mode Principle V warns against. Mitigations:

- Rolling text log at `%LOCALAPPDATA%\KbFix\watcher.log` records every reconciliation, every flap-backoff, every error. See research §R7.
- `--status` reports the log path so a troubleshooting user finds it in one command.
- Event lines are human-readable and timestamped in ISO-8601 UTC.
- Exit codes remain discoverable: existing codes 0/1/2/3/64 preserved, new codes 10–14 for `--status` states only, documented in `contracts/cli.md`.
- The one-shot mode's stdout report is unchanged.

### Constitution Check verdict

**PASS — no exceptions to record.** The Complexity Tracking table below is empty because there are no constitutional violations to justify.

### Re-check after Phase 1

Re-evaluated after writing `data-model.md`, `contracts/cli.md`, and `quickstart.md`. All five principles still pass. The design does not widen any registry/filesystem write surface beyond what was analyzed above.

## Project Structure

### Documentation (this feature)

```text
specs/003-background-watcher/
├── plan.md              # This file (/speckit.plan output)
├── research.md          # Phase 0 output — R1..R8 decisions
├── data-model.md        # Phase 1 output — 8 entities + filesystem/registry layout
├── quickstart.md        # Phase 1 output — build + manual verification script
├── contracts/
│   └── cli.md           # Phase 1 output — new CLI contract for --install/--uninstall/--status/--watch
├── checklists/
│   └── requirements.md  # Spec quality checklist (already passing)
└── tasks.md             # Phase 2 output (/speckit.tasks — NOT created by /speckit.plan)
```

### Source Code (repository root)

```text
src/
└── KbFix/
    ├── Program.cs                   # MODIFY: dispatch to Install/Uninstall/Status/Watch modes
    ├── KbFix.csproj                 # MODIFY: add trim + size-knob properties (feature story 4)
    ├── Cli/
    │   ├── Options.cs               # MODIFY: add Install/Uninstall/Status/Watch flags + mutual exclusion
    │   └── Reporter.cs              # unchanged by this feature
    ├── Diagnostics/
    │   └── ExitCodes.cs             # MODIFY: add codes 10..14 for --status states
    ├── Domain/                      # unchanged — reconciliation stays pure and reused
    │   ├── Action.cs
    │   ├── LayoutId.cs
    │   ├── LayoutSet.cs
    │   ├── PersistedConfig.cs
    │   ├── ReconciliationPlan.cs
    │   └── SessionState.cs
    ├── Platform/
    │   ├── KeyboardLayoutCatalog.cs # unchanged
    │   ├── PersistedConfigReader.cs # unchanged
    │   ├── SessionLayoutGateway.cs  # unchanged (watcher reuses via new ISessionReconciler facade)
    │   ├── TsfInterop.cs            # unchanged
    │   ├── Win32Interop.cs          # MODIFY: add FreeConsole P/Invoke (one line)
    │   └── Install/                 # NEW — concrete install/status executors
    │       ├── AutostartRegistry.cs     # HKCU\Run writer/reader/deleter
    │       ├── BinaryStaging.cs         # copy/delete %LOCALAPPDATA%\KbFix\kbfix.exe
    │       ├── WatcherDiscovery.cs      # mutex probe, PID file read, state classification
    │       ├── WatcherLauncher.cs       # ProcessStartInfo detach + FreeConsole
    │       └── InstallExecutor.cs       # applies InstallDecision steps sequentially
    └── Watcher/                     # NEW — watcher mode
        ├── WatcherMain.cs               # entry point for --watch
        ├── WatcherLoop.cs               # pure poll-and-reconcile loop
        ├── ISessionReconciler.cs        # seam over existing reconciliation pipeline
        ├── SessionReconciler.cs         # concrete adapter over PersistedConfigReader+SessionLayoutGateway
        ├── FlapDetector.cs              # sliding-window counter
        ├── InstallDecision.cs           # pure decision function for --install/--uninstall/--status
        ├── WatcherInstallation.cs       # snapshot record of current installed state
        └── WatcherLog.cs                # bounded rolling log (64 KB, one-deep rotation)

tests/
└── KbFix.Tests/
    ├── KbFix.Tests.csproj           # unchanged
    └── (existing tests — unchanged)
    └── Watcher/                     # NEW tests
        ├── InstallDecisionTests.cs      # exhaustive decision-table coverage
        ├── FlapDetectorTests.cs         # window/threshold/pause behavior
        └── WatcherLoopTests.cs          # poll loop over fake ISessionReconciler + fake clock

build.cmd                            # unchanged behavior; trimming is driven entirely by csproj
```

**Structure Decision**: Single-project layout (Option 1 from the template) unchanged from features 001/002. Two new subfolders inside `src/KbFix/`: `Watcher/` for pure logic and the mode entry point, and `Platform/Install/` for Win32/registry/filesystem executors. This keeps the pure/unpure separation the existing codebase already uses (`Domain/` = pure, `Platform/` = Win32) and makes the new code unit-testable where it has branches (`Watcher/`) and manually verifiable where it cannot be unit-tested (`Platform/Install/`).

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified.**

No constitutional violations. This table is intentionally empty.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|--------------------------------------|
| _(none)_  | _(none)_   | _(none)_                             |

## Phase 0: Outline & Research — ✅ Complete

Artifact: [research.md](./research.md). Resolved: R1 (detection = polling), R2 (autostart = HKCU\Run), R3 (single-instance = named mutex + stop event), R4 (process detach), R5 (binary staging to %LOCALAPPDATA%), R6 (trim + size-knobs, not AOT), R7 (bounded watcher.log), R8 (testability via pure-logic seams).

## Phase 1: Design & Contracts — ✅ Complete

Artifacts:

- [data-model.md](./data-model.md) — 8 entities and their state transitions.
- [contracts/cli.md](./contracts/cli.md) — full CLI contract for the new subcommands, additive to the 001 contract.
- [quickstart.md](./quickstart.md) — build, install, verify, uninstall manual-verification script.

Agent context update (Phase 1 step 3): run `.specify/scripts/bash/update-agent-context.sh claude` after this plan is written so `CLAUDE.md` reflects the new feature's active technologies. This is done at the end of /speckit.plan.

## Phase 2: Not covered by this command

`/speckit.plan` stops after Phase 1. `/speckit.tasks` will produce `tasks.md` next, decomposing this plan into sequenced implementation tasks mapped to the structure above.
