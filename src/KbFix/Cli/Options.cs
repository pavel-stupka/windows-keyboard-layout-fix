namespace KbFix.Cli;

internal sealed record Options(
    bool DryRun,
    bool Quiet,
    bool Help,
    bool Version,
    bool Install,
    bool Uninstall,
    bool Status,
    bool Watch)
{
    public static Options Defaults { get; } =
        new(false, false, false, false, false, false, false, false);

    public bool IsSubcommand => Install || Uninstall || Status || Watch;

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
        var install = false;
        var uninstall = false;
        var status = false;
        var watch = false;

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
                case "--install":
                    install = true;
                    break;
                case "--uninstall":
                    uninstall = true;
                    break;
                case "--status":
                    status = true;
                    break;
                case "--watch":
                    watch = true;
                    break;
                default:
                    usageExitCode = 64;
                    return Defaults;
            }
        }

        // Mutual exclusion: at most one of --install / --uninstall / --status / --watch,
        // and none of them combine with --dry-run.
        var subcommandCount = (install ? 1 : 0) + (uninstall ? 1 : 0) + (status ? 1 : 0) + (watch ? 1 : 0);
        if (subcommandCount > 1)
        {
            usageExitCode = 64;
            return Defaults;
        }
        if (subcommandCount == 1 && dryRun)
        {
            usageExitCode = 64;
            return Defaults;
        }

        return new Options(dryRun, quiet, help, version, install, uninstall, status, watch);
    }

    public const string UsageText =
        "Usage: kbfix [--dry-run] [--quiet] [--help] [--version]" + "\n" +
        "       kbfix --install   [--quiet]" + "\n" +
        "       kbfix --uninstall [--quiet]" + "\n" +
        "       kbfix --status    [--quiet]" + "\n" +
        "" + "\n" +
        "  Removes keyboard layouts from the current Windows session that are not" + "\n" +
        "  present in the user's persisted (HKCU) keyboard configuration." + "\n" +
        "" + "\n" +
        "One-shot options:" + "\n" +
        "  --dry-run, --preview   Inspect and report only; do not modify the session." + "\n" +
        "  -q, --quiet            Suppress the persisted/session sections in the report." + "\n" +
        "  -h, --help             Print this help and exit." + "\n" +
        "  --version              Print the version and exit." + "\n" +
        "" + "\n" +
        "Background-watcher commands (per-user, no elevation):" + "\n" +
        "  --install              Stage kbfix.exe under %LOCALAPPDATA%\\KbFix, register" + "\n" +
        "                         per-user autostart, and launch the watcher now." + "\n" +
        "  --uninstall            Stop the watcher, remove the autostart entry, and" + "\n" +
        "                         delete the staged binary." + "\n" +
        "  --status               Report whether the watcher is running and whether" + "\n" +
        "                         autostart is registered.";
}
