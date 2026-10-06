using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.Settings;

public enum AppTheme { System, Light, Dark }

public enum AppBackdrop { Mica, MicaAlt, Acrylic }

/// <summary>Window/appearance preferences (app_settings.json).</summary>
public sealed record AppSettings
{
    public AppTheme Theme { get; init; } = AppTheme.System;
    public AppBackdrop Backdrop { get; init; } = AppBackdrop.Mica;
    public bool NavigationPaneOpen { get; init; } = true;

    public static string FilePath => AppPaths.ConfigFile("app_settings.json");

    public static async Task<AppSettings> LoadAsync() =>
        await JsonStore.LoadAsync<AppSettings>(FilePath).ConfigureAwait(false) ?? new AppSettings();

    public Task SaveAsync() => JsonStore.SaveAsync(FilePath, this);
}
