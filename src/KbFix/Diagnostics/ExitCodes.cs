namespace KbFix.Diagnostics;

/// <summary>
/// Process exit codes. The values are part of the public CLI contract — see
/// <c>specs/001-fix-keyboard-layouts/contracts/cli.md</c>. Do not change them.
/// </summary>
internal static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Unsupported = 2;
    public const int Refused = 3;
    public const int Usage = 64;
}
