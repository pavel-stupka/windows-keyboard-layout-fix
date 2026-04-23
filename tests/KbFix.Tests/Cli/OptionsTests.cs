using KbFix.Cli;
using Xunit;

namespace KbFix.Tests.Cli;

public class OptionsTests
{
    [Fact]
    public void No_args_returns_defaults()
    {
        var opts = Options.Parse(Array.Empty<string>(), out var usage);
        Assert.Null(usage);
        Assert.False(opts.DryRun);
        Assert.False(opts.Quiet);
        Assert.False(opts.Help);
        Assert.False(opts.Version);
    }

    [Theory]
    [InlineData("--dry-run")]
    [InlineData("--preview")]
    public void Dry_run_aliases_set_dry_run(string flag)
    {
        var opts = Options.Parse(new[] { flag }, out var usage);
        Assert.Null(usage);
        Assert.True(opts.DryRun);
    }

    [Theory]
    [InlineData("-q")]
    [InlineData("--quiet")]
    public void Quiet_aliases_set_quiet(string flag)
    {
        var opts = Options.Parse(new[] { flag }, out var usage);
        Assert.Null(usage);
        Assert.True(opts.Quiet);
    }

    [Theory]
    [InlineData("-h")]
    [InlineData("-?")]
    [InlineData("--help")]
    public void Help_aliases_set_help(string flag)
    {
        var opts = Options.Parse(new[] { flag }, out var usage);
        Assert.Null(usage);
        Assert.True(opts.Help);
    }

    [Fact]
    public void Version_flag_sets_version()
    {
        var opts = Options.Parse(new[] { "--version" }, out var usage);
        Assert.Null(usage);
        Assert.True(opts.Version);
    }

    [Fact]
    public void Unknown_flag_yields_usage_exit_code_64()
    {
        Options.Parse(new[] { "--bogus" }, out var usage);
        Assert.Equal(64, usage);
    }

    [Fact]
    public void Combined_flags_work()
    {
        var opts = Options.Parse(new[] { "--dry-run", "--quiet" }, out var usage);
        Assert.Null(usage);
        Assert.True(opts.DryRun);
        Assert.True(opts.Quiet);
    }

    // ---------- 004: --verbose modifier ----------

    [Fact]
    public void Status_with_verbose_is_accepted()
    {
        var opts = Options.Parse(new[] { "--status", "--verbose" }, out var usage);
        Assert.Null(usage);
        Assert.True(opts.Status);
        Assert.True(opts.Verbose);
    }

    [Fact]
    public void Verbose_without_status_is_usage_error()
    {
        Options.Parse(new[] { "--verbose" }, out var usage);
        Assert.Equal(64, usage);
    }

    [Fact]
    public void Verbose_with_install_is_usage_error()
    {
        Options.Parse(new[] { "--install", "--verbose" }, out var usage);
        Assert.Equal(64, usage);
    }

    [Fact]
    public void Verbose_with_quiet_is_usage_error()
    {
        Options.Parse(new[] { "--status", "--verbose", "--quiet" }, out var usage);
        Assert.Equal(64, usage);
    }

    [Fact]
    public void Status_without_verbose_leaves_verbose_false()
    {
        var opts = Options.Parse(new[] { "--status" }, out var usage);
        Assert.Null(usage);
        Assert.True(opts.Status);
        Assert.False(opts.Verbose);
    }
}
