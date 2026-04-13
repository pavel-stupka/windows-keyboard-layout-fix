using System.Runtime.Versioning;
using KbFix.Watcher;
using Microsoft.Win32;

namespace KbFix.Platform.Install;

/// <summary>
/// Per-user autostart registration via
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run\KbFixWatcher</c>.
/// Pure registry I/O; no elevation required.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class AutostartRegistry
{
    public static string? TryReadRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WatcherInstallation.RunKeySubKey, writable: false);
            return key?.GetValue(WatcherInstallation.RunKeyValueName) as string;
        }
        catch
        {
            return null;
        }
    }

    public static void WriteRunKey(string stagedBinaryPath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(WatcherInstallation.RunKeySubKey, writable: true);
        if (key is null)
        {
            throw new InvalidOperationException(
                $"Failed to open HKCU\\{WatcherInstallation.RunKeySubKey} for writing.");
        }
        var value = FormatRunKeyValue(stagedBinaryPath);
        key.SetValue(WatcherInstallation.RunKeyValueName, value, RegistryValueKind.String);
    }

    /// <summary>Removes the Run key value. Returns true if it was present.</summary>
    public static bool DeleteRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(WatcherInstallation.RunKeySubKey, writable: true);
            if (key is null)
            {
                return false;
            }
            if (key.GetValue(WatcherInstallation.RunKeyValueName) is null)
            {
                return false;
            }
            key.DeleteValue(WatcherInstallation.RunKeyValueName, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string FormatRunKeyValue(string stagedBinaryPath) =>
        $"\"{stagedBinaryPath}\" --watch";
}
