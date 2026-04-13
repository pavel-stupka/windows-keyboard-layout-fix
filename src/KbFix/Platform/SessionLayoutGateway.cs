using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KbFix.Domain;

namespace KbFix.Platform;

/// <summary>
/// Reads and mutates the keyboard input layouts in the current Windows
/// session via the legacy <c>GetKeyboardLayoutList</c> / <c>UnloadKeyboardLayout</c>
/// API. Works with HKL values as the canonical identifier — every
/// <see cref="LayoutId"/> seen by the rest of the program holds an HKL hex
/// string in its <c>Klid</c> field, so persisted and session items compare
/// directly by HKL.
///
/// The hard part is converting the user's persisted (langId, klid) pairs
/// from <c>HKCU\Control Panel\International\User Profile</c> into the runtime
/// HKL form Windows actually loads them as. We use two complementary sources:
///   1. Static cross-language formula: when the user has a layout from
///      another language under their language slot, the HKL is
///      <c>(native_langid &lt;&lt; 16) | slot_langid</c>.
///   2. <c>LoadKeyboardLayout</c> with <c>KLF_SUBSTITUTE_OK</c>: asks Windows
///      directly. Catches the substitute high-word forms (e.g. <c>0xF0050405</c>)
///      that ctfmon emits for variant layouts and that no static formula
///      can predict.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SessionLayoutGateway : IDisposable
{
    private bool _disposed;

    public SessionLayoutGateway()
    {
        // No COM bring-up needed for the legacy path. The constructor stays
        // so the orchestrator's lifetime model doesn't change.
    }

    /// <summary>
    /// Resolve the user's persisted (langId, klid) pairs into the set of HKLs
    /// that should be considered "wanted" in the current session, plus a
    /// display map from each resolved <see cref="LayoutId"/> back to its
    /// original KLID for human-readable reporting.
    /// </summary>
    public ResolvedPersisted ResolvePersisted(IReadOnlyList<(ushort LangId, string Klid)> raw)
    {
        EnsureNotDisposed();
        var debug = Environment.GetEnvironmentVariable("KBFIX_DEBUG") == "1";

        var hkls = new HashSet<uint>();
        var displayMap = new Dictionary<LayoutId, string>();

        // Snapshot the existing process HKLs so we can later unload anything
        // we added during resolution and not pollute the process state.
        var beforeSnapshot = new HashSet<IntPtr>(SnapshotHkls());

        var loadedByMe = new List<IntPtr>();

        foreach (var (langId, klid) in raw)
        {
            // Parse the klid as a 32-bit hex value to get its native langId.
            if (!uint.TryParse(klid, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var klidValue))
            {
                continue;
            }
            var nativeLangId = (ushort)(klidValue & 0xFFFF);

            // -- Static candidate(s) --
            if (nativeLangId == langId)
            {
                // Native loading: HKL high word is either the Layout Id from
                // the catalog (variant layouts) or the langId itself (default).
                var layoutIdHigh = LookupLayoutIdHighWord(klid) ?? langId;
                var nativeHkl = ((uint)layoutIdHigh << 16) | langId;
                AddCandidate(nativeHkl, langId, klid);
            }
            else
            {
                // Cross-language: e.g. (0x0405, "00000409") = US layout under
                // Czech slot. Runtime HKL = native langId in high word, slot
                // langId in low word.
                var crossHkl = ((uint)nativeLangId << 16) | langId;
                AddCandidate(crossHkl, langId, klid);
            }

            // -- Dynamic candidate via LoadKeyboardLayout --
            // Catches the 0xFxxx substitute forms ctfmon emits for variant
            // layouts that the static formula above can't predict.
            //
            // We only trust the dynamic result for the NATIVE-langId case.
            // For cross-language entries the API doesn't know which slot we
            // mean and would return the layout's native HKL form, which may
            // collide with what is actually pollution (e.g. it would return
            // 0x04090409 for "00000409", and that HKL is exactly Pavel's
            // unwanted English-language entry).
            if (nativeLangId == langId)
            {
                var hkl = Win32Interop.LoadKeyboardLayout(
                    klid,
                    Win32Interop.KLF_SUBSTITUTE_OK | Win32Interop.KLF_NOTELLSHELL);
                if (hkl != IntPtr.Zero)
                {
                    var v = (uint)hkl.ToInt64();
                    AddCandidate(v, langId, klid);
                    if (!beforeSnapshot.Contains(hkl))
                    {
                        loadedByMe.Add(hkl);
                    }
                }
            }

            if (debug)
            {
                Console.Error.WriteLine($"[debug] persisted ({langId:X4},{klid}) → candidates so far: {string.Join(",", hkls.Select(x => x.ToString("X8")))}");
            }
        }

        // Cleanup: unload anything LoadKeyboardLayout added to our process
        // that wasn't there before. This keeps our process state observable to
        // any post-resolve ReadSession() call as a true mirror of the session.
        foreach (var hkl in loadedByMe)
        {
            Win32Interop.UnloadKeyboardLayout(hkl);
        }

        var ids = new List<LayoutId>(hkls.Count);
        foreach (var v in hkls)
        {
            var langId = (ushort)(v & 0xFFFF);
            if (langId == 0)
            {
                continue;
            }
            ids.Add(LayoutId.Create(langId, v.ToString("x8", CultureInfo.InvariantCulture)));
        }

        return new ResolvedPersisted(new LayoutSet(ids), displayMap);

        void AddCandidate(uint hklValue, ushort langId, string klid)
        {
            if (!hkls.Add(hklValue))
            {
                return;
            }
            var lowLang = (ushort)(hklValue & 0xFFFF);
            var key = LayoutId.Create(lowLang == 0 ? langId : lowLang,
                hklValue.ToString("x8", CultureInfo.InvariantCulture));
            displayMap[key] = klid;
        }
    }

    public SessionState ReadSession() => ReadSession(extraLangIds: Array.Empty<ushort>());

    public SessionState ReadSession(IReadOnlyCollection<ushort> extraLangIds)
    {
        EnsureNotDisposed();
        var debug = Environment.GetEnvironmentVariable("KBFIX_DEBUG") == "1";

        var hkls = SnapshotHkls();
        var ids = new List<LayoutId>(hkls.Length);
        LayoutId? activeLayout = null;
        var activeHkl = Win32Interop.GetKeyboardLayout(0);

        foreach (var hkl in hkls)
        {
            if (hkl == IntPtr.Zero)
            {
                continue;
            }
            var v = (uint)hkl.ToInt64();
            var langId = (ushort)(v & 0xFFFF);
            if (langId == 0)
            {
                continue;
            }
            var id = LayoutId.Create(langId, v.ToString("x8", CultureInfo.InvariantCulture));
            ids.Add(id);
            if (debug)
            {
                Console.Error.WriteLine($"[debug] session HKL 0x{v:X8} → ({langId:X4},{id.Klid})");
            }
            if (hkl == activeHkl)
            {
                activeLayout = id;
            }
        }

        var set = new LayoutSet(ids);
        if (set.Count == 0)
        {
            throw new PlatformNotSupportedException(
                "GetKeyboardLayoutList returned no entries for this process.");
        }

        return new SessionState(set, activeLayout ?? set.Sorted().First(), DateTime.UtcNow);
    }

    public AppliedAction Apply(PlannedAction action)
    {
        EnsureNotDisposed();

        try
        {
            // Klid is now the HKL hex form. Parse it back into an HKL handle
            // and call the legacy Win32 API directly.
            if (!uint.TryParse(
                    action.LayoutId.Klid,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var hklValue))
            {
                throw new InvalidOperationException(
                    $"Could not parse HKL from {action.LayoutId.Klid}.");
            }

            var hkl = new IntPtr((long)hklValue);

            switch (action.Kind)
            {
                case ActionKind.SwitchActive:
                    Win32Interop.ActivateKeyboardLayout(hkl, Win32Interop.KLF_REORDER);
                    break;

                case ActionKind.Deactivate:
                    if (!Win32Interop.UnloadKeyboardLayout(hkl))
                    {
                        var err = Marshal.GetLastWin32Error();
                        throw new Win32Exception(err);
                    }
                    break;
            }

            return new AppliedAction(action, Succeeded: true, Failure: null);
        }
        catch (Win32Exception ex)
        {
            return new AppliedAction(
                action,
                Succeeded: false,
                Failure: $"Win32 error {ex.NativeErrorCode}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new AppliedAction(action, Succeeded: false, Failure: ex.Message);
        }
    }

    public bool VerifyConverged(LayoutSet persistedHkls)
    {
        EnsureNotDisposed();
        var session = ReadSession();
        return session.Layouts.Difference(persistedHkls).Count == 0;
    }

    private static IntPtr[] SnapshotHkls()
    {
        var count = Win32Interop.GetKeyboardLayoutList(0, null);
        if (count <= 0)
        {
            return Array.Empty<IntPtr>();
        }
        var hkls = new IntPtr[count];
        Win32Interop.GetKeyboardLayoutList(count, hkls);
        return hkls;
    }

    private static ushort? LookupLayoutIdHighWord(string klid)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Control\Keyboard Layouts\{klid}",
                writable: false);
            var v = key?.GetValue("Layout Id") as string;
            if (!string.IsNullOrEmpty(v) &&
                ushort.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n))
            {
                return n;
            }
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private void EnsureNotDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SessionLayoutGateway));
        }
    }

    public void Dispose()
    {
        _disposed = true;
    }
}

/// <summary>
/// Result of <see cref="SessionLayoutGateway.ResolvePersisted"/>: the set of
/// HKL identifiers that the user's persisted configuration corresponds to,
/// plus a side-table mapping each one back to the original KLID for display.
/// </summary>
internal sealed record ResolvedPersisted(
    LayoutSet Hkls,
    IReadOnlyDictionary<LayoutId, string> DisplayKlids);
