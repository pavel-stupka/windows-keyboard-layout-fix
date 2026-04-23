using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using KbFix.Cli;
using KbFix.Diagnostics;
using KbFix.Domain;
using KbFix.Platform;
using KbFix.Platform.Install;
using KbFix.Watcher;

namespace KbFix;

[SupportedOSPlatform("windows")]
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var options = Options.Parse(args, out var usageExit);
        if (usageExit is { } code)
        {
            Console.Error.WriteLine("ERROR: unrecognised argument(s).");
            Console.Error.WriteLine();
            Console.Error.WriteLine(Options.UsageText);
            return code;
        }

        if (options.Help)
        {
            Console.Out.WriteLine(Options.UsageText);
            return ExitCodes.Success;
        }

        if (options.Version)
        {
            // Trim-safe: AssemblyName.Version flows from <Version> in the csproj.
            // ToString(3) emits MAJOR.MINOR.PATCH for proper semver per the 001 contract.
            var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            Console.Out.WriteLine($"kbfix {version}");
            return ExitCodes.Success;
        }

        if (options.Install)
        {
            return RunInstall(options);
        }

        if (options.Uninstall)
        {
            return RunUninstall(options);
        }

        if (options.Status)
        {
            return RunStatus(options);
        }

        if (options.Watch)
        {
            return WatcherMain.Run();
        }

        var reporter = new Reporter();

        IReadOnlyList<(ushort LangId, string Klid)> rawPersisted;
        try
        {
            rawPersisted = PersistedConfigReader.ReadRaw();
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return ExitCodes.Unsupported;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: failed to read persisted layout configuration: {ex.Message}");
            return ExitCodes.Failure;
        }

        SessionLayoutGateway? gateway = null;
        try
        {
            gateway = new SessionLayoutGateway();
        }
        catch (PlatformNotSupportedException ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return ExitCodes.Unsupported;
        }

        try
        {
            // Resolve the persisted (langId, klid) pairs into the set of HKLs
            // they correspond to in this running session. The display map lets
            // the reporter print friendly KLID names instead of raw HKL hex.
            ResolvedPersisted resolved;
            try
            {
                resolved = gateway.ResolvePersisted(rawPersisted);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"ERROR: failed to resolve persisted layouts: {ex.Message}");
                return ExitCodes.Failure;
            }

            var persisted = new PersistedConfig(resolved.Hkls, DateTime.UtcNow);

            SessionState session;
            try
            {
                session = gateway.ReadSession();
            }
            catch (PlatformNotSupportedException ex)
            {
                Console.Error.WriteLine($"ERROR: {ex.Message}");
                return ExitCodes.Unsupported;
            }

            var plan = ReconciliationPlan.Build(persisted, session);

            if (plan.Refused)
            {
                Console.Out.Write(reporter.FormatRefusal(persisted, session, plan.RefuseReason!, options.Quiet, resolved.DisplayKlids, rawPersisted));
                return ExitCodes.Refused;
            }

            if (plan.NoOp)
            {
                Console.Out.Write(reporter.FormatReport(
                    persisted, session,
                    actions: Array.Empty<AppliedAction>(),
                    outcome: Outcome.NoOp,
                    dryRun: options.DryRun,
                    quiet: options.Quiet,
                    displayKlids: resolved.DisplayKlids,
                    userFacingPersisted: rawPersisted));
                return ExitCodes.Success;
            }

            var applied = new List<AppliedAction>(plan.Actions.Count);
            var failed = false;
            foreach (var action in plan.Actions)
            {
                if (options.DryRun)
                {
                    applied.Add(new AppliedAction(action, Succeeded: true, Failure: null));
                    continue;
                }

                var result = gateway.Apply(action);
                applied.Add(result);
                if (!result.Succeeded)
                {
                    failed = true;
                    break;
                }
            }

            if (!options.DryRun && !failed)
            {
                if (!gateway.VerifyConverged(resolved.Hkls))
                {
                    failed = true;
                }
            }

            var outcome = failed ? Outcome.Failed : Outcome.Success;

            Console.Out.Write(reporter.FormatReport(
                persisted, session, applied, outcome,
                dryRun: options.DryRun,
                quiet: options.Quiet,
                displayKlids: resolved.DisplayKlids,
                userFacingPersisted: rawPersisted));

            if (failed)
            {
                Console.Error.WriteLine("ERROR: reconciliation did not complete successfully.");
                return ExitCodes.Failure;
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.GetType().Name}: {ex.Message}");
            if (ex is COMException com)
            {
                Console.Error.WriteLine($"  HRESULT: 0x{(uint)com.HResult:X8}");
            }
            if (ex.InnerException is { } inner)
            {
                Console.Error.WriteLine($"  inner: {inner.GetType().Name}: {inner.Message}");
            }
            if (Environment.GetEnvironmentVariable("KBFIX_DEBUG") == "1")
            {
                Console.Error.WriteLine($"  stack: {ex.StackTrace}");
            }
            return ExitCodes.Failure;
        }
        finally
        {
            gateway?.Dispose();
        }
    }

    private static int RunStatus(Options options)
    {
        try
        {
            var state = WatcherDiscovery.Probe();
            string report;
            if (options.Verbose)
            {
                var logTail = StatusReporter.ReadLogTail();
                var taskXml = Platform.Install.ScheduledTaskRegistry.QueryXml();
                report = StatusReporter.FormatVerbose(state, logTail, taskXml, state.LastExitReason);
            }
            else
            {
                report = StatusReporter.Format(state, options.Quiet);
            }
            Console.Out.Write(report);
            return StatusReporter.ExitCodeFor(state.Classify());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: status probe failed: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    private static int RunUninstall(Options options)
    {
        var invokingPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(invokingPath))
        {
            Console.Error.WriteLine("ERROR: could not determine the current binary path.");
            return ExitCodes.Failure;
        }

        try
        {
            var before = WatcherDiscovery.Probe();
            var steps = InstallDecision.ComputeUninstallSteps(before, invokingPath);
            var executor = new InstallExecutor();
            var results = executor.Apply(steps, invokingPath);

            var report = InstallReporter.FormatUninstall(before, results, options.Quiet);
            Console.Out.Write(report);

            var failed = results.Any(r => !r.Succeeded);
            if (failed)
            {
                Console.Error.WriteLine("ERROR: one or more uninstall steps failed. See report above.");
                return ExitCodes.Failure;
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: uninstall failed: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    private static int RunInstall(Options options)
    {
        var invokingPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(invokingPath))
        {
            Console.Error.WriteLine("ERROR: could not determine the current binary path.");
            return ExitCodes.Failure;
        }

        try
        {
            var before = WatcherDiscovery.Probe();
            var steps = InstallDecision.ComputeInstallSteps(before, invokingPath);
            var executor = new InstallExecutor();
            var results = executor.Apply(steps, invokingPath);

            // Small delay so the spawned watcher has time to acquire the mutex
            // before we probe state for the report.
            if (results.Any(r => r.Step is SpawnWatcherStep))
            {
                System.Threading.Thread.Sleep(500);
            }

            var after = WatcherDiscovery.Probe();
            var report = InstallReporter.FormatInstall(before, results, after, options.Quiet);
            Console.Out.Write(report);

            var failed = results.Any(r => !r.Succeeded);
            if (failed)
            {
                Console.Error.WriteLine("ERROR: one or more install steps failed. See report above.");
                return ExitCodes.Failure;
            }

            return ExitCodes.Success;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: install failed: {ex.Message}");
            return ExitCodes.Failure;
        }
    }
}
