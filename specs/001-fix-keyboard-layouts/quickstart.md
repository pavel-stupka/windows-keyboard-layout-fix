# Quickstart: Fix Windows Session Keyboard Layouts

**Feature**: 001-fix-keyboard-layouts

This is the minimal "build it, run it, verify it" path for the v1 utility.

## Prerequisites

- Windows 10 or Windows 11 (the bug being fixed primarily affects RDP sessions on these versions).
- .NET 8 SDK installed for the build machine. (End users do **not** need .NET — the published binary is self-contained.)
- A normal user account. No Administrator rights required.

## Build

From the repository root:

```powershell
dotnet publish src/KbFix/KbFix.csproj `
  -c Release `
  -r win-x64 `
  --self-contained `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o dist
```

The single-file binary is written to `dist/kbfix.exe`. Copy it anywhere; it
has no install step and no runtime dependency.

## Run (one-shot fix)

Double-click `kbfix.exe`, or from a terminal:

```powershell
.\kbfix.exe
```

Expected output on a polluted session:

```text
Persisted layouts:
  - 0405  00000405  Czech (QWERTY)
  - 0409  00000409  English (United States)

Session layouts:
  - 0405  00000405  Czech (QWERTY)
  - 0405  00010405  Czech (QWERTZ)
  - 0409  00000409  English (United States)

Actions:
  - Deactivate 0405 00010405: OK

Result: SUCCESS — removed 1 session-only layout(s); persisted set unchanged.
```

Exit code: `0`.

## Run (preview only)

```powershell
.\kbfix.exe --dry-run
```

Same report, but every action line is prefixed `(dry-run)` and nothing is
modified.

## Manual verification (mandatory before any release)

Per the project constitution, every release MUST be reproduced against the
real bug end-to-end:

1. From another machine, RDP into the test machine. Confirm that Windows
   silently injects an extra keyboard layout into the session (compare the
   language bar against `HKCU\Keyboard Layout\Preload`).
2. Run `kbfix.exe`. Confirm:
   - The unwanted layout is gone from the language bar.
   - You can still type in every persisted layout.
   - `HKCU\Keyboard Layout\Preload` is unchanged (use `reg query` before
     and after).
3. Run `kbfix.exe` again immediately. Confirm `Result: NO-OP` and exit `0`
   (idempotency check, SC-003).
4. Run `kbfix.exe --dry-run` against a freshly polluted session. Confirm the
   report lists the removal but the language bar is unchanged afterward.

If any of (1)–(4) fails, the release is blocked.

## Unit tests

```powershell
dotnet test tests/KbFix.Tests/KbFix.Tests.csproj
```

The unit suite covers the pure reconciliation logic (`ReconciliationPlan`
construction): no-op, simple removal, switch-before-remove, empty persisted
set, no-fallback-available, and idempotency-on-replay.

## Troubleshooting

| Symptom                                                                 | Likely cause / fix                                                                                  |
|-------------------------------------------------------------------------|-----------------------------------------------------------------------------------------------------|
| Exit code 2, message about unsupported platform                         | Running on a Windows version without TSF profile manager. Not supported in v1.                      |
| Exit code 3, "persisted set is empty"                                   | The user's `Preload` registry key is empty or missing. Fix in Windows Settings before retrying.     |
| Exit code 1 with a Win32 error code                                     | Capture stderr and the error code; that pair is the starting point for any bug report.             |
| `kbfix.exe` is blocked by SmartScreen                                   | Expected for an unsigned binary. Click "More info" → "Run anyway". Code signing is out of v1 scope. |
