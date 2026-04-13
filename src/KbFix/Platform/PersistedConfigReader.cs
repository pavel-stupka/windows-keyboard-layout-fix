using System.Globalization;
using System.Runtime.Versioning;
using KbFix.Domain;
using Microsoft.Win32;

namespace KbFix.Platform;

/// <summary>
/// Reads the user's persisted keyboard configuration. The authoritative source
/// is <c>HKCU\Control Panel\International\User Profile\&lt;langtag&gt;</c> —
/// each subkey is a configured input language and its values of the form
/// <c>XXXX:YYYYYYYY</c> (DWORD) explicitly state which keyboard layouts the
/// user added under that language. This survives the RDP "added layout" bug
/// because the bug pollutes the runtime <c>Preload</c> list, not <c>User Profile</c>.
///
/// Falls back to <c>HKCU\Keyboard Layout\Preload</c> on legacy Windows where
/// <c>User Profile</c> is missing.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class PersistedConfigReader
{
    private const string UserProfileSubKey = @"Control Panel\International\User Profile";
    private const string PreloadSubKey = @"Keyboard Layout\Preload";
    private const string SubstitutesSubKey = @"Keyboard Layout\Substitutes";

    /// <summary>
    /// Returns the raw user-configured (langId, klid) pairs. The session
    /// gateway is responsible for resolving each pair to the runtime HKL form
    /// because the static (langId, klid) → HKL formula is incomplete: ctfmon
    /// can rewrite the high word into the 0xFxxx range for substituted
    /// layouts and only Windows knows the current value.
    /// </summary>
    public static IReadOnlyList<(ushort LangId, string Klid)> ReadRaw()
    {
        var fromUserProfile = TryReadRawFromUserProfile();
        if (fromUserProfile.Count > 0)
        {
            return fromUserProfile;
        }

        var fromPreload = TryReadRawFromPreload();
        if (fromPreload.Count > 0)
        {
            return fromPreload;
        }

        throw new PlatformNotSupportedException(
            @"Could not read the user's persisted keyboard layout configuration from "
            + @"either HKCU\Control Panel\International\User Profile or HKCU\Keyboard Layout\Preload.");
    }

    /// <summary>
    /// Backward-compat wrapper used only by code paths that have not yet been
    /// migrated to the raw flow. Returns a synthetic <see cref="PersistedConfig"/>
    /// that uses the literal KLID strings as identifiers — this is wrong for
    /// runtime comparison and should not be relied on outside diagnostics.
    /// </summary>
    public static PersistedConfig Read()
    {
        var raw = ReadRaw();
        var ids = new List<LayoutId>(raw.Count);
        foreach (var (langId, klid) in raw)
        {
            ids.Add(LayoutId.Create(langId, klid));
        }
        return new PersistedConfig(new LayoutSet(ids), DateTime.UtcNow);
    }

    private static IReadOnlyList<(ushort, string)> TryReadRawFromUserProfile()
    {
        var result = new List<(ushort, string)>();
        using var root = Registry.CurrentUser.OpenSubKey(UserProfileSubKey, writable: false);
        if (root is null)
        {
            return result;
        }

        foreach (var subName in root.GetSubKeyNames())
        {
            using var sub = root.OpenSubKey(subName, writable: false);
            if (sub is null)
            {
                continue;
            }

            foreach (var valueName in sub.GetValueNames())
            {
                if (valueName.Length != 13 || valueName[4] != ':')
                {
                    continue;
                }

                if (!ushort.TryParse(
                        valueName.AsSpan(0, 4),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var langId)
                    || langId == 0)
                {
                    continue;
                }

                var klid = valueName.Substring(5).ToLowerInvariant();
                if (klid.Length != 8)
                {
                    continue;
                }

                result.Add((langId, klid));
            }
        }

        return result;
    }

    private static IReadOnlyList<(ushort, string)> TryReadRawFromPreload()
    {
        var result = new List<(ushort, string)>();
        using var preload = Registry.CurrentUser.OpenSubKey(PreloadSubKey, writable: false);
        if (preload is null)
        {
            return result;
        }

        using var substitutes = Registry.CurrentUser.OpenSubKey(SubstitutesSubKey, writable: false);

        foreach (var name in preload.GetValueNames())
        {
            var raw = preload.GetValue(name) as string;
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var klid = raw;
            if (substitutes is not null)
            {
                var sub = substitutes.GetValue(klid) as string;
                if (!string.IsNullOrWhiteSpace(sub))
                {
                    klid = sub;
                }
            }

            if (klid.Length != 8 || !ushort.TryParse(
                    klid.AsSpan(4, 4),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var langId)
                || langId == 0)
            {
                continue;
            }

            result.Add((langId, klid.ToLowerInvariant()));
        }

        return result;
    }
}
