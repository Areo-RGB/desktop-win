using System.Text.Json.Nodes;
using FluentDesktop.Core.Mcp;
using FluentDesktop.Core.Projects;
using FluentDesktop.Core.Skills;
using FluentDesktop.Core.Video;

namespace FluentDesktop.Core.Tests;

public class McpTests
{
    private static JsonObject Stdio(string cmd) => new() { ["type"] = "stdio", ["command"] = cmd };

    private static (string Root, McpPaths Paths, McpService Service) Service()
    {
        var root = Directory.CreateTempSubdirectory("fd-mcp").FullName;
        Directory.CreateDirectory(Path.Combine(root, ".claude"));
        Directory.CreateDirectory(Path.Combine(root, ".codex"));
        var paths = McpPaths.Under(root);
        return (root, paths, new McpService(paths, Path.Combine(root, "store.json")));
    }

    [Fact]
    public void SpliceKeepsCommentsAndOtherKeys()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "settings.json");
        File.WriteAllText(path, "{\n  // a comment\n  \"theme\": \"dark\",\n  \"mcpServers\": {\n    \"old\": {\"command\": \"x\"}\n  }\n}\n");
        JsonConfigFile.SpliceTopLevelKey(path, "mcpServers", new JsonObject { ["new"] = Stdio("npx") });
        var text = File.ReadAllText(path);
        Assert.Contains("// a comment", text);
        Assert.Contains("\"theme\": \"dark\"", text);
        Assert.DoesNotContain("\"old\"", text);
        Assert.True(JsonConfigFile.ReadServersMap(path, "mcpServers").ContainsKey("new"));
    }

    [Fact]
    public void SpliceInsertsMissingKey()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory().FullName, "opencode.json");
        File.WriteAllText(path, "{\n  \"$schema\": \"x\"\n}\n");
        JsonConfigFile.SpliceTopLevelKey(path, "mcp", new JsonObject());
        var text = File.ReadAllText(path);
        Assert.Contains("\"$schema\": \"x\"", text);
        Assert.Contains("\"mcp\": {}", text);
    }

    [Fact]
    public void TomlReplaceKeepsUnrelatedSettings()
    {
        const string text = "# top\nmodel = \"o3\"\n\n[mcp_servers.old]\ncommand = \"x\"\n\n[mcp_servers.old.env]\nA = \"1\"\n\n[profiles.fast]\nmodel = \"mini\"\n";
        var output = TomlMcp.ReplaceServers(text, [("fetch", TomlMcp.CodexEntry(new JsonObject { ["type"] = "http", ["url"] = "https://x/mcp", ["headers"] = new JsonObject { ["A"] = "b" }, ["command"] = "" }))]);
        Assert.Contains("# top", output);
        Assert.Contains("[profiles.fast]", output);
        Assert.DoesNotContain("old", output);
        var model = Tomlyn.Toml.ToModel(output);
        var entries = TomlMcp.NativeEntries(model, includeLegacy: true);
        var (id, entry) = Assert.Single(entries);
        Assert.Equal("fetch", id);
        Assert.Equal("https://x/mcp", entry.Str("url"));
        Assert.False(entry.ContainsKey("command"));
        Assert.Equal("b", entry["http_headers"]!["A"]!.GetValue<string>());
    }

    [Fact]
    public void UpsertToggleDeleteRoundTrip()
    {
        var (_, paths, svc) = Service();
        var apps = new McpApps { Claude = true, Codex = true, Gemini = true };
        var notes = svc.Upsert(McpServer.Create("fetch", Stdio("uvx"), apps));
        Assert.True(notes.Single(n => n.App == McpApp.Claude).Written);
        Assert.False(notes.Single(n => n.App == McpApp.Gemini).Written);
        Assert.Contains("fetch", File.ReadAllText(paths.CodexConfig));

        Assert.True(svc.Toggle("fetch", McpApp.Codex, false));
        Assert.DoesNotContain("fetch", File.ReadAllText(paths.CodexConfig));

        svc.Delete("fetch");
        Assert.Empty(svc.Servers);
        Assert.False(JsonConfigFile.ReadServersMap(paths.ClaudeJson, "mcpServers").ContainsKey("fetch"));
    }

    [Fact]
    public void ImportEnablesFlagWithoutOverwritingSpec()
    {
        var (_, paths, svc) = Service();
        File.WriteAllText(paths.ClaudeJson, "{\"mcpServers\": {\"fetch\": {\"command\": \"old\"}, \"other\": {\"url\": \"https://y\"}}}");
        svc.Upsert(McpServer.Create("fetch", Stdio("uvx"), new McpApps { Codex = true }));
        var report = svc.ImportAll();
        Assert.Equal(2, report.Changed);
        Assert.Equal("uvx", svc.Get("fetch")!.Server.Str("command"));
        Assert.True(svc.Get("fetch")!.Apps.Claude);
        Assert.True(svc.Get("other")!.Apps.Claude);
    }

    [Fact]
    public void PiToggleOffRemovesEntryLikeDesktopCc()
    {
        var (_, paths, svc) = Service();
        svc.Upsert(McpServer.Create("time", Stdio("uvx"), new McpApps { Pi = true }));
        var entry = JsonNode.Parse(File.ReadAllText(paths.PiMcpJson))!["mcpServers"]!["time"]!;
        Assert.True(entry["enabled"]!.GetValue<bool>());
        Assert.Equal("uvx", entry["command"]!.GetValue<string>());
        svc.Toggle("time", McpApp.Pi, false);
        Assert.Null(JsonNode.Parse(File.ReadAllText(paths.PiMcpJson))!["mcpServers"]!["time"]);
    }

    [Fact]
    public void HermesMergePreservesExtras()
    {
        var (root, paths, svc) = Service();
        Directory.CreateDirectory(paths.HermesDir);
        File.WriteAllText(paths.HermesConfig, "model: x\nmcp_servers:\n  fetch:\n    command: old\n    timeout: 99\n");
        svc.Upsert(McpServer.Create("fetch", Stdio("uvx"), new McpApps { Hermes = true }));
        var text = File.ReadAllText(paths.HermesConfig);
        Assert.Contains("timeout: 99", text);
        Assert.Contains("command: uvx", text);
        Assert.Contains("model: x", text);
        _ = root;
    }

    [Fact]
    public void ClaudeWrapsNpxOnWindows()
    {
        var obj = new JsonObject { ["command"] = "npx", ["args"] = new JsonArray("-y", "x") };
        ClaudeAdapter.WrapForWindows(obj, windows: true);
        Assert.Equal("cmd", obj.Str("command"));
        Assert.Equal("[\"/c\",\"npx\",\"-y\",\"x\"]", obj["args"]!.ToJsonString());
        var uvx = new JsonObject { ["command"] = "uvx" };
        ClaudeAdapter.WrapForWindows(uvx, windows: true);
        Assert.Equal("uvx", uvx.Str("command"));
    }

    [Fact]
    public void PasteRecognizesShapes()
    {
        Assert.Null(McpPaste.Recognize("  "));
        Assert.Equal("mcpServers JSON", McpPaste.Recognize("{\"mcpServers\": {\"a\": {\"command\": \"npx\"}}}")!.Format);
        var single = McpPaste.Recognize("{\"url\": \"https://x/mcp\", \"http_headers\": {\"k\": \"v\"}}")!;
        Assert.Equal("http", single.Items[0].Spec.Str("type"));
        Assert.True(single.Items[0].Spec.ContainsKey("headers"));
        var open = McpPaste.Recognize("{\"mcp\": {\"m\": {\"type\": \"local\", \"command\": [\"npx\", \"-y\", \"pkg\"]}}}")!;
        Assert.Equal("npx", open.Items[0].Spec.Str("command"));
        var toml = McpPaste.Recognize("[mcp_servers.fetch]\ncommand = \"uvx\"\nargs = [\"mcp-server-fetch\"]\n")!;
        Assert.Equal("Codex TOML", toml.Format);
        Assert.Throws<McpException>(() => McpPaste.Recognize("{\"foo\": 1}"));
    }

    [Fact]
    public void ValidationRules()
    {
        Assert.Throws<McpException>(() => McpValidate.Spec(new JsonObject { ["type"] = "stdio", ["command"] = "" }));
        Assert.Throws<McpException>(() => McpValidate.Spec(new JsonObject { ["type"] = "http", ["url"] = "ftp://x" }));
        McpValidate.Spec(new JsonObject { ["command"] = "npx" });
        Assert.Throws<McpException>(() => McpValidate.Name("my server", true, []));
        Assert.Throws<McpException>(() => McpValidate.Name("fetch", true, ["fetch"]));
    }
}

public class VideoChapterTests
{
    [Fact]
    public void ParsesSortsAndRejectsOverlap()
    {
        var chapters = VideoChapters.Parse("{\"chapters\": [{\"name\": \"b\", \"start\": 5000, \"end\": 6000}, {\"start\": 0, \"end\": 1000}]}");
        Assert.Equal("Chapter 2", chapters[0].Name);
        Assert.Throws<FormatException>(() => VideoChapters.Parse("[{\"start\": 0, \"end\": 2000}, {\"start\": 1000, \"end\": 3000}]"));
        Assert.Throws<FormatException>(() => VideoChapters.Parse("[{\"start\": 5, \"end\": 1}]"));
        Assert.Equal(chapters, VideoChapters.Parse(VideoChapters.ToPrettyJson(chapters)));
    }

    [Fact]
    public void FfMetadataFillsGaps()
    {
        var meta = VideoChapters.FfMetadata([new Chapter("A=1", 1000, 2000)]);
        Assert.Contains("START=0\nEND=1000\ntitle=–", meta);
        Assert.Contains("title=A\\=1", meta);
    }

    [Fact]
    public void KeyframesIncludeBoundaries()
    {
        var times = VideoChapters.KeyframeTimes([new Chapter("a", 1000, 3000)], 10, 4);
        Assert.Contains(1.0, times);
        Assert.Contains(3.0, times);
        Assert.Contains(8.0, times);
        Assert.DoesNotContain(0.0, times.Where(t => t > 0 && t < 1));
    }

    [Fact]
    public void ParsesProbeTagAndStream()
    {
        const string json = """
            {"streams":[{"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"r_frame_rate":"30000/1001","pix_fmt":"yuv420p","bit_rate":"3600000"}],
             "chapters":[{"start_time":"0.0","end_time":"1.0","tags":{"title":"–"}}],
             "format":{"duration":"12.5","size":"1000","tags":{"chapters":"[{\"name\":\"x\",\"start\":1000,\"end\":2000}]"}}}
            """;
        var info = VideoChapters.ParseProbe("f.mp4", json);
        Assert.Equal(ChapterSource.Tag, info.ChapterSource);
        Assert.Equal(12.5, info.DurationS);
        Assert.Equal(8, info.Video!.BitDepth);
        Assert.Equal(29.97, Math.Round(info.Video.Fps, 2));
        Assert.Empty(VideoChapters.Compare(info.Video, info.Video));
    }

    [Fact]
    public void RcloneProgressLines()
    {
        Assert.Equal(56, Assert.IsType<RcloneLine.Progress>(TrainingBucket.ParseRcloneLine("  231.3 MiB / 409.7 MiB, 56%, 4.7 MiB/s, ETA 38s")).Percent);
        Assert.IsType<RcloneLine.Error>(TrainingBucket.ParseRcloneLine("2024 ERROR : x: Failed to copy"));
        Assert.IsType<RcloneLine.Ignore>(TrainingBucket.ParseRcloneLine("Transferred: 1 / 1"));
        Assert.Equal("videos/a.mp4", TrainingBucket.KeyFor("videos", @"C:\x\a.mp4"));
    }

    [Fact]
    public void ReadsR2Properties()
    {
        var c = TrainingBucket.ParseProperties("# c\nr2.accountId=acc\nr2.bucket=b\nr2.accessKeyId=k\nr2.secretAccessKey=s\n");
        Assert.Equal("https://acc.r2.cloudflarestorage.com", c.Endpoint);
        Assert.Throws<YtDlp.YtDlpException>(() => TrainingBucket.ParseProperties("r2.bucket=b"));
    }
}

public class ProjectsAndSkillsTests
{
    [Fact]
    public void DetectsProjectKinds()
    {
        var root = Directory.CreateTempSubdirectory("fd-proj").FullName;
        var web = Directory.CreateDirectory(Path.Combine(root, "site")).FullName;
        File.WriteAllText(Path.Combine(web, "package.json"), "{\"scripts\":{\"dev\":\"vite --port 5174\"}}");
        File.WriteAllText(Path.Combine(web, "pnpm-lock.yaml"), "");
        var apps = Directory.CreateDirectory(Path.Combine(root, "apps", "phone")).FullName;
        File.WriteAllText(Path.Combine(apps, "build.gradle.kts"), "");
        Directory.CreateDirectory(Path.Combine(root, ".hidden"));

        var found = ProjectScanner.Discover(root);
        Assert.Equal(["phone", "site"], found.Select(p => p.Name));
        var site = found[1];
        Assert.Equal("pnpm", site.StartExe);
        Assert.Equal("http://localhost:5174", site.DevUrl);
        Assert.Equal("android", found[0].Kind);
    }

    [Fact]
    public void SkillMetadataFromFrontmatter()
    {
        Assert.Equal(("Nice", "Does things"), SkillService.Metadata("id", "---\r\nname: Nice\r\ndescription: Does things\r\n---\nbody"));
        Assert.Equal(("id", ""), SkillService.Metadata("id", "no frontmatter"));
        Assert.Throws<SkillException>(() => SkillService.Metadata("id", "---\nname: x\n"));
        Assert.False(SkillService.IsValidId("../x"));
        Assert.False(SkillService.IsValidId(".git"));
    }

    [Fact]
    public void SkillLifecycleWithLinks()
    {
        var root = Directory.CreateTempSubdirectory("fd-skills").FullName;
        var repo = Path.Combine(root, "skills-repo");
        Directory.CreateDirectory(repo);
        var appDirs = Enumerable.Range(0, 4).Select(i => Path.Combine(root, $"app{i}")).ToArray();
        var svc = new SkillService(repo, appDirs);
        svc.Initialize();
        svc.CreateSkill("demo", "---\nname: Demo\ndescription: d\n---\nhi");
        svc.SetEnabled("demo", SkillApp.Claude, true);
        var skill = Assert.Single(svc.List());
        Assert.True(skill.Enabled[1]);
        Assert.True(File.Exists(Path.Combine(appDirs[1], "demo", "SKILL.md")));

        svc.SetEnabled("demo", SkillApp.Claude, false);
        Assert.False(Directory.Exists(Path.Combine(appDirs[1], "demo")));

        svc.SaveSkill("demo", "---\nname: Demo2\n---\n");
        Assert.Equal("Demo2", svc.List()[0].Name);
        svc.Remove("demo");
        Assert.Empty(svc.List());
        var backup = svc.Backups()[^1];
        svc.Restore(backup);
        Assert.Equal("Demo2", svc.List()[0].Name);
    }
}
