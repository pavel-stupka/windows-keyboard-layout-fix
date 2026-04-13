# Implementation Plan: Fix Windows Session Keyboard Layouts

**Branch**: `001-fix-keyboard-layouts` | **Date**: 2026-04-10 | **Spec**: [spec.md](./spec.md)
**Input**: Feature specification from `/specs/001-fix-keyboard-layouts/spec.md`

## Summary

Build a single-file, self-contained Windows command-line utility that, when
launched by an unprivileged user, enumerates the keyboard input profiles in
the current Windows session, compares them against the user's persisted
configuration in `HKCU\Keyboard Layout\Preload`, and removes every layout
present in the session but absent from the persisted set — never modifying
the persisted set, never leaving the session with zero usable layouts, and
always switching the foreground layout to a survivor *before* removing it.
Implementation language is C# on .NET 8 published with `PublishSingleFile`,
using P/Invoke + COM interop to TSF's `ITfInputProcessorProfileMgr` for the
session-side reads and writes. The deliverable is one `kbfix.exe` the user
can double-click. See `research.md` for the decision rationale.

## Technical Context

**Language/Version**: C# 12 on .NET 8 (LTS)
**Primary Dependencies**: Windows Text Services Framework (TSF) via COM interop (`ITfInputProcessorProfileMgr`, `ITfInputProcessorProfiles`); Win32 input-locale APIs via P/Invoke (`GetKeyboardLayoutList`, `LoadKeyboardLayout`, `UnloadKeyboardLayout`, `ActivateKeyboardLayout`); standard `Microsoft.Win32.Registry` for reading `HKCU\Keyboard Layout\Preload`. No third-party NuGet packages.
**Storage**: None. The utility reads `HKCU\Keyboard Layout\Preload` (read-only) and the live TSF session state. It writes nothing to disk.
**Testing**: xUnit for the pure reconciliation logic (`ReconciliationPlan` construction). Manual end-to-end verification via the RDP repro documented in `quickstart.md` is mandatory before each release per the constitution.
**Target Platform**: Windows 10 and Windows 11 desktop (x64), interactive user session including Remote Desktop sessions.
**Project Type**: Single-project desktop CLI utility (no client/server split, no library/CLI split — one binary).
**Performance Goals**: A full run (read persisted, read session, plan, apply, verify, report) MUST complete in well under 1 second on a typical desktop. Cold start of the self-contained single-file binary should stay under ~500 ms.
**Constraints**: No Administrator elevation. No installer. No background process. No third-party runtime on the target machine (self-contained publish). No modification of persisted Windows settings. Idempotent. Single-file `.exe` distribution.
**Scale/Scope**: One user, one session, one run. The "scale" axis is irrelevant — this is a single-purpose desktop utility. Expected codebase: a few hundred lines of C# plus interop declarations.

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

The five core principles from `.specify/memory/constitution.md` are
evaluated against this plan:

| # | Principle                                       | Status | Evidence                                                                                                                                                                                                                                                              |
|---|-------------------------------------------------|--------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| I | Single Purpose & Simplicity                     | PASS   | One binary, one job (remove session-only layouts). No GUI, no service, no plugins. Phase 2 watcher mode is explicitly out of scope (FR-015, spec Assumptions). Codebase target ≈ a few hundred lines.                                                                 |
| II | User Configuration Is the Source of Truth      | PASS   | `HKCU\Keyboard Layout\Preload` is read as the desired state and never written (FR-002, FR-006). The utility never invents layouts. See `research.md` §R2.                                                                                                            |
| III | Safe & Reversible Operations                  | PASS   | Idempotent by construction (FR-007); switch-active-before-deactivate (FR-009); refuse-on-empty-persisted edge case (FR-008, exit code 3); pure unit-tested plan computation; verification re-read of session state at the end of every run (data-model.md §State Transitions). |
| IV | Native Windows Integration                      | PASS   | TSF `ITfInputProcessorProfileMgr` is the documented official API used by Windows Settings itself (`research.md` §R3, §R4). No registry edits to mutate state, no `ctfmon` killing, no undocumented hacks. Read-only registry access for the desired state only.       |
| V | Observability & Diagnosability                  | PASS   | Four-section human-readable report on stdout (FR-011); distinct exit codes 0/1/2/3/64 (`contracts/cli.md`); `--dry-run` for safe inspection (FR-010); errors include the underlying Win32/HRESULT detail.                                                              |

**Result**: GATE PASS. No principle violations. Complexity Tracking section
below is empty.

## Project Structure

### Documentation (this feature)

```text
specs/001-fix-keyboard-layouts/
├── plan.md              # This file
├── spec.md              # Feature specification (already written)
├── research.md          # Phase 0 — language, API, and approach decisions
├── data-model.md        # Phase 1 — in-memory entities and the plan algorithm
├── quickstart.md        # Phase 1 — build, run, and manual verification steps
├── contracts/
│   └── cli.md           # Phase 1 — the binary's CLI / stdout / exit-code contract
├── checklists/
│   └── requirements.md  # Spec quality checklist (from /speckit-specify)
└── tasks.md             # Phase 2 output — created by /speckit-tasks (NOT by this command)
```

### Source Code (repository root)

```text
src/
└── KbFix/
    ├── KbFix.csproj                # net8.0, win-x64, single-file publish settings
    ├── Program.cs                  # entry point, argument parsing, top-level orchestration
    ├── Cli/
    │   ├── Options.cs              # parsed command-line options (--dry-run, --quiet, …)
    │   └── Reporter.cs             # builds and prints the four-section report
    ├── Domain/
    │   ├── LayoutId.cs             # value type (langId, klid, profileGuid)
    │   ├── LayoutSet.cs            # immutable set with Difference/Contains
    │   ├── PersistedConfig.cs
    │   ├── SessionState.cs
    │   └── ReconciliationPlan.cs   # PURE; the unit-tested core
    ├── Platform/
    │   ├── PersistedConfigReader.cs   # reads HKCU\Keyboard Layout\Preload (read-only)
    │   ├── TsfInterop.cs              # COM interop declarations for TSF
    │   ├── Win32Interop.cs            # P/Invoke declarations for input-locale APIs
    │   └── SessionLayoutGateway.cs    # composes TSF + Win32 to read & mutate session
    └── Diagnostics/
        └── ExitCodes.cs            # the 0/1/2/3/64 mapping from contracts/cli.md

tests/
└── KbFix.Tests/
    ├── KbFix.Tests.csproj
    └── Domain/
        ├── ReconciliationPlanTests.cs   # all branches: no-op, simple, switch-first, empty, no-fallback
        └── LayoutSetTests.cs
```

**Structure Decision**: Single project (`src/KbFix/`) plus a single test
project (`tests/KbFix.Tests/`). The "platform" subfolder isolates everything
that touches Windows so the `Domain/` layer can stay pure and easily unit-
tested. This is the smallest layout that still keeps the testable core
free of P/Invoke and COM, in line with Principle I (Simplicity) and the
constitution's Development Workflow section. No client/server split, no
shared libraries, no separate CLI vs library — one binary, one purpose.

## Complexity Tracking

> **Fill ONLY if Constitution Check has violations that must be justified**

*(Empty — Constitution Check passed with no violations.)*
