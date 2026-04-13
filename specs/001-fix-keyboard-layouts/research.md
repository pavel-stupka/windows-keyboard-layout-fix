# Phase 0 Research: Fix Windows Session Keyboard Layouts

**Feature**: 001-fix-keyboard-layouts
**Date**: 2026-04-10

This document records the technology and approach decisions taken before
design. Each entry follows the format **Decision / Rationale / Alternatives
considered**.

## R1. Implementation language and runtime

**Decision**: C# on .NET 8, published as a self-contained, single-file,
trimmed Win-x64 executable (`dotnet publish -c Release -r win-x64
--self-contained -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`).

**Rationale**:
- Produces exactly the artifact the user asked for: one `.exe` they can
  double-click, with no installer and no required runtime on the target
  machine. Satisfies FR-001 directly.
- First-class P/Invoke and COM interop for the Win32 / TSF APIs needed to
  enumerate and remove session input profiles (see R3, R4).
- Mature tooling on Windows; the build is one command and reproducible from
  a vanilla SDK install.
- Binary size (~15–20 MB self-contained, single file) is acceptable for a
  utility distributed via download.

**Alternatives considered**:
- **Rust + `windows` crate**: smallest static binary and excellent type
  safety, but TSF (`ITfInputProcessorProfileMgr`) COM interop is more verbose
  and our target audience won't notice the binary-size win. Rejected to keep
  dev velocity and Windows-API ergonomics high.
- **C / C++ with raw Win32 + ATL**: smallest binary, no runtime, but more
  manual COM plumbing and weaker safety. Disproportionate cost for a utility
  whose hot path runs once per launch.
- **PowerShell + ps2exe**: trivial to write, but (a) `ps2exe` produces a
  wrapper, not a true native binary, (b) TSF COM interop in PowerShell is
  awkward, (c) execution policy and SmartScreen friction is worse than a
  signed `.exe`. Rejected.
- **Plain `.bat` / `.cmd`**: cannot reach the necessary Windows APIs without
  shelling out to other tools. Rejected.

## R2. Persisted ("desired") layout source of truth

**Decision**: Read the persisted layout set from the user's Windows registry
key `HKCU\Keyboard Layout\Preload` (with `HKCU\Keyboard Layout\Substitutes`
applied as overrides). Treat this as read-only — never write to it.

**Rationale**:
- This key is what the Windows Settings app populates when the user adds or
  removes a layout from their language. It survives reboot, which matches
  the spec's definition of "persisted".
- The fact that rebooting fixes the bug (per `SPECIFICATION.md`) confirms
  that the unwanted entries are *not* in this key — they live in transient
  session state. So `Preload` is a safe, authoritative reference.
- Reading the registry needs no elevation under HKCU.

**Alternatives considered**:
- **`EnumSystemLocales` / `GetUserPreferredUILanguages`**: returns languages,
  not specific keyboard layouts (a single language can have several layouts).
  Insufficient.
- **TSF `ITfInputProcessorProfileMgr::EnumProfiles` filtered by `enabled`**:
  returns *current* state, which is exactly the polluted state we are trying
  to clean up. Cannot be used as the desired state.
- **A user-provided config file**: violates Principle II (User Configuration
  Is the Source of Truth) — we'd be inventing a second source of truth.
  Rejected.

## R3. Session ("actual") layout enumeration

**Decision**: Enumerate the currently active input profiles for the user's
session via TSF: `ITfInputProcessorProfileMgr::EnumProfiles`, keeping
profiles whose `dwFlags` include `TF_IPP_FLAG_ACTIVE` and whose `catid` is a
keyboard category. Cross-check with `GetKeyboardLayoutList` for parity, but
treat the TSF result as authoritative because it reflects the language-bar
state the user actually sees.

**Rationale**:
- The Windows language bar / Settings UI is itself a TSF client; using the
  same API guarantees we see what the user sees.
- `GetKeyboardLayoutList` is per-process (each process loads layouts on
  demand) and is not authoritative for "what's available in the session".

**Alternatives considered**:
- **Reading `HKCU\Keyboard Layout\Preload` only**: would give us the desired
  state for free but tells us nothing about the polluted session state.
  Insufficient on its own.
- **Parsing `ctfmon` internals or undocumented session caches**: brittle and
  forbidden by Principle IV (no undocumented hacks when an API exists).

## R4. Removing unwanted session-only layouts

**Decision**: For each session-only profile that is *not* in the persisted
set, call `ITfInputProcessorProfileMgr::DeactivateProfile` (and, where the
profile corresponds to a classic keyboard layout HKL, also call
`UnloadKeyboardLayout` against the HKL to clean up the legacy layer). Apply
removals only after first switching the session's active profile to one that
will remain (FR-009), using
`ITfInputProcessorProfiles::ChangeCurrentLanguage` /
`ActivateProfile`.

**Rationale**:
- `DeactivateProfile` is exactly the operation Settings performs when a user
  removes a layout from a language. It is documented and unprivileged.
- Switching the active profile first guarantees we never try to remove the
  layout that is currently in use, preventing a race where the foreground
  app loses its IME mid-call.
- The combination satisfies FR-005, FR-008, FR-009.

**Alternatives considered**:
- **Rewriting `HKCU\Keyboard Layout\Preload`**: violates FR-006 (we are
  forbidden from modifying persisted config) and would not actually deactivate
  the running session profile anyway.
- **`UnloadKeyboardLayout` alone**: per-process, doesn't remove the profile
  from the session-wide language bar. Insufficient.
- **Killing/restarting `ctfmon.exe`**: a popular forum recipe and exactly the
  kind of "100+1 tip from the web" the spec warns against. Reboot-class side
  effects, no guarantee of success. Rejected per Principle III.

## R5. Preview / dry-run mode

**Decision**: A `--dry-run` (alias `--preview`) command-line flag. When set,
the tool performs all read operations (R2, R3) and computes the planned
changes, but skips every call in R4. The report still lists the actions
that *would* be taken, prefixed with `(dry-run)`.

**Rationale**: Smallest possible surface to satisfy User Story 2 / FR-010
without introducing a separate code path.

**Alternatives considered**:
- **A separate "preview" subcommand**: adds CLI surface area for no benefit
  in a single-purpose tool.

## R6. Reporting and exit codes

**Decision**: Plain UTF-8 text on stdout, four sections in fixed order
(persisted, session, actions, result). Errors go to stderr. Exit codes:
- `0` = success (including no-op)
- `1` = generic failure (unexpected exception)
- `2` = unsupported platform / required API missing (FR-014)
- `3` = empty persisted set; refused to reconcile (edge case)

**Rationale**: Predictable for both humans (User Story 3) and any future
caller that might wrap the binary. Matches FR-011 and FR-012.

**Alternatives considered**:
- **JSON output by default**: harder for the troubleshooting user to read
  at a glance. Could be added later behind `--json` if needed; not v1.

## R7. Testing strategy

**Decision**:
- **Unit tests** (xUnit) for the pure reconciliation logic: given a
  "persisted set" and an "actual set", compute the correct add/remove plan,
  including the edge cases (empty persisted → refuse; foreground layout in
  removal set → switch first; no-op → no actions).
- **Manual integration test** documented in `quickstart.md`: reproduce the
  bug via an RDP session and verify the tool fixes it. Constitution requires
  this to gate every release.
- **No automated end-to-end test against real Windows input APIs in v1**:
  the harness cost is high, the surface is one binary, and the constitution
  explicitly marks this kind of e2e test as optional.

**Rationale**: Maximises the value of cheap unit tests on the only non-trivial
logic in the program (the diff/plan computation), while keeping the I/O layer
thin enough to verify by hand. Aligns with Principle I (Simplicity) and the
constitution's Development Workflow section.

**Alternatives considered**:
- **Mocking the Windows API surface for full automated e2e**: large mock
  effort that wouldn't catch the actual TSF behaviour we care about, which
  is exactly what Principle V warns against.

## R8. Distribution

**Decision**: A single `.exe` produced by `dotnet publish` and committed
nowhere. CI is out of scope for v1; the build is reproduced locally with one
command documented in `quickstart.md`.

**Rationale**: Lowest-friction path to "user can download one file and run
it". No installer, no auto-update, no service registration — directly
matches FR-015.

**Alternatives considered**:
- **MSI installer**: violates "no installer" constraint and adds elevation.
- **GitHub Releases automation**: nice-to-have, but out of v1 scope.
