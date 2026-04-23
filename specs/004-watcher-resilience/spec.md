# Feature Specification: Watcher Resilience, Observability, and Self-Healing

**Feature Branch**: `004-watcher-resilience`
**Created**: 2026-04-23
**Status**: Draft
**Input**: User description: "Zvýšit stabilitu chování aplikace spuštěné na pozadí. Stane se, že se změní layout klávesnice, ale aplikace nezareaguje — v procesech Windows ani ve `status.cmd` není. Podezření: neproběhl autostart po restartu PC, nebo aplikace spadla, nebo se stalo něco jiného. Cílem je hloubková analýza a úprava spouštění a rezidentního běhu tak, aby watcher po restartu PC skutečně běžel a okamžitě reagoval na změny layoutu, které neodpovídají lokálnímu (HKCU) nastavení."

## Problem Statement (background)

<!--
  Informal framing, not a requirement. Kept here so reviewers understand
  what the feature is reacting to. All testable content is below.
-->

Feature 003 delivered a per-user background watcher registered under
`HKCU\...\Run\KbFixWatcher`, a staged binary in `%LOCALAPPDATA%\KbFix\`,
and `--install` / `--uninstall` / `--status` commands. In practice the
user has observed that, after an unknown trigger (reboot, crash, system
event), the watcher is simply **not running** — `status` and Task Manager
both confirm its absence — and keyboard layouts therefore drift again
without being corrected. The user cannot tell from the current diagnostics
whether the autostart entry never fired, whether the watcher was started
and crashed, or whether something else terminated it. This feature
addresses that gap end-to-end: keep the watcher alive, explain itself
when it is not, and make autostart robust enough that "installed" really
means "running after every reboot."

## User Scenarios & Testing *(mandatory)*

### User Story 1 — Watcher survives reboots unconditionally (Priority: P1)

A user runs `kbfix --install` once and from that point onward, the
watcher is running on their desktop within a bounded time after **every**
Windows sign-in — including cold boots, warm reboots, RDP
reconnects that establish a new interactive session, unlock after
screen lock, and power-on from sleep/hibernate. The user never has
to re-run `--install` or any manual command to "nudge" the watcher
back into existence. If the user opens `status.cmd` at any random
moment minutes after logging in, they see the watcher reported as
running.

**Why this priority**: This is the core symptom the user is reporting.
If the watcher is not running, feature 003 delivers zero value —
layouts drift and the user is back to running manual fixes. Everything
else in this feature (self-restart, diagnostics, logs) exists to make
this outcome trustworthy.

**Independent Test**: Starting from a clean install, reboot the machine
five times in a row in different modes (cold boot, restart, sleep+resume,
lock+unlock, sign-out+sign-in). After each sign-in, wait the declared
SC-001 window, then run `status.cmd`. Confirm watcher is reported as
running every single time. Also confirm the active layout is reconciled
against HKCU without any user intervention.

**Acceptance Scenarios**:

1. **Given** the watcher was installed in a prior session, **When** the
   user signs into Windows (after any form of reboot or sign-out), **Then**
   a watcher process belonging to that user is running within a short,
   bounded time after the interactive shell becomes responsive.
2. **Given** the user wakes the machine from sleep or hibernate and the
   same interactive session is restored, **When** the session comes back,
   **Then** either (a) the watcher that was running before sleep is still
   running, or (b) if Windows terminated it, a replacement watcher is
   running within a short, bounded time.
3. **Given** the user starts an RDP session to a machine where the user
   is already signed in, **When** the RDP session establishes, **Then**
   the watcher is running in the interactive session that the RDP client
   is attached to, and extra layouts that RDP injects are removed within
   the same window that feature 003 already guarantees.
4. **Given** the user has never previously installed the watcher, **When**
   the user runs `--install`, reboots, and does not run any further
   command, **Then** after the reboot the watcher is running unattended.

---

### User Story 2 — Watcher restarts itself after a crash (Priority: P1)

A user's watcher crashes mid-session for any reason — unhandled exception,
transient Win32/COM failure, the user's persisted configuration becoming
temporarily unreadable, the process being killed by another tool, or a
machine-wide event that stops background processes. Instead of the user
discovering hours later that layouts are drifting again, the watcher (or
a lightweight supervisor associated with it) brings a fresh watcher back
up within a short bounded time. The user does not have to run `--install`,
reboot, or do anything else. If a crash happens, the recovery happens
silently from the user's perspective, but the event is recorded so it is
discoverable on demand.

**Why this priority**: "Install and forget" (the P1 promise from spec 003)
is incompatible with a process that dies and stays dead until the next
sign-in. Without self-restart, the user has to either reboot or manually
re-run `--install` every time something goes wrong — exactly the fragility
this feature exists to eliminate.

**Independent Test**: With the watcher running, forcibly terminate the
watcher process (Task Manager "End task"). Do not run any recovery
command. Within the declared SC-002 window, confirm via `status.cmd`
that a watcher is running again. Repeat this three times in a row to
confirm the restart mechanism is itself durable and does not exhaust a
retry budget on the first try. Inspect the log and confirm the crash
and the subsequent restart are both recorded in a way a non-developer
can read.

**Acceptance Scenarios**:

1. **Given** the watcher is running, **When** the watcher process
   terminates unexpectedly (crash, external kill, OS-forced stop),
   **Then** a replacement watcher is running again within a short,
   bounded time, without any user action.
2. **Given** the watcher is running, **When** the user's persisted
   keyboard configuration is transiently unreadable and the watcher
   therefore exits (per feature 003's `ConfigUnrecoverable` path),
   **Then** after the configuration becomes readable again, a watcher
   is running again within a short, bounded time.
3. **Given** the self-restart mechanism has restarted the watcher, **When**
   the user runs `--status`, **Then** the output reflects the current
   running process (including its current identifier) — no stale PID is
   reported.
4. **Given** the watcher is restarting repeatedly and unsuccessfully in a
   tight cycle (pathological), **When** the retry rate exceeds a defined
   budget, **Then** the supervisor applies exponential backoff and, after
   a further threshold, stops retrying and records why, rather than
   burning the machine's CPU or filling the log indefinitely.

---

### User Story 3 — Diagnose why the watcher is not running (Priority: P1)

A user runs `status.cmd` and sees "watcher: not running." With this feature,
the same `status` output additionally tells the user enough to answer the
next question — **why** it is not running — without opening the log file
or reading developer documentation. The output distinguishes between
recognizable cases: autostart entry is missing; autostart entry is present
but pointing at a stale path; the watcher exited cleanly within the last
few minutes and a restart is pending; the watcher exited due to a
recognized error (config unreadable, refused, etc.); the watcher has
crashed repeatedly and the supervisor gave up; the machine was just
signed into and the grace period hasn't elapsed yet.

**Why this priority**: Even with self-restart, edge cases will produce
"not running" states. If the user cannot tell the cause, they cannot
decide whether to wait, to re-run `--install`, to investigate their
Windows configuration, or to file a bug. This is the difference between
a resilient system and a system that is silently broken.

**Independent Test**: Force each of the known "not running" states
(no autostart, stale-path autostart, recent clean exit, recent crash,
exceeded retry budget, first-sign-in grace window) and run
`status.cmd`. Confirm the output unambiguously identifies which state
the machine is in and, where applicable, what the user can do next.

**Acceptance Scenarios**:

1. **Given** the watcher has exited for a known reason in the last few
   minutes, **When** the user runs `--status`, **Then** the output names
   that reason in plain language (not a stack trace) and, where relevant,
   a suggested next step.
2. **Given** the supervisor has given up after exceeding its retry budget,
   **When** the user runs `--status`, **Then** the output clearly says so
   and tells the user how to re-arm the supervisor (for example, by
   re-running `--install` or a dedicated reset command).
3. **Given** the watcher is running but the supervisor itself is not
   registered for autostart, **When** the user runs `--status`, **Then**
   the output flags this as a degraded state (feature 003 already covered
   the symmetric case "running without autostart") and the exit code
   reflects the degraded state.
4. **Given** the log file exists, **When** the user runs `--status`,
   **Then** the output prints the log file's path so the user can open
   it without having to know the convention.

---

### User Story 4 — Verify autostart actually fires (Priority: P2)

A user who has just installed the watcher — or who is troubleshooting a
past silent failure — wants to prove that the autostart mechanism is
working without rebooting the machine. `--status` (or a dedicated
diagnostic command) reports the last time the autostart entry fired,
whether Windows is currently set to honor it, and whether anything on
the machine (Group Policy, third-party startup manager, antivirus,
"Startup apps" toggle in Task Manager) is overriding it. This gives the
user a concrete, pre-reboot answer to "will the watcher come back next
time I sign in."

**Why this priority**: This is a diagnostic nice-to-have. It is not
required for the resilience promise to hold — US1 and US2 together are
sufficient. But it is exactly what a user debugging the original symptom
needs, and it is cheap to add once the supervisor exists.

**Independent Test**: From an installed state, run the diagnostic command.
Confirm it reports "autostart will fire next logon: yes." Then use the
Windows Task Manager "Startup apps" tab to disable the entry. Re-run the
command. Confirm it now reports "autostart will fire next logon: no"
and names the override.

**Acceptance Scenarios**:

1. **Given** the user installed via `--install`, **When** the user runs
   the diagnostic, **Then** the output records at least one fire of the
   autostart entry since install (or, if install is minutes old,
   acknowledges that the first fire has not happened yet).
2. **Given** Windows' "Startup apps" toggle for the watcher has been
   disabled by the user, **When** the user runs the diagnostic, **Then**
   the output clearly indicates that autostart will not fire at next
   logon and identifies the override as the cause.

---

### User Story 5 — Cooperate with multi-session machines (Priority: P2)

A user signs into a machine where another user is already signed in (fast
user switching, RDP into a shared host). Each signed-in user who has
installed the watcher has their own independent watcher running in their
own session. One user's watcher crashing, being killed, or being
uninstalled must not affect the other user's watcher. The supervisor's
retries, logs, and PID tracking must be fully per-user.

**Why this priority**: Feature 003 already required per-user isolation.
This story re-asserts that requirement under the new supervision model
so the supervisor design does not accidentally break it (e.g. by using a
machine-global mutex, a shared log file, or a global scheduled task).

**Independent Test**: On a machine where user A and user B both have the
watcher installed, kill user A's watcher. Confirm user B's watcher keeps
running and is unaffected. Confirm A's watcher restarts via A's supervisor
and not via B's. Confirm the log file in A's `%LOCALAPPDATA%` records
only A's events.

**Acceptance Scenarios**:

1. **Given** user A and user B both have the watcher installed and
   running, **When** user A's watcher is killed, **Then** user B's
   watcher continues running uninterrupted.
2. **Given** user A's watcher has been killed, **When** user A's
   supervisor restarts it, **Then** the restart happens under user A's
   identity and touches only user A's files, registry, and processes.

---

### Edge Cases

- **Very early login**: the shell is responsive but `HKCU` mount, user
  profile services, or the user's language configuration are briefly
  unavailable. The watcher must back off and retry rather than exit
  `ConfigUnrecoverable` within the first few seconds; feature 003 already
  defines a 60-second grace period, and this feature must not shorten it.
- **Supervisor cannot spawn a replacement** because the staged binary has
  been deleted or moved (user cleaned up `%LOCALAPPDATA%` by hand). The
  supervisor must record this as a terminal state, not crash-loop, and
  `--status` must direct the user to re-run `--install`.
- **User logs off, then back on, while the supervisor believes it is
  between restart attempts**: the old supervisor state (in the logged-off
  session) must not influence the new session's supervisor. Per-session
  state, not per-user-profile state.
- **User upgrades the binary** by re-running `--install` from a newer
  `kbfix.exe`: the supervisor must pick up the new staged binary on the
  very next restart, not keep spawning the stale one.
- **System-wide antivirus or endpoint management tool quarantines the
  staged binary**: the supervisor must detect the missing binary,
  stop retrying, and surface the condition via `--status` and the log.
- **Watcher exits deliberately with a non-error reason** (e.g. a future
  `--uninstall` cooperative shutdown that the supervisor must *not*
  counter-spawn): the supervisor must distinguish "was asked to stop"
  from "died." This likely requires the existing stop event to also
  inform the supervisor.
- **Reboot happens while the supervisor is mid-restart-backoff**: backoff
  state is in-memory and therefore reset on reboot. Autostart on next
  login must re-establish a healthy state without any awareness of the
  pre-reboot backoff cycle.
- **Rapid log-off and log-on on the same account**: the previous session's
  watcher may not have fully released its single-instance primitive before
  the new session tries to claim it. The new session must wait briefly
  and/or detect an abandoned primitive rather than immediately declaring
  "already running" and exiting.
- **Autostart entry still present but pointing at a path that no longer
  exists** (the stale-path case feature 003 already classifies): the new
  supervisor must *not* override this and silently "fix" it, because the
  stale path usually means the user moved or deleted the binary on
  purpose. `--status` must continue to surface this state and leave the
  resolution to the user (re-install or uninstall).
- **Machine policy disables `HKCU\...\Run`**: the install path is dead
  and the supervisor cannot rely on it. The feature must detect this and
  either refuse to install with a clear message or document the
  alternative mechanism it falls back to.
- **Multiple copies of `kbfix.exe` on disk**: the supervisor must only
  restart the one it was configured with (the staged copy), not any other
  copy that happens to be running.

## Requirements *(mandatory)*

### Functional Requirements

**Autostart reliability**

- **FR-001**: After a successful `--install`, the watcher MUST be running
  within a bounded time (see SC-001) after every subsequent interactive
  sign-in of the same user, without any user action, on supported Windows
  versions, for every reboot mode Windows supports (cold boot, warm
  reboot, sign-out/sign-in, unlock, RDP-initiated new session).
- **FR-002**: The tool MUST verify, as part of `--install`, that the chosen
  autostart mechanism is actually effective on the current machine (for
  example, by detecting whether Windows' per-user "Startup apps" toggle,
  Group Policy, or another known override would prevent it from firing).
  If the install cannot reliably fire at next logon, the command MUST
  refuse with a clear explanation, or fall back to a defined alternative
  mechanism, rather than silently report success.
- **FR-003**: The tool MUST expose, to the user, whether autostart is
  currently effective (not just "registered"). The user MUST be able to
  answer "will the watcher come back next time I sign in?" without
  rebooting.
- **FR-004**: The install MUST use a launch mechanism that is robust to
  the common failure modes observed in practice: the user's `Run` key
  being overridden by third-party startup managers, the invoking binary
  being staged in a location Windows does not autostart from, and the
  autostart entry being registered but deprioritized to the point of not
  firing within the SC-001 window. Where multiple mechanisms are
  available, the tool MAY use more than one with documented precedence
  and idempotency, but MUST NOT produce duplicate effective autostarts.
- **FR-005**: Feature 003's guarantee that install/uninstall are
  idempotent and do not require elevation MUST continue to hold. Any new
  autostart mechanism this feature adds MUST also be installable,
  uninstallable, and inspectable entirely as the current user.

**Watcher supervision and self-restart**

- **FR-006**: The solution MUST ensure that if the watcher process exits
  for any reason other than a cooperative shutdown initiated via the
  documented stop path (`--uninstall`, stop event), a replacement watcher
  is spawned within a bounded time (see SC-002), without user action.
- **FR-007**: The supervision mechanism MUST distinguish a cooperative
  shutdown from an involuntary exit, and MUST NOT counter-spawn a
  replacement after a cooperative shutdown. `--uninstall` must remain
  terminal: once the user uninstalls, nothing should resurrect the
  watcher until `--install` is run again.
- **FR-008**: The supervision mechanism MUST apply exponential backoff
  between consecutive involuntary exits and MUST stop retrying after a
  defined threshold, so that a broken machine does not produce an
  infinite restart storm. The stopped state MUST be observable via
  `--status` and MUST be re-armable without requiring a reboot.
- **FR-009**: The supervisor itself MUST survive the kinds of failures it
  is protecting against. At minimum, the design MUST not place the
  entire watcher lifetime in a single process that, if it dies, takes the
  supervisor down with it. (The planning phase will pick between
  out-of-process supervisor, OS-provided supervision via Task Scheduler's
  "Restart on failure" option, or equivalent; the spec requires only the
  property, not a mechanism.)
- **FR-010**: The supervision mechanism MUST be per-user and MUST NOT
  affect other signed-in users on the same machine. A crash, uninstall,
  or retry-giveup for user A MUST have no visible effect on user B.
- **FR-011**: When a restart happens, the new watcher MUST be functional
  immediately (acquires its single-instance primitive, starts polling,
  writes to the same per-user log). The supervisor MUST NOT leave
  zombie processes, orphaned PID files, or a broken single-instance
  primitive that prevents future watchers from starting.

**Observability and diagnostics**

- **FR-012**: The watcher MUST record, at INFO level, every lifecycle
  transition that matters for diagnosing the reported symptom: process
  start, process stop (with cooperative-vs-involuntary distinction),
  supervisor restart, supervisor backoff, supervisor giveup, autostart
  fire-detected-at-login (if observable). These events MUST remain
  readable in the existing log file without requiring additional tools.
- **FR-013**: On an involuntary exit, the watcher (or supervisor, whichever
  is appropriate) MUST record the proximate cause in a machine-parseable
  form (e.g. exit code, named reason) so that `--status` can present it
  without guessing.
- **FR-014**: `--status` MUST, in addition to feature 003's output, report:
  (a) the last observed lifecycle transition and its timestamp, (b) the
  current supervisor state (healthy / backing off / gave up / unknown),
  (c) whether autostart is currently *effective* for next logon, and
  (d) the log file path. Exit codes MUST be extended so scripts can
  distinguish "healthy" from "degraded — supervisor backing off" from
  "degraded — supervisor gave up" from the feature-003 baseline codes.
  New codes MUST NOT overlap the existing ones (0, 1, 2, 3, 10, 11, 12,
  13, 14, 64).
- **FR-015**: The log file MUST remain size-bounded (feature 003 already
  rotates at 64 KB; this feature MUST NOT remove that bound) and MUST
  NOT contain personally identifying content beyond what feature 003
  already writes.
- **FR-016**: A user MUST be able to retrieve a single self-contained
  diagnostic snapshot — what `--status` sees, the last N log lines, the
  observed autostart state — in one command, suitable for pasting into
  a bug report. (This does not require a new subcommand if `--status
  --verbose` or equivalent is acceptable; the requirement is on the
  capability, not the flag spelling.)

**Compatibility and scope**

- **FR-017**: All feature 003 requirements MUST continue to hold. In
  particular: the tool MUST NOT require elevation, MUST NOT add new
  third-party NuGet dependencies, MUST continue to ship as a single
  self-contained binary, MUST continue to respect the "no visible UI"
  guarantee (no tray icon, no windows), and MUST continue to target
  Windows 10 1809 and later.
- **FR-018**: The one-shot `kbfix` invocation (no flags) MUST continue
  to work exactly as before. The new supervision mechanism MUST NOT run
  during one-shot mode.
- **FR-019**: The `--uninstall` command MUST fully tear down whatever
  new autostart mechanism this feature adds, in addition to what
  feature 003 already tears down. After `--uninstall`, nothing the
  machine does — reboots, user re-sign-ins, policy refreshes, scheduled
  tasks firing — MUST cause a kbfix process to start again until
  `--install` is run again.
- **FR-020**: The feature MUST NOT modify the user's persisted keyboard
  configuration (feature 001, 003 constitutional invariant). It MUST
  NOT modify layouts under any circumstances other than the existing
  reconciliation path.

### Key Entities

- **Supervisor**: The component responsible for ensuring that a watcher
  process is running at all times during the interactive session, for the
  installed user. Its state is per-user-per-session and includes the
  retry counter, the backoff deadline, and the "gave up" flag. At most
  one supervisor is effective per user session. The concrete hosting
  model (out-of-process helper, OS-provided mechanism, hybrid) is
  decided at planning time.
- **Autostart mechanism**: The per-user Windows facility that triggers
  the supervisor (or the watcher, if there is no separate supervisor
  process) at next sign-in. This feature may use more than one such
  facility; whatever set is used is observable, enumerable, and
  reversible via `--uninstall`.
- **Lifecycle event**: A timestamped record of a state transition —
  "watcher started," "watcher exited (reason)", "supervisor backing
  off," "supervisor gave up," "autostart fired at login." Persisted in
  the existing log and summarized by `--status`.
- **Supervision state**: The pair (retry count, backoff deadline, gave-up
  flag) reported by `--status` and used by the supervisor to decide
  what to do on the next watcher exit.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: For at least ten consecutive sign-ins in mixed reboot modes
  (cold boot, warm reboot, sign-out/sign-in, unlock, RDP reconnect) on a
  machine where the watcher was previously installed, a watcher process
  is running within 30 seconds of the interactive shell becoming
  responsive. Zero sign-ins in the sample result in "watcher not running
  at 30 seconds."
- **SC-002**: When the watcher process is forcibly killed, a replacement
  watcher process is running again within 15 seconds, on a typical
  machine, in at least 9 out of 10 successive trials. The 10th trial, if
  it fails, must not leave the machine in a permanently broken state —
  a further kill or a reboot must recover.
- **SC-003**: On a machine where the watcher is running and healthy, the
  combined CPU cost of the watcher and the supervision mechanism,
  averaged over one minute of idle desktop, is indistinguishable from
  zero in Task Manager (under 0.1% on a typical 4-core machine).
  Resident memory overhead added by the supervisor (on top of the
  feature-003 watcher baseline) stays under a small fixed budget that a
  user would not notice — a few megabytes at most.
- **SC-004**: The user can answer the three diagnostic questions — "is
  the watcher running right now?", "will it be running after my next
  sign-in?", "why did it stop last time?" — entirely from the output of
  `status.cmd`, in under 30 seconds of a user's attention, with no
  developer knowledge required.
- **SC-005**: Across a 30-day unattended run on a machine used normally
  (including RDP sessions, sleep/resume cycles, and occasional reboots),
  the watcher is running whenever the user is signed in, 99%+ of wall-
  clock time. The remaining <1% is bounded by SC-001 and SC-002 windows
  plus explicitly uninstalled time.
- **SC-006**: The install-time verification (FR-002) correctly identifies,
  on a machine where Windows' "Startup apps" toggle has been used to
  disable the watcher, that autostart will not be effective — before the
  user has to reboot to find out.
- **SC-007**: A pathological crash loop (watcher restarts, immediately
  dies, restarts again) does not, after 5 minutes, generate more than a
  bounded amount of log output (target: under 1 MB) or consume more than
  a bounded amount of CPU time (target: under 30 CPU-seconds). After the
  supervisor gives up, CPU usage returns to baseline and the log stops
  growing.
- **SC-008**: The existing feature-003 unit tests continue to pass. New
  unit tests cover the supervisor's restart decision logic, the
  cooperative-vs-involuntary classification, and the backoff/giveup
  state machine. The manual verification described in the project
  constitution (RDP-injected layout is removed by the watcher) continues
  to pass unchanged.
- **SC-009**: On a clean Windows 11 install with no developer tools,
  copying the release binary, running `install.cmd`, rebooting, and
  doing nothing else produces a running, healthy watcher within
  SC-001's window — no additional commands, no elevation, no .NET
  runtime prerequisite.

## Assumptions

These are the design assumptions this feature rests on. Planning is free
to revisit them if evidence justifies it, but they are the current
intended direction.

- **Windows' per-user `HKCU\...\Run` entry is the right *primary* autostart
  mechanism, and a supplementary per-user Scheduled Task ("At logon",
  "Restart on failure: every 1 minute, up to N times") is the right
  *belt-and-suspenders* mechanism.** The `Run` key covers the common case
  cheaply; the scheduled task covers cases where the `Run` key is
  suppressed by a startup manager, by "Startup apps" toggles, or by
  timing conditions that delay it past the SC-001 window. Both can be
  installed per-user without elevation. This pair, rather than either
  alone, is the simplest combination that meets SC-001 across the reboot
  modes listed. Feature 003 already uses the `Run` key; adding the
  scheduled task is the concrete hypothesis for the resilience story.
- **Supervision lives in the watcher itself, with an out-of-process fallback
  supplied by the Scheduled Task.** The watcher's own process catches its
  own per-cycle exceptions (already true in feature 003 for the poll
  loop). A separate lightweight supervisor process is not introduced in
  this feature; the scheduled task plays the role of the supervisor when
  the whole watcher process has died. This keeps the codebase small and
  avoids the bug-surface of writing a second background process.
- **The watcher process's exit reasons are already rich enough to drive the
  new "why did it stop?" diagnostics** (feature 003 defines `StopSignaled`
  and `ConfigUnrecoverable`). This feature will extend the reason set
  (add `CooperativeUninstall`, `CrashedUnhandled`) and persist the last
  reason where `--status` can read it, but does not require a fundamental
  rework of the lifecycle model.
- **The `KBFIX_DEBUG=1` environment variable already provides the "chatty
  per-poll-cycle" log level** (feature 003). This feature does not change
  that; it adds new event types at INFO level that are always on.
- **`%LOCALAPPDATA%\KbFix\` is the canonical per-user staging and log
  directory** (feature 003) and remains so. Any new files this feature
  introduces (scheduled task definition export, last-exit-reason file,
  etc.) go in that same directory and are cleaned up by `--uninstall`.
- **Users cannot be expected to install .NET SDKs, run PowerShell as admin,
  or edit Task Scheduler XML by hand.** Every new mechanism this feature
  introduces MUST be installable and removable by the existing
  `install.cmd` / `uninstall.cmd` wrappers, with no prerequisite tooling
  beyond what feature 003 already required.
- **"Current user" continues to mean the interactive user who ran
  `--install`** (feature 003), even if that install was launched from an
  elevated terminal. This feature does not introduce a SYSTEM-scoped or
  machine-wide supervisor.
- **Nothing in this feature introduces a persistent service.** A Windows
  Service would run in session 0 and could not reach the user's
  interactive keyboard-layout state, which was already the reasoning in
  feature 003. The supervision added here stays in the user's interactive
  session, triggered by per-user autostart and per-user scheduled tasks
  only.
- **Backwards compatibility for already-installed users is required.** A
  user who installed under feature 003 and upgrades their binary MUST
  transition cleanly: their existing `Run` key continues to work and the
  new mechanism is added on the next `--install` without them having to
  `--uninstall` first.
