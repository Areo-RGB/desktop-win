using System.Text.Json;
using System.Text.RegularExpressions;
using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.YtDlp;

public class YtDlpException(string message) : Exception(message);

public sealed class YtDlpCancelledException() : YtDlpException("Download canceled by user.");

/// <summary>Wraps the yt-dlp CLI: metadata, downloads with progress, history.</summary>
public sealed partial class YtDlpService(string executable = "yt-dlp", string? historyFilePath = null)
{
    private ManagedProcess? _active;
    private volatile bool _cancelRequested;

    public string HistoryFilePath { get; } = historyFilePath ?? AppPaths.ConfigFile("yt_dlp_history.json");

    public Task<bool> IsAvailableAsync() => ProcessRunner.CommandExistsAsync(executable);

    public static string DefaultDownloadDir => AppPaths.Downloads;

    public async Task<YtVideoInfo> ParseVideoInfoAsync(string url)
    {
        var clean = url.Trim();
        if (clean.Length == 0) throw new YtDlpException("Please enter a valid video URL.");
        var result = await ProcessRunner.RunAsync(executable, ["-j", "--no-warnings", "--flat-playlist", clean],
            new ProcessOptions { Timeout = TimeSpan.FromSeconds(25) }).ConfigureAwait(false);
        if (!result.Ok)
        {
            var message = result.Stderr.Trim().Length > 0 ? result.Stderr.Trim() : result.Stdout.Trim();
            throw new YtDlpException(message.Length == 0 ? "Failed to parse video details." : message);
        }
        var first = result.Stdout.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (first == null) throw new YtDlpException("yt-dlp returned no video metadata.");
        try
        {
            using var doc = JsonDocument.Parse(first);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new YtDlpException("Metadata was not a JSON object.");
            return YtVideoInfo.FromJson(doc.RootElement, clean);
        }
        catch (JsonException e)
        {
            throw new YtDlpException($"Failed to parse video metadata: {e.Message}");
        }
    }

    public static List<string> BuildDownloadArguments(YtDownloadOptions o, string url, string outputDirectory)
    {
        var args = new List<string> { "--newline", "--no-colors", "--progress", "-P", outputDirectory, "-o", "%(title)s [%(id)s].%(ext)s" };
        if (o.DownloadType == YtDownloadType.Audio)
        {
            args.AddRange(["-x", "--audio-format", o.AudioFormat.Value()]);
        }
        else
        {
            args.Add("-f");
            args.Add(o.Quality.MaxHeight() is { } h ? $"bestvideo[height<={h}]+bestaudio/best" : "bestvideo+bestaudio/best");
        }
        if (o.EmbedSubs) args.AddRange(["--embed-subs", "--write-subs", "--sub-langs", "all"]);
        if (o.EmbedThumbnail) args.Add("--embed-thumbnail");
        if (o.EmbedMetadata) args.Add("--embed-metadata");
        if (o.EmbedChapters) args.Add("--embed-chapters");
        args.Add(o.IsPlaylist ? "--yes-playlist" : "--no-playlist");
        args.Add(url);
        return args;
    }

    /// <summary>Tracks the output file and turns yt-dlp lines into progress updates.</summary>
    public sealed class ProgressParser(string outputDirectory)
    {
        private static readonly string[] Markers = ["[download] Destination:", "[Merger] Merging formats into", "[ExtractAudio] Destination:"];

        public string OutputFile { get; private set; } = Path.Combine(outputDirectory, "downloaded_media");

        public YtDownloadProgress? Parse(string line)
        {
            var t = line.Trim();
            if (t.Length == 0) return null;
            foreach (var marker in Markers)
            {
                var idx = t.IndexOf(marker, StringComparison.Ordinal);
                if (idx < 0) continue;
                var candidate = t[(idx + marker.Length)..].Trim();
                if (candidate.Length > 0) OutputFile = StripQuotes(candidate);
            }

            if (t.StartsWith("[download]", StringComparison.Ordinal))
            {
                var pm = Percent().Match(t);
                if (!pm.Success || !double.TryParse(pm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var percent)) return null;
                var bounded = Math.Clamp(percent, 0, 100);
                return new YtDownloadProgress(
                    bounded,
                    TotalSize().Match(t) is { Success: true } s ? s.Groups[1].Value : "--",
                    Speed().Match(t) is { Success: true } sp ? sp.Groups[1].Value : "--",
                    Eta().Match(t) is { Success: true } e ? e.Groups[1].Value : "--",
                    $"Downloading ({bounded.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}%)",
                    OutputFile);
            }
            if (t.StartsWith("[ffmpeg]", StringComparison.Ordinal) || t.StartsWith("[Merger]", StringComparison.Ordinal))
            {
                return new YtDownloadProgress(99, "--", "--", "processing", "Processing media with ffmpeg…", OutputFile);
            }
            return null;
        }
    }

    public async Task<string> GetVersionAsync()
    {
        var result = await ProcessRunner.RunAsync(executable, ["--version"],
            new ProcessOptions { Timeout = TimeSpan.FromSeconds(15) }).ConfigureAwait(false);
        if (!result.Ok) throw new YtDlpException("Could not read yt-dlp version.");
        return result.Stdout.Trim();
    }

    public async Task<string> UpdateAsync(Action<string>? onOutput = null)
    {
        var result = await ProcessRunner.RunAsync(executable, ["-U"],
            new ProcessOptions { Timeout = TimeSpan.FromMinutes(3) }).ConfigureAwait(false);
        var output = (result.Stdout + "\n" + result.Stderr).Trim();
        if (!result.Ok) throw new YtDlpException(output.Length == 0 ? "yt-dlp update failed." : output);
        return output;
    }

    /// <summary>Picks the most useful yt-dlp log lines for an error message.</summary>
    private static string ExtractFailureDetail(List<string> log)
    {
        var errors = log
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.Length > 300 ? l[..300] : l)
            .TakeLast(3)
            .ToList();
        if (errors.Count > 0) return string.Join("\n", errors);
        var fallback = log
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("[download]", StringComparison.Ordinal))
            .TakeLast(3)
            .ToList();
        return fallback.Count > 0 ? string.Join("\n", fallback) : "";
    }

    private static string? UpdateHint(string detail) =>
        detail.Contains("403", StringComparison.Ordinal) ||
        detail.Contains("Forbidden", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("SABR", StringComparison.OrdinalIgnoreCase)
            ? "YouTube blocked the download. Update yt-dlp (`yt-dlp -U`) and retry."
            : detail.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) ||
              detail.Contains("ffprobe", StringComparison.OrdinalIgnoreCase)
                ? "Audio conversion needs ffmpeg on PATH (`winget install yt-dlp.FFmpeg`)."
                : detail.Contains("older than", StringComparison.OrdinalIgnoreCase)
                    ? "Your yt-dlp version is old. Update it (`yt-dlp -U`) and retry."
                    : null;

    public async Task<string> DownloadAsync(YtDownloadOptions options, Action<YtDownloadProgress>? onProgress)
    {
        var url = options.Url.Trim();
        if (url.Length == 0) throw new YtDlpException("Please enter a valid video URL.");
        if (_active != null) throw new YtDlpException("A download is already running.");

        var outputDir = options.OutputDir.Trim().Length == 0 ? DefaultDownloadDir : options.OutputDir.Trim();
        Directory.CreateDirectory(outputDir);
        var parser = new ProgressParser(outputDir);
        _cancelRequested = false;

        void Emit(YtDownloadProgress p)
        {
            if (!_cancelRequested) onProgress?.Invoke(p);
        }

        var log = new List<string>();
        void Handle(string line)
        {
            lock (log)
            {
                log.Add(line);
                if (log.Count > 200) log.RemoveAt(0);
            }
            if (parser.Parse(line) is { } p) Emit(p);
        }

        Emit(new YtDownloadProgress(0, "Starting…", "--", "--", "Initializing yt-dlp engine…", parser.OutputFile));
        try
        {
            var managed = ProcessRunner.StartStreaming(executable, BuildDownloadArguments(options, url, outputDir), Handle, Handle);
            _active = managed;
            var code = await managed.WaitAsync().ConfigureAwait(false);
            managed.Dispose();
            if (_cancelRequested) throw new YtDlpCancelledException();
            if (code != 0)
            {
                List<string> snapshot;
                lock (log) snapshot = [.. log];
                var detail = ExtractFailureDetail(snapshot);
                var hint = detail.Length > 0 ? UpdateHint(detail) : null;
                var message = detail.Length > 0
                    ? $"Download failed (exit {code}): {detail}"
                    : $"yt-dlp exited with code {code}. Check the URL or parameters.";
                if (hint != null) message += $"\n{hint}";
                throw new YtDlpException(message);
            }
            Emit(new YtDownloadProgress(100, "Done", "Done", "00:00", "Download completed successfully!", parser.OutputFile));
            return parser.OutputFile;
        }
        catch (YtDlpException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw new YtDlpException($"Failed to start yt-dlp: {e.Message}");
        }
        finally
        {
            _active = null;
            _cancelRequested = false;
        }
    }

    public void StopCurrentDownload()
    {
        _cancelRequested = true;
        _active?.Kill();
    }

    public async Task<List<YtHistoryItem>> LoadHistoryAsync() =>
        await JsonStore.LoadAsync<List<YtHistoryItem>>(HistoryFilePath).ConfigureAwait(false) ?? [];

    public async Task SaveHistoryAsync(YtHistoryItem item)
    {
        var current = await LoadHistoryAsync().ConfigureAwait(false);
        var updated = new[] { item }.Concat(current.Where(e => e.Id != item.Id)).Take(50).ToList();
        await JsonStore.SaveAsync(HistoryFilePath, updated).ConfigureAwait(false);
    }

    public Task ClearHistoryAsync() => JsonStore.SaveAsync(HistoryFilePath, new List<YtHistoryItem>());

    private static string StripQuotes(string v) =>
        v.Length >= 2 && ((v[0] == '"' && v[^1] == '"') || (v[0] == '\'' && v[^1] == '\'')) ? v[1..^1] : v;

    [GeneratedRegex(@"(\d+(?:\.\d+)?)%")]
    private static partial Regex Percent();

    [GeneratedRegex(@"\bof\s+([~\d.]+\s*[KMGTP]?i?B)")]
    private static partial Regex TotalSize();

    [GeneratedRegex(@"\bat\s+([\d.]+\s*[KMGTP]?i?B/s)")]
    private static partial Regex Speed();

    [GeneratedRegex(@"\bETA\s+([\d:]+)")]
    private static partial Regex Eta();
}
