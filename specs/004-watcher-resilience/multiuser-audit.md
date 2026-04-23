# Per-User Isolation Audit — Feature 004

**Purpose**: Reviewer checklist for PR approval. Enumerates every new
write surface feature 004 introduces and confirms each resolves to
per-user, per-session storage — no machine-global resources, no cross-user
leakage. Required by user story US5 and by constitution §II (User
Configuration Is the Source of Truth) / §III (Safe & Reversible Operations).

## Write surfaces introduced by 004

| Surface | Scope | Verified by |
|---------|-------|-------------|
| `%LOCALAPPDATA%\KbFix\last-exit.json` | Per-user profile (HKCU-equivalent folder) | `WatcherInstallation.LastExitFilePath` rooted at `Environment.SpecialFolder.LocalApplicationData` |
| `%LOCALAPPDATA%\KbFix\scheduled-task.xml` | Per-user profile | `WatcherInstallation.ScheduledTaskXmlPath` rooted at `Environment.SpecialFolder.LocalApplicationData` |
| Task Scheduler `\KbFix\KbFixWatcher` | Per-user task namespace (requires the user SID in `<Principal><UserId>`) | `ScheduledTaskRegistryTests.BuildTaskXml_contains_expected_user_sid_in_both_principal_and_trigger` + `..._does_not_request_elevation_anywhere` |

## Existing surfaces preserved from 003 (unchanged)

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\KbFixWatcher` — per-user registry
- `%LOCALAPPDATA%\KbFix\` directory tree — per-user profile
- Named kernel objects `Local\KbFixWatcher.Instance`, `Local\KbFixWatcher.StopEvent` — per-session (`Local\` namespace is session-scoped, not global)

## Read-only surfaces (new)

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run\KbFixWatcher` — per-user, read-only probe

## Things feature 004 does NOT do

- Does not create a Windows Service (which would live in Session 0, violating constitution §I and defeating the watcher's purpose).
- Does not write under `HKLM\`, `HKCR\`, or any machine-wide registry hive.
- Does not create a Scheduled Task in the root namespace (`\TaskName` directly) — the task is always under `\KbFix\`.
- Does not use the `\\Global\` kernel-object namespace.
- Does not set `<RunLevel>HighestAvailable</RunLevel>` or SYSTEM / Administrators principals in the task XML — verified by `ScheduledTaskRegistryTests.BuildTaskXml_does_not_request_elevation_anywhere`.
- Does not write to `HKCU\Keyboard Layout\Preload` or `HKCU\Control Panel\International\User Profile` (the user's persisted language configuration) — that invariant is inherited from features 001 and 003 and unchanged.

## Verification

- All surfaces above resolve to per-user paths by construction. There is no path that could be built from feature 004's code that lands in a machine-global location without explicit user SID or `Environment.SpecialFolder.LocalApplicationData` derivation.
- `ScheduledTaskRegistry.BuildTaskXml(stagedPath, userSid)` takes the SID as a parameter; the only caller is `InstallExecutor.ApplyExportScheduledTaskXml`, which obtains it via `ScheduledTaskRegistry.CurrentUserSid()` → `WindowsIdentity.GetCurrent().User?.Value`. On a desktop session, that is always the interactive user.
- Uninstall tears down every surface in this document (see `data-model.md` §9 and `SupervisorDecision.AppendUninstallSteps`).

## Sign-off

PR reviewer: confirm by inspection that no code path under
`src/KbFix/Platform/Install/` or `src/KbFix/Watcher/` **writes** to:
- `HKLM\*`, `HKCR\*`, `HKU\*` (other users), `HKEY_PERFORMANCE_DATA\*`
- `%PROGRAMDATA%\*`, `%PROGRAMFILES%\*`, `%WINDIR%\*`
- `\\Global\*` kernel objects
- Task Scheduler root namespace (`\`) or any user namespace other than `\KbFix\`

Reads of HKLM are permitted for documented catalogs the tool needs — these
are pure lookups and cannot affect any other user. Verified 2026-04-23:

| File | HKLM reference | Purpose |
|------|----------------|---------|
| `src/KbFix/Platform/KeyboardLayoutCatalog.cs` | `Registry.LocalMachine.OpenSubKey(..., writable: false)` | Reads `SYSTEM\CurrentControlSet\Control\Keyboard Layouts\<KLID>` for KLID-to-friendly-name resolution. Inherited from feature 001; unchanged. |
| `src/KbFix/Platform/SessionLayoutGateway.cs` | `Registry.LocalMachine.OpenSubKey(..., writable: false)` | Same catalog; reads `Layout Id` per-KLID. Inherited; unchanged. |
| `src/KbFix/Cli/Reporter.cs` | `Registry.LocalMachine.OpenSubKey(..., writable: false)` | Same catalog; reads `Layout Text`. Inherited; unchanged. |

A grep for writes — `Registry.LocalMachine.CreateSubKey`, `Registry.LocalMachine.SetValue`, `Registry.LocalMachine.DeleteSubKey`, `S-1-5-18`, `HighestAvailable`, `Global\\` — must return no hits in production code. Tests are allowed to assert the *absence* of these patterns in generated artefacts.
