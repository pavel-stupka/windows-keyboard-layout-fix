using System.Runtime.InteropServices;

namespace KbFix.Platform;

/// <summary>
/// COM interop declarations for the Windows Text Services Framework
/// (<c>ITfInputProcessorProfileMgr</c> / <c>ITfInputProcessorProfiles</c>).
/// Declaration-only — no business logic. The members declared here are the
/// minimum needed by <see cref="SessionLayoutGateway"/>.
/// </summary>
internal static class TsfInterop
{
    /// <summary>CLSID of the input processor profiles class.</summary>
    public static readonly Guid CLSID_TF_InputProcessorProfiles =
        new("33C53A50-F456-4884-B049-85FD643ECFED");

    /// <summary>Category GUID for keyboard text services.</summary>
    public static readonly Guid GUID_TFCAT_TIP_KEYBOARD =
        new("34745C63-B2F0-4784-8B67-5E12C8701A31");

    public const uint TF_IPP_FLAG_ACTIVE = 0x00000001;
    public const uint TF_IPP_FLAG_ENABLED = 0x00000002;
    public const uint TF_IPP_FLAG_SUBSTITUTEDBYINPUTPROCESSOR = 0x00000004;

    public const int TF_PROFILETYPE_INPUTPROCESSOR = 0x0001;
    public const int TF_PROFILETYPE_KEYBOARDLAYOUT = 0x0002;

    public const uint CLSCTX_INPROC_SERVER = 0x1;

    public const uint COINIT_APARTMENTTHREADED = 0x2;
    public const uint COINIT_DISABLE_OLE1DDE = 0x4;

    public static readonly Guid IID_IUnknown =
        new("00000000-0000-0000-C000-000000000046");

    public static readonly Guid IID_ITfInputProcessorProfileMgr =
        new(0x71c6e74e, 0x0f28, 0x11d8, 0xa8, 0x2a, 0x00, 0x06, 0x5b, 0x84, 0x43, 0x5c);

    public static readonly Guid IID_ITfInputProcessorProfiles =
        new(0x1F02B6C5, 0x7842, 0x4EE6, 0x8A, 0x0B, 0x9A, 0x24, 0x18, 0x3A, 0x95, 0xCA);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoCreateInstance(
        ref Guid rclsid,
        IntPtr pUnkOuter,
        uint dwClsContext,
        ref Guid riid,
        out IntPtr ppv);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern void CoUninitialize();

    [StructLayout(LayoutKind.Sequential)]
    public struct TF_INPUTPROCESSORPROFILE
    {
        public int dwProfileType;
        public ushort langid;
        public Guid clsid;
        public Guid guidProfile;
        public Guid catid;
        public IntPtr hklSubstitute;
        public uint dwCaps;
        public IntPtr hkl;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TF_LANGUAGEPROFILE
    {
        public Guid clsid;
        public ushort langid;
        public Guid catid;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fActive;
        public Guid guidProfile;
    }

    [ComImport]
    [Guid("3d61bf11-ac5f-42c8-a4cb-931bcc28c744")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumTfLanguageProfiles
    {
        void Clone(out IEnumTfLanguageProfiles ppEnum);

        [PreserveSig]
        int Next(
            uint ulCount,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] TF_LANGUAGEPROFILE[] pProfile,
            out uint pcFetched);

        void Reset();

        void Skip(uint ulCount);
    }

    [ComImport]
    [Guid("1F02B6C5-7842-4EE6-8A0B-9A24183A95CA")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfInputProcessorProfiles
    {
        void Register(ref Guid rclsid);
        void Unregister(ref Guid rclsid);
        void AddLanguageProfile(
            ref Guid rclsid,
            ushort langid,
            ref Guid guidProfile,
            [MarshalAs(UnmanagedType.LPWStr)] string pchDesc,
            uint cchDesc,
            [MarshalAs(UnmanagedType.LPWStr)] string pchIconFile,
            uint cchFile,
            uint uIconIndex);
        void RemoveLanguageProfile(ref Guid rclsid, ushort langid, ref Guid guidProfile);
        void EnumInputProcessorInfo(out IntPtr ppEnum);
        void GetDefaultLanguageProfile(
            ushort langid,
            ref Guid catid,
            out Guid pclsid,
            out Guid pguidProfile);
        void SetDefaultLanguageProfile(
            ushort langid,
            ref Guid rclsid,
            ref Guid guidProfile);
        void ActivateLanguageProfile(
            ref Guid rclsid,
            ushort langid,
            ref Guid guidProfile);
        void GetActiveLanguageProfile(
            ref Guid rclsid,
            out ushort plangid,
            out Guid pguidProfile);
        void GetLanguageProfileDescription(
            ref Guid rclsid,
            ushort langid,
            ref Guid guidProfile,
            [MarshalAs(UnmanagedType.BStr)] out string pbstrProfile);
        void GetCurrentLanguage(out ushort plangid);
        void ChangeCurrentLanguage(ushort langid);
        void GetLanguageList(out IntPtr ppLangId, out uint pulCount);
        void EnumLanguageProfiles(ushort langid, out IEnumTfLanguageProfiles ppEnum);
        void EnableLanguageProfile(
            ref Guid rclsid,
            ushort langid,
            ref Guid guidProfile,
            [MarshalAs(UnmanagedType.Bool)] bool fEnable);
        void IsEnabledLanguageProfile(
            ref Guid rclsid,
            ushort langid,
            ref Guid guidProfile,
            [MarshalAs(UnmanagedType.Bool)] out bool pfEnable);
        void EnableLanguageProfileByDefault(
            ref Guid rclsid,
            ushort langid,
            ref Guid guidProfile,
            [MarshalAs(UnmanagedType.Bool)] bool fEnable);
        void SubstituteKeyboardLayout(
            ref Guid rclsid,
            ushort langid,
            ref Guid guidProfile,
            IntPtr hKL);
    }

    [ComImport]
    [Guid("71C6E74C-0F28-11D8-A82A-00065B84435C")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IEnumTfInputProcessorProfiles
    {
        void Clone(out IEnumTfInputProcessorProfiles ppEnum);

        [PreserveSig]
        int Next(
            uint ulCount,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] TF_INPUTPROCESSORPROFILE[] pProfile,
            out uint pcFetched);

        void Reset();

        void Skip(uint ulCount);
    }

    [ComImport]
    [Guid("71c6e74e-0f28-11d8-a82a-00065b84435c")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITfInputProcessorProfileMgr
    {
        void ActivateProfile(
            int dwProfileType,
            ushort langid,
            ref Guid clsid,
            ref Guid guidProfile,
            IntPtr hkl,
            uint dwFlags);

        void DeactivateProfile(
            int dwProfileType,
            ushort langid,
            ref Guid clsid,
            ref Guid guidProfile,
            IntPtr hkl,
            uint dwFlags);

        void GetProfile(
            int dwProfileType,
            ushort langid,
            ref Guid clsid,
            ref Guid guidProfile,
            IntPtr hkl,
            out TF_INPUTPROCESSORPROFILE pProfile);

        void EnumProfiles(ushort langid, out IEnumTfInputProcessorProfiles ppEnum);

        void ReleaseInputProcessor(ref Guid rclsid, uint dwFlags);

        void RegisterProfile(
            ref Guid rclsid,
            ushort langid,
            ref Guid guidProfile,
            [MarshalAs(UnmanagedType.LPWStr)] string pchDesc,
            uint cchDesc,
            [MarshalAs(UnmanagedType.LPWStr)] string pchIconFile,
            uint cchFile,
            uint uIconIndex,
            IntPtr hklSubstitute,
            uint dwPreferredLayout,
            [MarshalAs(UnmanagedType.Bool)] bool bEnabledByDefault,
            uint dwFlags);

        void UnregisterProfile(
            ref Guid rclsid,
            ushort langid,
            ref Guid guidProfile,
            uint dwFlags);

        void GetActiveProfile(ref Guid catid, out TF_INPUTPROCESSORPROFILE pProfile);
    }
}
