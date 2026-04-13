# Feature Specification: Background Watcher, Autostart, and Slim Binary

**Feature Branch**: `003-background-watcher`
**Created**: 2026-04-13
**Status**: Draft
**Input**: User description: "Extend KbFix so it can run as a persistent background process that immediately re-applies the keyboard-layout fix whenever Windows changes the session's layouts (notably after Remote Desktop or fast-user-switching injects extras). Add `--install` to register the watcher to auto-start at Windows login and launch it immediately, `--uninstall` to stop any running watcher and remove autostart, and `--status` to report whether the watcher is running and whether autostart is registered. Also shrink the published binary, which is currently over 80 MB."

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Install and forget (Priority: P1)

A user who is tired of re-running `kbfix` after every RDP session opens a terminal, runs `kbfix --install`, and from that moment onward never sees a stray keyboard layout again, even across reboots, log-offs, and RDP reconnects. The command registers autostart at Windows login for the current user and launches the watcher immediately so the fix takes effect without logging out.

**Why this priority**: This is the whole point of the feature. If this works, the tool delivers "set it once, forget it" value and makes the manual one-shot mode nearly unnecessary for end users. Everything else (status, uninstall, size) is in service of this flow.

**Independent Test**: Run `kbfix --install` in a fresh user session. Confirm the watcher is now running. Open an RDP connection that normally injects an extra layout, disconnect, and verify the extra layout disappears within a few seconds without any manual command. Reboot; confirm the watcher is running again immediately after login.

**Acceptance Scenarios**:

1. **Given** the user has never installed the watcher, **When** they run `kbfix --install`, **Then** the command reports success, the watcher starts running in the background in that same user session, and an autostart entry is registered for the current user.
2. **Given** the watcher is installed and running, **When** the user logs out and logs back in, **Then** the watcher is running again shortly after the Windows shell is ready, without any user action.
3. **Given** the watcher is installed and running, **When** Windows injects one or more extra keyboard layouts into the session (for example at the end of an RDP session or on a fast-user-switch), **Then** the extras are removed within a few seconds and the active layout returns to one that exists in the user's persisted keyboard configuration.
4. **Given** the watcher is already installed, **When** the user runs `kbfix --install` a second time, **Then** the command is idempotent: it does not create duplicate autostart entries, does not spawn a second watcher, and reports the existing installation as "already installed."
5. **Given** the command is run without administrator privileges, **When** `kbfix --install` executes, **Then** it succeeds; elevation is never requested.

---

### User Story 2 — Clean uninstall (Priority: P1)

A user wants to remove the background watcher entirely — either because they are troubleshooting, because they want to test a new version, or because they simply do not want the feature anymore. They run `kbfix --uninstall` and the tool stops any running watcher belonging to this user and removes the autostart entry, leaving no trace. This is P1 because an install feature without a matching uninstall is a footgun.

**Independent Test**: Starting from an installed state, run `kbfix --uninstall`. Confirm the watcher process is no longer running, the autostart entry is gone, a reboot does not bring the watcher back, and `kbfix --status` reports everything as "not present."

**Acceptance Scenarios**:

1. **Given** the watcher is installed and running, **When** the user runs `kbfix --uninstall`, **Then** the watcher process for the current user stops within a few seconds, the autostart entry is removed, and the command reports what it did.
2. **Given** autostart is registered but no watcher is currently running (e.g. crashed or killed), **When** the user runs `kbfix --uninstall`, **Then** the command removes the autostart entry anyway and reports success.
3. **Given** nothing is installed, **When** the user runs `kbfix --uninstall`, **Then** the command reports "nothing to do" with an exit code that does not indicate an error.
4. **Given** the command is run without administrator privileges, **When** `kbfix --uninstall` executes, **Then** it succeeds without elevation.

---

### User Story 3 — Inspect current state (Priority: P2)

A user runs `kbfix --status` to see, at a glance, whether the watcher is installed and running for the current user. The output answers two questions clearly and without jargon: "Is the watcher running right now?" and "Will Windows start it automatically next time I log in?" This is P2 because it is not strictly required for the feature to work, but it is essential for troubleshooting and user confidence.

**Independent Test**: Run `kbfix --status` in each of the states produced by the other stories (nothing installed, installed and running, installed but not running, autostart without running process) and verify the output is unambiguous and matches reality.

**Acceptance Scenarios**:

1. **Given** the watcher is installed and running, **When** the user runs `kbfix --status`, **Then** the output shows both "watcher: running" and "autostart: registered," plus enough identifying information (process identifier or similar) to tell the user which process is the watcher.
2. **Given** autostart is registered but no watcher process is running, **When** the user runs `kbfix --status`, **Then** the output shows "watcher: not running" and "autostart: registered," and the exit code distinguishes this state from "fully installed and healthy."
3. **Given** nothing is installed, **When** the user runs `kbfix --status`, **Then** the output clearly says so.
4. **Given** the command is run without administrator privileges, **When** `kbfix --status` executes, **Then** it succeeds and produces accurate output.

---

### User Story 4 — Smaller download (Priority: P2)

A user — or someone distributing the tool to a small team — notices the published binary is over 80 MB and complains that it is absurdly large for what the tool does. After this feature, a fresh publish produces a dramatically smaller single-file executable that still works on a clean Windows 11 machine without any pre-installed runtime, and still does everything prior versions did (one-shot fix, watcher, install, uninstall, status).

**Why this priority**: Size is an ergonomic and distribution concern, not a functional one, so it is P2. It is grouped with this feature because the new auto-start flow means the binary will sit in users' profiles long-term, so a smaller footprint matters more than before.

**Independent Test**: Run the release build and measure the size of the produced executable. Copy that executable to a freshly-installed Windows 11 machine with no developer tools, and run all subcommands (`kbfix` one-shot, `--install`, `--uninstall`, `--status`) to confirm they still work.

**Acceptance Scenarios**:

1. **Given** a release publish of the updated project, **When** the build completes, **Then** the produced executable is substantially smaller than the current 80+ MB baseline and meets the size target defined in Success Criteria.
2. **Given** the shrunk executable is copied to a clean Windows 11 machine with no .NET runtime or developer tools, **When** any supported subcommand is run, **Then** it works correctly with no "runtime missing" errors and no extra downloads.
3. **Given** the shrunk executable, **When** the existing one-shot fix is run against a session with stray layouts, **Then** it produces the same result as before the size reduction (no regressions in core behavior).

---

### Edge Cases

- **Multiple user sessions on one machine** (fast-user-switching, RDP host with several users): each installed user must have an independent watcher that only touches their own session; installing or uninstalling for one user must not affect the other.
- **Stale state from a prior version or crashed watcher**: `--install` must cope with an orphaned autostart entry pointing at an old binary path, and `--uninstall` must not hang waiting on a process that has already died.
- **Binary moved after install**: if the user relocates `kbfix.exe` after installing, the registered autostart entry will point at a stale path. The spec must define the expected behavior (either re-install required, or autostart entry is re-validated on next `--status`).
- **Watcher cannot reach the user's persisted configuration** (e.g. HKCU hive not yet loaded at very early login): the watcher must not crash-loop; it must back off and retry until the user state is available.
- **User has zero persisted layouts** (theoretical edge case): the watcher must not unload the currently-active layout into a state where no layout at all is active.
- **Layout flapping**: if something else on the system keeps re-injecting a layout immediately after the watcher removes it (e.g. a misbehaving IME), the watcher must not burn CPU in a tight loop; it must rate-limit its reactions and, after a threshold, log and pause instead of fighting forever.
- **Running `--install` as Administrator**: the command must still install for the *current interactive user*, not for SYSTEM, and must not rely on elevation.
- **Running the watcher on a machine with no RDP ever used**: it must be quiet, cheap, and not interfere with normal interactive layout switching (the user's `Alt+Shift` / Win+Space presses must still work).
- **Removing the binary without running `--uninstall`**: a later `--status` (run from a new copy of the binary) must still detect that autostart is registered under a stale path and offer a clean path forward.

## Requirements *(mandatory)*

### Functional Requirements

**Background watcher mode**

- **FR-001**: The tool MUST provide a mode in which it runs continuously in the user's interactive desktop session, monitors the set of keyboard layouts currently loaded in that session, and automatically applies the existing reconciliation logic whenever that set diverges from the user's persisted keyboard configuration.
- **FR-002**: The watcher MUST react to layout changes within a few seconds of their appearing in the session, including changes it did not initiate (such as layouts injected by Remote Desktop, fast-user-switching, or third-party input software).
- **FR-003**: The watcher MUST run entirely within the current user's Windows session with the user's own privileges. It MUST NOT require administrator rights and MUST NOT rely on any mechanism that would run it outside the user's interactive session.
- **FR-004**: The watcher MUST be a single-instance-per-user process. Attempts to start a second watcher for the same user MUST detect the existing one and exit cleanly instead of racing it.
- **FR-005**: The watcher MUST consume negligible resources when idle (no visible CPU or memory pressure during normal desktop use) and MUST NOT open any visible window, tray icon, or other UI element unless a future story explicitly adds one.
- **FR-006**: The watcher MUST be safe to run alongside the existing one-shot mode: running `kbfix` (one-shot) while the watcher is active MUST NOT corrupt state, crash either process, or produce spurious "layout removed / re-added" flapping.
- **FR-007**: The watcher MUST protect itself from pathological flapping: if it detects that the same unwanted layout is being re-injected more than a configurable number of times per minute, it MUST back off rather than enter a tight reconcile loop.
- **FR-008**: If the watcher encounters an unrecoverable error (for example, the user's persisted configuration is unreadable for longer than a short grace period), it MUST exit cleanly rather than crash-loop, in a way that `--status` can still report accurately afterwards.

**Install / uninstall / status commands**

- **FR-009**: The tool MUST accept a command-line switch (conventionally `--install`) that, in a single invocation and without elevation, registers the tool to start automatically when the current user next logs into Windows, AND launches the watcher in the current session so the user sees the effect immediately.
- **FR-010**: The tool MUST accept a command-line switch (conventionally `--uninstall`) that, in a single invocation and without elevation, stops any running watcher belonging to the current user AND removes the autostart registration for the current user, and reports what it did.
- **FR-011**: The tool MUST accept a command-line switch (conventionally `--status`) that reports, for the current user, whether a watcher process is currently running and whether an autostart entry is currently registered, in a format that is readable by a human at a glance.
- **FR-012**: `--install`, `--uninstall`, and `--status` MUST be idempotent: running any of them repeatedly MUST not break the installed state, produce duplicate autostart entries, or spawn duplicate watcher processes.
- **FR-013**: `--install` MUST use a path to the binary that remains valid across reboots for the current user. If the invoking binary is in a location that is not suitable for a stable autostart entry, the command MUST either refuse with a clear explanation or copy itself to a stable per-user location — the chosen behavior MUST be documented for the user.
- **FR-014**: `--install` launching the watcher MUST detach the watcher from the invoking console so that closing the terminal, or the `--install` command returning, does NOT terminate the watcher.
- **FR-015**: `--uninstall` MUST stop the watcher cooperatively when possible (give it a short moment to shut down) and only fall back to a forced termination if cooperative shutdown does not complete in a bounded time.
- **FR-016**: `--status` MUST be able to distinguish at least these states and report them unambiguously: (a) nothing installed, (b) autostart registered and watcher running, (c) autostart registered but watcher not running, (d) watcher running without autostart registered.
- **FR-017**: The exit codes of `--install`, `--uninstall`, and `--status` MUST be consistent and documented, so that scripts can check them. `--status` in particular MUST use different exit codes for "fully installed and healthy" vs. "partially installed or degraded" vs. "not installed."

**Compatibility with existing one-shot mode**

- **FR-018**: The existing one-shot invocation (running `kbfix` with no new switches) MUST continue to work unchanged: same behavior, same output, same exit codes, same absence of side-effects outside the current session.
- **FR-019**: Adding the new subcommands MUST NOT require admin rights, new NuGet dependencies outside the existing set, or additional runtime prerequisites on the target machine beyond what the one-shot mode already requires.

**Binary size**

- **FR-020**: A release publish of the tool MUST produce a single self-contained executable that runs on a clean supported Windows machine with no pre-installed .NET runtime and no other external dependencies.
- **FR-021**: The published binary size MUST meet the target defined in Success Criteria SC-006.
- **FR-022**: The size reduction MUST NOT break any existing functional requirement from prior features, including the one-shot fix, the COM-interop code paths the tool uses to interrogate Windows input services, and all unit tests in the existing test suite.
- **FR-023**: `build.cmd` (the existing release entry point) MUST continue to produce the published binary, and any new build knobs needed for size reduction MUST be part of the standard release build — users MUST NOT have to pass extra flags to get the small binary.

### Key Entities

- **Watcher process**: The long-running background instance of `kbfix` that observes the session and triggers reconciliation. Identified per-user; at most one per user session.
- **Autostart registration**: The per-user Windows mechanism that causes the watcher to start automatically at login. It has two observable properties: "present / absent" and, if present, "which binary path it points at."
- **Installed state**: The pair (autostart registration, watcher process running). `--status` reports on this pair.
- **Session layout set**: The set of keyboard layouts currently loaded in the user's interactive session — the state the watcher observes and compares against the user's persisted configuration. (Already modeled by prior features; referenced here.)
- **Persisted keyboard configuration**: The user's intended layouts, as defined in their Windows language settings. (Already modeled by prior features; referenced here.)

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: After running the install command once, the user can survive at least ten consecutive RDP disconnect/reconnect cycles without ever seeing a stray keyboard layout for more than a few seconds.
- **SC-002**: After Windows login completes, the watcher becomes effective within ten seconds on a typical machine.
- **SC-003**: When the watcher is idle (no layout changes), its CPU usage is indistinguishable from zero on a one-minute average, and its resident memory stays within a small fixed budget that a user would not notice.
- **SC-004**: The install and uninstall commands each complete in under five seconds on a typical machine and never prompt the user for administrator approval.
- **SC-005**: The status command produces human-readable output in under one second.
- **SC-006**: The published release binary is at most 20 MB, and ideally under 15 MB. (The current baseline is over 80 MB, so this is roughly a 4–6× reduction.)
- **SC-007**: A user who runs `--install` once never has to run any keyboard-layout command manually again for the lifetime of their Windows install, assuming no new features are requested.
- **SC-008**: All unit tests from prior features continue to pass after the feature lands, and new tests cover the install / uninstall / status / watcher lifecycle.
- **SC-009**: On a clean Windows 11 machine with no developer tools installed, copying the released binary and running any supported subcommand produces the expected result with no runtime errors.

## Assumptions

These are the design decisions derived from the three independent analyses that fed into this spec. They are recorded here (rather than as strict requirements) so that the planning phase can revisit them if something concrete changes, but they are the current intended direction.

- **The watcher runs as a normal user-session console process, not as a Windows Service.** Windows Services run in the isolated Session 0 and cannot observe or modify the interactive user's keyboard-layout state, which is exactly the state this tool needs to fix. A user-session process is the only hosting model that can actually do the job.
- **Change detection is built on periodic polling of the session's loaded layouts, plus the existing reconciliation logic.** The alternatives that were considered and rejected:
  - Watching `HKCU\Keyboard Layout\Preload` with `RegNotifyChangeKeyValue` — rejected because RDP typically injects layouts directly into the session without touching that registry key, so the signal would miss the primary use case.
  - Listening for `WM_INPUTLANGCHANGE` on a hidden window — useful as a secondary "wake up" signal for user-initiated switches, but insufficient on its own because it does not fire reliably for system-injected layouts.
  - TSF notification sinks (`ITfLanguageProfileNotifySink` and friends) — rejected as primary mechanism for this use case because they are designed around modern input-method activation rather than legacy Win32 layout loads, and add significant complexity for limited additional coverage.
  - Polling is reliable against RDP injection, has negligible CPU cost at a several-second interval, and reuses the reconciliation code that is already unit-tested. A hidden message-pump window is a reasonable optional enhancement as a low-latency wake-up hint.
- **Autostart is registered via a per-user mechanism that does not require administrator privileges.** The two viable candidates are a Task Scheduler per-user "at logon" task and the per-user `Run` registry key. Both are acceptable; planning will pick one primary and optionally the other as a fallback. `%APPDATA%\...\Startup\` shortcuts were considered but are more fragile and timing-sensitive.
- **Single-instance enforcement and watcher discovery use a named synchronization primitive scoped to the user's session**, with a process-enumeration fallback for the case where a crashed watcher failed to release it. This is what `--status` interrogates, and what a second `--install` uses to decide "already running."
- **`--install` detaches the new watcher from the invoking console** by launching it with flags that give it no inherited console window, no redirected standard streams, and no parent-wait, so the installer command can exit immediately while the watcher continues.
- **Binary size reduction relies on trimming and size-tuning flags of the existing .NET 8 self-contained single-file publish**, not on Native AOT. Native AOT was considered and rejected for this iteration because the tool's COM-interop surface (input-services interfaces) is not a low-risk target for .NET 8 AOT, and the spec targets are achievable comfortably with trimmed self-contained publishing. If planning later revisits AOT, the size target could tighten further.
- **No new third-party NuGet dependencies are introduced.** Everything the feature needs is available in the .NET 8 BCL plus the existing Win32 / COM interop already in the codebase.
- **Nothing in this feature changes the tool's write surface outside of the per-user autostart registration**, the watcher's own single-instance primitive, and (optionally) a per-user stable copy of the binary if `--install` decides to stage one. In particular, the tool continues to write nothing to `HKCU\Keyboard Layout\Preload` or any other persisted keyboard configuration key.
- **Target OS remains Windows 10 1809 and later**, matching the existing project's `SupportedOSPlatformVersion`. No earlier Windows versions are supported by this feature.
- **"Current user" means the interactive user who invoked the command**, even if the command was launched from an elevated prompt. `--install` run from an elevated terminal installs for the interactive user, not for SYSTEM.
