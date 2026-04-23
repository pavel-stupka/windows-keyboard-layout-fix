using System.Runtime.Versioning;
using KbFix.Watcher;
using Microsoft.Win32;

namespace KbFix.Platform.Install;

/// <summary>
/// Read-only probe for the per-user Startup-Apps toggle that Task Manager
/// (and the Windows Settings Startup page) manage for every <c>HKCU\Run</c>
/// entry. The backing store is a binary value under
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run</c>.
/// If the value is absent, the entry is enabled (Windows default); byte 0
/// bit 0 clear means enabled, byte 0 bit 0 set means user-disabled.
/// See <c>specs/004-watcher-resilience/research.md</c> §R4.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class StartupApprovedProbe
{
    /// <summary>
    /// Returns <c>true</c> when the user's Run-key entry for
    /// <see cref="WatcherInstallation.RunKeyValueName"/> is either absent
    /// from StartupApproved\Run (Windows default = enabled) or marked
    /// enabled. Returns <c>false</c> only when it is explicitly disabled.
    /// </summary>
    public static bool IsRunKeyApproved(Func<RegistryKey?>? openSubKey = null)
    {
        try
        {
            using var key = openSubKey is null
                ? Registry.CurrentUser.OpenSubKey(WatcherInstallation.StartupApprovedSubKey, writable: false)
                : openSubKey();

            if (key is null)
            {
                return true;
            }
            if (key.GetValue(WatcherInstallation.RunKeyValueName) is not byte[] bytes || bytes.Length == 0)
            {
                return true;
            }
            // Byte 0 low bit: 0 = enabled, 1 = user-disabled. Microsoft
            // actually ships two sentinel prefixes: 0x02 (enabled),
            // 0x03 (disabled by user).
            return (bytes[0] & 0x01) == 0;
        }
        catch
        {
            return true;
        }
    }
}
