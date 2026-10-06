using FluentDesktop.Core.Dashboard;
using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Core.SpielerPlus;

namespace FluentDesktop.Core.Tests;

public class DashboardStoreTests
{
    [Fact]
    public async Task CreatesDefaultsAndReinsertsMissing()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "cmds.json");
        var store = new DashboardCommandStore(path);
        var first = await store.LoadAsync();
        Assert.Equal(DashboardCommand.DefaultCommands.Select(c => c.Id), first.Select(c => c.Id));

        var custom = new DashboardCommand { Id = "custom_1", Title = "x", Executable = "echo", Arguments = ["hi"] };
        await store.SaveAsync([custom]);
        var reloaded = await store.LoadAsync();
        Assert.Contains(reloaded, c => c.Id == "custom_1");
        Assert.Contains(reloaded, c => c.Id == "pi_update");
        Assert.DoesNotContain("commandString", File.ReadAllText(path));
    }

    [Fact]
    public async Task ReadsFlutterFormat()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "cmds.json");
        File.WriteAllText(path, """[{"id":"pi_update","title":"pi update","executable":"pi","arguments":["update"],"description":"d","assetIconPath":"assets/images/cc_apps/pi.svg","fallbackIconCode":58148,"isDefault":true}]""");
        var loaded = await new DashboardCommandStore(path).LoadAsync();
        Assert.Equal("pi", loaded[0].Executable);
        Assert.Equal("Assets/cc_apps/pi.svg", loaded[0].BrandIcon);
    }

    [Fact]
    public void BrandIconMatchesAgents()
    {
        Assert.Equal("Assets/cc_apps/claude.svg", new DashboardCommand { Executable = "claude" }.BrandIcon);
        Assert.Equal("Assets/cc_apps/openai.svg", new DashboardCommand { Title = "Codex CLI" }.BrandIcon);
        Assert.Null(new DashboardCommand { Executable = "pip", Title = "pipx" }.BrandIcon);
    }
}

public class CommandLineTests
{
    [Fact]
    public void QuotesWindowsArgs()
    {
        Assert.Equal("plain", WindowsCommandLine.Quote("plain"));
        Assert.Equal("\"a b\"", WindowsCommandLine.Quote("a b"));
        Assert.Equal("\"say \\\"hi\\\"\"", WindowsCommandLine.Quote("say \"hi\""));
        Assert.Equal("C:\\dir\\", WindowsCommandLine.Quote("C:\\dir\\"));
        Assert.Equal("\"C:\\my dir\\\\\"", WindowsCommandLine.Quote("C:\\my dir\\"));
        Assert.Equal("\"\"", WindowsCommandLine.Quote(""));
        Assert.Equal("a^&b", WindowsCommandLine.Quote("a&b", forCmd: true));
    }

    [Fact]
    public void SplitsUserArguments()
    {
        Assert.Equal(["-e", "bash -c x", "y"], WindowsCommandLine.Split("  -e \"bash -c x\"  y "));
        Assert.Empty(WindowsCommandLine.Split("   "));
    }
}

public class ProcessRunnerTests
{
    [Fact]
    public async Task RunsAndCaptures()
    {
        if (OperatingSystem.IsWindows()) return;
        var res = await ProcessRunner.RunAsync("sh", ["-c", "echo out; echo err >&2; exit 3"]);
        Assert.Equal(3, res.ExitCode);
        Assert.Equal("out", res.Stdout.Trim());
        Assert.Equal("err", res.Stderr.Trim());
    }

    [Fact]
    public async Task StreamsLinesAndShellMode()
    {
        if (OperatingSystem.IsWindows()) return;
        var lines = new List<string>();
        using var p = ProcessRunner.StartStreaming("echo", ["a b", "$HOME"], l => { lock (lines) lines.Add(l); }, options: new ProcessOptions { UseShell = true });
        Assert.Equal(0, await p.WaitAsync());
        Assert.Equal(["a b $HOME"], lines);
    }

    [Fact]
    public async Task TimesOut()
    {
        if (OperatingSystem.IsWindows()) return;
        await Assert.ThrowsAsync<TimeoutException>(() =>
            ProcessRunner.RunAsync("sleep", ["5"], new ProcessOptions { Timeout = TimeSpan.FromMilliseconds(200) }));
    }

    [Fact]
    public void ResolvesOnPath()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.NotNull(ExecutableResolver.Resolve("sh"));
        Assert.Null(ExecutableResolver.Resolve("definitely-not-a-command-xyz"));
    }
}

public class SpielerplusTests
{
    [Fact]
    public void SeasonEnd()
    {
        Assert.Equal("2027-06-30", SpielerplusConfig.DefaultSeasonEnd(new DateTime(2026, 6, 1)));
        Assert.Equal("2026-06-30", SpielerplusConfig.DefaultSeasonEnd(new DateTime(2026, 5, 31)));
    }

    [Fact]
    public async Task ExtractsEmbeddedScripts()
    {
        var dir = Path.Combine(Directory.CreateTempSubdirectory().FullName, "python");
        var service = new SpielerplusService(runtimeDir: dir);
        Assert.True(await service.VerifyAssetsAsync());
        foreach (var name in SpielerplusService.BundledFiles) Assert.True(File.Exists(Path.Combine(dir, name)), name);
    }

    [Fact]
    public void DfbnetArgsIncludeOptionalIds()
    {
        var service = new SpielerplusService(runtimeDir: "/rt");
        var cfg = new SpielerplusConfig { DfbnetClubId = "42", BrowserProfile = "" };
        var args = service.BuildDfbnetArgs(cfg, "a.csv", "a.xlsx", "a.json");
        Assert.Equal(Path.Combine("/rt", "dfbnet_api.py"), args[0]);
        Assert.Contains("--club-id", args);
        Assert.Equal("Default", args[args.IndexOf("--profile") + 1]);
        Assert.DoesNotContain("--team-type-id", args);
    }

    [Fact]
    public async Task ReadsFixturesJson()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, """{"fixtures":[{"date_iso":"2026-09-01","time_text":"19:00","home":"FC Hertha 03 III","away":"X","venue":"Ernst-Reuter-Sportfeld"}]}""");
        var fixtures = await SpielerplusService.ReadFixturesJsonAsync(path);
        Assert.Single(fixtures);
        Assert.True(fixtures[0].Scheduled);
        Assert.True(fixtures[0].IsHome("Ernst-Reuter-Sportf"));
        Assert.Equal("FC Hertha 03 III vs X", fixtures[0].MatchupLabel);
    }
}
