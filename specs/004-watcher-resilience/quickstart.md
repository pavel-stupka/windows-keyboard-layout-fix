# Quickstart — Watcher Resilience, Observability, and Self-Healing

**Feature**: 004-watcher-resilience
**Audience**: developers implementing or reviewing the feature, and end users verifying a release build.

This quickstart is **additive** to `specs/003-background-watcher/quickstart.md`.
All verifications there still apply. What follows is the new surface
this feature introduces.

---

## 1. Build

From the repository root:

```bat
build.cmd release
```

Expected size is **unchanged from 003** (≤ 20 MB, ideally < 15 MB). If
size grows by more than 0.5 MB, investigate — the new code is a few
hundred LOC plus `System.Text.Json`, which is already in the BCL. Size
growth beyond that would indicate a trimming regression.

Sanity check on the build machine:

```bat
kbfix --version
kbfix --help
kbfix --dry-run
```

All three behave exactly as before.

---

## 2. Install the watcher (fresh machine)

```bat
kbfix --install
```

Expected stdout (default verbosity, new lines called out with `← NEW`):

```text
KbFix installer
  staged:     C:\Users\<you>\AppData\Local\KbFix\kbfix.exe
  autostart:  HKCU\Run\KbFixWatcher = "...\kbfix.exe" --watch
  task:       \KbFix\KbFixWatcher  (At logon + Restart on failure)   ← NEW
  effective:  yes                                                    ← NEW
  watcher:    started (pid <N>)

Installed. The watcher will also start automatically at your next Windows login.
```

Post-install verifications:

1. **Run key present** — same as 003:
   ```bat
   reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v KbFixWatcher
   ```
2. **Scheduled Task present** — new:
   ```bat
   schtasks /Query /TN "KbFix\KbFixWatcher"
   ```
   Status should be `Ready`. Principal (user) should be the current
   interactive user.
3. **Task XML readable** — new:
   ```bat
   type "%LOCALAPPDATA%\KbFix\scheduled-task.xml"
   ```
   Contains `<LogonType>InteractiveToken</LogonType>` and
   `<RunLevel>LeastPrivilege</RunLevel>`. NEVER contains `HighestAvailable`.
4. **Watcher running** — unchanged:
   ```bat
   status.cmd
   ```
   (Or `kbfix --status`. Output now includes `task:`, `supervisor:`, `last exit:`,
   and `autostart effective` lines — see §5 below.)

---

## 3. Upgrade from a 003 install

Starting state: user already has feature-003 installed (Run key +
staged binary + running watcher, no Scheduled Task yet). Re-run
`--install` from the new binary:

```bat
<path-to-new-kbfix>.exe --install
```

Expected behaviour:

- The existing watcher's lifetime is **preserved** (mutex held; no
  redundant respawn). If the user ran `--install` from the staged
  binary, no processes are killed.
- If the user ran it from a *different* (newer) copy, the existing
  watcher is cooperatively stopped, the binary is replaced, the Run
  key is re-written (same value), the task is created, and a new
  watcher is spawned. This is the `003-upgrade-case-B` path in
  research R8.
- The Run key is **not rewritten** if it already points at the staged
  binary. The Scheduled Task is added idempotently.

Verify after upgrade:

```bat
kbfix --status
```

Should report `task: \KbFix\KbFixWatcher (Ready...)`, `supervisor: healthy`.

---

## 4. Verify resilience — Layer 1 (restart after in-session crash)

Layer 1 is the watcher's own in-process try/catch (003 behavior). This
should already be robust. Spot-check by simulating a transient error:

- Rename `%LOCALAPPDATA%\KbFix\` briefly while the watcher is running
  (simulates a transient config-read failure). Wait ~65 seconds. The
  watcher should still be alive: it backed off but did not exit
  because the directory became readable again quickly enough.
- Rename it for > 60 seconds. The watcher exits with
  `ConfigUnrecoverable`. Rename back. Within 90 seconds, the
  Scheduled Task (layer 2) restarts it. Verify via `status.cmd`.

---

## 5. Verify resilience — Layer 2 (restart after external kill)

This is the **primary new verification step** for feature 004.

Starting state: `--install` completed; `kbfix --status` reports
`InstalledHealthy`.

### Trial — single kill

1. Open Task Manager → Details → find `kbfix.exe` → End Task.
2. Note the time. Run `kbfix --status` immediately — should show
   `supervisor: restart pending (N left)` and exit 15.
3. Wait 90 seconds (the Task Scheduler restart interval + startup
   overhead).
4. Run `kbfix --status` again — should show `supervisor: healthy`,
   watcher running, exit 0.

If step 4 fails, inspect:

- `schtasks /Query /TN "KbFix\KbFixWatcher" /V /FO LIST` — check
  `Last Result` (0 means success), `Next Run Time`, `Status`.
- `%LOCALAPPDATA%\KbFix\watcher.log` — should contain a
  `process-startup reason=SupervisorObservedDead` line.
- `%LOCALAPPDATA%\KbFix\last-exit.json` — since the previous watcher
  was killed (not cooperatively stopped), this file contains the
  exit reason written by the *new* watcher's startup code
  (`SupervisorObservedDead`).

### Trial — 10 consecutive kills

Repeat the single-kill trial 10 times in a row, spacing each trial
120 seconds apart. All 10 should recover within the 90-second window.
Record a failure only if the recovery did not happen at all for a
given trial — a slightly-over-90s recovery on a busy machine is
acceptable.

### Trial — pathological crash loop

This tests SC-007 (give-up behaviour). Temporarily break the watcher
so it exits non-zero immediately on startup:

- For instance, make the staging directory unreadable by renaming
  `watcher.log` to a read-only file of the same path. Then kill the
  running watcher. The task will try to restart 3 times at 1-minute
  intervals and then give up.
- After about 5 minutes, `kbfix --status` should report
  `supervisor: gave up (re-run --install)`, exit 16.
- `watcher.log` should NOT be larger than ~100 KB (SC-007 target: < 1 MB).
- Re-run `kbfix --install`. The task is recreated, restoring the
  restart budget. Fix the original problem. Watcher starts.

---

## 6. Verify autostart effectiveness probe (SC-006)

Scenario: the user disables the Startup Apps toggle for `KbFixWatcher`
without running `--uninstall`.

1. After install, open Task Manager → Startup apps → find `KbFixWatcher`
   → Disable.
2. Run `kbfix --status`. Expected output:
   - `autostart: registered ... (DISABLED by Startup Apps)`
   - `autostart effective at next logon:  yes (via Scheduled Task)`
     — because the task alone is still enough.
   - Exit 0.
3. Now additionally disable the Scheduled Task:
   ```bat
   schtasks /Change /TN "KbFix\KbFixWatcher" /DISABLE
   ```
4. Run `kbfix --status`. Expected:
   - `autostart effective at next logon:  no (both mechanisms disabled)`.
   - Exit 17 (`AutostartDegraded`).
5. Run `kbfix --install`. It detects the degraded state, re-enables
   the task (and surfaces the Startup Apps toggle in the report).
   Exit 0.

---

## 7. Verify reboot resilience (SC-001)

Perform the following cycle **five times** in mixed modes:

| Mode              | Steps                                                     |
|-------------------|-----------------------------------------------------------|
| Cold boot         | Shut down fully; power on; sign in.                       |
| Warm reboot       | Start → Restart.                                          |
| Sign-out/sign-in  | Start → Sign out; then sign in again.                     |
| Lock + unlock     | Win+L; then unlock.                                       |
| Sleep + resume    | Close lid or Start → Sleep; then resume.                  |

After each sign-in, within 30 seconds of the shell becoming responsive
(SC-001 target), run:

```bat
status.cmd
```

Expected in each case: `State: InstalledHealthy`, exit 0.

Record any trial where the state is not Healthy at 30 s. The tool
should now have reduced such failures to none (or a single, clearly
attributable outlier).

### RDP-reconnect variant

On a machine where you are already signed in, RDP in from another
device. After the session establishes:

1. Verify `status.cmd` reports Healthy within 30 s.
2. Trigger an unwanted layout injection (typical RDP symptom).
3. Verify the watcher removes it within a few seconds
   (unchanged from 003 behaviour — 004 does not slow this down).

---

## 8. Verify `--status --verbose`

```bat
kbfix --status --verbose
```

Expected: everything `kbfix --status` prints, PLUS:

- `----- watcher.log (tail) -----` ... up to 40 lines ... `----- end -----`
- `----- scheduled-task.xml -----` ... full XML ... `----- end -----`
- `----- last-exit.json -----` ... pretty-printed ... `----- end -----`

Exit code is identical to `kbfix --status` for the same machine state.

Copy the entire output to a file and confirm it's suitable for pasting
into a bug report without further redaction (no PII, no stack traces).

---

## 9. Uninstall verification (FR-019)

```bat
kbfix --uninstall
```

Expected stdout (new lines called out):

```text
KbFix uninstaller
  watcher:    stopped (cooperative shutdown)
  autostart:  HKCU\Run\KbFixWatcher  (removed)
  task:       \KbFix\KbFixWatcher  (removed)   ← NEW
  staged:     C:\Users\<you>\AppData\Local\KbFix\  (cleared)

Uninstalled.
```

Verify nothing remains:

```bat
reg query "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v KbFixWatcher
:: expect: ERROR

schtasks /Query /TN "KbFix\KbFixWatcher"
:: expect: ERROR: The system cannot find the file specified.

dir "%LOCALAPPDATA%\KbFix\"
:: expect: directory not found  (or empty + removed on next login)
```

Then reboot. Confirm no `kbfix.exe` process starts. This verifies
FR-019 — `--uninstall` is truly terminal.

---

## 10. Policy / degraded-environment verification

On a machine where Task Scheduler user-namespace writes are blocked
(this is rare outside heavily-managed corporate environments — most
developers will NOT be able to produce this condition, and that is
fine):

1. Run `kbfix --install`.
2. Expected: install succeeds with a warning message
   (`task: NOT installed — Task Scheduler denied creation (policy)`).
   Run key and staged binary are installed normally.
3. `kbfix --status` exits 0 if Run key alone is effective, or
   exit 17 if the Startup Apps toggle is also disabled.

If you cannot reproduce this, it is acceptable to verify via unit
tests only — `SupervisorDecisionTests` covers the decision branch
for "task creation refused by policy."

---

## 11. Multi-user verification (US5)

Optional on developer machines; recommended on shared-host environments
(RDP hosts, fast-user-switching kiosks). If reproducible:

1. Sign in as user A. Run `install.cmd`. Verify watcher running.
2. Switch users (Win+L → switch user). Sign in as user B. Run
   `install.cmd`. Verify user B's watcher is running.
3. Back to user A (fast-user-switch). Kill user A's watcher via Task
   Manager. Confirm user B's watcher is unaffected (either query via
   B's `status.cmd` after switching, or observe the PID from a
   previous snapshot is still alive).
4. User A's watcher restarts within 90 s under A's supervisor; user B
   was never touched.

If a multi-user environment is not available, `multiuser-audit.md` in
the feature folder documents the per-user-isolation properties by
construction — the code cannot produce cross-user effects by design.

## Manual-verification gate for release

Per constitution § Development Workflow & Quality Gates, the manual
RDP-verification gate inherited from 003 is **still required** for
every release. This feature adds these **new** manual verifications
to the release checklist:

- [ ] §2 fresh install on a clean machine — Healthy within 30 s.
- [ ] §3 003→004 upgrade — does not kill an in-flight watcher.
- [ ] §5 single external kill — recovered within 90 s.
- [ ] §5 10 consecutive kills — 9 of 10 recover within 90 s.
- [ ] §6 Startup Apps toggle probe — correctly reports effectiveness.
- [ ] §7 reboot resilience — 5 trials × 5 reboot modes, all Healthy at 30 s.
- [ ] §9 uninstall — no residual state; reboot does not resurrect.
- [ ] §11 multi-user isolation (when reproducible).

These replace no prior checklist items; they are added to the release
manual-verification script.
