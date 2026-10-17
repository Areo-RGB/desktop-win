using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FluentDesktop.Core.YtDlp;

/// <summary>Local video library scanning and Cloudflare R2 (S3 API, SigV4) access.</summary>
public sealed partial class YtLibraryService(HttpClient? http = null)
{
    private static readonly HashSet<string> VideoExtensions = ["mp4", "mkv", "webm", "mov", "m4v", "avi"];
    private static readonly string[] ImageExtensions = ["jpg", "jpeg", "png", "webp"];

    private readonly HttpClient _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

    public Task<YtLocalLibrary> ScanLocalFilesAsync(string rootPath) => Task.Run(() =>
    {
        var trimmed = rootPath.Trim();
        if (trimmed.Length == 0) throw new YtDlpException("Choose a library folder first.");
        if (!Directory.Exists(trimmed)) throw new YtDlpException("Library folder does not exist.");
        var root = Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var folders = new List<YtLocalFolder>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
            folders.Add(new YtLocalFolder(Path.GetFileName(dir), dir, ScanVideos(dir)));
        }
        var unsorted = ScanVideos(root);
        if (unsorted.Count > 0) folders.Add(new YtLocalFolder("Unsorted", root, unsorted));
        folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return new YtLocalLibrary(root, folders);
    });

    private static List<YtLocalVideo> ScanVideos(string directory)
    {
        var videos = new List<YtLocalVideo>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!IsVideo(file)) continue;
                var info = new FileInfo(file);
                videos.Add(new YtLocalVideo(info.Name, file, info.Length, info.LastWriteTime, FindThumbnail(file)));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // Skip unreadable folders.
        }
        videos.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return videos;
    }

    private static string? FindThumbnail(string videoPath)
    {
        var stem = Path.Combine(Path.GetDirectoryName(videoPath)!, Path.GetFileNameWithoutExtension(videoPath));
        return ImageExtensions.Select(ext => $"{stem}.{ext}").FirstOrDefault(File.Exists);
    }

    /// <summary>Deletes a file only when it is strictly inside the library root.</summary>
    public static void DeleteLocalFile(string rootPath, string filePath)
    {
        if (!IsInside(rootPath, filePath) || !File.Exists(filePath))
        {
            throw new YtDlpException("Refusing to delete a file outside the library root.");
        }
        File.Delete(filePath);
    }

    public static void DeleteLocalFolder(string rootPath, string folderPath)
    {
        if (!IsInside(rootPath, folderPath) || !Directory.Exists(folderPath))
        {
            throw new YtDlpException("Refusing to delete a folder outside the library root.");
        }
        Directory.Delete(folderPath, recursive: true);
    }

    internal static bool IsInside(string rootPath, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(candidate);
        return full.StartsWith(root, comparison) && full.Length > root.Length;
    }

    public async Task<YtCloudLibrary> ListR2VideosAsync(YtR2Config config)
    {
        if (!config.CanList) throw new YtDlpException("Enter Cloudflare R2 credentials first.");
        var objects = new List<(string Key, long Size, DateTimeOffset? Modified)>();
        string? token = null;
        do
        {
            var query = new Dictionary<string, string> { ["list-type"] = "2" };
            if (token != null) query["continuation-token"] = token;
            using var response = await SendSignedAsync(config, HttpMethod.Get, query: query).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new YtDlpException($"R2 list failed ({(int)response.StatusCode}): {body}");
            objects.AddRange(ParseObjects(body));
            token = FirstXmlValue(body, "NextContinuationToken");
        }
        while (!string.IsNullOrEmpty(token));

        var folders = objects
            .Where(o => IsVideo(o.Key))
            .GroupBy(o =>
            {
                var slash = o.Key.LastIndexOf('/');
                return slash <= 0 ? "Unsorted" : o.Key[..slash];
            })
            .Select(g => new YtCloudFolder(g.Key, g.Key, g
                .Select(o => new YtCloudVideo(o.Key[(o.Key.LastIndexOf('/') + 1)..], o.Key, o.Size, o.Modified, PublicUrl(config, o.Key)))
                .OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new YtCloudLibrary(config.Bucket, folders);
    }

    /// <summary>Every object under <paramref name="prefix"/>, following continuation tokens.</summary>
    public async Task<List<(string Key, long Size, DateTimeOffset? Modified)>> ListR2ObjectsAsync(YtR2Config config, string prefix)
    {
        if (!config.CanList) throw new YtDlpException("Enter Cloudflare R2 credentials first.");
        var objects = new List<(string Key, long Size, DateTimeOffset? Modified)>();
        string? token = null;
        do
        {
            var query = new Dictionary<string, string> { ["list-type"] = "2", ["prefix"] = prefix };
            if (token != null) query["continuation-token"] = token;
            using var response = await SendSignedAsync(config, HttpMethod.Get, query: query).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new YtDlpException($"R2 listing failed ({(int)response.StatusCode}): {body}");
            objects.AddRange(ParseObjects(body));
            token = FirstXmlValue(body, "NextContinuationToken");
        }
        while (!string.IsNullOrEmpty(token));
        return objects;
    }

    public async Task DeleteR2ObjectAsync(YtR2Config config, string key)
    {
        using var response = await SendSignedAsync(config, HttpMethod.Delete, key: key).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
        {
            throw new YtDlpException($"R2 delete failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
        }
    }

    public async Task<string> UploadLocalVideoToR2Async(YtR2Config config, YtLocalVideo video, string folderName, IProgress<double>? progress = null)
    {
        if (!config.CanList) throw new YtDlpException("Enter Cloudflare R2 credentials first.");
        var key = $"{(folderName.Trim().Length == 0 ? "Unsorted" : folderName)}/{video.Name}";
        progress?.Report(0);
        await using var stream = File.OpenRead(video.Path);
        var content = new StreamContent(stream);
        content.Headers.ContentType = new MediaTypeHeaderValue(ContentType(video.Path));
        content.Headers.ContentLength = stream.Length;
        using var response = await SendSignedAsync(config, HttpMethod.Put, key: key, content: content).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new YtDlpException($"R2 upload failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync().ConfigureAwait(false)}");
        }
        progress?.Report(100);
        return PublicUrl(config, key);
    }

    private async Task<HttpResponseMessage> SendSignedAsync(
        YtR2Config config, HttpMethod method, string key = "", IReadOnlyDictionary<string, string>? query = null, HttpContent? content = null)
    {
        var request = BuildSignedRequest(config, method, key, query, content?.Headers.ContentType?.ToString(), DateTimeOffset.UtcNow);
        request.Content = content;
        return await _http.SendAsync(request).ConfigureAwait(false);
    }

    /// <summary>Builds an AWS SigV4 (region "auto", UNSIGNED-PAYLOAD) request for R2.</summary>
    internal static HttpRequestMessage BuildSignedRequest(
        YtR2Config config, HttpMethod method, string key, IReadOnlyDictionary<string, string>? query, string? contentType, DateTimeOffset now)
    {
        var endpoint = new Uri(config.Endpoint.TrimEnd('/'));
        var encodedKey = EncodedKey(key);
        var canonicalUri = $"/{config.Bucket}{(encodedKey.Length == 0 ? "" : "/" + encodedKey)}";
        var canonicalQuery = CanonicalQuery(query ?? new Dictionary<string, string>());
        var uri = new UriBuilder(endpoint)
        {
            Path = endpoint.AbsolutePath.TrimEnd('/') + canonicalUri,
            Query = canonicalQuery,
        }.Uri;

        var date = now.ToString("yyyyMMdd");
        var timestamp = now.ToString("yyyyMMdd'T'HHmmss'Z'");
        const string payloadHash = "UNSIGNED-PAYLOAD";
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = endpoint.IsDefaultPort ? endpoint.Host : $"{endpoint.Host}:{endpoint.Port}",
            ["x-amz-content-sha256"] = payloadHash,
            ["x-amz-date"] = timestamp,
        };
        if (contentType != null) headers["content-type"] = contentType;

        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalHeaders = string.Concat(headers.Select(h => $"{h.Key}:{h.Value}\n"));
        var canonicalRequest = string.Join('\n', method.Method, canonicalUri, canonicalQuery, canonicalHeaders, signedHeaders, payloadHash);
        var scope = $"{date}/auto/s3/aws4_request";
        var stringToSign = string.Join('\n', "AWS4-HMAC-SHA256", timestamp, scope, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var signingKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + config.SecretAccessKey), date);
        signingKey = Hmac(signingKey, "auto");
        signingKey = Hmac(signingKey, "s3");
        signingKey = Hmac(signingKey, "aws4_request");
        var signature = Hex(Hmac(signingKey, stringToSign));

        var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", timestamp);
        request.Headers.TryAddWithoutValidation("Authorization",
            $"AWS4-HMAC-SHA256 Credential={config.AccessKeyId}/{scope}, SignedHeaders={signedHeaders}, Signature={signature}");
        return request;
    }

    public static string PublicUrl(YtR2Config config, string key)
    {
        var baseUrl = config.PublicBaseUrl.Trim().TrimEnd('/');
        return baseUrl.Length == 0
            ? $"{config.Endpoint.TrimEnd('/')}/{config.Bucket}/{EncodedKey(key)}"
            : $"{baseUrl}/{EncodedKey(key)}";
    }

    internal static string EncodedKey(string key) =>
        string.Join('/', key.Split('/').Where(p => p.Length > 0 && p != "." && p != "..").Select(Uri.EscapeDataString));

    internal static string CanonicalQuery(IReadOnlyDictionary<string, string> query) =>
        string.Join('&', query.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => $"{Uri.EscapeDataString(e.Key)}={Uri.EscapeDataString(e.Value)}"));

    internal static List<(string Key, long Size, DateTimeOffset? Modified)> ParseObjects(string xml)
    {
        var list = new List<(string, long, DateTimeOffset?)>();
        foreach (Match m in ContentsBlock().Matches(xml))
        {
            var body = m.Groups[1].Value;
            if (FirstXmlValue(body, "Key") is not { } rawKey) continue;
            var size = long.TryParse(FirstXmlValue(body, "Size"), out var s) ? s : 0;
            DateTimeOffset? modified = DateTimeOffset.TryParse(FirstXmlValue(body, "LastModified"), out var d) ? d : null;
            list.Add((WebUtility.HtmlDecode(rawKey), size, modified));
        }
        return list;
    }

    internal static string? FirstXmlValue(string xml, string tag)
    {
        var m = Regex.Match(xml, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static bool IsVideo(string path) => VideoExtensions.Contains(Extension(path));

    private static string Extension(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    private static string ContentType(string path) => Extension(path) switch
    {
        "mkv" => "video/x-matroska",
        "mov" => "video/quicktime",
        "webm" => "video/webm",
        "m4v" => "video/x-m4v",
        "avi" => "video/x-msvideo",
        _ => "video/mp4",
    };

    private static byte[] Hmac(byte[] key, string value) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [GeneratedRegex("<Contents>(.*?)</Contents>", RegexOptions.Singleline)]
    private static partial Regex ContentsBlock();
}
