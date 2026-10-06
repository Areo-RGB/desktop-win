using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.Dashboard;

/// <summary>Loads / saves dashboard_commands.json, re-inserting missing defaults.</summary>
public sealed class DashboardCommandStore(string? filePath = null)
{
    public string FilePath { get; } = filePath ?? AppPaths.ConfigFile("dashboard_commands.json");

    public async Task<List<DashboardCommand>> LoadAsync()
    {
        var defaults = DashboardCommand.DefaultCommands;
        if (!File.Exists(FilePath))
        {
            await SaveAsync(defaults).ConfigureAwait(false);
            return defaults;
        }

        var loaded = await JsonStore.LoadAsync<List<DashboardCommand>>(FilePath).ConfigureAwait(false);
        if (loaded == null) return defaults;

        // Ensure the built-in commands are always present, in sequence.
        var changed = false;
        for (var i = 0; i < defaults.Count; i++)
        {
            if (loaded.Any(c => c.Id == defaults[i].Id)) continue;
            loaded.Insert(Math.Clamp(i, 0, loaded.Count), defaults[i]);
            changed = true;
        }
        if (changed) await SaveAsync(loaded).ConfigureAwait(false);
        return loaded;
    }

    public Task SaveAsync(IEnumerable<DashboardCommand> commands) =>
        JsonStore.SaveAsync(FilePath, commands.ToList());
}
