using System.Globalization;

namespace KbFix.Domain;

/// <summary>
/// Stable identifier for a single Windows keyboard layout / TSF input profile.
/// Equality is based on all three fields — comparing only by <see cref="LangId"/>
/// is forbidden because the bug we are fixing relies on multiple layouts sharing
/// a language (e.g. cs-CZ QWERTY vs cs-CZ QWERTZ).
/// </summary>
internal readonly record struct LayoutId
{
    public ushort LangId { get; }

    /// <summary>Keyboard Layout ID — exactly 8 hex digits.</summary>
    public string Klid { get; }

    /// <summary>TSF profile GUID; null for pure legacy HKL-only layouts.</summary>
    public Guid? ProfileGuid { get; }

    private LayoutId(ushort langId, string klid, Guid? profileGuid)
    {
        LangId = langId;
        Klid = klid;
        ProfileGuid = profileGuid;
    }

    public static LayoutId Create(ushort langId, string klid, Guid? profileGuid = null)
    {
        if (langId == 0)
        {
            throw new ArgumentException("LangId must be non-zero.", nameof(langId));
        }

        if (klid is null || klid.Length != 8 || !IsHex(klid))
        {
            throw new ArgumentException(
                $"Klid must be exactly 8 hex digits (got '{klid}').", nameof(klid));
        }

        // Normalise to lowercase so equality is case-insensitive in practice.
        return new LayoutId(langId, klid.ToLowerInvariant(), profileGuid);
    }

    /// <summary>
    /// Deterministic ordering by (<see cref="LangId"/>, <see cref="Klid"/>).
    /// Used for stable report output and stable plan-action ordering.
    /// </summary>
    public int Compare(LayoutId other)
    {
        var byLang = LangId.CompareTo(other.LangId);
        if (byLang != 0)
        {
            return byLang;
        }

        return string.CompareOrdinal(Klid, other.Klid);
    }

    public override string ToString()
    {
        return string.Create(CultureInfo.InvariantCulture, $"{LangId:X4} {Klid}");
    }

    private static bool IsHex(string s)
    {
        foreach (var c in s)
        {
            var ok = (c >= '0' && c <= '9')
                  || (c >= 'a' && c <= 'f')
                  || (c >= 'A' && c <= 'F');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
