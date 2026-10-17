using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.Video;

public sealed record Chapter(string Name, long StartMs, long EndMs);

public enum ChapterSource { Tag, Markers, None }

public enum VideoEncoder { Nvenc, X265 }

/// <summary>The video-stream properties a faithful re-encode should keep.</summary>
public sealed record SourceVideo(
    string Codec, long Width, long Height, string FrameRate, string PixFmt, long? Bitrate,
    string? ColorPrimaries, string? ColorTransfer, string? ColorSpace, string? ColorRange)
{
    public int BitDepth => PixFmt.Contains("12") ? 12 : PixFmt.Contains("10") ? 10 : 8;

    public double Fps
    {
        get
        {
            var parts = FrameRate.Split('/');
            var n = double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a) ? a : 0;
            var d = parts.Length > 1 && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) ? b : 1;
            return d > 0 ? n / d : 0;
        }
    }

    public string Summary =>
        string.Format(CultureInfo.InvariantCulture, "{0} {1}×{2} {3:0.00} fps {4}-bit{5} {6}",
            Codec, Width, Height, Fps, BitDepth,
            Bitrate is { } b ? string.Format(CultureInfo.InvariantCulture, " {0:0.0} Mb/s", b / 1e6) : "",
            ColorPrimaries ?? "untagged colours");
}

public sealed record VideoInfo(
    string Path, double DurationS, long SizeBytes, IReadOnlyList<Chapter> Chapters,
    ChapterSource ChapterSource, int MarkerCount, SourceVideo? Video);

public sealed record EncodeOptions(VideoEncoder Encoder, int Quality, double GopS, SourceVideo? MatchSource);

public sealed record LocalVideo(string Path, string Name, long SizeBytes);

/// <summary>
/// Video chapter metadata: read with ffprobe, write with ffmpeg (stream copy),
/// or re-encode to HEVC with a keyframe at every chapter boundary. Shares the
/// <c>chapters</c> tag format with DesktopCC and the Training Hub player.
/// </summary>
public static class VideoChapters
{
    /// <summary>Title of the filler markers covering gaps between chapters.</summary>
    public const string GapTitle = "–";
    public const string ChaptersTag = "chapters";

    public const string Template = """
        [
          {"name": "1. Stationary Fast Feet", "start": 10200, "end": 25200},
          {"name": "2. Forwards - Backwards", "start": 28400, "end": 45000}
        ]
        """;

    private static readonly string[] VideoExtensions = ["mp4", "m4v", "mov", "mkv", "webm", "avi"];

    /// <summary>A list or {"chapters": [...]}, start/end in ms; sorted, no overlaps.</summary>
    public static List<Chapter> Parse(string text)
    {
        JsonNode? value;
        try { value = JsonNode.Parse(text); }
        catch (JsonException e) { throw new FormatException($"Invalid JSON: {e.Message}"); }
        var list = value switch
        {
            JsonArray arr => arr,
            JsonObject obj => obj["chapters"] as JsonArray ?? throw new FormatException("Expected a list or {\"chapters\": [...]}"),
            _ => throw new FormatException("Expected a list of chapters"),
        };
        var chapters = new List<Chapter>();
        for (var i = 0; i < list.Count; i++)
        {
            var number = i + 1;
            var item = list[i] as JsonObject;
            long Ms(string key)
            {
                if (item?[key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) && d >= 0) return (long)Math.Round(d);
                throw new FormatException($"Chapter {number}: \"{key}\" must be milliseconds");
            }
            var (start, end) = (Ms("start"), Ms("end"));
            if (end <= start) throw new FormatException($"Chapter {number}: end must be after start");
            var name = item?["name"] is JsonValue n && n.TryGetValue<string>(out var s) && s.Trim().Length > 0 ? s.Trim() : $"Chapter {number}";
            chapters.Add(new Chapter(name, start, end));
        }
        chapters.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        for (var i = 1; i < chapters.Count; i++)
        {
            if (chapters[i].StartMs < chapters[i - 1].EndMs) throw new FormatException($"\"{chapters[i].Name}\" overlaps \"{chapters[i - 1].Name}\"");
        }
        return chapters;
    }

    private static string One(Chapter c) =>
        new JsonObject { ["name"] = c.Name, ["start"] = c.StartMs, ["end"] = c.EndMs }.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>Compact JSON as written to the tag.</summary>
    public static string ToJson(IEnumerable<Chapter> chapters) => "[" + string.Join(",", chapters.Select(One)) + "]";

    /// <summary>One chapter per line, for the editor.</summary>
    public static string ToPrettyJson(IEnumerable<Chapter> chapters) => "[\n" + string.Join(",\n", chapters.Select(c => "  " + One(c))) + "\n]";

    // ---- probing ----------------------------------------------------------

    public static async Task<VideoInfo> ProbeAsync(string path)
    {
        var result = await ProcessRunner.RunAsync("ffprobe",
            ["-v", "error", "-show_format", "-show_streams", "-show_chapters", "-of", "json", path]).ConfigureAwait(false);
        if (!result.Ok) throw new InvalidOperationException(result.CombinedOutput);
        return ParseProbe(path, result.Stdout);
    }

    private static string? Text(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 && s is not ("unknown" or "N/A") ? s : null;

    private static long? Number(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && long.TryParse(s, out var l) ? l : null;

    private static double? Seconds(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    internal static VideoInfo ParseProbe(string path, string json)
    {
        var data = JsonNode.Parse(json) as JsonObject ?? new JsonObject();
        var format = data["format"] as JsonObject;
        List<Chapter>? tagChapters = null;
        if (Text(format?["tags"]?[ChaptersTag]) is { } tag)
        {
            try { tagChapters = Parse(tag); } catch (FormatException) { }
        }
        var markers = (data["chapters"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        List<Chapter> chapters;
        ChapterSource source;
        if (tagChapters != null)
        {
            (chapters, source) = (tagChapters, ChapterSource.Tag);
        }
        else
        {
            chapters = [];
            for (var i = 0; i < markers.Count; i++)
            {
                var title = Text(markers[i]["tags"]?["title"]) ?? $"Chapter {i + 1}";
                if (title == GapTitle || Seconds(markers[i]["start_time"]) is not { } s || Seconds(markers[i]["end_time"]) is not { } e) continue;
                chapters.Add(new Chapter(title, (long)Math.Round(s * 1000), (long)Math.Round(e * 1000)));
            }
            source = chapters.Count == 0 ? ChapterSource.None : ChapterSource.Markers;
        }
        return new VideoInfo(path, Seconds(format?["duration"]) ?? 0, Number(format?["size"]) ?? 0,
            chapters, source, markers.Count, ParseSourceVideo(data));
    }

    private static SourceVideo? ParseSourceVideo(JsonObject data)
    {
        if (data["streams"] is not JsonArray streams) return null;
        var all = streams.OfType<JsonObject>().ToList();
        if (all.FirstOrDefault(s => Text(s["codec_type"]) == "video") is not { } video) return null;
        var bitrate = Number(video["bit_rate"]);
        if (bitrate == null && Number(data["format"]?["bit_rate"]) is { } total)
        {
            var audio = all.Where(s => Text(s["codec_type"]) == "audio").Sum(s => Number(s["bit_rate"]) ?? 0);
            bitrate = total >= audio ? total - audio : null;
        }
        long Int(string key) => video[key] is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;
        return new SourceVideo(Text(video["codec_name"]) ?? "unknown", Int("width"), Int("height"),
            Text(video["r_frame_rate"]) ?? "0/1", Text(video["pix_fmt"]) ?? "yuv420p", bitrate,
            Text(video["color_primaries"]), Text(video["color_transfer"]), Text(video["color_space"]), Text(video["color_range"]));
    }

    /// <summary>How a re-encode differs in the properties that matter (bitrate beyond ±25 %).</summary>
    public static List<string> Compare(SourceVideo source, SourceVideo output)
    {
        var diffs = new List<string>();
        if ((source.Width, source.Height) != (output.Width, output.Height))
            diffs.Add($"size {source.Width}×{source.Height} → {output.Width}×{output.Height}");
        if (Math.Abs(source.Fps - output.Fps) > 0.01)
            diffs.Add(string.Format(CultureInfo.InvariantCulture, "frame rate {0:0.000} → {1:0.000}", source.Fps, output.Fps));
        if (Math.Min(source.BitDepth, 10) != output.BitDepth) diffs.Add($"bit depth {source.BitDepth} → {output.BitDepth}");
        foreach (var (name, a, b) in new[]
        {
            ("primaries", source.ColorPrimaries, output.ColorPrimaries),
            ("transfer", source.ColorTransfer, output.ColorTransfer),
            ("matrix", source.ColorSpace, output.ColorSpace),
            ("range", source.ColorRange, output.ColorRange),
        })
        {
            if (a != null && a != b) diffs.Add($"{name} {a} → {b ?? "-"}");
        }
        if (source.Bitrate is { } sa && output.Bitrate is { } sb)
        {
            var ratio = (double)sb / sa;
            if (ratio is < 0.75 or > 1.25) diffs.Add(string.Format(CultureInfo.InvariantCulture, "bitrate {0:0.0} → {1:0.0} Mb/s", sa / 1e6, sb / 1e6));
        }
        return diffs;
    }

    // ---- ffmpeg -----------------------------------------------------------

    /// <summary>FFMETADATA: every chapter plus "–" markers for the gaps.</summary>
    public static string FfMetadata(IEnumerable<Chapter> chapters)
    {
        var sb = new StringBuilder(";FFMETADATA1\n");
        void Marker(long start, long end, string title) =>
            sb.Append($"[CHAPTER]\nTIMEBASE=1/1000\nSTART={start}\nEND={end}\ntitle={Escape(title)}\n");
        long position = 0;
        foreach (var c in chapters)
        {
            if (c.StartMs > position) Marker(position, c.StartMs, GapTitle);
            Marker(c.StartMs, c.EndMs, c.Name);
            position = Math.Max(position, c.EndMs);
        }
        return sb.ToString();
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (c is '=' or ';' or '#' or '\\' or '\n') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Chapter starts and ends, plus one every <paramref name="gopS"/> seconds not crowding one.</summary>
    public static List<double> KeyframeTimes(IEnumerable<Chapter> chapters, double durationS, double gopS)
    {
        var boundaries = chapters.SelectMany(c => new[] { c.StartMs / 1000.0, c.EndMs / 1000.0 }).Distinct().OrderBy(t => t).ToList();
        var times = new List<double>(boundaries);
        if (gopS > 0)
        {
            for (var t = 0.0; t < durationS; t += gopS)
            {
                if (boundaries.All(b => Math.Abs(t - b) >= gopS / 2)) times.Add(t);
            }
        }
        var result = new List<double>();
        foreach (var t in times.Where(t => t >= 0 && t < durationS).OrderBy(t => t))
        {
            if (result.Count == 0 || Math.Abs(result[^1] - t) >= 0.0005) result.Add(t);
        }
        return result;
    }

    private static List<string> BaseArgs(string source, IReadOnlyList<Chapter> chapters, bool stripOtherTags, string ffmetadataFile) =>
    [
        "-hide_banner", "-nostats", "-loglevel", "error",
        "-progress", "pipe:1", // key=value progress lines on stdout
        "-y", "-i", source, "-i", ffmetadataFile,
        "-map", "0:v:0", "-map", "0:a?",
        "-map_metadata", stripOtherTags ? "-1" : "0",
        "-map_chapters", chapters.Count == 0 ? "-1" : "1",
        // An empty value removes the tag.
        "-metadata", $"{ChaptersTag}={(chapters.Count == 0 ? "" : ToJson(chapters))}",
    ];

    private static void Finish(List<string> args, string output) =>
        // use_metadata_tags keeps the custom chapters tag in MP4.
        args.AddRange(["-movflags", "+faststart+use_metadata_tags", output]);

    /// <summary>Rewrite only the metadata (stream copy).</summary>
    public static List<string> WriteMetadataArgs(string source, string output, IReadOnlyList<Chapter> chapters, bool stripOtherTags, string ffmetadataFile)
    {
        var args = BaseArgs(source, chapters, stripOtherTags, ffmetadataFile);
        args.AddRange(["-c", "copy"]);
        Finish(args, output);
        return args;
    }

    /// <summary>HEVC re-encode with forced keyframes.</summary>
    public static List<string> EncodeArgs(string source, string output, IReadOnlyList<Chapter> chapters, bool stripOtherTags,
        string ffmetadataFile, IReadOnlyList<double> keyframes, EncodeOptions options)
    {
        var args = new List<string>();
        if (options.Encoder == VideoEncoder.Nvenc) args.AddRange(["-hwaccel", "cuda"]);
        args.AddRange(BaseArgs(source, chapters, stripOtherTags, ffmetadataFile));
        var matched = options.MatchSource;
        var tenBit = matched is { BitDepth: >= 10 };
        List<string> rate = matched?.Bitrate is { } bitrate
            ? ["-b:v", bitrate.ToString(CultureInfo.InvariantCulture), "-maxrate", (bitrate * 3 / 2).ToString(CultureInfo.InvariantCulture), "-bufsize", (bitrate * 2).ToString(CultureInfo.InvariantCulture)]
            : options.Encoder == VideoEncoder.Nvenc
                ? ["-cq", options.Quality.ToString(CultureInfo.InvariantCulture), "-b:v", "0"]
                : ["-crf", options.Quality.ToString(CultureInfo.InvariantCulture)];
        if (options.Encoder == VideoEncoder.Nvenc)
        {
            args.AddRange(["-c:v", "hevc_nvenc", "-preset", "p5", "-tune", "hq", "-rc", "vbr"]);
            args.AddRange(rate);
            args.AddRange(["-profile:v", tenBit ? "main10" : "main", "-forced-idr", "1", "-no-scenecut", "1", "-g", "600", "-pix_fmt", tenBit ? "p010le" : "yuv420p"]);
        }
        else
        {
            args.AddRange(["-c:v", "libx265", "-preset", "medium"]);
            args.AddRange(rate);
            args.AddRange(["-x265-params", "scenecut=0:open-gop=0:keyint=600:min-keyint=1", "-pix_fmt", tenBit ? "yuv420p10le" : "yuv420p"]);
        }
        if (matched != null)
        {
            // setparams reaches the bitstream reliably; -color_* flags are partly ignored by libx265.
            var parameters = new[]
            {
                ("color_primaries", matched.ColorPrimaries), ("color_trc", matched.ColorTransfer),
                ("colorspace", matched.ColorSpace), ("range", matched.ColorRange),
            }.Where(p => p.Item2 != null).Select(p => $"{p.Item1}={p.Item2}").ToList();
            if (parameters.Count > 0) args.AddRange(["-vf", "setparams=" + string.Join(':', parameters)]);
            args.AddRange(["-fps_mode", "passthrough"]);
        }
        args.AddRange(["-force_key_frames", string.Join(',', keyframes.Select(t => t.ToString("0.000", CultureInfo.InvariantCulture))),
            "-tag:v", "hvc1", "-c:a", "copy"]);
        Finish(args, output);
        return args;
    }

    /// <summary>Seconds encoded so far from an ffmpeg -progress line.</summary>
    public static double? ProgressSeconds(string line) =>
        line.StartsWith("out_time_us=") && double.TryParse(line[12..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var us) && us >= 0
            ? us / 1_000_000
            : null;

    public static string TempOutput(string path) =>
        System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path) ?? "", $".{System.IO.Path.GetFileName(path)}.metadata-tmp.mp4");

    public static string DefaultEncodeOutput(string path) => System.IO.Path.ChangeExtension(path, "keyframes.mp4");

    /// <summary>Checks the written file: same chapters in the tag and as markers.</summary>
    public static async Task<VideoInfo> VerifyAsync(string path, IReadOnlyList<Chapter> expected)
    {
        var info = await ProbeAsync(path).ConfigureAwait(false);
        if (expected.Count == 0)
        {
            if (info.ChapterSource != ChapterSource.None) throw new InvalidOperationException("Chapters are still present after removing them");
        }
        else if (info.ChapterSource != ChapterSource.Tag || !info.Chapters.SequenceEqual(expected))
        {
            throw new InvalidOperationException("The chapters tag in the output doesn't match");
        }
        else if (info.MarkerCount < expected.Count)
        {
            throw new InvalidOperationException($"Wrote {info.MarkerCount} chapter markers, expected at least {expected.Count}");
        }
        return info;
    }

    /// <summary>Video files under <paramref name="dir"/> (recursive, hidden skipped), by name.</summary>
    public static List<LocalVideo> ScanVideos(string dir)
    {
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"{dir} not found. Is the drive connected?");
        var videos = new List<LocalVideo>();
        var stack = new Stack<string>([dir]);
        while (stack.TryPop(out var folder))
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(folder).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var path in entries)
            {
                var name = System.IO.Path.GetFileName(path);
                if (name.StartsWith('.')) continue;
                if (Directory.Exists(path)) { stack.Push(path); continue; }
                var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                if (VideoExtensions.Contains(ext)) videos.Add(new LocalVideo(path, name, new FileInfo(path).Length));
            }
        }
        videos.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return videos;
    }

    /// <summary>A cached JPEG still a few seconds in, keyed by path, size and mtime.</summary>
    public static async Task<string?> ThumbnailAsync(string video)
    {
        var info = new FileInfo(video);
        if (!info.Exists) return null;
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{video}|{info.Length}|{info.LastWriteTimeUtc.Ticks}")))[..16].ToLowerInvariant();
        var dir = System.IO.Path.Combine(AppPaths.LocalAppData, "fluent_desktop", "video-thumbs");
        Directory.CreateDirectory(dir);
        var output = System.IO.Path.Combine(dir, key + ".jpg");
        if (File.Exists(output)) return output;
        // A few seconds in skips black lead-ins; short clips fall back to the first frame.
        foreach (var seek in new[] { "3", "0" })
        {
            try
            {
                var result = await ProcessRunner.RunAsync("ffmpeg",
                    ["-v", "error", "-y", "-ss", seek, "-i", video, "-frames:v", "1", "-vf", "scale=320:-2", "-q:v", "4", output],
                    new ProcessOptions { Timeout = TimeSpan.FromSeconds(30) }).ConfigureAwait(false);
                if (result.Ok && File.Exists(output)) return output;
            }
            catch (Exception e) when (e is TimeoutException or System.ComponentModel.Win32Exception) { }
        }
        return null;
    }

    public static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1000 && unit < units.Length - 1) { value /= 1000; unit++; }
        return string.Format(CultureInfo.InvariantCulture, "{0:0.0} {1}", value, units[unit]);
    }

    public static string Clock(long ms)
    {
        var total = ms / 1000;
        var (h, m, s, frac) = (total / 3600, total % 3600 / 60, total % 60, ms % 1000);
        return h > 0 ? $"{h}:{m:00}:{s:00}.{frac:000}" : $"{m}:{s:00}.{frac:000}";
    }
}
