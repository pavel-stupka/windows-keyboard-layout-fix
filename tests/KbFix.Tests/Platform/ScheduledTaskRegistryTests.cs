using KbFix.Platform.Install;
using KbFix.Watcher;
using Xunit;

namespace KbFix.Tests.Platform;

public class ScheduledTaskRegistryTests
{
    private const string StagedPath = @"C:\Users\alice\AppData\Local\KbFix\kbfix.exe";
    private const string Sid = "S-1-5-21-1111111111-2222222222-3333333333-1001";

    private string Xml => ScheduledTaskRegistry.BuildTaskXml(StagedPath, Sid);

    [Fact]
    public void BuildTaskXml_contains_expected_user_sid_in_both_principal_and_trigger()
    {
        var xml = Xml;

        // LogonTrigger UserId.
        Assert.Contains("<UserId>" + Sid + "</UserId>", xml);
        // Principal UserId.
        Assert.Contains("<Principal id=\"Author\">", xml);
    }

    [Fact]
    public void BuildTaskXml_uses_interactive_token_logon_type()
    {
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", Xml);
    }

    [Fact]
    public void BuildTaskXml_uses_least_privilege_run_level()
    {
        Assert.Contains("<RunLevel>LeastPrivilege</RunLevel>", Xml);
    }

    [Fact]
    public void BuildTaskXml_does_not_request_elevation_anywhere()
    {
        var xml = Xml;

        Assert.DoesNotContain("HighestAvailable", xml);
        Assert.DoesNotContain("S-1-5-18", xml);                      // SYSTEM SID
        Assert.DoesNotContain("S-1-5-32-544", xml);                  // BUILTIN\Administrators
        Assert.DoesNotContain("<LogonType>Password</LogonType>", xml);
    }

    [Fact]
    public void BuildTaskXml_includes_restart_on_failure_exactly_as_research_R1_specifies()
    {
        var xml = Xml;

        Assert.Contains("<RestartOnFailure>", xml);
        Assert.Contains("<Interval>PT1M</Interval>", xml);
        Assert.Contains("<Count>3</Count>", xml);
    }

    [Fact]
    public void BuildTaskXml_points_command_at_supplied_staged_binary()
    {
        var xml = Xml;

        Assert.Contains($"<Command>{StagedPath}</Command>", xml);
        Assert.Contains("<Arguments>--watch</Arguments>", xml);
    }

    [Fact]
    public void BuildTaskXml_emits_logon_trigger_enabled_true()
    {
        var xml = Xml;

        Assert.Contains("<LogonTrigger>", xml);
        Assert.Contains("<Enabled>true</Enabled>", xml);
    }

    [Fact]
    public void BuildTaskXml_has_multiple_instances_policy_ignore_new()
    {
        Assert.Contains("<MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>", Xml);
    }

    [Fact]
    public void BuildTaskXml_has_unlimited_execution_time()
    {
        // PT0S in Settings/ExecutionTimeLimit means "unlimited" per MS docs.
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", Xml);
    }

    [Fact]
    public void BuildTaskXml_lives_under_KbFix_namespace_not_root()
    {
        Assert.Contains("<URI>\\KbFix\\KbFixWatcher</URI>", Xml);
        // Never registers at root (\\<name>).
        Assert.DoesNotContain("<URI>\\KbFixWatcher</URI>", Xml);
    }

    [Fact]
    public void BuildTaskXml_declares_task_scheduler_12_schema()
    {
        var xml = Xml;

        Assert.Contains("<Task version=\"1.2\"", xml);
        Assert.Contains("xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\"", xml);
    }

    [Fact]
    public void BuildTaskXml_escapes_special_xml_characters_in_the_command_path()
    {
        var weirdPath = @"C:\Users\a & b\kbfix.exe";
        var xml = ScheduledTaskRegistry.BuildTaskXml(weirdPath, Sid);

        Assert.Contains("<Command>C:\\Users\\a &amp; b\\kbfix.exe</Command>", xml);
        Assert.DoesNotContain("<Command>C:\\Users\\a & b", xml);
    }

    [Fact]
    public void ScheduledTaskName_is_under_user_namespace()
    {
        Assert.StartsWith("KbFix\\", WatcherInstallation.ScheduledTaskName);
    }
}
