# Feature Specification: Fix Windows Session Keyboard Layouts

**Feature Branch**: `001-fix-keyboard-layouts`
**Created**: 2026-04-10
**Status**: Draft
**Input**: User description: "Naplánuj vývoj aplikace tak, aby výsledkem byl spustitelný utility prográmek (exe / bat / něco, co provede požadovaný úkol)" — combined with the project brief in `SPECIFICATION.md`: a Windows utility that, when launched, removes any keyboard layouts Windows added to the *current session* that are not part of the user's *persisted* keyboard configuration.

## User Scenarios & Testing *(mandatory)*

### User Story 1 — One-shot session cleanup (Priority: P1) 🎯 MVP

A Windows user has just connected to their machine via Remote Desktop. After
logging in they notice that, beside their normal layouts (e.g. Czech QWERTY +
English), Windows has silently injected an unwanted layout for the session
(e.g. Czech QWERTZ or US). The persisted Windows language settings are
unchanged and still show only the layouts the user configured. The user
launches the utility (by double-clicking it or running it from a terminal).
The utility inspects the persisted configuration and the live session, removes
every session-only layout that is not in the persisted set, and confirms what
it did. The user can immediately switch between the remaining layouts as
expected.

**Why this priority**: This is the entire reason the project exists. Without
this story, the tool delivers no value. It is also a complete MVP — a single
manual run is exactly what the SPECIFICATION.md brief describes for v1.

**Independent Test**: Reproduce the bug by establishing an RDP session that
causes Windows to inject an extra layout, then run the utility once and
verify that (a) the unwanted layout is gone from the session's layout
indicator, (b) the persisted language settings are unchanged, and (c) the
utility reported success.

**Acceptance Scenarios**:

1. **Given** the persisted configuration lists exactly the user's intended layouts AND the current session contains those layouts plus one or more extra layouts injected by Windows, **When** the user runs the utility, **Then** every extra session-only layout is removed, the intended layouts remain active, and the utility reports which layouts it removed.
2. **Given** the persisted configuration and the current session already match exactly, **When** the user runs the utility, **Then** the utility makes no changes and reports that nothing needed to be done, exiting successfully.
3. **Given** the user runs the utility a second time immediately after a successful first run, **When** the second run completes, **Then** the result is identical to the first run's end state (the operation is idempotent).
4. **Given** the persisted configuration is unchanged, **When** the utility finishes (whether it changed anything or not), **Then** the persisted Windows language settings are byte-for-byte identical to what they were before the run.

---

### User Story 2 — Preview without changes (Priority: P2)

Before letting the utility touch their session, a cautious user wants to see
what it *would* do. They run the utility in a preview mode that performs the
same inspection and reporting but makes no modifications. They read the
report, decide they are comfortable, and then run the utility for real.

**Why this priority**: Builds trust in a tool that mutates input handling — a
component users are nervous about losing. It also doubles as a diagnostic for
users who are not yet sure whether they actually have the bug.

**Independent Test**: With a session that contains an extra injected layout,
run the utility in preview mode and verify that (a) the report lists the
extra layout as "would remove", (b) the live session is unchanged afterward,
and (c) the exit code reflects success.

**Acceptance Scenarios**:

1. **Given** a session with one or more extra layouts, **When** the user runs the utility in preview mode, **Then** the report names the layouts that would be removed AND the session's active layouts are unchanged.
2. **Given** a session that already matches the persisted configuration, **When** the user runs the utility in preview mode, **Then** the report states that no changes are needed.

---

### User Story 3 — Clear diagnostic output (Priority: P3)

A user who is troubleshooting a flaky keyboard wants to capture what the
utility saw and did so they can paste it into a support thread or save it
for later. The utility prints a human-readable report covering: the
layouts in the persisted configuration, the layouts currently in the session,
the actions taken (or that would be taken in preview mode), and the final
result.

**Why this priority**: Important for diagnosability per the project
constitution, but the tool is still useful with terse output. Bundled here so
the reporting requirements have an explicit acceptance test.

**Independent Test**: Run the utility (in any mode) and verify that the
output contains all four sections — persisted layouts, session layouts,
actions, and result — and is readable without specialised tools.

**Acceptance Scenarios**:

1. **Given** any run of the utility, **When** the run completes, **Then** the output contains the persisted layout set, the observed session layout set, the list of actions taken (possibly empty), and a final success/failure line.
2. **Given** the utility encounters an error while inspecting or modifying the session, **When** the run ends, **Then** the output identifies which step failed and the utility exits with a non-zero code.

---

### Edge Cases

- The persisted configuration contains zero keyboard layouts (corrupt or unusual setup): the utility MUST refuse to "reconcile" toward an empty set, leave the session untouched, report the situation, and exit with a non-zero code. Removing all layouts would lock the user out of typing.
- The session is missing a layout that *is* in the persisted configuration: the utility's job is removal, not addition; it MUST report the discrepancy but MUST NOT attempt to add layouts in v1. (Adding is reserved for a possible later iteration; the immediate bug is that Windows adds *too many*, not too few.)
- The currently active (foreground) layout is one of the layouts that would be removed: the utility MUST switch the session to a layout that will remain (a persisted one) before removing the unwanted one, so the user is never left with no active layout.
- The user runs the utility twice in quick succession: the second run finds nothing to do and exits cleanly (idempotency).
- The user runs the utility on a system whose Windows version does not expose the needed input-locale management: the utility MUST detect this, report it clearly, make no changes, and exit non-zero.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: The utility MUST be distributable and runnable as a single self-contained artifact that the user can launch by double-clicking it or invoking it from a terminal, without installing anything else.
- **FR-002**: The utility MUST read the user's persisted Windows keyboard layout configuration and treat it as the authoritative desired state.
- **FR-003**: The utility MUST enumerate the keyboard layouts currently active in the user's session.
- **FR-004**: The utility MUST compute the difference between the session layouts and the persisted layouts and identify every layout present in the session but absent from the persisted configuration ("session-only layouts").
- **FR-005**: The utility MUST remove every session-only layout from the current session, and MUST NOT remove or alter any layout that is part of the persisted configuration.
- **FR-006**: The utility MUST NOT modify the persisted Windows language/keyboard configuration in any way.
- **FR-007**: The utility MUST be idempotent: running it again immediately after a successful run MUST produce the same end state with no further changes.
- **FR-008**: The utility MUST never leave the session with zero usable input layouts. If a planned change would result in zero active layouts, it MUST abort that change and report the situation.
- **FR-009**: If the layout currently active in the foreground would be removed, the utility MUST first switch the session to a layout that will remain (one from the persisted set).
- **FR-010**: The utility MUST support a preview / dry-run mode that performs detection and reporting but makes no modifications to the session.
- **FR-011**: The utility MUST print a human-readable report that includes (a) the persisted layout set, (b) the observed session layout set, (c) the list of actions taken or that would be taken, and (d) a final success/failure line.
- **FR-012**: The utility MUST exit with a zero exit code on success (including the no-op case) and a non-zero exit code on any failure.
- **FR-013**: The utility MUST run under the current user's session privileges and MUST NOT require Administrator elevation for v1.
- **FR-014**: The utility MUST detect when the underlying platform does not support the operations it needs, report that clearly, make no changes, and exit non-zero.
- **FR-015**: The utility MUST NOT install background services, register at startup, or persist any state of its own; v1 is strictly a one-shot tool the user invokes manually.

### Key Entities

- **Persisted Layout Set**: The set of keyboard layouts the user has configured in Windows language settings. Authoritative. Read-only from the utility's perspective.
- **Session Layout Set**: The set of keyboard layouts currently active in the user's logged-in session. May drift from the persisted set due to the Windows bug this utility addresses. Mutable by the utility (removals only).
- **Reconciliation Report**: The structured human-readable output describing what the utility observed and did (or would do) during a single run.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: After a single run of the utility on a session that has been polluted by Windows-injected layouts, 100% of the session-only layouts are gone and 100% of the user's intended (persisted) layouts remain — verified by inspecting the session's layout indicator and the persisted settings.
- **SC-002**: A user who has the bug can resolve it end-to-end (download/launch → run → confirmed clean session) in under 30 seconds, without rebooting and without opening Windows Settings.
- **SC-003**: Running the utility a second time immediately after a successful first run produces no further changes in 100% of cases (idempotency).
- **SC-004**: Across all runs of the utility, 0 cases occur in which the persisted Windows language/keyboard settings are modified.
- **SC-005**: Across all runs of the utility, 0 cases occur in which the user is left with zero active keyboard layouts.
- **SC-006**: A user reading the utility's report can, without prior knowledge of the tool, correctly state which layouts were present, which were removed, and whether the run succeeded — measured by informal review of the output.

## Assumptions

- The utility targets the currently logged-in interactive user's session (including sessions established over Remote Desktop). System-wide reconciliation across other users' sessions is out of scope.
- Windows exposes a supported way for an unprivileged user-mode program to enumerate and remove keyboard layouts from its own session. (If this turns out to be false on some target Windows version, that platform is reported as unsupported per FR-014 rather than worked around with destructive techniques.)
- A textual report printed to a terminal / console window is sufficient for v1; no graphical user interface is required.
- The user knows how to launch a downloaded executable on their own machine (double-click or run from a terminal). No installer, no auto-update, no shortcut creation.
- A resident / background watcher mode that re-runs the cleanup automatically when Windows re-injects a layout is **out of scope for v1** and is reserved for a possible later iteration, per the project constitution.
- The utility's job is removal of unwanted session-only layouts. Adding missing layouts back into the session (the inverse problem) is **not** in scope for v1.
- The user interface language of the report is the developer's choice for v1 (English or Czech); localisation is not a requirement.
