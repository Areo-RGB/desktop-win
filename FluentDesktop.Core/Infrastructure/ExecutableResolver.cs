namespace FluentDesktop.Core.Infrastructure;

/// <summary>PATH (+ PATHEXT on Windows) lookup, like `where` / `which`.</summary>
public static class ExecutableResolver
{
    private static readonly string[] DefaultPathExt = [".COM", ".EXE", ".BAT", ".CMD"];

    public static bool IsBatchFile(string path) =>
        path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the full path of <paramref name="executable"/>, or null when it
    /// cannot be found. Paths (containing a separator) are returned as-is when
    /// the file exists.
    /// </summary>
    public static string? Resolve(string executable, string? pathVariable = null)
    {
        if (string.IsNullOrWhiteSpace(executable)) return null;
        var windows = OperatingSystem.IsWindows();
        var extensions = windows ? PathExtensions() : [""];

        if (executable.Contains('/') || executable.Contains('\\'))
        {
            if (File.Exists(executable)) return executable;
            if (windows && !Path.HasExtension(executable))
            {
                foreach (var ext in extensions)
                {
                    if (File.Exists(executable + ext)) return executable + ext;
                }
            }
            return null;
        }

        pathVariable ??= EnvironmentEnricher.Build(null)["PATH"];
        var separator = windows ? ';' : ':';
        var hasExtension = windows && extensions.Any(e => executable.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        foreach (var rawDir in pathVariable.Split(separator, StringSplitOptions.RemoveEmptyEntries))
        {
            var dir = rawDir.Trim().Trim('"');
            if (dir.Length == 0) continue;
            if (!windows || hasExtension)
            {
                var candidate = Path.Combine(dir, executable);
                if (File.Exists(candidate)) return candidate;
                if (!windows) continue;
            }
            if (windows && !hasExtension)
            {
                foreach (var ext in extensions)
                {
                    var candidate = Path.Combine(dir, executable + ext);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        return null;
    }

    private static string[] PathExtensions()
    {
        var raw = Environment.GetEnvironmentVariable("PATHEXT");
        if (string.IsNullOrWhiteSpace(raw)) return DefaultPathExt;
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
