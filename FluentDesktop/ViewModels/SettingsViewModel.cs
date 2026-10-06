using CommunityToolkit.Mvvm.ComponentModel;
using FluentDesktop.Core.DeviceMirror;
using FluentDesktop.Core.Settings;
using FluentDesktop.Helpers;

namespace FluentDesktop.ViewModels;

public sealed partial class SettingsViewModel : ViewModelBase
{
    private AppSettings _settings = new();

    [ObservableProperty]
    private int _themeIndex;

    [ObservableProperty]
    private int _backdropIndex;

    [ObservableProperty]
    private bool _isAdbRestarting;

    public string AdbPath => AdbService.AdbPath;

    public string AppVersion => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public async Task<AppSettings> LoadAsync()
    {
        _settings = await AppSettings.LoadAsync();
        ThemeIndex = (int)_settings.Theme;
        BackdropIndex = (int)_settings.Backdrop;
        return _settings;
    }

    partial void OnThemeIndexChanged(int value)
    {
        var theme = (AppTheme)Math.Clamp(value, 0, 2);
        if (_settings.Theme == theme) return;
        _settings = _settings with { Theme = theme };
        App.MainWindow.ApplyTheme(theme);
        _ = _settings.SaveAsync();
    }

    partial void OnBackdropIndexChanged(int value)
    {
        var backdrop = (AppBackdrop)Math.Clamp(value, 0, 2);
        if (_settings.Backdrop == backdrop) return;
        _settings = _settings with { Backdrop = backdrop };
        App.MainWindow.ApplyBackdrop(backdrop);
        _ = _settings.SaveAsync();
    }

    public Task SetPaneOpenAsync(bool open)
    {
        _settings = _settings with { NavigationPaneOpen = open };
        return _settings.SaveAsync();
    }

    public async Task RestartAdbServerAsync()
    {
        IsAdbRestarting = true;
        try
        {
            await AdbService.RestartServerAsync();
            if (AdbService.LastError is { } error) Services.Notifier.Error($"ADB restart failed: {error}");
            else Services.Notifier.Success("ADB server restarted");
        }
        finally
        {
            IsAdbRestarting = false;
        }
    }
}
