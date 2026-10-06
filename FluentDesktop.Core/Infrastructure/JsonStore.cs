using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FluentDesktop.Core.Infrastructure;

/// <summary>Tiny JSON-file persistence helper (camelCase, indented).</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static async Task<T?> LoadAsync<T>(string path)
    {
        try
        {
            if (!File.Exists(path)) return default;
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, Options).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"JsonStore: failed to load {path}: {e.Message}");
            return default;
        }
    }

    public static async Task SaveAsync<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await using (var stream = File.Create(tmp))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options).ConfigureAwait(false);
            }
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e)
        {
            Debug.WriteLine($"JsonStore: failed to save {path}: {e.Message}");
        }
    }
}
