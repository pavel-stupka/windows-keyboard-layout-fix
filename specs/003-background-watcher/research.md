# Phase 0 Research: Background Watcher, Autostart, and Slim Binary

**Feature**: 003-background-watcher
**Date**: 2026-04-13
**Spec**: [spec.md](./spec.md)

This document resolves every technical unknown raised by the feature spec so the Phase 1 design can proceed from firm decisions. Each section ends with a **Decision**, **Rationale**, and **Alternatives considered** block.

---

## R1. Layout-change detection mechanism

**Question**: How should the watcher detect that the session's loaded keyboard layouts have diverged from the user's persisted configuration, fast enough that a stray layout is gone within a few seconds, cheaply enough to be invisible at rest, and reliably enough to catch RDP-injected layouts?

**Investigation**:

- RDP does NOT modify `HKCU\Keyboard Layout\Preload` when it injects a layout. The injection happens in live session state via Win32 APIs. A registry watcher on `Preload` is therefore the wrong signal for the primary use case.
- The existing `HKCU\Control Panel\International\User Profile` source (used by `PersistedConfigReader.TryReadRawFromUserProfile`, `src/KbFix/Platform/PersistedConfigReader.cs:69`) is likewise not written by RDP.
- `WM_INPUTLANGCHANGE` is delivered to the focus window after a user-initiated switch. It does not reliably fire for system-injected layouts that arrive without changing the active layout. Using it as a *secondary* wake-up hint is fine; using it as the primary signal is not.
- `ITfLanguageProfileNotifySink` / `ITfInputProcessorProfileActivationSink` target modern IME profile activation, not the legacy Win32 HKL set. Adding TSF sinks for this would introduce COM apartment/lifetime complexity (threading, `AdviseSink`/`UnadviseSink`, STA pump) for little additional coverage.
- `GetKeyboardLayoutList` (already used by `SessionLayoutGateway.SnapshotHkls`) returns the authoritative in-session HKL set. Calling it every few seconds is a few hundred nanoseconds of kernel32 work — effectively free.
- The existing reconciliation pipeline (`PersistedConfigReader.ReadRaw` → `gateway.ResolvePersisted` → `gateway.ReadSession` → `ReconciliationPlan.Build` → `gateway.Apply` → `gateway.VerifyConverged`, see `src/KbFix/Program.cs:47-146`) is already stateless per invocation and idempotent, which is exactly what a polling loop needs.

**Decision**: Periodic polling loop, interval 2 seconds by default, running on the STA main thread of the watcher process. Each iteration re-runs the existing one-shot reconciliation pipeline (ReadRaw → Resolve → ReadSession → Build → Apply → Verify). No registry watcher, no TSF sinks, no message pump in v1.

**Rationale**:

- Guaranteed to detect RDP injections because it is checking the actual session state, not a proxy signal.
- Zero new code for detection — the existing reconciliation loop is the detection.
- Polling at 2 s satisfies "stray layout disappears within a few seconds" (SC-001, SC-002) while keeping the watcher comfortably inside "indistinguishable from zero CPU on a one-minute average" (SC-003): `GetKeyboardLayoutList` + a registry read + a set-diff is sub-millisecond work per iteration.
- Re-reading `PersistedConfigReader.ReadRaw` each iteration also picks up the case where the user changes their language settings in Windows Settings while the watcher is running, with no extra machinery.
- No new threading model; the watcher reuses `[STAThread]` just like the one-shot mode (`src/KbFix/Program.cs:14`).

**Alternatives considered**:

| Option | Why rejected |
|---|---|
| `RegNotifyChangeKeyValue` on `HKCU\Keyboard Layout\Preload` | RDP does not touch Preload. Would miss the primary use case. |
| `RegNotifyChangeKeyValue` on `HKCU\Control Panel\International\User Profile` (subtree) | Same reason — these registry paths are persisted-config sources, not live-session state. |
| Hidden-window `WM_INPUTLANGCHANGE` / `WM_INPUTLANGCHANGEREQUEST` message loop | Unreliable for system-injected layouts. Adds a message pump and a hidden HWND for marginal benefit over polling. Revisitable as an optional *wake-up hint* in a later iteration, not v1. |
| TSF notification sinks (`ITfSource::AdviseSink(ITfLanguageProfileNotifySink)`) | Targets modern IME activation, not legacy HKL changes. High complexity / COM lifetime risk for unclear coverage. |
| `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` | Detects focus changes, still requires polling `GetKeyboardLayoutList` to see what changed. Adds hook overhead for no new information. |
| WMI event subscription | High latency, no direct event for layout list changes, heavy for a small utility. |

**Tunables** (not settings-file-backed in v1; compile-time constants with sensible defaults):

- `PollInterval` — default 2 s.
- `IdleBackoff` — after *N* consecutive no-op cycles, stretch the interval to 5 s. After *M* more, stretch to 10 s. On the first non-no-op, snap back to 2 s.
- `FlapCeiling` — if the watcher applies non-no-op reconciliations more than 10 times in a rolling 60-second window, it logs and pauses for 5 minutes before resuming (FR-007 flapping protection).

---

## R2. Autostart registration mechanism

**Question**: How should `--install` register the watcher to start at Windows login, per-user, without admin rights, robustly across reboots and RDP sessions?

**Investigation**:

| Mechanism | Admin? | Visible in Settings → Startup Apps? | Timing | C# Integration |
|---|---|---|---|---|
| `HKCU\...\CurrentVersion\Run` | No | Yes | Before shell | `Microsoft.Win32.Registry` — trivial, one-liner |
| Startup folder (`shell:startup`) | No | Yes | After shell | Needs `IWshRuntimeLibrary` (COM) or manual `.lnk` writer |
| Task Scheduler per-user "at logon" task | No | No (hidden; visible only in Task Scheduler UI) | Before shell, can be delayed | `schtasks.exe` shell-out or `Schedule.Service` COM |
| `RunOnce`, Group Policy, logon scripts | — | — | — | Not applicable to per-user no-admin scenario |

- `HKCU\Run` is already on the `Microsoft.Win32.Registry` dependency (same package already in `KbFix.csproj:23`). No new dependencies.
- Task Scheduler via `schtasks.exe` shell-out is viable but adds a process dependency, an XML template, and error-handling for `schtasks` exit codes. The COM `Schedule.Service` route is cleaner but adds COM interop surface that trimming has to keep alive.
- `HKCU\Run` is visible in Settings → Startup Apps, which the spec's "transparency" assumption actually favors — users can see the tool is installed and disable it manually if they want. Hidden installation is slightly adversarial for a utility they chose to install.

**Decision**:

- **Primary mechanism: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`**, value name `KbFixWatcher`, value data the full quoted path to `kbfix.exe --watch`.
- **No fallback** in v1. If the Run key is unavailable (HKCU not writable — essentially impossible for an interactive user), `--install` reports a clear error and exits non-zero. Adding Task Scheduler as a fallback would be unjustified complexity without a real failure mode to cover.
- `--install` writes the value idempotently (overwrite OK; if the existing value already equals the intended data, report "already installed").

**Rationale**:

- Zero new NuGet or COM dependencies.
- Per-user, no admin.
- Visible to the user in the standard Windows "Startup apps" UI, satisfying Principle V (Observability) by giving the user a second channel to discover what this tool has installed.
- Trimming-safe: `Microsoft.Win32.Registry` already ships with the framework and is trim-friendly.

**Alternatives considered**:

- *Task Scheduler (`schtasks.exe`)*: No concrete advantage over `HKCU\Run` for this feature. Added complexity (external process invocation, XML templating, error-mapping) and lower visibility. Revisitable later if `HKCU\Run` proves unreliable in practice.
- *Startup folder `.lnk`*: Needs a shortcut writer (WSH COM or a hand-rolled IShellLink wrapper). File-based and fragile; users accidentally delete shortcuts. Worse ergonomics than a single Registry value.

---

## R3. Single-instance primitive and watcher discovery

**Question**: How does the watcher guarantee at most one instance per user, and how does `--status` (and a second `--install`) discover whether a watcher is running right now?

**Investigation**:

- A **named mutex** scoped to `Local\` is per-session by default and does not need any explicit SID suffix, because `Local\` objects are already sandboxed to the creating session. `Global\` mutexes would require the mutex name to be SID-suffixed to be truly per-user and may touch ACL concerns — unnecessary for a per-session tool.
- `System.Threading.Mutex` in .NET has native support for named mutexes via its `initiallyOwned=true, name=...` constructor, and `Mutex.TryOpenExisting(name, out)` lets `--status` probe without creating.
- `--status` must also be able to report "watcher is running at PID X." A mutex alone does not expose a PID. Options:
  - The watcher, after acquiring the mutex, writes its PID into a small **lock file** at a well-known per-user path (e.g. `%LOCALAPPDATA%\KbFix\watcher.pid`). `--status` reads this file, but ONLY trusts it if the mutex is also held (mutex is the truth; the file is just the decoration).
  - Or: the watcher hosts a named pipe server and responds to queries. Heavier; adds a server loop to the watcher; no value over the lock file.
  - Or: process enumeration by module path. Works, but imposes `System.Diagnostics.Process.GetProcesses()` which pulls in a non-trivial trimming surface.
- For graceful shutdown via `--uninstall`, the watcher must listen for a stop signal. Options:
  - **`SetConsoleCtrlHandler` + `GenerateConsoleCtrlEvent`**: requires the stopping process to be attached to the same console group. The watcher runs detached with no console, so this is awkward.
  - **Named event** (`EventWaitHandle` with `EventResetMode.ManualReset`, named `Local\KbFixWatcher.StopEvent`): the watcher's main loop checks the event between poll cycles (or waits on it with a timeout instead of `Thread.Sleep`). `--uninstall` opens the event and signals it. Clean, cross-process, trim-safe.
  - **Graceful `Process.Kill(entireProcessTree:false)`** as a hard fallback if the event-based shutdown doesn't complete within 3 seconds.

**Decision**:

- **Single-instance primitive**: `System.Threading.Mutex` named `Local\KbFixWatcher.Instance`. Acquired with `initiallyOwned: true` at watcher startup; if acquisition fails (already held) the second process logs "another KbFix watcher is already running in this session" and exits 0 (not an error — this is expected when autostart races with a manual launch).
- **Discovery for `--status` and `--install`**: probe the mutex with `Mutex.TryOpenExisting(name, out _)`. If the handle opens, a watcher exists.
- **PID decoration (optional in status output)**: watcher writes its own PID to `%LOCALAPPDATA%\KbFix\watcher.pid` after mutex acquisition and deletes the file on clean shutdown. `--status` reads the file only as a cosmetic "PID = …" decoration; the mutex is the authority. A stale PID file with no live mutex is ignored (and cleaned up by the next `--install` or `--status`).
- **Stop signal**: `EventWaitHandle` named `Local\KbFixWatcher.StopEvent` (manual-reset). Watcher main loop waits on it with a poll-interval timeout (`WaitHandle.WaitOne(pollInterval)`) instead of `Thread.Sleep`, so a signal can interrupt the sleep immediately. `--uninstall` opens the event via `EventWaitHandle.TryOpenExisting` and calls `Set()`, then waits up to 3 s for the mutex to become unowned, then falls back to `Process.Kill` on whatever process currently holds `Local\KbFixWatcher.Instance` (identified via the PID file).

**Rationale**:

- Entirely inside `System.Threading` + a one-line `File.WriteAllText` — no new dependencies, nothing COM-heavy for AOT/trimming to worry about.
- `Local\` scoping means different Windows sessions on the same machine (RDP host with multiple users) get independent watchers for free.
- Event-based stop gives a clean cooperative shutdown path; fallback to `Process.Kill` keeps `--uninstall` bounded in time (SC-004).

**Alternatives considered**:

- `Global\` mutex with SID suffix: unnecessary; more code to handle, no benefit for a per-session tool.
- Named pipe RPC: heavier; no requirement for two-way communication.
- File lock (FileShare.None) instead of mutex: file-based locks survive crashes only partially; mutex is the correct primitive.
- Windows `Job Object` with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE: solves the wrong problem (we don't want the watcher tied to the installer's lifetime; we want the opposite).

---

## R4. Launching a detached watcher from `--install`

**Question**: How should `--install`, running in an interactive console, spawn the watcher child so that closing the installer's terminal does NOT kill the watcher, and no console window appears for the watcher itself?

**Investigation**:

- `Process.Start` in .NET 8 with `ProcessStartInfo` supports several relevant flags:
  - `UseShellExecute = false` — invoke CreateProcess directly, no shell.
  - `CreateNoWindow = true` — CREATE_NO_WINDOW flag: child has no console window.
  - `RedirectStandardOutput/Error/Input = false` — do not keep pipes that tether the child to the parent.
  - Not calling `Process.WaitForExit()` and disposing the returned `Process` object — the parent no longer cares about the child.
- For a console subsystem exe (`<OutputType>Exe</OutputType>` with no `DisableWinExeOutputInference`), the child inherits the parent's console by default even with `CreateNoWindow = true`. The cleanest way to truly detach is to pass `DETACHED_PROCESS` (0x00000008) instead of `CREATE_NO_WINDOW` (0x08000000) via a `CreateProcess` P/Invoke — but that is more code and trimming surface.
- An easier alternative that works in practice: change the watcher process subsystem so its executable is already "windowless." But `KbFix.csproj` ships a single executable, not two.
- The **simplest and correct** approach: a plain console subsystem EXE, launched with `UseShellExecute = false`, `CreateNoWindow = true`, and no redirects. On Windows 10/11 this spawns a child in a new (hidden) console belonging to the child process; closing the parent's console does not affect it. Empirically this works for .NET 8 console apps.
- A safer alternative: use `cmd.exe /c start "" /B kbfix.exe --watch`. `start /B` explicitly starts without a new console window and the `/c` makes `cmd.exe` exit after spawning. But this introduces `cmd.exe` as a middleman and is uglier.

**Decision**:

- Primary: `Process.Start(new ProcessStartInfo { FileName = selfPath, Arguments = "--watch", UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = installedDirectory })`. No redirects. Returned `Process` is disposed immediately (we don't track it).
- If in manual testing this proves to still tie the watcher to the installer's console on any supported Windows build, fall back to the `DETACHED_PROCESS` P/Invoke path (documented as a known follow-up in `plan.md` complexity tracking).
- The watcher, on startup in `--watch` mode, calls `FreeConsole()` defensively as a belt-and-braces detach. This is a one-line P/Invoke already consistent with the codebase's Win32 interop style.

**Rationale**: Simplest approach first, verified manually (per constitution's manual-verification gate); fallback documented; no new dependencies.

**Alternatives considered**: `cmd.exe /c start /B` (rejected — middleman); creating a separate WinExe "kbfixwatcher.exe" subproject (rejected — doubles the binary count, contradicts Principle I).

---

## R5. Binary staging ("is the invoking path stable enough for autostart?")

**Question**: When the user runs `C:\Downloads\kbfix.exe --install`, should the tool point its autostart entry at `C:\Downloads\kbfix.exe` or copy itself somewhere more durable first?

**Investigation**:

- Downloads folders, USB sticks, and `%TEMP%` subpaths are all places a user might run `kbfix.exe` from. An autostart entry pointing at `C:\Downloads\kbfix.exe` will silently fail after the user cleans Downloads or the USB stick is unplugged.
- A stable per-user staging location is `%LOCALAPPDATA%\KbFix\` (e.g. `C:\Users\<user>\AppData\Local\KbFix\kbfix.exe`). It is per-user (no admin), survives profile operations, and is the same location the lock file already lives (R3).
- Copying the binary is cheap (single-file exe, ~15 MB target). It removes the stale-path class of bug entirely.

**Decision**:

- `--install` **always** stages the currently-executing binary to `%LOCALAPPDATA%\KbFix\kbfix.exe` (creating the directory if needed), overwriting any prior copy, then registers the autostart entry pointing at the staged path, then launches the watcher from the staged path.
- If `--install` is invoked on a binary that already lives at the staging path (self-install from the staged copy), it skips the copy and goes straight to Run-key registration + launch.
- `--uninstall` removes the Run key, signals the watcher to stop, and deletes the staged binary + its directory (best effort — if the staged binary is the one currently running `--uninstall`, deletion is deferred via `MoveFileEx(..., MOVEFILE_DELAY_UNTIL_REBOOT)` OR simply skipped with a message; decision: skip with a message. Leaving an unused exe in AppData is a tiny footprint; reboot-delay deletion is more machinery than it's worth).
- If the user later runs a *newer* `kbfix.exe` from somewhere else with `--install`, staging overwrites the old copy. `--status`, run from anywhere, reads the Run key to find the staged path and reports it.

**Rationale**:

- Eliminates the "binary moved after install" edge case listed in the spec.
- One well-known location simplifies `--status`, `--uninstall`, and the lock-file layout.
- Copying a ~15 MB file is imperceptible on any modern disk.

**Alternatives considered**:

- *Point the Run key at the invoking path as-is*: simpler code but fragile; surfaces as "watcher silently doesn't start after the user cleaned Downloads" — a classic unhappy path.
- *Stage to `%APPDATA%\KbFix\`* (roaming): might follow the user across machines via domain profile roaming, which could be surprising (autostart appears on a different machine). `%LOCALAPPDATA%` is the correct home for per-machine binaries.
- *Stage to `%PROGRAMFILES%`*: requires admin; violates FR-003.

---

## R6. Binary size reduction

**Question**: What csproj settings reduce the published binary from 80+ MB to the ≤20 MB target (SC-006), without breaking COM interop, P/Invoke, or the existing test suite?

**Investigation of current state** (`src/KbFix/KbFix.csproj`):

```
PublishSingleFile = true
SelfContained = true
IncludeNativeLibrariesForSelfExtract = true
InvariantGlobalization = true
```

Notably missing: `PublishTrimmed`. Self-contained single-file without trimming ships essentially the entire .NET 8 runtime (≈75 MB) inside the exe, explaining the 80 MB figure exactly.

**Trimming compatibility audit** of this codebase:

| Feature used | Trim-safe? | Notes |
|---|---|---|
| `Microsoft.Win32.Registry` | Yes | Static members, no reflection. |
| Win32 `[DllImport]` in `Platform/Win32Interop.cs` | Yes | Trim-root by default, P/Invoke stubs are not pruned. |
| TSF `[ComImport]` interfaces in `Platform/TsfInterop.cs` | Mostly yes | Runtime-callable COM wrappers work with trimming; trimmer can prune unused methods on COM interfaces. Need to mark the interface types as trim-roots (via `DynamicDependency`) OR ensure every method is reachable from static code (it already is, per `SessionLayoutGateway`). |
| `Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()` in `Program.cs:34` | Yes with a trim warning | Attribute is already preserved by `[assembly:]` metadata, but the trimmer may warn on `GetCustomAttribute<T>()`. Workaround: suppress the warning or replace with `typeof(Program).Assembly.GetName().Version` only. |
| `System.Threading.Mutex`, `EventWaitHandle` | Yes | BCL primitives, trim-rooted. |
| `Process.Start(ProcessStartInfo)` | Yes | BCL. |
| `Console` I/O | Yes | BCL. |

No JSON serialization, no reflection-based DI, no dynamically-loaded assemblies, no resources beyond defaults. **This codebase is an ideal trim candidate.**

**Size estimates** (from .NET 8 field reports on similar tiny console apps):

| Configuration | Expected size |
|---|---|
| Current (self-contained single-file, no trim) | 80+ MB (matches observation) |
| + `PublishTrimmed=true`, `TrimMode=partial` | ~25–35 MB |
| + `TrimMode=full` (aka `link`) | ~12–18 MB |
| + size-knob suite (`DebuggerSupport=false`, `StackTraceSupport=false`, `EventSourceSupport=false`, `HttpActivityPropagationSupport=false`, `UseSystemResourceKeys=true`, `MetadataUpdaterSupport=false`) | ~10–14 MB |
| Native AOT (`PublishAot=true`) | ~5–8 MB but **COM interop risk** in .NET 8 |

**Decision**: Apply the full trim-mode + size-knob suite, targeting ~12 MB measured. Native AOT is rejected for v1 due to COM-interop risk against `TsfInterop.cs` — it is revisitable in a later iteration if the risk can be isolated behind `[UnconditionalSuppressMessage]` / `[DynamicDependency]` annotations with confidence.

Concrete csproj additions (conceptual; exact XML lives in the implementation, not here):

- `PublishTrimmed = true`
- `TrimMode = full`
- `DebuggerSupport = false`
- `StackTraceSupport = false`
- `EventSourceSupport = false`
- `HttpActivityPropagationSupport = false`
- `UseSystemResourceKeys = true`
- `MetadataUpdaterSupport = false`
- `EnableUnsafeBinaryFormatterSerialization = false` (already default)
- Keep existing: `PublishSingleFile`, `SelfContained`, `IncludeNativeLibrariesForSelfExtract`, `InvariantGlobalization`.
- Replace `GetCustomAttribute<AssemblyInformationalVersionAttribute>()` in `Program.cs` with trim-safe alternative (`Assembly.GetName().Version` is enough since the version attribute is driven by `<Version>` in the csproj and already flows to `AssemblyName.Version`). This removes the one trim warning this codebase would produce.
- Add `[DynamicDependency]` on TSF `[ComImport]` interface types only if the first trimmed publish actually loses methods — do not over-annotate speculatively.
- `build.cmd` in release mode already passes `-c Release` to `dotnet publish`. Because the csproj already sets the publish properties, no new `build.cmd` flags are needed (FR-023).

**Rationale**:

- Hits SC-006 (≤20 MB, ideally <15 MB) with measured margin.
- Keeps all COM interop working because `TsfInterop.cs` uses static `[ComImport]` interface definitions referenced directly from static code in `SessionLayoutGateway`; the trimmer has a static reachability path to every method actually used.
- Manual release verification (per constitution Dev Workflow gate) will confirm the one-shot path still works after trimming — same test the constitution already requires for any release.

**Alternatives considered**:

- *Framework-dependent publish*: ~200 KB exe but requires the user to install .NET 8 runtime first. Violates FR-020 ("no pre-installed runtime") and SC-009.
- *Native AOT*: ~5–8 MB but COM interop in .NET 8 AOT is not a zero-risk path for `ITfInputProcessorProfiles`/`ITfInputProcessorProfileMgr`. Rejected this round; revisitable.
- *Just `TrimMode=partial`*: safer but misses the target by a comfortable margin (~30 MB). No reason not to go full when the audit says the code is trim-safe.
- *ReadyToRun*: marginal startup win, adds ~2–3 MB. Not worth it for a tool that launches at login once and then sits idle.

---

## R7. Logging and observability for the watcher

**Question**: The watcher is a long-lived background process. Principle V (Observability) says "a silent fixer is indistinguishable from a broken fixer." How does it report what it's doing without opening a window?

**Investigation**:

- Event Log: heavy, requires registering a source (admin on first use in some Windows versions), over-engineered for this tool.
- Rolling log file in `%LOCALAPPDATA%\KbFix\watcher.log`: simple, per-user, trimming-safe, easy to tail when troubleshooting. Size-bounded by truncating at a small cap (e.g. 64 KB) and rotating once to `watcher.log.1`.
- stderr / stdout: watcher has no attached console, so these go nowhere — useless on their own.
- ETW: trim-unfriendly (`EventSource`); already disabled via `EventSourceSupport=false`.

**Decision**:

- Watcher writes a minimal text log to `%LOCALAPPDATA%\KbFix\watcher.log`. Each line is `YYYY-MM-DDTHH:mm:ssZ <level> <event>`. Events of interest: `start`, `stop`, `reconcile-noop`, `reconcile-applied count=N`, `reconcile-failed reason=...`, `flap-backoff`, `config-read-failed`, `session-empty-refused`.
- Log rotates when it exceeds 64 KB: rename `.log` → `.log.1` (overwrite), start fresh `.log`. One-deep rotation is plenty for a personal utility.
- The one-shot mode is **unchanged**; it still writes its report to stdout (FR-018).
- `--status` mentions the log path in its output so a troubleshooting user can find it instantly.

**Rationale**: Zero-dependency, trim-safe, size-bounded, discoverable. Satisfies Principle V for a background process.

**Alternatives considered**: Event Log (too heavy), `Microsoft.Extensions.Logging` (new dependency), stdout (invisible to a detached process).

---

## R8. Testability

**Question**: How do the new components get unit-tested without spinning up a real Windows session?

**Investigation**:

- The existing test project (`tests/KbFix.Tests`) tests pure domain logic (`ReconciliationPlan`, `LayoutSet`, etc.) without touching Win32. That model is the blueprint.
- New pure-logic components:
  - Install / uninstall / status state machine (given `(autostartRegistered, watcherRunning) → WhatToDo`) — pure function, easy to test.
  - Flap detector (given timestamps → should back off?) — pure function.
  - Watcher poll-loop logic without the actual `SessionLayoutGateway` — abstract the gateway behind an `ISessionLayoutGateway`-style seam (already exists as a class; extract a minimal interface limited to what the watcher uses).
- Integration with the actual Run key, mutex, event, and `%LOCALAPPDATA%` is exercised manually per the constitution's manual-verification gate. No test harness for real Windows interop is in scope for v1.

**Decision**:

- Add these new testable units:
  - `InstallDecision` — pure state machine for `--install` / `--uninstall` / `--status`.
  - `FlapDetector` — pure sliding-window counter with injected clock.
  - `WatcherLoop` — pure orchestration with injected gateway, clock, logger, and cancellation source.
- Introduce a minimal interface `ISessionReconciler` that wraps the existing `(PersistedConfigReader.ReadRaw, SessionLayoutGateway.*)` flow, so `WatcherLoop` can be tested against a fake.
- Concrete registry / mutex / process-detach code stays in a thin `Platform/Install/*.cs` set, exercised only via manual verification.

**Rationale**: Keeps unit-test coverage on every piece of logic that has branches; avoids the trap of trying to mock Win32 APIs; keeps with the existing project's testing philosophy.

---

## Unknowns resolved

Every "NEEDS CLARIFICATION" candidate that this feature could have raised is now decided:

- Detection: polling (R1). ✓
- Hosting model: user-session console process (spec assumption + R1). ✓
- Autostart: `HKCU\Run` only (R2). ✓
- Single-instance primitive: `Local\` named mutex + stop event + optional PID file (R3). ✓
- Detach from installer console: `ProcessStartInfo { UseShellExecute=false, CreateNoWindow=true }` + defensive `FreeConsole()` (R4). ✓
- Binary staging: always stage to `%LOCALAPPDATA%\KbFix\` (R5). ✓
- Binary size: `PublishTrimmed=true` + `TrimMode=full` + size-knob suite, not AOT (R6). ✓
- Logging: bounded `watcher.log` in `%LOCALAPPDATA%\KbFix\` (R7). ✓
- Testing strategy: pure-logic units + manual verification per constitution (R8). ✓

No `NEEDS CLARIFICATION` markers remain for the plan template to inherit.
