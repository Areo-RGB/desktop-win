using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentDesktop.Core.GitHub;
using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Helpers;
using Microsoft.UI.Xaml.Media;

namespace FluentDesktop.ViewModels;

public sealed partial class GithubRepoItem(GithubRepo repo) : ObservableObject
{
    public GithubRepo Repo { get; } = repo;
    public string NameWithOwner => Repo.NameWithOwner;
    public string Description => Repo.Description;
    public bool HasDescription => Repo.Description.Length > 0;
    public string? Language => Repo.PrimaryLanguage;
    public bool HasLanguage => Repo.PrimaryLanguage != null;
    public string Updated => $"Updated: {Format.Relative(Repo.UpdatedAt)}";
    public string Pushed => $"Pushed: {Format.Relative(Repo.PushedAt)}";
    public string Stars => Repo.StargazerCount.ToString();
    public bool HasStars => Repo.StargazerCount > 0;
    public string Badge => Repo.IsArchived ? "ARCHIVED" : Repo.IsPrivate ? "PRIVATE" : "PUBLIC";
    public Brush BadgeBrush => Repo.IsArchived ? Bind.Neutral : Repo.IsPrivate ? Bind.Warning : Bind.Success;

    [ObservableProperty]
    private bool _isSelected;
}

public sealed record PageButton(int Index, bool IsCurrent)
{
    public bool IsEllipsis => Index < 0;
    public string Label => IsEllipsis ? "…" : (Index + 1).ToString();
}

public sealed partial class GithubViewModel : ViewModelBase
{
    public const int PageSize = 10;

    private readonly GithubCliService _cli = new();
    private readonly HashSet<string> _selected = [];
    private List<GithubRepo> _all = [];
    private IReadOnlyList<GithubRepo> _filtered = [];
    private bool _initialized;

    public ObservableCollection<GithubRepoItem> PagedRepos { get; } = [];
    public ObservableCollection<PageButton> PageButtons { get; } = [];

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _isAuthed;
    [ObservableProperty] private bool _isOperating;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _searchQuery = "";
    [ObservableProperty] private int _visibilityIndex;
    [ObservableProperty] private int _sortIndex;
    [ObservableProperty] private int _currentPage;
    [ObservableProperty] private int _totalPages;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _publicCount;
    [ObservableProperty] private int _privateCount;
    [ObservableProperty] private int _archivedCount;
    [ObservableProperty] private int _filteredCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool? _selectAllState = false;
    [ObservableProperty] private string _selectionLabel = "Select All Filtered (0)";
    [ObservableProperty] private string _pageSummary = "";
    [ObservableProperty] private string _pageLabel = "";
    [ObservableProperty] private string _emptyMessage = "No repositories cached in app data.";

    public IReadOnlyList<string> VisibilityOptions { get; } = Enum.GetValues<VisibilityFilter>().Select(v => v.Label()).ToList();
    public IReadOnlyList<string> SortOptions { get; } = Enum.GetValues<RepoSortOption>().Select(v => $"Sort: {v.Label()}").ToList();

    public IReadOnlyList<GithubRepo> AllRepos => _all;
    public IReadOnlyCollection<string> SelectedNames => _selected;

    public bool AuthWarningVisible => !IsAuthed;
    public bool CanSync => IsAuthed && !IsLoading;
    public bool CanGoBack => CurrentPage > 0;
    public bool CanGoForward => CurrentPage < TotalPages - 1;
    public bool HasSelection => SelectedCount > 0;
    public bool HasRepos => FilteredCount > 0;
    public bool ShowSyncNow => _all.Count == 0 && IsAuthed;

    partial void OnIsAuthedChanged(bool value)
    {
        OnPropertyChanged(nameof(AuthWarningVisible));
        OnPropertyChanged(nameof(CanSync));
        OnPropertyChanged(nameof(ShowSyncNow));
    }

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(CanSync));
    partial void OnSearchQueryChanged(string value) => Refilter(resetPage: true);
    partial void OnVisibilityIndexChanged(int value) => Refilter(resetPage: true);
    partial void OnSortIndexChanged(int value) => Refilter(resetPage: true);
    partial void OnSelectedCountChanged(int value) => OnPropertyChanged(nameof(HasSelection));
    partial void OnFilteredCountChanged(int value) => OnPropertyChanged(nameof(HasRepos));

    public async Task InitAsync()
    {
        if (_initialized) return;
        _initialized = true;
        IsLoading = true;
        SetRepos(await _cli.LoadLocalReposAsync());
        IsAuthed = await _cli.CheckAuthAsync();
        IsLoading = false;
    }

    public async Task ReloadLocalAsync()
    {
        IsLoading = true;
        StatusMessage = "Loading cached repositories...";
        try
        {
            SetRepos(await _cli.LoadLocalReposAsync());
            _selected.Clear();
            StatusMessage = $"Loaded {_all.Count} cached repositories.";
        }
        catch (Exception e)
        {
            StatusMessage = $"Error loading cached repos: {e.Message}";
        }
        finally
        {
            IsLoading = false;
            Refilter(resetPage: false);
        }
    }

    public async Task FetchRemoteAsync()
    {
        IsLoading = true;
        StatusMessage = "Fetching repositories from GitHub CLI...";
        try
        {
            var repos = await _cli.SyncRemoteReposAsync();
            SetRepos(repos);
            _selected.Clear();
            StatusMessage = $"Successfully updated {repos.Count} repositories.";
        }
        catch (Exception e)
        {
            StatusMessage = $"Error fetching repos: {e.Message}";
        }
        finally
        {
            IsLoading = false;
            Refilter(resetPage: false);
        }
    }

    private void SetRepos(List<GithubRepo> repos)
    {
        _all = repos;
        TotalCount = repos.Count;
        PublicCount = repos.Count(r => !r.IsPrivate);
        PrivateCount = repos.Count(r => r.IsPrivate);
        ArchivedCount = repos.Count(r => r.IsArchived);
        OnPropertyChanged(nameof(ShowSyncNow));
        Refilter(resetPage: false);
    }

    private void Refilter(bool resetPage)
    {
        _filtered = GithubRules.FilterAndSort(_all, SearchQuery, (VisibilityFilter)Math.Max(0, VisibilityIndex), (RepoSortOption)Math.Max(0, SortIndex));
        FilteredCount = _filtered.Count;
        EmptyMessage = _all.Count == 0 ? "No repositories cached in app data." : "No repositories match your current filter.";
        if (resetPage) CurrentPage = 0;
        RenderPage();
    }

    public void GoToPage(int page)
    {
        if (TotalPages <= 0) return;
        CurrentPage = Math.Clamp(page, 0, TotalPages - 1);
        RenderPage();
    }

    private void RenderPage()
    {
        TotalPages = (int)Math.Ceiling(_filtered.Count / (double)PageSize);
        if (TotalPages == 0) CurrentPage = 0;
        else if (CurrentPage >= TotalPages) CurrentPage = TotalPages - 1;

        PagedRepos.Clear();
        foreach (var repo in _filtered.Skip(CurrentPage * PageSize).Take(PageSize))
        {
            var item = new GithubRepoItem(repo) { IsSelected = _selected.Contains(repo.NameWithOwner) };
            PagedRepos.Add(item);
        }

        PageButtons.Clear();
        foreach (var p in GithubRules.VisiblePageNumbers(CurrentPage, TotalPages)) PageButtons.Add(new PageButton(p, p == CurrentPage));

        var start = _filtered.Count == 0 ? 0 : CurrentPage * PageSize + 1;
        var end = Math.Min(CurrentPage * PageSize + PageSize, _filtered.Count);
        PageSummary = $"Showing {start}–{end} of {_filtered.Count} • {PageSize} per page";
        PageLabel = $"Page {CurrentPage + 1} of {TotalPages}";
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
        UpdateSelectionState();
    }

    public void ToggleSelect(GithubRepoItem item)
    {
        if (!_selected.Remove(item.NameWithOwner)) _selected.Add(item.NameWithOwner);
        item.IsSelected = _selected.Contains(item.NameWithOwner);
        UpdateSelectionState();
    }

    public void SetSelectAll(bool select)
    {
        foreach (var repo in _filtered)
        {
            if (select) _selected.Add(repo.NameWithOwner);
            else _selected.Remove(repo.NameWithOwner);
        }
        foreach (var item in PagedRepos) item.IsSelected = _selected.Contains(item.NameWithOwner);
        UpdateSelectionState();
    }

    public void ClearSelection() => SetSelectAll(false);

    private void UpdateSelectionState()
    {
        SelectedCount = _selected.Count;
        var any = _filtered.Any(r => _selected.Contains(r.NameWithOwner));
        var all = _filtered.Count > 0 && _filtered.All(r => _selected.Contains(r.NameWithOwner));
        SelectAllState = all ? true : any ? null : false;
        SelectionLabel = _selected.Count == 0 ? $"Select All Filtered ({_filtered.Count})" : $"{_selected.Count} Selected";
    }

    public IAsyncEnumerable<BatchProgress> ExecuteBatch(GithubBatchAction action) => _cli.ExecuteBatchAsync(_selected.ToList(), action);

    private async Task<GithubOperationResult> OperateAsync(Func<Task<GithubOperationResult>> op, string errorPrefix, bool reloadCache)
    {
        IsOperating = true;
        try
        {
            var result = await op();
            if (reloadCache) SetRepos(await _cli.LoadLocalReposAsync());
            StatusMessage = result.Message;
            return result;
        }
        catch (Exception e)
        {
            StatusMessage = $"{errorPrefix}: {e.Message}";
            return GithubOperationResult.Failure(StatusMessage);
        }
        finally
        {
            IsOperating = false;
        }
    }

    public Task<GithubOperationResult> CreateRepoAsync(CreateRepoRequest request) =>
        OperateAsync(() => _cli.CreateRepoAsync(request), "Error creating repo", reloadCache: true);

    public Task<GithubOperationResult> InitLocalRepoAsync(string path) =>
        OperateAsync(() => _cli.InitLocalRepoAsync(path), "Error running `git init`", reloadCache: false);

    public Task<GithubOperationResult> ConnectAsync(ConnectLocalRequest request) =>
        OperateAsync(() => _cli.ConnectLocalToRemoteAsync(request), "Error connecting local repo", reloadCache: true);

    public Task<LocalRepoInfo> InspectLocalDirAsync(string path) => _cli.InspectLocalDirAsync(path);
}
