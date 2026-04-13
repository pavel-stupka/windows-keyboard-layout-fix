<!--
SYNC IMPACT REPORT
==================
Version change: (template) → 1.0.0
Bump rationale: Initial ratification — first concrete constitution derived from
SPECIFICATION.md (Windows keyboard layout repair utility). All template
placeholders resolved.

Modified principles:
- [PRINCIPLE_1_NAME] → I. Single Purpose & Simplicity
- [PRINCIPLE_2_NAME] → II. User Configuration Is the Source of Truth
- [PRINCIPLE_3_NAME] → III. Safe & Reversible Operations
- [PRINCIPLE_4_NAME] → IV. Native Windows Integration
- [PRINCIPLE_5_NAME] → V. Observability & Diagnosability

Added sections:
- Technical Constraints & Platform Requirements
- Development Workflow & Quality Gates
- Governance

Removed sections: none

Templates requiring updates:
- ✅ .specify/templates/plan-template.md — Constitution Check gate references
  apply generically; no edits required (gate text is determined at plan time).
- ✅ .specify/templates/spec-template.md — no constitution-driven mandatory
  sections added/removed; no edits required.
- ✅ .specify/templates/tasks-template.md — task categories already cover
  setup/foundational/stories/polish; no edits required.
- ⚠ .specify/templates/commands/*.md — directory not present in repo; no action.
- ✅ README.md — not present; no action.

Deferred / TODO items:
- None. Ratification date set to today (2026-04-10) as the initial adoption.
-->

# Windows Keyboard Layout Fixer Constitution

## Core Principles

### I. Single Purpose & Simplicity

The project is a small Windows utility with one job: ensure that the set of
active keyboard layouts in the current Windows session matches exactly the set
of layouts the user has configured in Windows language settings — nothing more.
The codebase MUST stay minimal and free of unrelated features. YAGNI applies:
no general-purpose "keyboard manager", no GUI shells, no configuration UIs, no
plugin systems. Phase 2 (resident watcher) is explicitly out of scope for v1
and MUST NOT influence v1 architectural decisions beyond leaving a clean seam.

**Rationale**: The motivating problem is narrow and well-defined. Scope creep
is the primary risk for utility tools and would defeat the goal of a tiny,
trustworthy fixer.

### II. User Configuration Is the Source of Truth

The persisted Windows language/keyboard configuration (the per-user settings
visible in Windows Settings) is the ONLY authoritative definition of which
layouts may be present. The utility MUST reconcile the current session toward
that authoritative state and MUST NOT introduce, infer, or "guess" additional
layouts. The utility MUST NOT modify the persisted configuration itself; it
only adjusts the live session to match what is already persisted.

**Rationale**: The bug being fixed is precisely that Windows diverges the live
session from the user's stored preferences. Treating stored settings as
authoritative is the entire purpose of the tool.

### III. Safe & Reversible Operations

Every change the utility performs on the live session MUST be safe under the
following invariants:
- It MUST never remove or alter persisted user settings.
- It MUST never leave the session with zero usable input layouts.
- On any failure during reconciliation, it MUST leave the session in a state
  no worse than it found (best-effort rollback or fail-closed).
- It MUST be re-runnable (idempotent): running it twice in a row produces the
  same result as running it once.
- It MUST NOT require destructive registry edits when an official API path is
  available (see Principle IV).

**Rationale**: Users reach for this tool because Windows already misbehaves
with their keyboard. The fixer must be strictly safer than the bug it
addresses; a utility that can lock the user out of typing is unacceptable.

### IV. Native Windows Integration

The utility MUST use supported Windows APIs (input/locale management, e.g. the
documented Text Services Framework / input-method APIs) as the primary
mechanism for inspecting and adjusting session layouts. Registry manipulation
is permitted ONLY when no supported API exists for the required operation, and
any such use MUST be documented in code with the reason and the exact key
touched. The utility MUST run on supported Windows desktop versions without
requiring third-party runtimes beyond what the chosen implementation language
already needs.

**Rationale**: The web is full of brittle registry hacks for this exact
problem and "nothing works properly" (per SPECIFICATION.md). Sticking to
official APIs is what differentiates this tool from those failed attempts.

### V. Observability & Diagnosability

The utility MUST clearly communicate what it did and why, suitable for a user
who is troubleshooting a keyboard problem under stress:
- It MUST report, in human-readable form, the persisted layouts it read, the
  session layouts it observed, and the actions it took (added/removed/none).
- It MUST return a non-zero exit code on failure and zero on success (including
  the no-op case where nothing needed to change).
- It SHOULD support a dry-run / preview mode that performs detection and
  reporting without mutating session state.
- Errors MUST identify the failing operation and the underlying Windows error
  where applicable.

**Rationale**: A silent fixer is indistinguishable from a broken fixer,
especially for a bug whose symptoms are intermittent and session-scoped.

## Technical Constraints & Platform Requirements

- **Target platform**: Windows desktop (Windows 10 and Windows 11, including
  Remote Desktop sessions, since RDP is the primary trigger for the bug).
- **Privilege model**: The utility MUST operate within the current user's
  session and MUST NOT require Administrator elevation for its v1 use case.
  If a future feature genuinely needs elevation, that requirement MUST be
  justified in the relevant plan's Complexity Tracking section.
- **Distribution**: A single self-contained executable runnable by
  double-click or from a terminal. No installer is required for v1.
- **Dependencies**: Minimize third-party dependencies. Each added dependency
  MUST be justified by a concrete need documented in the plan.
- **Out of scope for v1**: resident/background watcher mode, GUI windows,
  system tray integration, multi-user/system-wide reconciliation, modifying
  persisted language settings, hotkeys, telemetry. These are explicitly
  reserved for a possible Phase 2 and MUST NOT leak into v1 scope.

## Development Workflow & Quality Gates

- **Specification-first**: Behavior changes MUST originate from a feature spec
  under `specs/` produced via the speckit workflow before implementation.
- **Plan gate**: Every implementation plan MUST include a Constitution Check
  that explicitly evaluates the five Core Principles above and either passes
  them or records a justified exception in Complexity Tracking.
- **Manual verification is mandatory**: Because the bug being fixed is
  environmental (RDP-induced session drift), every release MUST be manually
  verified by reproducing the bug (e.g., via an RDP session that injects an
  unwanted layout) and confirming the utility restores the expected state.
- **Automated tests**: Unit tests for pure logic (e.g., diffing desired vs.
  actual layout sets) are REQUIRED. End-to-end tests that touch real Windows
  input APIs are OPTIONAL and only added when they can run reliably.
- **Reviews**: Any change that touches session-mutating code paths MUST be
  reviewed against Principle III (Safe & Reversible Operations) before merge.

## Governance

This constitution supersedes ad-hoc conventions for this project. Amendments
follow this procedure:

1. Propose the change in a commit or PR that updates this file and bumps the
   version per the semantic-versioning rules below.
2. Update any dependent templates and runtime guidance files in the same
   change set, or list them as follow-ups in the Sync Impact Report.
3. Merge requires confirmation that all five Core Principles remain coherent
   and that no principle is silently weakened.

**Versioning policy** (applies to `Version` line below):
- **MAJOR**: A principle is removed, redefined in a backward-incompatible way,
  or governance rules change in a way that invalidates prior plans.
- **MINOR**: A new principle or section is added, or an existing principle is
  materially expanded.
- **PATCH**: Wording, clarification, typo fixes, or non-semantic refinements.

**Compliance review**: Each plan's Constitution Check is the routine
compliance gate. Any violation discovered after merge MUST be tracked as a
follow-up task and resolved before the next release.

**Version**: 1.0.0 | **Ratified**: 2026-04-10 | **Last Amended**: 2026-04-10
