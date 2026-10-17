using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentDesktop.Core.Mcp;
using FluentDesktop.Helpers;

namespace FluentDesktop.ViewModels;

public sealed partial class McpCell(McpRow row, McpApp app, bool enabled) : ObservableObject
{
    public McpRow Row { get; } = row;
    public McpApp App { get; } = app;
    public string ToolTip => $"{Row.Id} in {App.Label()} ({App.ConfigNote()})";

    [ObservableProperty]
    private bool _isEnabled = enabled;
}

public sealed class McpRow
{
    public McpRow(McpServer server)
    {
        Server = server;
        Cells = McpAppInfo.All.Select(a => new McpCell(this, a, server.Apps.Get(a))).ToList();
    }

    public McpServer Server { get; }
    public string Id => Server.Id;
    public string Summary => McpSpec.Summary(Server.Server);
    public string Transport => McpSpec.TransportOf(Server.Server).ToString().ToLowerInvariant();
    public string Description => Server.Description ?? "";
    public IReadOnlyList<McpCell> Cells { get; }
}

/// <summary>Header button per app: toggles that app for every shown server.</summary>
public sealed record McpColumn(McpApp App)
{
    public string Label => App.Label();
    public string ToolTip => $"Toggle {App.Label()} for every shown server ({App.ConfigNote()})";
}

public sealed partial class McpViewModel : ViewModelBase
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private McpService? _service;

    public ObservableCollection<McpRow> Rows { get; } = [];
    public IReadOnlyList<McpColumn> Columns { get; } = McpAppInfo.All.Select(a => new McpColumn(a)).ToList();

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _serverCount;

    partial void OnSearchChanged(string value) => Rebuild();

    public IReadOnlyList<string> ExistingIds => _service?.ExistingIds ?? [];

    public async Task EnsureLoadedAsync()
    {
        if (_service != null) return;
        _service = await Task.Run(() => new McpService());
        Rebuild();
    }

    private void Rebuild()
    {
        if (_service == null) return;
        var q = Search.Trim();
        Rows.Clear();
        foreach (var s in _service.Servers)
        {
            if (q.Length > 0 && !s.Id.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !McpSpec.Summary(s.Server).Contains(q, StringComparison.OrdinalIgnoreCase)) continue;
            Rows.Add(new McpRow(s));
        }
        ServerCount = _service.Servers.Count;
        CountText = ServerCount == 1 ? "1 server" : $"{ServerCount} servers";
    }

    /// <summary>Runs <paramref name="work"/> on a worker thread, one at a time, then refreshes.</summary>
    private async Task<T?> RunAsync<T>(Func<McpService, T> work)
    {
        if (_service == null) return default;
        await _gate.WaitAsync();
        IsBusy = true;
        try
        {
            var service = _service;
            return await Task.Run(() => work(service));
        }
        catch (Exception e) when (e is McpException or IOException or UnauthorizedAccessException)
        {
            Services.Notifier.Error(e.Message);
            AppLogs.Push("MCP", e.Message, LogLevel.Error);
            return default;
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
            Rebuild();
        }
    }

    public async Task ToggleAsync(McpCell cell, bool enabled)
    {
        var written = await RunAsync(s => (bool?)s.Toggle(cell.Row.Id, cell.App, enabled));
        if (written == false)
        {
            Services.Notifier.Show($"{cell.App.Label()} isn't installed; flag saved, nothing written");
        }
        else if (written == true)
        {
            AppLogs.Push("MCP", $"{cell.Row.Id}: {cell.App.Label()} {(enabled ? "on" : "off")}");
        }
    }

    /// <summary>Header click: on for all shown servers unless all are already on.</summary>
    public async Task ToggleColumnAsync(McpApp app)
    {
        var ids = Rows.Select(r => r.Id).ToList();
        if (ids.Count == 0) return;
        var allOn = Rows.All(r => r.Server.Apps.Get(app));
        var errors = await RunAsync(s =>
        {
            var failures = new List<string>();
            foreach (var id in ids)
            {
                try { s.Toggle(id, app, !allOn); }
                catch (McpException e) { failures.Add($"{id}: {e.Message}"); }
            }
            return failures;
        });
        if (errors is { Count: > 0 }) Services.Notifier.Error(string.Join("; ", errors));
        else Services.Notifier.Success($"{app.Label()} turned {(allOn ? "off" : "on")} for {ids.Count} server(s)");
    }

    public async Task<bool> SaveAsync(McpServer server)
    {
        var notes = await RunAsync(s => s.Upsert(server));
        if (notes == null) return false;
        var skipped = notes.Where(n => !n.Written).Select(n => n.App.Label()).ToList();
        var msg = $"Saved “{server.Id}”" + (skipped.Count > 0 ? $" ({string.Join(", ", skipped)} not installed, skipped)" : "");
        Services.Notifier.Success(msg);
        AppLogs.Push("MCP", msg, LogLevel.Success);
        return true;
    }

    public async Task DeleteAsync(string id)
    {
        if (await RunAsync(s => { s.Delete(id); return true; }))
        {
            Services.Notifier.Success($"Deleted “{id}”");
            AppLogs.Push("MCP", $"Deleted {id}", LogLevel.Success);
        }
    }

    public async Task DisableEverywhereAsync(string id)
    {
        await RunAsync(s =>
        {
            foreach (var app in McpAppInfo.All)
            {
                try { s.Toggle(id, app, false); } catch (McpException) { }
            }
            return true;
        });
        Services.Notifier.Success($"“{id}” turned off everywhere");
    }

    public async Task ImportAsync()
    {
        var report = await RunAsync(s => s.ImportAll());
        if (report == null) return;
        var parts = report.PerApp.Where(p => p.Count > 0).Select(p => $"{p.App.Label()} {p.Count}").ToList();
        if (parts.Count == 0) parts.Add("no new servers");
        var msg = $"Import finished: {report.Changed} ({string.Join(", ", parts)})";
        if (report.Errors.Count > 0)
        {
            msg += $" — {report.Errors.Count} skipped: {string.Join("; ", report.Errors)}";
            Services.Notifier.Error(msg);
            AppLogs.Push("MCP", msg, LogLevel.Warning);
        }
        else
        {
            Services.Notifier.Success(msg);
            AppLogs.Push("MCP", msg, LogLevel.Success);
        }
    }

    public async Task RewriteAsync()
    {
        var results = await RunAsync(s => s.RewriteAll());
        if (results == null) return;
        var failures = results.Where(r => r.Error != null).Select(r => $"{r.App.Label()}: {r.Error}").ToList();
        var skipped = results.Where(r => r.Error == null && !r.Written).Select(r => r.App.Label()).ToList();
        if (failures.Count > 0)
        {
            Services.Notifier.Error("Rewrite failed: " + string.Join("; ", failures));
            return;
        }
        var msg = "Every installed app rewritten from these flags";
        if (skipped.Count > 0) msg += $" ({string.Join(", ", skipped)} not installed, skipped)";
        Services.Notifier.Success(msg);
        AppLogs.Push("MCP", msg, LogLevel.Success);
    }

    public string ExportJson() => _service?.ExportJson() ?? "{}";

    public string? RowJson(string id) => _service?.RowJson(id);

    /// <summary>Clone of the stored server for the edit form.</summary>
    public McpServer? Get(string id) => _service?.Get(id) is { } s
        ? new McpServer
        {
            Id = s.Id, Name = s.Name, Server = (JsonObject)s.Server.DeepClone(), Apps = s.Apps.Clone(),
            Description = s.Description, Homepage = s.Homepage, Docs = s.Docs, Tags = s.Tags?.ToList(),
        }
        : null;
}
