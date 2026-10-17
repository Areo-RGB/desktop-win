using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Core.Projects;
using FluentDesktop.Core.Settings;
using FluentDesktop.Helpers;

namespace FluentDesktop.ViewModels;

public sealed partial class ProjectItem(ProjectDef def) : ObservableObject
{
    public ProjectDef Def { get; } = def;
    public string Name => Def.Name;
    public string Path => Def.Path;
    public string Kind => Def.Kind;
    public string CommandLine => Def.CommandLine;
    public bool CanStart => Def.CanStart;
    public bool HasUrl => Def.DevUrl.Length > 0;
    public string KindGlyph => Def.Kind switch
    {
        "flutter" => "",
        "web" => "",
        "android" => "",
        "extension" => "",
        _ => "",
    };

    public LogBuffer Logs { get; } = new(200);

    internal ManagedProcess? Process;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartEnabled))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isLogOpen;

    public bool StartEnabled => CanStart && !IsRunning;
}

public sealed partial class ProjectsViewModel : ViewModelBase, IDisposable
{
    private static readonly string[] Filters = ["all", "flutter", "web", "android", "other"];
    private readonly Dictionary<string, ProjectItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public ObservableCollection<ProjectItem> Visible { get; } = [];

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private int _filterIndex;

    [ObservableProperty]
    private string _header = "Projects";

    [ObservableProperty]
    private string _rootText = "";

    [ObservableProperty]
    private int _hiddenCount;

    partial void OnSearchChanged(string value) => Refilter();

    partial void OnFilterIndexChanged(int value) => Refilter();

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        var hidden = await JsonStore.LoadAsync<List<string>>(AppPaths.ConfigFile(ProjectScanner.HiddenFile)) ?? [];
        _hidden = new HashSet<string>(hidden, StringComparer.OrdinalIgnoreCase);
        await RescanAsync();
    }

    public async Task RescanAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        var root = FolderSettings.Load().Projects;
        RootText = $"Discovered under {root}";
        try
        {
            var found = await Task.Run(() => ProjectScanner.Discover(root));
            foreach (var def in found)
            {
                // Keep running items (and their logs) across rescans.
                if (!_items.TryGetValue(def.Path, out var item) || (!item.IsRunning && item.Def != def)) _items[def.Path] = new ProjectItem(def);
            }
            foreach (var stale in _items.Keys.Where(k => !found.Any(f => string.Equals(f.Path, k, StringComparison.OrdinalIgnoreCase)) && !_items[k].IsRunning).ToList())
            {
                _items.Remove(stale);
            }
            Refilter();
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void Refilter()
    {
        var q = Search.Trim().ToLowerInvariant();
        var filter = Filters[Math.Clamp(FilterIndex, 0, Filters.Length - 1)];
        var items = _items.Values
            .Where(p => !_hidden.Contains(p.Path))
            .Where(p => filter == "all" || p.Kind == filter || (filter == "other" && p.Kind is not ("flutter" or "web" or "android")))
            .Where(p => q.Length == 0 || p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || p.Path.Contains(q, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();
        Visible.Clear();
        foreach (var item in items) Visible.Add(item);
        Header = $"Projects · {items.Count}";
        HiddenCount = _hidden.Count;
    }

    public void Start(ProjectItem item)
    {
        if (!item.StartEnabled) return;
        item.Logs.Append($"$ {item.CommandLine}");
        AppLogs.Push("Projects", $"{item.Name}: {item.CommandLine}", LogLevel.Command);
        try
        {
            item.Process = ProcessRunner.StartStreaming(item.Def.StartExe, item.Def.StartArgs,
                line => OnUI(() => item.Logs.Append(line)),
                line => OnUI(() => item.Logs.Append("[ERR] " + line)),
                new ProcessOptions { WorkingDirectory = item.Path });
        }
        catch (Exception e)
        {
            item.Logs.Append("[ERR] " + e.Message);
            AppLogs.Push("Projects", $"{item.Name}: {e.Message}", LogLevel.Error);
            item.IsLogOpen = true;
            return;
        }
        item.IsRunning = true;
        var process = item.Process;
        _ = Task.Run(async () =>
        {
            var code = await process.WaitAsync();
            OnUI(() =>
            {
                if (!ReferenceEquals(item.Process, process)) return;
                item.Process = null;
                item.IsRunning = false;
                item.Logs.Append(code == 0 ? "[exit 0]" : $"[ERR] [exit {code}]");
                AppLogs.Push("Projects", $"{item.Name} exited with {code}", code == 0 ? LogLevel.Success : LogLevel.Error);
                process.Dispose();
            });
        });
    }

    public void Stop(ProjectItem item)
    {
        if (item.Process?.Kill() == true) item.Logs.Append("[killed]");
    }

    public async Task HideAsync(ProjectItem item)
    {
        _hidden.Add(item.Path);
        await SaveHiddenAsync();
        Refilter();
    }

    public async Task ShowAllAsync()
    {
        _hidden.Clear();
        await SaveHiddenAsync();
        Refilter();
    }

    private Task SaveHiddenAsync() => JsonStore.SaveAsync(AppPaths.ConfigFile(ProjectScanner.HiddenFile), _hidden.ToList());

    public void Dispose()
    {
        foreach (var item in _items.Values) item.Process?.Kill();
    }
}
