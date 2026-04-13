# windows_keyboard_layout Development Guidelines

Auto-generated from all feature plans. Last updated: 2026-04-10

## Active Technologies
- C# 12 on .NET 8 (LTS) + Windows Text Services Framework (TSF) via COM interop (`ITfInputProcessorProfileMgr`, `ITfInputProcessorProfiles`); Win32 input-locale APIs via P/Invoke (`GetKeyboardLayoutList`, `LoadKeyboardLayout`, `UnloadKeyboardLayout`, `ActivateKeyboardLayout`); standard `Microsoft.Win32.Registry` for reading `HKCU\Keyboard Layout\Preload`. No third-party NuGet packages. (001-fix-keyboard-layouts)
- None. The utility reads `HKCU\Keyboard Layout\Preload` (read-only) and the live TSF session state. It writes nothing to disk. (001-fix-keyboard-layouts)

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
- 001-fix-keyboard-layouts: Added C# 12 on .NET 8 (LTS) + Windows Text Services Framework (TSF) via COM interop (`ITfInputProcessorProfileMgr`, `ITfInputProcessorProfiles`); Win32 input-locale APIs via P/Invoke (`GetKeyboardLayoutList`, `LoadKeyboardLayout`, `UnloadKeyboardLayout`, `ActivateKeyboardLayout`); standard `Microsoft.Win32.Registry` for reading `HKCU\Keyboard Layout\Preload`. No third-party NuGet packages.

- 001-fix-keyboard-layouts: Added

<!-- MANUAL ADDITIONS START -->
<!-- MANUAL ADDITIONS END -->
