namespace KbFix.Diagnostics;

/// <summary>
/// Process exit codes. The values are part of the public CLI contract — see
/// <c>specs/001-fix-keyboard-layouts/contracts/cli.md</c> for 0/1/2/3/64,
/// <c>specs/003-background-watcher/contracts/cli.md</c> for 10..14, and
/// <c>specs/004-watcher-resilience/contracts/cli.md</c> for 15..17. Do not
/// change existing values.
/// </summary>
internal static class ExitCodes
{
    // 001 contract — unchanged.
    public const int Success = 0;
    public const int Failure = 1;
    public const int Unsupported = 2;
    public const int Refused = 3;
    public const int Usage = 64;

    // 003 contract — --status state reporting.
    public const int NotInstalled = 10;
    public const int InstalledNotRunning = 11;
    public const int RunningWithoutAutostart = 12;
    public const int StalePath = 13;
    public const int MixedOrCorrupt = 14;

    // 004 contract — supervisor + autostart-effectiveness states.
    public const int SupervisorBackingOff = 15;
    public const int SupervisorGaveUp = 16;
    public const int AutostartDegraded = 17;
}
