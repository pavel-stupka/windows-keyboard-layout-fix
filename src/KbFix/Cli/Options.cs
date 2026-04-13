namespace KbFix.Cli;

internal sealed record Options(
    bool DryRun,
    bool Quiet,
    bool Help,
    bool Version)
{
    public static Options Defaults { get; } = new(false, false, false, false);

    /// <summary>
    /// Parse the process command line. On unrecognised input, sets
    /// <paramref name="usageExitCode"/> to <c>64</c> and returns
    /// <see cref="Defaults"/>; otherwise sets it to <c>null</c>.
    /// </summary>
    public static Options Parse(string[] args, out int? usageExitCode)
    {
        usageExitCode = null;
        var dryRun = false;
        var quiet = false;
        var help = false;
        var version = false;

        foreach (var raw in args)
        {
            switch (raw)
            {
                case "--dry-run":
                case "--preview":
                    dryRun = true;
                    break;
                case "-q":
                case "--quiet":
                    quiet = true;
                    break;
                case "-h":
                case "-?":
                case "--help":
                    help = true;
                    break;
                case "--version":
                    version = true;
                    break;
                default:
                    usageExitCode = 64;
                    return Defaults;
            }
        }

        return new Options(dryRun, quiet, help, version);
    }

    public const string UsageText =
        "Usage: kbfix [--dry-run] [--quiet] [--help] [--version]" + "\n" +
        "" + "\n" +
        "  Removes keyboard layouts from the current Windows session that are not" + "\n" +
        "  present in the user's persisted (HKCU) keyboard configuration." + "\n" +
        "" + "\n" +
        "Options:" + "\n" +
        "  --dry-run, --preview   Inspect and report only; do not modify the session." + "\n" +
        "  -q, --quiet            Suppress the persisted/session sections in the report." + "\n" +
        "  -h, --help             Print this help and exit." + "\n" +
        "  --version              Print the version and exit.";
}
