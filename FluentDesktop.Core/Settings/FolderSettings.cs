using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.Settings;

/// <summary>Folders set once in Settings, persisted in paths.json (shared with DesktopCC).</summary>
public sealed record FolderSettings
{
    public const string FileName = "paths.json";

    /// <summary>Scanned by Projects.</summary>
    public string Projects { get; init; } = Path.Combine(AppPaths.Home, "Projects");

    /// <summary>yt-dlp output folder.</summary>
    public string Downloads { get; init; } = AppPaths.Downloads;

    /// <summary>Video page's local files.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("video_library")]
    public string VideoLibrary { get; init; } = Path.Combine(AppPaths.Videos, "video_metadata");

    /// <summary>Read fresh each time so pages pick up edits made in Settings.</summary>
    public static FolderSettings Load()
    {
        var loaded = JsonStore.LoadAsync<FolderSettings>(AppPaths.ConfigFile(FileName)).GetAwaiter().GetResult() ?? new FolderSettings();
        var defaults = new FolderSettings();
        // A Linux path written by DesktopCC on another machine is useless here.
        return loaded with
        {
            Projects = Usable(loaded.Projects) ?? defaults.Projects,
            Downloads = Usable(loaded.Downloads) ?? defaults.Downloads,
            VideoLibrary = Usable(loaded.VideoLibrary) ?? defaults.VideoLibrary,
        };
    }

    private static string? Usable(string? path) =>
        string.IsNullOrWhiteSpace(path) || (OperatingSystem.IsWindows() && path.StartsWith('/')) ? null : path;

    public Task SaveAsync() => JsonStore.SaveAsync(AppPaths.ConfigFile(FileName), this);
}
