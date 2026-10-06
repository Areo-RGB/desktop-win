using System.Text.Json;
using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.SpielerPlus;

/// <summary>
/// Runs the bundled Python pipeline (FUSSBALL.DE / DFBnet → CSV/XLSX, ICS →
/// SpielerPlus import, participation export). Scripts are embedded in this
/// assembly and extracted to %LOCALAPPDATA%\fluent_desktop\spielerplus\python.
/// </summary>
public sealed class SpielerplusService(string? configFilePath = null, string? runtimeDir = null)
{
    public static readonly string[] BundledFiles =
    [
        "fussball_csv.py", "dfbnet_api.py", "dfbnet_auth.py", "browser_open.py", "ics_fetch.py",
        "spielerplus_probe.py", "spielerplus_create.py", "spielerplus_participation.py", "run_import.py",
        "requirements.txt", "games_en.xlsx",
    ];

    private static List<string>? _pythonCache;

    public string ConfigFilePath { get; } = configFilePath ?? AppPaths.ConfigFile("spielerplus_config.json");

    public string RuntimeDir { get; } = runtimeDir ?? ResolveRuntimeDir();

    public static string ResolveRuntimeDir()
    {
        var overrideDir = Environment.GetEnvironmentVariable("SPIELERPLUS_IMPORTER_DIR")?.Trim();
        if (string.IsNullOrEmpty(overrideDir))
        {
            return Path.Combine(AppPaths.LocalAppData, "fluent_desktop", "spielerplus", "python");
        }
        var trimmed = overrideDir.TrimEnd('/', '\\');
        return Path.GetFileName(trimmed) == "python" ? trimmed : Path.Combine(trimmed, "python");
    }

    private string Script(string name) => Path.Combine(RuntimeDir, name);

    public string DefaultTemplate => Script("games_en.xlsx");

    public async Task<SpielerplusConfig> LoadConfigAsync() =>
        ((await JsonStore.LoadAsync<SpielerplusConfig>(ConfigFilePath).ConfigureAwait(false)) ?? new()).ApplyDateDefaults();

    public Task SaveConfigAsync(SpielerplusConfig config) => JsonStore.SaveAsync(ConfigFilePath, config);

    /// <summary>Writes the embedded scripts to <see cref="RuntimeDir"/>, skipping unchanged files.</summary>
    public async Task ExtractAssetsAsync()
    {
        Directory.CreateDirectory(RuntimeDir);
        var assembly = typeof(SpielerplusService).Assembly;
        foreach (var name in BundledFiles)
        {
            await using var resource = assembly.GetManifestResourceStream($"spielerplus/{name}")
                ?? throw new FileNotFoundException($"Embedded resource spielerplus/{name} is missing");
            using var buffer = new MemoryStream();
            await resource.CopyToAsync(buffer).ConfigureAwait(false);
            var bytes = buffer.ToArray();

            var target = Path.Combine(RuntimeDir, name);
            if (File.Exists(target) && (await File.ReadAllBytesAsync(target).ConfigureAwait(false)).AsSpan().SequenceEqual(bytes)) continue;
            var tmp = target + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, target, overwrite: true);
        }
    }

    public async Task<bool> VerifyAssetsAsync()
    {
        try
        {
            await ExtractAssetsAsync().ConfigureAwait(false);
            return File.Exists(Script("fussball_csv.py"));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Finds a Python 3 interpreter: python3, python, then the `py -3` launcher.</summary>
    public static async Task<List<string>?> ResolvePythonCommandAsync()
    {
        if (_pythonCache != null) return _pythonCache;
        List<string>[] candidates = [["python3"], ["python"], ["py", "-3"]];
        foreach (var candidate in candidates)
        {
            try
            {
                if (!await ProcessRunner.CommandExistsAsync(candidate[0]).ConfigureAwait(false)) continue;
                var probe = await ProcessRunner.RunAsync(candidate[0], candidate.Skip(1).Append("--version"),
                    new ProcessOptions { Timeout = TimeSpan.FromSeconds(10) }).ConfigureAwait(false);
                // The Windows Store "python" stub exits non-zero without printing a version.
                if (probe.Ok && $"{probe.Stdout}\n{probe.Stderr}".Contains("Python 3")) return _pythonCache = candidate;
            }
            catch
            {
                // Try the next candidate.
            }
        }
        return null;
    }

    public static async Task<string> PythonLabelAsync() =>
        (await ResolvePythonCommandAsync().ConfigureAwait(false)) is { } cmd ? string.Join(' ', cmd) : "Python 3";

    private static async Task<CommandResult> RunPythonAsync(IEnumerable<string> args, IReadOnlyDictionary<string, string>? env, Action<string>? onLog)
    {
        var cmd = await ResolvePythonCommandAsync().ConfigureAwait(false)
            ?? throw new InvalidOperationException("No Python 3 interpreter found on PATH (tried 'python3', 'python', 'py -3').");
        var merged = new Dictionary<string, string>(env ?? new Dictionary<string, string>()) { ["PYTHONIOENCODING"] = "utf-8" };
        var result = await ProcessRunner.RunAsync(cmd[0], cmd.Skip(1).Concat(args), new ProcessOptions { Environment = merged }).ConfigureAwait(false);
        if (onLog != null)
        {
            foreach (var line in (result.Stdout + "\n" + result.Stderr).Split('\n'))
            {
                if (line.Trim().Length > 0) onLog(line.TrimEnd('\r'));
            }
        }
        return result;
    }

    private static string Or(string value, string fallback) => value.Trim().Length == 0 ? fallback : value.Trim();

    private static string OutputDirOf(SpielerplusConfig c) => Or(c.OutputDir, AppPaths.Desktop);

    private static string TempJson(string prefix) => AppPaths.TempFile($"{prefix}_{DateTimeOffset.Now.ToUnixTimeMilliseconds()}.json");

    public static async Task<List<SpielerplusFixture>> ReadFixturesJsonAsync(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("fixtures", out var list) || list.ValueKind != JsonValueKind.Array) return [];
            return list.Deserialize<List<SpielerplusFixture>>() ?? [];
        }
        catch
        {
            return [];
        }
    }

    public List<string> BuildFussballArgs(SpielerplusConfig c, string csvOut, string xlsxOut, string jsonOut) =>
    [
        Script("fussball_csv.py"),
        "--url", c.FussballUrl.Trim(),
        "--date-from", Or(c.FussballDateFrom, SpielerplusConfig.TodayIso()),
        "--date-to", Or(c.FussballDateTo, SpielerplusConfig.DefaultSeasonEnd()),
        "--home-venue", Or(c.HomeMarker, "Ernst-Reuter-Sportf"),
        "--out", csvOut,
        "--xlsx-out", xlsxOut,
        "--template", DefaultTemplate,
        "--json-out", jsonOut,
    ];

    public List<string> BuildDfbnetArgs(SpielerplusConfig c, string csvOut, string xlsxOut, string jsonOut)
    {
        var args = new List<string>
        {
            Script("dfbnet_api.py"),
            "--base-url", Or(c.DfbnetBaseUrl, "https://www.dfbnet.org/sbo-mobile/v2/rest"),
            "--team-name", Or(c.DfbnetTeamName, "FC Hertha 03 III"),
            "--date-from", Or(c.FussballDateFrom, SpielerplusConfig.TodayIso()),
            "--date-to", Or(c.DfbnetDateTo, SpielerplusConfig.DefaultSeasonEnd()),
            "--home-venue", Or(c.HomeMarker, "Ernst-Reuter-Sportf"),
            "--out", csvOut,
            "--xlsx-out", xlsxOut,
            "--template", DefaultTemplate,
            "--json-out", jsonOut,
            "--agent-browser-auth",
            "--session", Or(c.BrowserSession, "spielerplus-ics"),
            "--profile", Or(c.BrowserProfile, "Default"),
        };
        if (c.DfbnetClubId.Trim().Length > 0) args.AddRange(["--club-id", c.DfbnetClubId.Trim()]);
        if (c.DfbnetTeamTypeId.Trim().Length > 0) args.AddRange(["--team-type-id", c.DfbnetTeamTypeId.Trim()]);
        if (c.DfbnetCompetitionTypeId.Trim().Length > 0) args.AddRange(["--competition-type-id", c.DfbnetCompetitionTypeId.Trim()]);
        return args;
    }

    public async Task<SpielerplusFetchResult> FetchFixturesAsync(SpielerplusConfig config, FixtureSource source, Action<string>? onLog)
    {
        var dir = OutputDirOf(config);
        Directory.CreateDirectory(dir);
        var csv = Path.Combine(dir, "spielerplus_games.csv");
        var xlsx = Path.Combine(dir, "spielerplus_games.xlsx");
        var json = TempJson("spielerplus_fixtures");
        var args = source == FixtureSource.Fussball
            ? BuildFussballArgs(config, csv, xlsx, json)
            : BuildDfbnetArgs(config, csv, xlsx, json);

        var result = await RunPythonAsync(args, null, onLog).ConfigureAwait(false);
        if (!result.Ok) return new SpielerplusFetchResult(false, result.ExitCode, Error: $"Fetch failed with exit code {result.ExitCode}");

        var fixtures = await ReadFixturesJsonAsync(json).ConfigureAwait(false);
        try { File.Delete(json); } catch { /* temp file */ }
        return new SpielerplusFetchResult(true, result.ExitCode, csv, xlsx, fixtures);
    }

    public Task<CommandResult> FetchIcsAsync(SpielerplusConfig c, string eventsOut, Action<string>? onLog)
    {
        var args = new List<string>
        {
            Script("ics_fetch.py"),
            "--url", c.IcsUrl.Trim(),
            "--out", eventsOut,
            "--include", Or(c.IcsInclude, "game,birthday,event"),
            "--team-hint", Or(c.IcsTeamHint, "FC Hertha 03"),
        };
        if (c.IcsFromDate.Trim().Length > 0) args.AddRange(["--from-date", c.IcsFromDate.Trim()]);
        return RunPythonAsync(args, null, onLog);
    }

    public Task<CommandResult> ProbeExistingAsync(SpielerplusConfig c, string existingOut, bool headed, Action<string>? onLog)
    {
        var args = new List<string>
        {
            Script("spielerplus_probe.py"),
            "--session", Or(c.BrowserSession, "spielerplus-ics"),
            "--profile", Or(c.BrowserProfile, "Default"),
            "--out", existingOut,
        };
        if (headed) args.Add("--headed");
        return RunPythonAsync(args, null, onLog);
    }

    public Task<CommandResult> ExportParticipationAsync(SpielerplusConfig c, string csvOut, bool headed, Action<string>? onLog)
    {
        var args = new List<string>
        {
            Script("spielerplus_participation.py"),
            "--session", Or(c.BrowserSession, "spielerplus-ics"),
            "--profile", Or(c.BrowserProfile, "Default"),
            "--url", Or(c.ParticipationUrl, "https://www.spielerplus.de/en/participation"),
            "--out", TempJson("spielerplus_participation"),
            "--csv", csvOut,
        };
        if (headed) args.Add("--headed");
        return RunPythonAsync(args, null, onLog);
    }

    public string ParticipationCsvPath(SpielerplusConfig c) => Path.Combine(OutputDirOf(c), "spielerplus_participation.csv");

    public Task<CommandResult> RunFullImportAsync(SpielerplusConfig c, bool dryRun, int limit, bool headed, Action<string>? onLog)
    {
        var workdir = Directory.CreateTempSubdirectory("spielerplus_import_").FullName;
        var args = new List<string>
        {
            Script("run_import.py"),
            "--ics-url", c.IcsUrl.Trim(),
            "--session", Or(c.BrowserSession, "spielerplus-ics"),
            "--workdir", workdir,
            "--team-hint", Or(c.IcsTeamHint, "FC Hertha 03"),
        };
        if (dryRun) args.Add("--dry-run");
        if (limit > 0) args.AddRange(["--limit", $"{limit}"]);
        if (headed) args.Add("--headed");
        var env = new Dictionary<string, string> { ["AGENT_BROWSER_PROFILE"] = Or(c.BrowserProfile, "Default") };
        return RunPythonAsync(args, env, onLog);
    }

    public static string NewTempJson(string prefix) => TempJson(prefix);
}
