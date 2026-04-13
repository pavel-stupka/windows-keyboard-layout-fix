using System.Globalization;
using System.Runtime.Versioning;
using KbFix.Domain;
using Microsoft.Win32;

namespace KbFix.Platform;

/// <summary>
/// Read-only catalog of all installed keyboard layouts on this machine,
/// built from <c>HKLM\SYSTEM\CurrentControlSet\Control\Keyboard Layouts</c>.
///
/// Recovers <see cref="LayoutId"/>s from runtime HKLs by understanding the
/// three ways Windows packs an HKL:
///   1. Native loading: HKL high word == HKL low word == langId. KLID is the
///      8-hex form of the langId.
///   2. Cross-language loading: HKL high word is itself a real langId belonging
///      to another installed language. The user is typing with that other
///      language's default layout while the input slot belongs to a different
///      language (this is the "English layout under Czech" case).
///   3. Variant loading: HKL high word is a small "Layout Id" value (e.g.
///      0x0001 for Czech QWERTY) defined under a specific KLID's registry
///      entry. KLID is that one.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class KeyboardLayoutCatalog
{
    private const string CatalogKey = @"SYSTEM\CurrentControlSet\Control\Keyboard Layouts";

    /// <summary>All KLIDs in the catalog (raw subkey names).</summary>
    private readonly HashSet<string> _allKlids = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>LangIds that own at least one KLID in the catalog (real installed languages).</summary>
    private readonly HashSet<ushort> _knownLangIds = new();

    /// <summary>(langId, layoutId) → KLID, for variant lookups.</summary>
    private readonly Dictionary<(ushort langId, ushort layoutId), string> _variantIndex = new();

    public static KeyboardLayoutCatalog Load()
    {
        var c = new KeyboardLayoutCatalog();
        using var root = Registry.LocalMachine.OpenSubKey(CatalogKey, writable: false);
        if (root is null)
        {
            return c;
        }

        foreach (var subName in root.GetSubKeyNames())
        {
            if (subName.Length != 8)
            {
                continue;
            }
            if (!uint.TryParse(subName, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var klidValue))
            {
                continue;
            }

            var langId = (ushort)(klidValue & 0xFFFF);
            if (langId == 0)
            {
                continue;
            }

            c._allKlids.Add(subName);
            c._knownLangIds.Add(langId);

            using var sub = root.OpenSubKey(subName, writable: false);
            if (sub is null)
            {
                continue;
            }

            var layoutIdValue = sub.GetValue("Layout Id") as string;
            if (!string.IsNullOrEmpty(layoutIdValue) &&
                ushort.TryParse(layoutIdValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var layoutIdNum))
            {
                c._variantIndex[(langId, layoutIdNum)] = subName;
            }
        }

        return c;
    }

    /// <summary>
    /// Convert a runtime HKL into a (langId, klid) pair, applying the
    /// native / cross-language / variant rules described above.
    /// </summary>
    public LayoutId NormalizeFromHkl(uint hkl)
    {
        var low = (ushort)(hkl & 0xFFFF);            // input language slot
        var high = (ushort)((hkl >> 16) & 0xFFFF);   // packed identifier

        string klid;

        if (high == low)
        {
            // Case 1: native loading. Default layout for this language.
            // The default KLID is "<langid>0000" hex (low word = langId, high word = 0).
            klid = ((uint)low).ToString("x8", CultureInfo.InvariantCulture);
        }
        else if (high < 0xF000 && _knownLangIds.Contains(high))
        {
            // Case 2: cross-language load. The high word is a real installed
            // language; the user is using that language's default layout
            // under a different input language slot.
            klid = ((uint)high).ToString("x8", CultureInfo.InvariantCulture);
        }
        else if (_variantIndex.TryGetValue((low, high), out var variantKlid))
        {
            // Case 3: variant load. High word matches a registered Layout Id.
            klid = variantKlid;
        }
        else
        {
            // Unknown. Keep the literal HKL bytes as a KLID — it will be
            // treated as session-only because nothing in the persisted set
            // can match this synthetic identifier.
            klid = hkl.ToString("x8", CultureInfo.InvariantCulture);
        }

        return LayoutId.Create(low, klid);
    }

    /// <summary>
    /// Best-effort reverse mapping from a (langId, klid) pair to an HKL value
    /// suitable for <c>UnloadKeyboardLayout</c>. Returns <see cref="IntPtr.Zero"/>
    /// when the catalog cannot resolve the pair.
    /// </summary>
    public IntPtr TryHklFor(LayoutId id)
    {
        var klidLow = (ushort)0;
        if (id.Klid.Length == 8 &&
            uint.TryParse(id.Klid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var klidValue))
        {
            klidLow = (ushort)(klidValue & 0xFFFF);
        }
        else
        {
            return IntPtr.Zero;
        }

        ushort high;

        if (klidLow == id.LangId)
        {
            // Native: HKL = (langId, langId) — UNLESS the KLID has its own Layout Id,
            // in which case the variant high-word applies.
            high = id.LangId;
            using var root = Registry.LocalMachine.OpenSubKey(CatalogKey, writable: false);
            using var sub = root?.OpenSubKey(id.Klid, writable: false);
            var layoutIdValue = sub?.GetValue("Layout Id") as string;
            if (!string.IsNullOrEmpty(layoutIdValue) &&
                ushort.TryParse(layoutIdValue, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var liNum))
            {
                high = liNum;
            }
        }
        else
        {
            // Cross-language load: HKL high = native langId of the KLID, low = slot.
            high = klidLow;
        }

        var hkl = ((uint)high << 16) | id.LangId;
        return new IntPtr((long)hkl);
    }
}
