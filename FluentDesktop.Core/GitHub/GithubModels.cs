using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace FluentDesktop.Core.GitHub;

public sealed record GithubRepo
{
    public string Name { get; init; } = "";
    public string NameWithOwner { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsPrivate { get; init; }
    public bool IsArchived { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset PushedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? CreatedAt { get; init; }
    public string Visibility { get; init; } = "PUBLIC";
    public string Url { get; init; } = "";
    public int StargazerCount { get; init; }
    public int ForkCount { get; init; }

    /// <summary>`gh` returns {"name": "..."}; the cache stores a plain string.</summary>
    [JsonConverter(typeof(LanguageConverter))]
    public string? PrimaryLanguage { get; init; }

    private sealed class LanguageConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    var s = reader.GetString();
                    return string.IsNullOrEmpty(s) ? null : s;
                case JsonTokenType.StartObject:
                    using (var doc = JsonDocument.ParseValue(ref reader))
                    {
                        return doc.RootElement.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && n.GetString() is { Length: > 0 } name
                            ? name
                            : null;
                    }
                default:
                    reader.Skip();
                    return null;
            }
        }

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        {
            if (value == null) writer.WriteNullValue();
            else writer.WriteStringValue(value);
        }
    }
}

public sealed record CreateRepoRequest(
    string Name,
    string Description = "",
    bool IsPrivate = false,
    bool AddReadme = true,
    string? GitignoreTemplate = null,
    string? LicenseTemplate = null,
    bool CloneAfterCreate = false,
    string? CloneDirectory = null);

public sealed record ConnectLocalRequest(
    string LocalPath,
    string? ExistingRemoteName = null,
    string? NewRepoName = null,
    bool NewRepoPrivate = false,
    bool PushAfterConnect = true,
    string RemoteName = "origin",
    string? Branch = null);

public sealed record LocalRepoInfo(
    string Path,
    bool Exists,
    bool IsGitRepo,
    string? Branch = null,
    IReadOnlyDictionary<string, string>? Remotes = null,
    bool HasUncommittedChanges = false,
    string? TopLevel = null)
{
    public IReadOnlyDictionary<string, string> RemoteMap => Remotes ?? new Dictionary<string, string>();
}

public sealed record GithubOperationResult(bool Success, string Message, IReadOnlyList<string> Logs, string? RepoNameWithOwner = null)
{
    public static GithubOperationResult Failure(string message, IEnumerable<string>? logs = null) =>
        new(false, message, logs?.ToList() ?? []);

    public static GithubOperationResult Ok(string message, IEnumerable<string>? logs = null, string? repo = null) =>
        new(true, message, logs?.ToList() ?? [], repo);
}

public enum GithubBatchAction
{
    Delete,
    Archive,
    SetPrivate,
    SetPublic,
}

public static class GithubBatchActionExtensions
{
    public static string Label(this GithubBatchAction a) => a switch
    {
        GithubBatchAction.Delete => "Delete Permanently",
        GithubBatchAction.Archive => "Archive Repository",
        GithubBatchAction.SetPrivate => "Make Private",
        _ => "Make Public",
    };
}

public sealed record BatchItemResult(string NameWithOwner, bool Success, string Message);

public sealed record BatchProgress(int CurrentIndex, int TotalCount, string CurrentRepo, IReadOnlyList<BatchItemResult> Results, bool IsCompleted);

public enum RepoSortOption
{
    UpdatedDesc,
    UpdatedAsc,
    PushedDesc,
    PushedAsc,
    NameAsc,
    NameDesc,
    StarsDesc,
}

public enum VisibilityFilter
{
    All,
    PublicOnly,
    PrivateOnly,
    ArchivedOnly,
}

public static partial class GithubRules
{
    public static string Label(this RepoSortOption o) => o switch
    {
        RepoSortOption.UpdatedDesc => "Last Updated (Newest First)",
        RepoSortOption.UpdatedAsc => "Last Updated (Oldest First)",
        RepoSortOption.PushedDesc => "Last Pushed (Newest First)",
        RepoSortOption.PushedAsc => "Last Pushed (Oldest First)",
        RepoSortOption.NameAsc => "Name (A-Z)",
        RepoSortOption.NameDesc => "Name (Z-A)",
        _ => "Stars (High to Low)",
    };

    public static string Label(this VisibilityFilter f) => f switch
    {
        VisibilityFilter.All => "All Visibilities",
        VisibilityFilter.PublicOnly => "Public Only",
        VisibilityFilter.PrivateOnly => "Private Only",
        _ => "Archived Only",
    };

    /// <summary>Letters, digits, '.', '-', '_'; 1–100 chars; no leading/trailing '.' or "..".</summary>
    public static bool IsValidRepoName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 100) return false;
        if (name.Contains('/') || name.Contains(' ') || name.Contains("..")) return false;
        if (name.StartsWith('.') || name.EndsWith('.')) return false;
        return RepoNamePattern().IsMatch(name);
    }

    public static IReadOnlyList<GithubRepo> FilterAndSort(IEnumerable<GithubRepo> repos, string query, VisibilityFilter filter, RepoSortOption sort)
    {
        var q = query.Trim().ToLowerInvariant();
        var filtered = repos.Where(r => filter switch
        {
            VisibilityFilter.PublicOnly => !r.IsPrivate && !r.IsArchived,
            VisibilityFilter.PrivateOnly => r.IsPrivate && !r.IsArchived,
            VisibilityFilter.ArchivedOnly => r.IsArchived,
            _ => true,
        }).Where(r => q.Length == 0 ||
            r.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            r.NameWithOwner.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            r.Description.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            (r.PrimaryLanguage ?? "").Contains(q, StringComparison.OrdinalIgnoreCase));

        return (sort switch
        {
            RepoSortOption.UpdatedDesc => filtered.OrderByDescending(r => r.UpdatedAt),
            RepoSortOption.UpdatedAsc => filtered.OrderBy(r => r.UpdatedAt),
            RepoSortOption.PushedDesc => filtered.OrderByDescending(r => r.PushedAt),
            RepoSortOption.PushedAsc => filtered.OrderBy(r => r.PushedAt),
            RepoSortOption.NameAsc => filtered.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            RepoSortOption.NameDesc => filtered.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderByDescending(r => r.StargazerCount),
        }).ToList();
    }

    /// <summary>Page indices to show, with -1 as an ellipsis marker.</summary>
    public static List<int> VisiblePageNumbers(int currentPage, int totalPages)
    {
        if (totalPages <= 7) return Enumerable.Range(0, totalPages).ToList();
        var middle = new SortedSet<int> { currentPage - 1, currentPage, currentPage + 1 };
        middle.RemoveWhere(p => p <= 0 || p >= totalPages - 1);
        var result = new List<int>();
        var last = -1;
        foreach (var p in new[] { 0 }.Concat(middle).Append(totalPages - 1))
        {
            if (p - last > 1) result.Add(-1);
            result.Add(p);
            last = p;
        }
        return result;
    }

    public static string ShortRemote(string url)
    {
        var m = ShortRemotePattern().Match(url);
        return m.Success ? m.Groups[1].Value : url;
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex RepoNamePattern();

    [GeneratedRegex(@"github\.com[:/](.+?)(\.git)?$")]
    private static partial Regex ShortRemotePattern();
}
