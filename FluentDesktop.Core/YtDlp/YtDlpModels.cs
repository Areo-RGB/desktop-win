using System.Text.Json;
using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.YtDlp;

public enum YtDownloadType { Video, Audio }

public enum YtVideoQuality { Best, P1080, P720, P480, P360 }

public enum YtAudioFormat { Mp3, M4a }

public static class YtOptionExtensions
{
    public static int? MaxHeight(this YtVideoQuality q) => q switch
    {
        YtVideoQuality.P1080 => 1080,
        YtVideoQuality.P720 => 720,
        YtVideoQuality.P480 => 480,
        YtVideoQuality.P360 => 360,
        _ => null,
    };

    public static string Label(this YtVideoQuality q) => q switch
    {
        YtVideoQuality.Best => "Best available",
        YtVideoQuality.P1080 => "1080p Full HD",
        YtVideoQuality.P720 => "720p HD",
        YtVideoQuality.P480 => "480p SD",
        _ => "360p Low",
    };

    public static string Value(this YtVideoQuality q) => q.MaxHeight() is { } h ? $"{h}p" : "best";

    public static string Value(this YtAudioFormat f) => f == YtAudioFormat.Mp3 ? "mp3" : "m4a";

    public static string Label(this YtAudioFormat f) => f == YtAudioFormat.Mp3 ? "MP3" : "M4A AAC";
}

public sealed record YtDownloadOptions(
    string Url,
    YtDownloadType DownloadType,
    YtVideoQuality Quality,
    YtAudioFormat AudioFormat,
    bool EmbedSubs,
    bool EmbedThumbnail,
    bool EmbedMetadata,
    bool EmbedChapters,
    bool IsPlaylist,
    string OutputDir);

public sealed record YtDownloadProgress(double Percent, string TotalSize, string Speed, string Eta, string Status, string? Filename = null);

public sealed record YtHistoryItem
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "Untitled download";
    public string Thumbnail { get; init; } = "";
    public string Uploader { get; init; } = "Unknown uploader";
    public string DownloadType { get; init; } = "video";
    public string Quality { get; init; } = "best";
    public string FilePath { get; init; } = "";

    /// <summary>Unix milliseconds (matches the Flutter history file).</summary>
    public long Timestamp { get; init; }

    public DateTimeOffset When => DateTimeOffset.FromUnixTimeMilliseconds(Timestamp);
}

public sealed record YtVideoInfo(
    string Id,
    string Title,
    string Thumbnail,
    string Uploader,
    int Duration,
    string Url,
    long? ViewCount = null,
    bool IsPlaylist = false,
    int? PlaylistCount = null)
{
    public string DurationFormatted => FormatDuration(Duration);

    public static YtVideoInfo FromJson(JsonElement data, string url)
    {
        var hasEntries = data.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array;
        var isPlaylist = Str(data, "_type") == "playlist" || hasEntries;
        var duration = (int)(Num(data, "duration") ?? 0);
        var playlistCount = (int?)Num(data, "playlist_count") ?? (hasEntries ? entries.GetArrayLength() : null);
        return new YtVideoInfo(
            Id: Str(data, "id") ?? DateTimeOffset.Now.ToUnixTimeMilliseconds().ToString(),
            Title: Str(data, "title") ?? "Unknown title",
            Thumbnail: ThumbnailFrom(data),
            Uploader: Str(data, "uploader") ?? Str(data, "channel") ?? Str(data, "uploader_id") ?? "Unknown uploader",
            Duration: duration,
            Url: url,
            ViewCount: Num(data, "view_count"),
            IsPlaylist: isPlaylist,
            PlaylistCount: isPlaylist ? playlistCount : null);
    }

    public static string FormatDuration(int seconds)
    {
        if (seconds <= 0) return "0:00";
        var h = seconds / 3600;
        var m = seconds % 3600 / 60;
        var s = seconds % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null && v.ToString().Trim() is { Length: > 0 } s ? s : null;

    private static long? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return (long)Math.Round(v.GetDouble());
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var n) ? n : null;
    }

    private static string ThumbnailFrom(JsonElement data)
    {
        if (Str(data, "thumbnail") is { } t) return t;
        if (data.TryGetProperty("thumbnails", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray().Reverse())
            {
                if (item.ValueKind == JsonValueKind.Object && Str(item, "url") is { } u) return u;
            }
        }
        return "";
    }
}

public sealed record YtLocalVideo(string Name, string Path, long Size, DateTime? Modified, string? Thumbnail);

public sealed record YtLocalFolder(string Name, string Path, IReadOnlyList<YtLocalVideo> Videos);

public sealed record YtLocalLibrary(string RootPath, IReadOnlyList<YtLocalFolder> Folders);

public sealed record YtCloudVideo(string Name, string Key, long Size, DateTimeOffset? Modified, string Url);

public sealed record YtCloudFolder(string Name, string Path, IReadOnlyList<YtCloudVideo> Videos);

public sealed record YtCloudLibrary(string Bucket, IReadOnlyList<YtCloudFolder> Folders);

public sealed record YtR2Config(
    string AccountId,
    string AccessKeyId,
    string SecretAccessKey,
    string Endpoint,
    string Bucket,
    string PublicBaseUrl)
{
    public bool CanList =>
        AccessKeyId.Trim().Length > 0 && SecretAccessKey.Trim().Length > 0 &&
        Endpoint.Trim().Length > 0 && Bucket.Trim().Length > 0;

    /// <summary>
    /// Reads R2 credentials from .env files (YT_DLP_ENV_FILE, else
    /// %USERPROFILE%\Desktop\auth\.env, then .\.env) overlaid by the process environment.
    /// </summary>
    public static YtR2Config FromEnvironment()
    {
        var values = new Dictionary<string, string>();
        var overridePath = Environment.GetEnvironmentVariable("YT_DLP_ENV_FILE")?.Trim();
        var candidates = new List<string>
        {
            string.IsNullOrEmpty(overridePath) ? Path.Combine(AppPaths.Home, "Desktop", "auth", ".env") : overridePath,
            Path.Combine(".", ".env"),
        };
        foreach (var path in candidates)
        {
            foreach (var (k, v) in LoadDotEnv(path)) values[k] = v;
        }
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string k && entry.Value is string v) values[k] = v;
        }
        string Get(string key, string fallback = "") => values.TryGetValue(key, out var v) ? v : fallback;
        var account = Get("R2_ACCOUNT_ID", Get("CLOUDFLARE_ACCOUNT_ID"));
        return new YtR2Config(
            account,
            Get("R2_ACCESS_KEY_ID"),
            Get("R2_SECRET_ACCESS_KEY"),
            Get("R2_ENDPOINT", account.Length == 0 ? "" : $"https://{account}.r2.cloudflarestorage.com"),
            Get("R2_BUCKET", "yt-dlp-gui-videos-20260906"),
            Get("R2_PUBLIC_BASE_URL"));
    }

    public static Dictionary<string, string> LoadDotEnv(string path)
    {
        var values = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(path)) return values;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
                {
                    value = value[1..^1];
                }
                values[key] = value;
            }
        }
        catch
        {
            // Unreadable files are ignored.
        }
        return values;
    }
}
