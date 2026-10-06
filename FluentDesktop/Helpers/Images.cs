using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace FluentDesktop.Helpers;

/// <summary>
/// Image loading that works for both the folder build and the single-file
/// exe: bundled assets are embedded resources (no Assets\ folder needed next
/// to the exe), local files are read into memory.
/// </summary>
public static class Images
{
    private static readonly Dictionary<string, byte[]?> AssetCache = new(StringComparer.OrdinalIgnoreCase);

    // Manifest names come from %(RecursiveDir), which uses '\' on Windows.
    private static readonly Lazy<Dictionary<string, string>> ResourceNames = new(() =>
        typeof(Images).Assembly.GetManifestResourceNames()
            .GroupBy(n => n.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase));

    public static async Task<BitmapImage> FromBytesAsync(byte[] bytes, int decodeWidth = 0)
    {
        var image = new BitmapImage();
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        await image.SetSourceAsync(await ToStreamAsync(bytes));
        return image;
    }

    /// <summary>Raw bytes of an embedded asset ("Assets/x.svg"), or null when missing.</summary>
    public static byte[]? AssetBytes(string assetPath)
    {
        var key = assetPath.Replace('\\', '/').TrimStart('/');
        lock (AssetCache)
        {
            if (AssetCache.TryGetValue(key, out var cached)) return cached;
            using var stream = ResourceNames.Value.TryGetValue(key, out var name)
                ? typeof(Images).Assembly.GetManifestResourceStream(name)
                : null;
            byte[]? bytes = null;
            if (stream != null)
            {
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                bytes = buffer.ToArray();
            }
            AssetCache[key] = bytes;
            return bytes;
        }
    }

    /// <summary>SVG or bitmap from an embedded asset path ("Assets/x.svg").</summary>
    public static ImageSource? FromAsset(string? assetPath, int decodeWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(assetPath) || AssetBytes(assetPath) is not { } bytes) return null;
        if (assetPath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            var svg = new SvgImageSource();
            _ = SetSourceAsync(svg, bytes);
            return svg;
        }
        var bitmap = new BitmapImage();
        if (decodeWidth > 0) bitmap.DecodePixelWidth = decodeWidth;
        _ = SetSourceAsync(bitmap, bytes);
        return bitmap;
    }

    public static ImageSource? FromFile(string? path, int decodeWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        var image = new BitmapImage();
        if (decodeWidth > 0) image.DecodePixelWidth = decodeWidth;
        _ = LoadFileAsync(image, path);
        return image;
    }

    public static ImageSource? FromUrl(string? url, int decodeWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return new BitmapImage(uri) { DecodePixelWidth = decodeWidth };
    }

    /// <summary>
    /// Writes an embedded asset to %LOCALAPPDATA%\FluentDesktop\Assets for
    /// APIs that need a real file path (AppWindow.SetIcon).
    /// </summary>
    public static string? ExtractAsset(string assetPath)
    {
        if (AssetBytes(assetPath) is not { } bytes) return null;
        try
        {
            var target = Path.Combine(Core.Infrastructure.AppPaths.LocalAppData, "FluentDesktop", assetPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(target) || new FileInfo(target).Length != bytes.Length)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllBytes(target, bytes);
            }
            return target;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IRandomAccessStream> ToStreamAsync(byte[] bytes)
    {
        var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(bytes.AsBuffer());
        stream.Seek(0);
        return stream;
    }

    private static async Task SetSourceAsync(SvgImageSource svg, byte[] bytes)
    {
        try { await svg.SetSourceAsync(await ToStreamAsync(bytes)); }
        catch { /* leave blank */ }
    }

    private static async Task SetSourceAsync(BitmapImage bitmap, byte[] bytes)
    {
        try { await bitmap.SetSourceAsync(await ToStreamAsync(bytes)); }
        catch { /* leave blank */ }
    }

    private static async Task LoadFileAsync(BitmapImage bitmap, string path)
    {
        try { await SetSourceAsync(bitmap, await File.ReadAllBytesAsync(path)); }
        catch { /* leave blank */ }
    }
}
