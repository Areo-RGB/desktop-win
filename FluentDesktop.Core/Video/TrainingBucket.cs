using System.Globalization;
using System.Text.RegularExpressions;
using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Core.YtDlp;

namespace FluentDesktop.Core.Video;

public sealed record R2Object(string Key, long Size, DateTimeOffset? LastModified)
{
    public string Name => Key[(Key.LastIndexOf('/') + 1)..];
}

/// <summary>One rclone output line: progress (0–100), a real error, or chatter.</summary>
public abstract record RcloneLine
{
    public sealed record Progress(double Percent) : RcloneLine;
    public sealed record Error(string Message) : RcloneLine;
    public sealed record Ignore : RcloneLine;
}

/// <summary>
/// The Training Hub R2 bucket, shared with DesktopCC: credentials come from
/// <c>r2.properties</c> in the config dir. Listing/deleting are SigV4 HTTP
/// calls; uploads go through rclone (multipart, retries, checksums).
/// </summary>
public static partial class TrainingBucket
{
    public const string PropertiesFile = "r2.properties";
    private const string RcloneRemote = "desktopcc";

    public static string PropertiesPath => AppPaths.ConfigFile(PropertiesFile);

    public static YtR2Config LoadCredentials()
    {
        var path = PropertiesPath;
        if (!File.Exists(path)) throw new YtDlpException($"No R2 credentials: add {path}");
        return ParseProperties(File.ReadAllText(path));
    }

    internal static YtR2Config ParseProperties(string text)
    {
        var map = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq > 0) map[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
        string Get(string key) => map.TryGetValue(key, out var v) && v.Length > 0 ? v : throw new YtDlpException($"{PropertiesFile} is missing {key}");
        var account = Get("r2.accountId");
        return new YtR2Config(account, Get("r2.accessKeyId"), Get("r2.secretAccessKey"),
            $"https://{account}.r2.cloudflarestorage.com", Get("r2.bucket"), "");
    }

    public static async Task<List<R2Object>> ListAsync(YtLibraryService library, YtR2Config credentials, string prefix)
    {
        var objects = await library.ListR2ObjectsAsync(credentials, prefix).ConfigureAwait(false);
        // Folder placeholder objects ("prefix/") carry no data.
        return objects.Where(o => !o.Key.EndsWith('/')).Select(o => new R2Object(o.Key, o.Size, o.Modified)).ToList();
    }

    /// <summary>Listing prefix plus the file name.</summary>
    public static string KeyFor(string prefix, string file)
    {
        prefix = prefix.Trim();
        if (prefix.Length > 0 && !prefix.EndsWith('/')) prefix += "/";
        return prefix + Path.GetFileName(file);
    }

    /// <summary>Starts <c>rclone copyto</c> of <paramref name="file"/> to <paramref name="key"/>, overwriting it.</summary>
    public static async Task<ManagedProcess> StartUploadAsync(YtR2Config c, string key, string file, Action<string> onLine)
    {
        if (!await ProcessRunner.CommandExistsAsync("rclone").ConfigureAwait(false))
            throw new YtDlpException("Uploads need rclone on PATH (https://rclone.org/install/); listing still works");
        string[] args =
        [
            "copyto", file, $"{RcloneRemote}:{c.Bucket}/{key}",
            "-v", // stats lines are INFO; without this there is no progress
            "--s3-no-check-bucket", // the token is scoped to one bucket
            "--s3-upload-cutoff=100M", "--s3-chunk-size=25M", "--s3-upload-concurrency=4",
            "--retries=5", "--stats-one-line", "--stats=1s",
            "--config", OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
        ];
        var env = new Dictionary<string, string>
        {
            ["RCLONE_CONFIG_DESKTOPCC_TYPE"] = "s3",
            ["RCLONE_CONFIG_DESKTOPCC_PROVIDER"] = "Cloudflare",
            ["RCLONE_CONFIG_DESKTOPCC_ACCESS_KEY_ID"] = c.AccessKeyId,
            ["RCLONE_CONFIG_DESKTOPCC_SECRET_ACCESS_KEY"] = c.SecretAccessKey,
            ["RCLONE_CONFIG_DESKTOPCC_ENDPOINT"] = $"https://{c.AccountId}.r2.cloudflarestorage.com",
            ["RCLONE_CONFIG_DESKTOPCC_ACL"] = "private",
        };
        return ProcessRunner.StartStreaming("rclone", args, onLine, onLine, new ProcessOptions { Environment = env });
    }

    [GeneratedRegex(@"(\d+(?:\.\d+)?)%")]
    private static partial Regex PercentRegex();

    public static RcloneLine ParseRcloneLine(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0) return new RcloneLine.Ignore();
        if (trimmed.Contains("ERROR") || trimmed.Contains("Failed to")) return new RcloneLine.Error(trimmed);
        // Stats lines: `231.344 MiB / 409.700 MiB, 56%, 4.7 MiB/s, ETA 38s`.
        var m = PercentRegex().Match(trimmed);
        return m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct)
            ? new RcloneLine.Progress(Math.Clamp(pct, 0, 100))
            : new RcloneLine.Ignore();
    }
}
