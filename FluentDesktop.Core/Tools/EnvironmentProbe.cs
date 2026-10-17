using System.Runtime.InteropServices;
using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.Tools;

/// <summary>Host information and CLI tool versions for the Tools page.</summary>
public static class EnvironmentProbe
{
    private static readonly (string Exe, string[] Args)[] Probes =
    [
        ("flutter", ["--version"]),
        ("adb", ["version"]),
        ("scrcpy", ["--version"]),
        ("yt-dlp", ["--version"]),
        ("gh", ["--version"]),
        ("npx", ["--version"]),
        ("ffmpeg", ["-version"]),
        ("rclone", ["version"]),
        ("git", ["--version"]),
    ];

    public static List<(string Key, string Value)> SystemInfo()
    {
        var memory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return
        [
            ("OS", RuntimeInformation.OSDescription),
            ("Host", Environment.MachineName),
            ("CPU", Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString()),
            ("Cores", $"{Environment.ProcessorCount} logical"),
            ("Memory", $"{memory / (1024.0 * 1024 * 1024):0.0} GiB"),
            (".NET", RuntimeInformation.FrameworkDescription),
            ("App", "Fluent Desktop (WinUI 3 + C#)"),
        ];
    }

    /// <summary>First output line of each tool's version command, or "missing".</summary>
    public static async Task<List<(string Key, string Value)>> ToolVersionsAsync()
    {
        var tasks = Probes.Select(async p =>
        {
            try
            {
                var result = await ProcessRunner.RunAsync(p.Exe, p.Args, new ProcessOptions { Timeout = TimeSpan.FromSeconds(8) }).ConfigureAwait(false);
                var first = result.CombinedOutput.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
                return (p.Exe, result.Ok ? first ?? "?" : "missing");
            }
            catch (Exception)
            {
                return (p.Exe, "missing");
            }
        });
        return (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();
    }
}
