using KbFix.Platform.Install;
using KbFix.Watcher;
using Microsoft.Win32;
using Xunit;

namespace KbFix.Tests.Platform;

/// <summary>
/// Unit tests for <see cref="StartupApprovedProbe"/>. The probe takes an
/// optional <see cref="RegistryKey"/>-factory seam so the full classifier
/// can be exercised without touching the live registry. The real
/// HKCU-read path is covered by the quickstart §6 manual verification.
/// </summary>
public class StartupApprovedProbeTests : IDisposable
{
    private readonly string _keyPath;

    public StartupApprovedProbeTests()
    {
        // Use a unique scratch HKCU subkey so parallel test runs do not
        // collide. Deleted in Dispose.
        _keyPath = $@"Software\KbFix.Tests\StartupApprovedProbe.{Guid.NewGuid():N}";
        Registry.CurrentUser.CreateSubKey(_keyPath, writable: true)?.Dispose();
    }

    public void Dispose()
    {
        try { Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false); } catch { }
    }

    private RegistryKey? OpenKey() => Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);

    private void SetValue(byte[] bytes)
    {
        using var k = Registry.CurrentUser.OpenSubKey(_keyPath, writable: true);
        k!.SetValue(WatcherInstallation.RunKeyValueName, bytes, RegistryValueKind.Binary);
    }

    [Fact]
    public void Returns_true_when_key_is_missing_entirely()
    {
        // Factory returns null → StartupApproved\Run subkey does not exist,
        // which is Windows' default and means every HKCU\Run entry is enabled.
        Assert.True(StartupApprovedProbe.IsRunKeyApproved(() => null));
    }

    [Fact]
    public void Returns_true_when_value_is_missing_from_key()
    {
        // Key exists but no value for KbFixWatcher → default enabled.
        using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
        Assert.True(StartupApprovedProbe.IsRunKeyApproved(OpenKey));
    }

    [Fact]
    public void Returns_true_when_value_starts_with_0x02_enabled_sentinel()
    {
        SetValue(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        Assert.True(StartupApprovedProbe.IsRunKeyApproved(OpenKey));
    }

    [Fact]
    public void Returns_false_when_value_starts_with_0x03_disabled_by_user_sentinel()
    {
        SetValue(new byte[] { 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });

        Assert.False(StartupApprovedProbe.IsRunKeyApproved(OpenKey));
    }

    [Fact]
    public void Returns_true_when_low_bit_is_zero_even_for_other_high_bits()
    {
        // Any byte with bit 0 clear means enabled — e.g. 0x00, 0x06.
        SetValue(new byte[] { 0x06, 0x00, 0x00 });

        Assert.True(StartupApprovedProbe.IsRunKeyApproved(OpenKey));
    }

    [Fact]
    public void Returns_false_when_low_bit_is_set()
    {
        // Any byte with bit 0 set means disabled — e.g. 0x01, 0x03, 0xFF.
        SetValue(new byte[] { 0xFF, 0x00, 0x00 });

        Assert.False(StartupApprovedProbe.IsRunKeyApproved(OpenKey));
    }

    [Fact]
    public void Returns_true_when_value_is_zero_length()
    {
        SetValue(Array.Empty<byte>());

        Assert.True(StartupApprovedProbe.IsRunKeyApproved(OpenKey));
    }

    [Fact]
    public void Returns_true_when_factory_throws()
    {
        Assert.True(StartupApprovedProbe.IsRunKeyApproved(() => throw new InvalidOperationException("probe failure")));
    }
}
