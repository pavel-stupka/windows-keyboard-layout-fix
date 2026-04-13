# Quickstart — Background Watcher, Autostart, and Slim Binary

**Feature**: 003-background-watcher
**Audience**: developers implementing or reviewing the feature, and end users verifying a release build.

## 1. Build the slim release binary

From the repository root:

```bat
build.cmd release
```

Expected result:

- A single `kbfix.exe` is produced under `dist\` (or wherever `build.cmd release` currently places it — unchanged from feature 002).
- The resulting file size is **at most 20 MB**, ideally **under 15 MB**. If the first trimmed build is larger, inspect `dotnet publish` output for trim warnings and re-check `TrimMode` / size-knob properties in `src/KbFix/KbFix.csproj`.
- No extra flags are needed. The build.cmd contract from feature 002 is preserved.

### Sanity-check the trimmed binary

On the build machine, from `dist\`:

```bat
kbfix --version
kbfix --help
kbfix --dry-run
```

All three MUST succeed and produce the same output as before the size reduction. In particular, `kbfix --dry-run` MUST still print the same four-section report as the 001 CLI contract.

### Clean-machine verification (mandatory before release)

Copy the produced `kbfix.exe` to a Windows 11 machine that has **no .NET runtime installed and no developer tools**. Run:

```bat
kbfix
kbfix --status
```

Both must run without a "runtime missing" dialog.

## 2. Install the watcher

```bat
kbfix --install
```

Expected stdout (default verbosity):

```text
KbFix installer
  staged:   C:\Users\<you>\AppData\Local\KbFix\kbfix.exe
  autostart: HKCU\Run\KbFixWatcher = "C:\Users\<you>\AppData\Local\KbFix\kbfix.exe" --watch
  watcher:   started (pid <N>)

Installed. The watcher will also start automatically at your next Windows login.
```

Verify:

- `%LOCALAPPDATA%\KbFix\kbfix.exe` exists and matches the binary you installed from.
- `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v KbFixWatcher` prints the expected value.
- `tasklist /fi "imagename eq kbfix.exe"` lists one running process.
- `%LOCALAPPDATA%\KbFix\watcher.log` exists and has a `start` line with the current timestamp.

## 3. Verify it actually fixes RDP injections

1. Note the current keyboard layouts in `Settings → Time & language → Language & region → Typing → Advanced keyboard settings → Input language hot keys → Change key sequence`. Or just press `Win+Space` — you should see only the layouts you have configured.
2. Open a Remote Desktop connection to any machine. Disconnect.
3. Within **a few seconds** (SC-001, SC-002), pressing `Win+Space` should show only your configured layouts again. If RDP injected an extra layout, the watcher must have removed it already.
4. Open `%LOCALAPPDATA%\KbFix\watcher.log`. You should see a `reconcile-applied count=1` (or similar) line with a timestamp from the last few seconds.

Repeat the RDP connect/disconnect cycle 10 times. No stray layout should ever survive more than a few seconds (SC-001).

## 4. Check status

```bat
kbfix --status
```

Expected state for a healthy install:

```text
KbFix status
  watcher:   running (pid <N>)
  autostart: registered  ("C:\Users\<you>\AppData\Local\KbFix\kbfix.exe" --watch)
  staged:   present      (C:\Users\<you>\AppData\Local\KbFix\kbfix.exe)
  log:       C:\Users\<you>\AppData\Local\KbFix\watcher.log

State: InstalledHealthy
```

Exit code is 0. If status shows anything else, see `contracts/cli.md` for the meaning of each state.

### Status edge cases to verify manually

- **Kill the watcher** via Task Manager. Run `kbfix --status` → `State: InstalledNotRunning`, exit code 11.
- **Log off and log back in.** The watcher should be running again automatically. `kbfix --status` → `State: InstalledHealthy`.
- **Delete the Run key** via `reg delete`. Run `kbfix --status` → `State: RunningWithoutAutostart`, exit code 12.

## 5. Reinstall with a newer version

1. Make a small code change (e.g. bump the version in `KbFix.csproj`).
2. `build.cmd release`.
3. Run the new binary's `kbfix --install` from wherever it was published.
4. Expected: the old watcher is signaled to stop, staged binary is overwritten, a new watcher is spawned.
5. `kbfix --status` should still report `State: InstalledHealthy` with a new PID.

## 6. Uninstall

```bat
kbfix --uninstall
```

Expected stdout for a healthy install:

```text
KbFix uninstaller
  watcher:   stopped (pid was <N>, cooperative shutdown)
  autostart: HKCU\Run\KbFixWatcher  (removed)
  staged:   C:\Users\<you>\AppData\Local\KbFix\kbfix.exe  (deleted)

Uninstalled.
```

Verify:

- `tasklist /fi "imagename eq kbfix.exe"` no longer lists the watcher.
- `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Run /v KbFixWatcher` returns `ERROR: The system was unable to find the specified registry key or value.`
- `%LOCALAPPDATA%\KbFix\` is gone (or at most contains a residual `kbfix.exe.old` if `--uninstall` was run from the staged binary; see contract).
- `kbfix --status` now reports `State: NotInstalled`, exit code 10.
- Reboot. After login, the watcher should NOT be running.

## 7. Idempotency checks

All three commands are idempotent. Verify:

- `kbfix --install` twice in a row → second run reports `Already installed.`, exit 0.
- `kbfix --uninstall` twice in a row → second run reports `Nothing to uninstall.`, exit 0.
- `kbfix --status` any number of times → no side effects, same output each time.

## 8. One-shot mode regression check

The original one-shot mode must be byte-for-byte unchanged:

```bat
kbfix
kbfix --dry-run
kbfix --quiet
kbfix --help
kbfix --version
```

All five commands must produce the same output as the 001 contract (`specs/001-fix-keyboard-layouts/contracts/cli.md`). This is required by FR-018 and SC-008.

## 9. Troubleshooting

If something is wrong, the first thing to read is `%LOCALAPPDATA%\KbFix\watcher.log`. Useful entries:

| Log line | Meaning |
|---|---|
| `start` | Watcher acquired mutex and entered the loop. |
| `reconcile-noop` | Session matched persisted config; nothing to do. |
| `reconcile-applied count=N` | Removed N stray layouts. |
| `reconcile-failed reason=...` | An `Apply` step failed; watcher will retry next iteration. |
| `flap-backoff` | Too many fixes in the last minute; watcher is pausing. |
| `config-read-failed` | `PersistedConfigReader` could not read the user's language settings. |
| `session-empty-refused` | Would have emptied the session layout set; refused for safety (Principle III). |
| `stop` | Watcher received stop event and is shutting down cleanly. |

If `--status` reports `StalePath`, run `kbfix --install` from the current binary to re-stage, or `kbfix --uninstall` to clear everything.
