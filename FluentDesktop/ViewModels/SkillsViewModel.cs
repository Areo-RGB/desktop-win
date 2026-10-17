using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentDesktop.Core.Skills;
using FluentDesktop.Helpers;

namespace FluentDesktop.ViewModels;

public sealed partial class SkillCell(SkillRow row, SkillApp app, bool enabled) : ObservableObject
{
    public SkillRow Row { get; } = row;
    public SkillApp App { get; } = app;
    public string ToolTip => $"{Row.Name} in {SkillService.Label(App)}";
    public bool IsEnabled { get; } = enabled;
}

public sealed class SkillRow
{
    public SkillRow(Skill skill)
    {
        Skill = skill;
        Cells = SkillService.AllApps.Select((a, i) => new SkillCell(this, a, skill.Enabled[i])).ToList();
    }

    public Skill Skill { get; }
    public string Id => Skill.Id;
    public string Name => Skill.Name;
    public string Description => Skill.Description.Length > 0 ? Skill.Description : Skill.Id;
    public IReadOnlyList<SkillCell> Cells { get; }
}

public sealed record SkillColumn(SkillApp App)
{
    public string Label => SkillService.Label(App);
}

/// <summary>Skills store for Pi, Claude Code, Codex and OpenCode (DesktopCC's Skills page).</summary>
public sealed partial class SkillsViewModel : ViewModelBase
{
    private readonly SkillService _service = new();
    private List<Skill> _skills = [];
    private bool _started;

    public ObservableCollection<SkillRow> Rows { get; } = [];
    public ObservableCollection<string> Backups { get; } = [];
    public IReadOnlyList<SkillColumn> Columns { get; } = SkillService.AllApps.Select(a => new SkillColumn(a)).ToList();

    public string RepoDir => _service.RepoDir;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private string? _busy;

    public bool IsIdle => Busy == null;

    [ObservableProperty]
    private string _search = "";

    [ObservableProperty]
    private int _filterIndex;

    [ObservableProperty]
    private string _countText = "";

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _statusIsError;

    partial void OnSearchChanged(string value) => Refilter();

    partial void OnFilterIndexChanged(int value) => Refilter();

    public async Task EnsureStartedAsync()
    {
        if (_started) return;
        _started = true;
        await RunAsync("Syncing skills", s =>
        {
            s.Initialize();
            return s.Sync() is { } warning ? $"Sync: {warning}" : "";
        });
    }

    /// <summary>
    /// One operation at a time on a worker; the list and backups are re-read
    /// even after a failure so partial filesystem outcomes show honestly.
    /// </summary>
    public async Task<bool> RunAsync(string label, Func<SkillService, string> operation)
    {
        if (Busy != null) return false;
        Busy = label;
        Status = "";
        string? message = null;
        string? error = null;
        List<Skill>? skills = null;
        List<string>? backups = null;
        await Task.Run(() =>
        {
            try { message = operation(_service); }
            catch (Exception e) when (e is SkillException or IOException or UnauthorizedAccessException) { error = e.Message; }
            try { skills = _service.List(); backups = _service.Backups(); }
            catch (Exception e) when (e is SkillException or IOException or UnauthorizedAccessException) { error ??= $"Could not read skills: {e.Message}"; }
        });
        Busy = null;
        if (skills != null) _skills = skills;
        if (backups != null)
        {
            Backups.Clear();
            foreach (var b in Enumerable.Reverse(backups)) Backups.Add(b);
        }
        Refilter();
        if (error != null)
        {
            (Status, StatusIsError) = (error, true);
            AppLogs.Push("Skills", error, LogLevel.Error);
            return false;
        }
        if (!string.IsNullOrEmpty(message))
        {
            (Status, StatusIsError) = (message, message.StartsWith("Sync:"));
            AppLogs.Push("Skills", message, StatusIsError ? LogLevel.Warning : LogLevel.Success);
        }
        return true;
    }

    private void Refilter()
    {
        var q = Search.Trim();
        var shown = _skills.Where(s => FilterIndex switch
            {
                1 => !s.Enabled.Any(e => e),
                >= 2 and <= 5 => s.Enabled[FilterIndex - 2],
                _ => true,
            })
            .Where(s => q.Length == 0 || s.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        s.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || s.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Rows.Clear();
        foreach (var s in shown) Rows.Add(new SkillRow(s));
        CountText = $"{shown.Count} shown / {_skills.Count} total";
    }

    public Task ToggleAsync(SkillCell cell, bool enabled) =>
        RunAsync(enabled ? "Enabling" : "Disabling", s =>
        {
            s.SetEnabled(cell.Row.Id, cell.App, enabled);
            return "";
        });

    /// <summary>Header click: turn the app on for every shown skill, or off if all are on.</summary>
    public Task ToggleColumnAsync(SkillApp app)
    {
        var ids = Rows.Select(r => r.Id).ToList();
        var index = Array.IndexOf(SkillService.AllApps, app);
        var enable = !(Rows.Count > 0 && Rows.All(r => r.Skill.Enabled[index]));
        return RunAsync("Updating", s =>
        {
            var failures = new List<string>();
            foreach (var id in ids)
            {
                try { s.SetEnabled(id, app, enable); }
                catch (SkillException e) { failures.Add($"{id}: {e.Message}"); }
            }
            if (failures.Count > 0) throw new SkillException(string.Join("; ", failures));
            return $"{SkillService.Label(app)} {(enable ? "enabled" : "turned off")} for {ids.Count} skills";
        });
    }

    public Task<bool> SaveAsync(string id, string content, bool existing) =>
        RunAsync("Saving", s =>
        {
            if (existing) s.SaveSkill(id, content); else s.CreateSkill(id, content);
            return Synced(s, existing ? $"Saved {id}" : $"Created {id}");
        });

    public async Task<string?> ReadAsync(string id)
    {
        try { return await Task.Run(() => _service.ReadSkill(id)); }
        catch (Exception e) when (e is SkillException or IOException)
        {
            (Status, StatusIsError) = (e.Message, true);
            return null;
        }
    }

    public Task RemoveAsync(string id) => RunAsync("Uninstalling", s => { s.Remove(id); return Synced(s, $"Uninstalled {id} (backup kept)"); });

    public Task RestoreAsync(string backup) => RunAsync("Restoring", s => { s.Restore(backup); return Synced(s, $"Restored {backup}"); });

    public Task ImportFolderAsync(string folder) => RunAsync("Importing", s => { s.ImportFolder(folder); return Synced(s, $"Imported {Path.GetFileName(folder.TrimEnd('\\', '/'))}"); });

    public Task ImportFromAppsAsync() => RunAsync("Importing", s => Synced(s, $"Imported {s.ImportFromApps()} skills from apps"));

    public Task RefreshAsync() => RunAsync("Syncing skills", s => s.Sync() is { } warning ? $"Sync: {warning}" : "Synced with the repository");

    private static string Synced(SkillService s, string message) =>
        s.Sync() is { } warning ? $"{message}; sync pending: {warning}" : message;
}
