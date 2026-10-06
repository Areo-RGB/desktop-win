using System.Collections;

namespace FluentDesktop.Core.Infrastructure;

/// <summary>
/// Builds the child-process environment: the parent environment plus common
/// tool directories that GUI apps often miss from PATH (npm, cargo, bun,
/// Android platform-tools, WinGet links...).
/// </summary>
public static class EnvironmentEnricher
{
    public static Dictionary<string, string> Build(IReadOnlyDictionary<string, string>? overrides)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var env = new Dictionary<string, string>(comparer);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string k && entry.Value is string v) env[k] = v;
        }
        if (overrides != null)
        {
            foreach (var (k, v) in overrides) env[k] = v;
        }

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        env.TryGetValue("PATH", out var currentPath);
        var parts = (currentPath ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var extra in ExtraPaths())
        {
            if (!parts.Contains(extra, comparer) && Directory.Exists(extra)) parts.Add(extra);
        }
        env["PATH"] = string.Join(separator, parts);
        return env;
    }

    private static IEnumerable<string> ExtraPaths()
    {
        var home = AppPaths.Home;
        if (OperatingSystem.IsWindows())
        {
            var appData = AppPaths.RoamingAppData;
            var local = AppPaths.LocalAppData;
            yield return Path.Combine(appData, "npm");
            yield return Path.Combine(home, ".cargo", "bin");
            yield return Path.Combine(home, ".bun", "bin");
            yield return Path.Combine(home, ".local", "bin");
            yield return Path.Combine(local, "Android", "Sdk", "platform-tools");
            yield return Path.Combine(local, "Microsoft", "WinGet", "Links");
            yield return Path.Combine(local, "Programs", "Python", "Launcher");
            yield return @"C:\Program Files\nodejs";
            yield return @"C:\Program Files\Git\cmd";
            yield return @"C:\Program Files\GitHub CLI";
            yield break;
        }

        foreach (var p in new[] { "/usr/local/bin", "/usr/bin", "/bin", "/usr/local/sbin", "/usr/sbin", "/sbin" })
        {
            yield return p;
        }
        yield return Path.Combine(home, ".local", "bin");
        yield return Path.Combine(home, ".cargo", "bin");
        yield return Path.Combine(home, ".bun", "bin");
        yield return Path.Combine(home, "Android", "Sdk", "platform-tools");
    }
}
