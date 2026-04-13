using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace KbFix.Platform;

/// <summary>
/// P/Invoke declarations for the legacy Win32 input-locale APIs. These are
/// kept declaration-only — no business logic — so the platform layer can
/// import them without dragging interop concerns into the rest of the code.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Win32Interop
{
    public const uint KLF_ACTIVATE = 0x00000001;
    public const uint KLF_SUBSTITUTE_OK = 0x00000002;
    public const uint KLF_REORDER = 0x00000008;
    public const uint KLF_REPLACELANG = 0x00000010;
    public const uint KLF_NOTELLSHELL = 0x00000080;
    public const uint KLF_SETFORPROCESS = 0x00000100;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int GetKeyboardLayoutList(int nBuff, [Out] IntPtr[]? lpList);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadKeyboardLayout(string pwszKLID, uint Flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnloadKeyboardLayout(IntPtr hkl);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr ActivateKeyboardLayout(IntPtr hkl, uint Flags);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FreeConsole();
}
