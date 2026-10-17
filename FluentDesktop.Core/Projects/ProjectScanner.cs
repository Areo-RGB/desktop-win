using System.Text.RegularExpressions;

namespace FluentDesktop.Core.Projects;

public sealed record ProjectDef(string Name, string Path, string Kind, string StartExe, IReadOnlyList<string> StartArgs, string DevUrl)
{
    public bool CanStart => StartExe.Length > 0;

    public string CommandLine => StartArgs.Count == 0 ? StartExe : $"{StartExe} {string.Join(' ', StartArgs)}";
}

/// <summary>Discovers projects under the Projects folder (two levels deep).</summary>
public static partial class ProjectScanner
{
    public const string HiddenFile = "projects_hidden.json";

    private static readonly string[] Containers = ["android_apps", "apps", "flutter_apps", "web_apps", "extensions"];

    public static ProjectDef? Detect(string dir)
    {
        var name = System.IO.Path.GetFileName(dir.TrimEnd('\\', '/'));
        if (name.Length == 0 || name.StartsWith('.')) return null;
        bool Has(string f) => File.Exists(System.IO.Path.Combine(dir, f)) || Directory.Exists(System.IO.Path.Combine(dir, f));

        if (Has("pubspec.yaml"))
            return new(name, dir, "flutter", "flutter", ["run", "-d", OperatingSystem.IsWindows() ? "windows" : "linux"], "");
        if (Has("package.json"))
        {
            var text = SafeRead(System.IO.Path.Combine(dir, "package.json"));
            var pm = Has("pnpm-lock.yaml") ? "pnpm" : Has("bun.lock") || Has("bun.lockb") ? "bun" : Has("yarn.lock") ? "yarn" : "npm";
            var port = ExtractPort(text);
            return new(name, dir, "web", pm, ["run", "dev"], port == null ? "" : $"http://localhost:{port}");
        }
        if (Has("build.gradle") || Has("build.gradle.kts") || Has("gradlew"))
        {
            return OperatingSystem.IsWindows()
                ? new(name, dir, "android", "cmd", ["/C", "gradlew.bat", "assembleDebug"], "")
                : new(name, dir, "android", "bash", ["gradlew", "assembleDebug"], "");
        }
        if (Has("manifest.json")) return new(name, dir, "extension", "", [], "");
        return null;
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); } catch (IOException) { return ""; }
    }

    [GeneratedRegex(@"--port(?:=|\s+)(\d+)")]
    private static partial Regex PortRegex();

    /// <summary>--port=NNNN or --port NNNN anywhere in package.json.</summary>
    public static string? ExtractPort(string packageJson) =>
        PortRegex().Match(packageJson) is { Success: true } m ? m.Groups[1].Value : null;

    public static List<ProjectDef> Discover(string root)
    {
        var result = new List<ProjectDef>();
        if (!Directory.Exists(root)) return result;
        void ScanChildren(string dir)
        {
            foreach (var child in SafeDirs(dir))
            {
                if (Detect(child) is { } def) result.Add(def);
            }
        }
        foreach (var dir in SafeDirs(root))
        {
            var name = System.IO.Path.GetFileName(dir);
            if (name.StartsWith('.')) continue;
            if (Containers.Contains(name)) { ScanChildren(dir); continue; }
            if (Detect(dir) is { } def) { result.Add(def); continue; }
            ScanChildren(dir);
        }
        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    private static IEnumerable<string> SafeDirs(string dir)
    {
        try { return Directory.GetDirectories(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}
