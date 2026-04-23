# windows_keyboard_layout Development Guidelines

Auto-generated from all feature plans. Last updated: 2026-04-23

## Active Technologies
- C# 12 on .NET 8 (LTS) + Windows Text Services Framework (TSF) via COM interop (`ITfInputProcessorProfileMgr`, `ITfInputProcessorProfiles`); Win32 input-locale APIs via P/Invoke (`GetKeyboardLayoutList`, `LoadKeyboardLayout`, `UnloadKeyboardLayout`, `ActivateKeyboardLayout`); standard `Microsoft.Win32.Registry` for reading `HKCU\Keyboard Layout\Preload`. No third-party NuGet packages. (001-fix-keyboard-layouts)
- None. The utility reads `HKCU\Keyboard Layout\Preload` (read-only) and the live TSF session state. It writes nothing to disk. (001-fix-keyboard-layouts)
- Windows batch (`cmd.exe`) for `build.cmd`; builds C# 12 on .NET 8 (LTS), unchanged from the existing project. + .NET 8 SDK (`dotnet` CLI) on `PATH`. No new NuGet packages, no new scripting runtimes. The existing `KbFix.sln`, `src/KbFix/KbFix.csproj`, and `tests/KbFix.Tests/KbFix.Tests.csproj` are reused as-is. (002-build-script)
- Filesystem only. The script reads nothing persistent, writes only to the output directory (`dist/` by default) and to stdout/stderr. (002-build-script)

- (001-fix-keyboard-layouts)

## Project Structure

```text
src/
tests/
```

## Commands

# Add commands for 

## Code Style

: Follow standard conventions

## Recent Changes
- 002-build-script: Added Windows batch (`cmd.exe`) for `build.cmd`; builds C# 12 on .NET 8 (LTS), unchanged from the existing project. + .NET 8 SDK (`dotnet` CLI) on `PATH`. No new NuGet packages, no new scripting runtimes. The existing `KbFix.sln`, `src/KbFix/KbFix.csproj`, and `tests/KbFix.Tests/KbFix.Tests.csproj` are reused as-is.
- 001-fix-keyboard-layouts: Added C# 12 on .NET 8 (LTS) + Windows Text Services Framework (TSF) via COM interop (`ITfInputProcessorProfileMgr`, `ITfInputProcessorProfiles`); Win32 input-locale APIs via P/Invoke (`GetKeyboardLayoutList`, `LoadKeyboardLayout`, `UnloadKeyboardLayout`, `ActivateKeyboardLayout`); standard `Microsoft.Win32.Registry` for reading `HKCU\Keyboard Layout\Preload`. No third-party NuGet packages.

- 001-fix-keyboard-layouts: Added

<!-- MANUAL ADDITIONS START -->
<!-- MANUAL ADDITIONS END -->
