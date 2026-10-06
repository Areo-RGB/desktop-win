using System.Text.Json.Serialization;
using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.Dashboard;

/// <summary>
/// A preconfigured command on the dashboard. JSON shape matches the Flutter
/// app's dashboard_commands.json so existing files load unchanged.
/// </summary>
public sealed record DashboardCommand
{
    public string Id { get; init; } = $"cmd_{DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
    public string Title { get; init; } = "Custom Command";
    public string Executable { get; init; } = "";
    public List<string> Arguments { get; init; } = [];
    public string Description { get; init; } = "";
    public string? SystemIconPath { get; init; }
    public string? AssetIconPath { get; init; }
    public int? FallbackIconCode { get; init; }
    public bool IsDefault { get; init; }

    [JsonIgnore]
    public string CommandString => $"{Executable} {string.Join(' ', Arguments)}".Trim();

    /// <summary>Bundled brand icon for known CLI agents, else the asset icon.</summary>
    [JsonIgnore]
    public string? BrandIcon
    {
        get
        {
            var haystack = $"{Id} {Title} {Executable} {string.Join(' ', Arguments)}".ToLowerInvariant();
            if (haystack.Contains("claude")) return "Assets/cc_apps/claude.svg";
            if (haystack.Contains("opencode")) return "Assets/cc_apps/opencode.svg";
            if (haystack.Contains("openclaw")) return "Assets/cc_apps/openclaw.svg";
            if (haystack.Contains("gemini")) return "Assets/cc_apps/gemini.svg";
            if (haystack.Contains("openai") || haystack.Contains("codex")) return "Assets/cc_apps/openai.svg";
            if (System.Text.RegularExpressions.Regex.IsMatch(haystack, "(^|[^a-z])pi([^a-z]|$)")) return "Assets/cc_apps/pi.svg";
            if (AssetIconPath is { } asset) return asset.Replace("assets/images/", "Assets/");
            return null;
        }
    }

    public static List<DashboardCommand> DefaultCommands
    {
        get
        {
            var home = AppPaths.Home;
            var windows = OperatingSystem.IsWindows();
            var piBin = windows ? "pi" : ResolveOrBare(Path.Combine(home, ".local", "bin", "pi"), "pi");
            var ompBin = windows ? "omp" : ResolveOrBare(Path.Combine(home, ".bun", "bin", "omp"), "omp");
            var list = new List<DashboardCommand>
            {
                new()
                {
                    Id = "pi_update",
                    Title = "pi update",
                    Executable = piBin,
                    Arguments = ["update"],
                    Description = "Update pi coding agent and packages",
                    AssetIconPath = "assets/images/cc_apps/pi.svg",
                    IsDefault = true,
                },
            };
            list.Add(windows
                ? new DashboardCommand
                {
                    Id = "omp_terminal",
                    Title = "OMP Terminal",
                    Executable = "cmd",
                    Arguments = ["/c", "start", "powershell", "-NoExit", "-Command", "omp"],
                    Description = "Launch OMP in an external PowerShell window",
                    AssetIconPath = "assets/images/omp_icon.png",
                    IsDefault = true,
                }
                : new DashboardCommand
                {
                    Id = "omp_ghostty",
                    Title = "OMP Ghostty",
                    Executable = ResolveOrBare("/usr/bin/ghostty", "ghostty"),
                    Arguments = ["-e", $"bash -c \"{ompBin}; exec bash\""],
                    Description = "Launch OMP in external Ghostty terminal",
                    AssetIconPath = "assets/images/omp_icon.png",
                    IsDefault = true,
                });
            list.Add(new DashboardCommand
            {
                Id = "omp_update",
                Title = "omp update",
                Executable = ompBin,
                Arguments = ["update"],
                Description = "Update Oh My Posh prompt engine",
                AssetIconPath = "assets/images/omp_icon.png",
                IsDefault = true,
            });
            return list;
        }
    }

    private static string ResolveOrBare(string path, string bare) => File.Exists(path) ? path : bare;
}
