using System.Text.Json;
using FluentDesktop.Core.DesktopCommander;
using FluentDesktop.Core.DeviceMirror;
using FluentDesktop.Core.GitHub;
using FluentDesktop.Core.Hub;
using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Core.YtDlp;

namespace FluentDesktop.Core.Tests;

public class DesktopCommanderParserTests
{
    [Fact]
    public void ExtractsUrlCodeAndWaitingMarker()
    {
        var ev = DesktopCommanderOutputParser.Parse("\u001b[32mOpen https://desktopcommander.app/verify?x=1). Code: ABCD-1234 Waiting for authorization\u001b[0m");
        Assert.NotNull(ev);
        Assert.Equal("https://desktopcommander.app/verify?x=1", ev!.AuthUrl);
        Assert.Equal("ABCD-1234", ev.AuthCode);
        Assert.True(ev.WaitingForAuthorization);
        Assert.False(ev.Line.Contains('\u001b'));
    }

    [Fact]
    public void DetectsDeviceIdAndOnline()
    {
        Assert.Equal("dev-42", DesktopCommanderOutputParser.Parse("Device ID assigned: dev-42")!.DeviceId);
        Assert.True(DesktopCommanderOutputParser.Parse("Device marked as online")!.Online);
        Assert.Null(DesktopCommanderOutputParser.Parse("   "));
    }

    [Fact]
    public void SettingsNormalize()
    {
        var s = new DesktopCommanderSettings { ChromeProfile = "  ", VerificationTimeoutSeconds = 999 }.Normalized();
        Assert.Equal("Default", s.ChromeProfile);
        Assert.Equal(300, s.VerificationTimeoutSeconds);
    }

    [Fact]
    public void StateCapsLogs()
    {
        var state = new DesktopCommanderState();
        for (var i = 0; i < 510; i++) state = state.AppendLog($"line {i}");
        Assert.Equal(500, state.Logs.Count);
        Assert.Equal("line 10", state.Logs[0]);
    }
}

public class DesktopCommanderServiceTests
{
    private sealed class FakeRunner : IDesktopCommanderProcessRunner
    {
        public readonly List<(string Exe, IReadOnlyList<string> Args, Action<string>? OnLine)> Started = [];
        public bool HasAgentBrowser { get; init; }

        public Task<bool> CommandExistsAsync(string name) => Task.FromResult(name == "npx" || (name == "agent-browser" && HasAgentBrowser));

        public IProcessHandle Start(string executable, IReadOnlyList<string> arguments, Action<string>? onLine, IReadOnlyDictionary<string, string>? environment = null)
        {
            Started.Add((executable, arguments, onLine));
            var handle = new Handle();
            // Only the long-running `npx ... remote` stays alive.
            if (executable != "npx") handle.Exit.TrySetResult(0);
            return handle;
        }

        public void Open(string target) { }

        public sealed class Handle : IProcessHandle
        {
            public readonly TaskCompletionSource<int> Exit = new();
            public Task<int> WaitAsync() => Exit.Task;
            public bool Kill() => Exit.TrySetResult(-1);
        }
    }

    [Fact]
    public async Task PairingOutputMovesToAuthorizing()
    {
        var runner = new FakeRunner();
        using var service = new DesktopCommanderService(runner);
        await service.StartAsync(new DesktopCommanderSettings { AutoVerify = false });
        Assert.True(service.State.ProcessActive);
        var onLine = runner.Started[0].OnLine!;
        onLine("Visit https://example.com/verify to pair");
        onLine("Code: WXYZ-9876");
        onLine("Waiting for authorization...");
        Assert.Equal(DesktopCommanderStatus.Authorizing, service.State.Status);
        Assert.Equal("WXYZ-9876", service.State.AuthCode);
        Assert.Equal("AUTH", service.State.Badge);

        onLine("Presence tracked");
        Assert.Equal(DesktopCommanderStatus.Online, service.State.Status);
    }

    [Fact]
    public async Task StopIgnoresLateOutput()
    {
        var runner = new FakeRunner();
        using var service = new DesktopCommanderService(runner);
        await service.StartAsync(new DesktopCommanderSettings { AutoVerify = false });
        var onLine = runner.Started[0].OnLine!;
        await service.StopAsync();
        var count = service.State.Logs.Count;
        onLine("late line");
        Assert.Equal(count, service.State.Logs.Count);
        Assert.Equal(DesktopCommanderStatus.Stopped, service.State.Status);
    }
}

public class AdbTests
{
    [Fact]
    public void ParsesDevicesSkippingDaemonChatter()
    {
        const string output = "List of devices attached\n* daemon started successfully\nR58M123 device usb:1-1 product:x model:SM_G\nemulator-5554 unauthorized\n\n";
        var devices = AdbService.ParseDevicesList(output);
        Assert.Equal(2, devices.Count);
        Assert.True(devices[0].IsOnline);
        Assert.Equal("unauthorized", devices[1].State);
    }

    [Fact]
    public void AppliesProperties()
    {
        var device = new AdbDevice { Serial = "x", State = "device" };
        AdbService.ApplyProperties(device, [
            "Google", "Pixel 8", "15", "35",
            "  AC powered: false\n  USB powered: true\n  level: 87\n",
            "Physical size: 1080x2400", "Physical density: 420",
            "192.168.1.0/24 dev wlan0 proto kernel scope link src 192.168.1.23"]);
        Assert.Equal("Google Pixel 8", device.DisplayName);
        Assert.Equal(87, device.BatteryLevel);
        Assert.True(device.IsCharging);
        Assert.Equal(2400, device.ScreenHeight);
        Assert.Equal(420, device.ScreenDensity);
        Assert.Equal("192.168.1.23", device.IpAddress);
    }

    [Fact]
    public void ScrcpyArgsAreDeterministic()
    {
        var cfg = new ScrcpyConfig { AlwaysOnTop = true, MaxFps = 0, RecordVideo = true };
        Assert.Equal(
            ["-s", "abc", "--window-title", "SCRCPY — abc", "--always-on-top", "--stay-awake", "-m", "1080", "-b", "8M", "--video-codec=h264", "--hwdec=auto", "--record", "out.mp4"],
            cfg.ToArgs("abc", "out.mp4"));
    }
}

public class GithubTests
{
    [Theory]
    [InlineData("my-app", true)]
    [InlineData("a.b_c", true)]
    [InlineData(".hidden", false)]
    [InlineData("a..b", false)]
    [InlineData("with space", false)]
    [InlineData("owner/repo", false)]
    [InlineData("", false)]
    public void ValidatesRepoNames(string name, bool valid) => Assert.Equal(valid, GithubRules.IsValidRepoName(name));

    [Fact]
    public void BuildsCreateArgs()
    {
        var args = GithubCliService.BuildCreateArgs(new CreateRepoRequest("x", "desc", IsPrivate: true, GitignoreTemplate: "None", LicenseTemplate: "mit", CloneAfterCreate: true));
        Assert.Equal(["repo", "create", "x", "--private", "--description", "desc", "--add-readme", "--license", "mit", "--clone"], args);
    }

    [Fact]
    public void ParsesGhJsonWithLanguageObject()
    {
        const string json = """[{"name":"a","nameWithOwner":"o/a","isPrivate":true,"updatedAt":"2026-01-02T03:04:05Z","pushedAt":"2026-01-01T00:00:00Z","primaryLanguage":{"name":"Dart"},"stargazerCount":3}]""";
        var repos = JsonSerializer.Deserialize<List<GithubRepo>>(json, JsonStore.Options)!;
        Assert.Equal("Dart", repos[0].PrimaryLanguage);
        Assert.True(repos[0].IsPrivate);

        // Round-trips through the cache as a plain string (Flutter cache format).
        var cached = JsonSerializer.Serialize(repos, JsonStore.Options);
        Assert.Contains("\"primaryLanguage\": \"Dart\"", cached);
        Assert.Equal("Dart", JsonSerializer.Deserialize<List<GithubRepo>>(cached, JsonStore.Options)![0].PrimaryLanguage);
    }

    [Fact]
    public void FiltersAndSorts()
    {
        var repos = new[]
        {
            new GithubRepo { Name = "b", NameWithOwner = "o/b", StargazerCount = 5 },
            new GithubRepo { Name = "a", NameWithOwner = "o/a", IsPrivate = true, StargazerCount = 9 },
            new GithubRepo { Name = "c", NameWithOwner = "o/c", IsArchived = true },
        };
        Assert.Equal(["a", "b", "c"], GithubRules.FilterAndSort(repos, "", VisibilityFilter.All, RepoSortOption.NameAsc).Select(r => r.Name));
        Assert.Equal(["b"], GithubRules.FilterAndSort(repos, "", VisibilityFilter.PublicOnly, RepoSortOption.NameAsc).Select(r => r.Name));
        Assert.Equal(["a"], GithubRules.FilterAndSort(repos, "O/A", VisibilityFilter.All, RepoSortOption.NameAsc).Select(r => r.Name));
        Assert.Equal("a", GithubRules.FilterAndSort(repos, "", VisibilityFilter.All, RepoSortOption.StarsDesc)[0].Name);
    }

    [Fact]
    public void PaginationEllipsis()
    {
        Assert.Equal([0, 1, 2, 3], GithubRules.VisiblePageNumbers(0, 4));
        Assert.Equal([0, -1, 4, 5, 6, -1, 9], GithubRules.VisiblePageNumbers(5, 10));
        Assert.Equal([0, 1, -1, 9], GithubRules.VisiblePageNumbers(0, 10));
    }

    [Fact]
    public async Task ConnectExistingRunsGitSequence()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var calls = new List<string>();
        var service = new GithubCliService(Path.Combine(dir, "cache.json"), (exe, args, wd) =>
        {
            calls.Add($"{exe} {string.Join(' ', args)}");
            var fail = args is ["rev-parse", ..] || args is ["remote", "get-url", ..] || exe == "gh";
            return Task.FromResult(new CommandResult(fail ? 1 : 0, "", ""));
        });
        var result = await service.ConnectLocalToRemoteAsync(new ConnectLocalRequest(dir, ExistingRemoteName: "me/repo", Branch: "main"));
        Assert.True(result.Success, result.Message);
        Assert.Contains("git init", calls);
        Assert.Contains("git remote add origin https://github.com/me/repo.git", calls);
        Assert.Contains("git push -u origin main", calls);
    }

    [Fact]
    public void ShortRemote() => Assert.Equal("me/repo", GithubRules.ShortRemote("git@github.com:me/repo.git"));
}

public class HubTests
{
    [Fact]
    public void ParsesNetstatListeners()
    {
        const string output = """
            Active Connections

              Proto  Local Address          Foreign Address        State           PID
              TCP    0.0.0.0:3000           0.0.0.0:0              LISTENING       4242
              TCP    [::]:3000              [::]:0                 LISTENING       4242
              TCP    0.0.0.0:30001          0.0.0.0:0              LISTENING       77
              TCP    127.0.0.1:3000         127.0.0.1:5000         ESTABLISHED     88
            """;
        var (pids, lines) = HubService.ParseNetstatListeners(output, 3000);
        Assert.Equal(["4242"], pids);
        Assert.Equal(2, lines.Count);
    }
}

public class YtDlpTests
{
    [Fact]
    public void ParsesProgressAndDestination()
    {
        var parser = new YtDlpService.ProgressParser("/out");
        Assert.Null(parser.Parse("[download] Destination: /out/Video [abc].mp4"));
        Assert.Equal("/out/Video [abc].mp4", parser.OutputFile);
        var p = parser.Parse("[download]  42.5% of ~10.00MiB at  2.00MiB/s ETA 00:03")!;
        Assert.Equal(42.5, p.Percent);
        Assert.Equal("~10.00MiB", p.TotalSize);
        Assert.Equal("2.00MiB/s", p.Speed);
        Assert.Equal("00:03", p.Eta);
        Assert.Equal(99, parser.Parse("[Merger] Merging formats into \"/out/final.mkv\"")!.Percent);
        Assert.Equal("/out/final.mkv", parser.OutputFile);
    }

    [Fact]
    public void BuildsDownloadArgs()
    {
        var opts = new YtDownloadOptions("u", YtDownloadType.Video, YtVideoQuality.P720, YtAudioFormat.Mp3, true, false, true, false, false, "/o");
        var args = YtDlpService.BuildDownloadArguments(opts, "u", "/o");
        Assert.Contains("bestvideo[height<=720]+bestaudio/best", args);
        Assert.Contains("--embed-subs", args);
        Assert.Contains("--embed-metadata", args);
        Assert.Equal("--no-playlist", args[^2]);

        var audio = YtDlpService.BuildDownloadArguments(opts with { DownloadType = YtDownloadType.Audio, AudioFormat = YtAudioFormat.M4a }, "u", "/o");
        Assert.Contains("m4a", audio);
        Assert.DoesNotContain("-f", audio);
    }

    [Fact]
    public void ParsesVideoInfo()
    {
        using var doc = JsonDocument.Parse("""{"id":"x","title":"T","channel":"C","duration":3725.4,"view_count":1500,"thumbnails":[{"url":"a"},{"url":"b"}]}""");
        var info = YtVideoInfo.FromJson(doc.RootElement, "http://u");
        Assert.Equal("C", info.Uploader);
        Assert.Equal("1:02:05", info.DurationFormatted);
        Assert.Equal("b", info.Thumbnail);
        Assert.False(info.IsPlaylist);
        Assert.Equal("1.5K", Format.Count(info.ViewCount!.Value));
    }

    [Fact]
    public void LoadsDotEnvWithQuotes()
    {
        var path = Path.GetTempFileName();
        File.WriteAllText(path, "# comment\nR2_BUCKET=\"bucket\"\nR2_ACCESS_KEY_ID='key'\nBROKEN\n");
        var values = YtR2Config.LoadDotEnv(path);
        Assert.Equal("bucket", values["R2_BUCKET"]);
        Assert.Equal("key", values["R2_ACCESS_KEY_ID"]);
        Assert.False(values.ContainsKey("BROKEN"));
    }

    [Fact]
    public void ParsesR2ListingXml()
    {
        const string xml = "<ListBucketResult><Contents><Key>Music/a &amp; b.mp4</Key><Size>12</Size><LastModified>2026-01-01T00:00:00.000Z</LastModified></Contents><Contents><Key>x.txt</Key><Size>1</Size></Contents><NextContinuationToken>tok</NextContinuationToken></ListBucketResult>";
        var objects = YtLibraryService.ParseObjects(xml);
        Assert.Equal("Music/a & b.mp4", objects[0].Key);
        Assert.Equal(12, objects[0].Size);
        Assert.Equal("tok", YtLibraryService.FirstXmlValue(xml, "NextContinuationToken"));
        Assert.Equal("Music/a%20%26%20b.mp4", YtLibraryService.EncodedKey("Music/a & b.mp4"));
    }

    [Fact]
    public void SignsR2RequestDeterministically()
    {
        var cfg = new YtR2Config("acc", "AKID", "secret", "https://acc.r2.cloudflarestorage.com", "bucket", "");
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var a = YtLibraryService.BuildSignedRequest(cfg, HttpMethod.Get, "", new Dictionary<string, string> { ["list-type"] = "2" }, null, now);
        var b = YtLibraryService.BuildSignedRequest(cfg, HttpMethod.Get, "", new Dictionary<string, string> { ["list-type"] = "2" }, null, now);
        var auth = a.Headers.GetValues("Authorization").Single();
        Assert.Equal(auth, b.Headers.GetValues("Authorization").Single());
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKID/20260102/auto/s3/aws4_request, SignedHeaders=host;x-amz-content-sha256;x-amz-date, Signature=", auth);
        Assert.Equal("https://acc.r2.cloudflarestorage.com/bucket?list-type=2", a.RequestUri!.ToString());
    }

    [Fact]
    public void RefusesDeletesOutsideRoot()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        Assert.True(YtLibraryService.IsInside(root, Path.Combine(root, "a", "b.mp4")));
        Assert.False(YtLibraryService.IsInside(root, root));
        Assert.False(YtLibraryService.IsInside(root, root + "-evil/x.mp4"));
        Assert.False(YtLibraryService.IsInside(root, Path.Combine(root, "..", "x.mp4")));
    }

    [Fact]
    public async Task ScansLocalLibrary()
    {
        var root = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(root, "Shows"));
        File.WriteAllText(Path.Combine(root, "Shows", "ep1.mp4"), "x");
        File.WriteAllText(Path.Combine(root, "Shows", "ep1.jpg"), "x");
        File.WriteAllText(Path.Combine(root, "loose.mkv"), "x");
        File.WriteAllText(Path.Combine(root, "notes.txt"), "x");
        var lib = await new YtLibraryService().ScanLocalFilesAsync(root);
        Assert.Equal(["Shows", "Unsorted"], lib.Folders.Select(f => f.Name));
        Assert.EndsWith("ep1.jpg", lib.Folders[0].Videos[0].Thumbnail);
        Assert.Single(lib.Folders[1].Videos);
    }
}
