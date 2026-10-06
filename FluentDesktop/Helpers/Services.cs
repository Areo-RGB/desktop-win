using FluentDesktop.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace FluentDesktop.Helpers;

/// <summary>
/// App-lifetime singletons. View models outlive page navigation so a running
/// download, command or tunnel keeps streaming while you look elsewhere.
/// </summary>
public static class Services
{
    public static readonly Notifier Notifier = new();

    private static readonly Lazy<DashboardViewModel> _dashboard = new(() => new DashboardViewModel());
    private static readonly Lazy<SpielerplusViewModel> _spielerplus = new(() => new SpielerplusViewModel());
    private static readonly Lazy<HubViewModel> _hub = new(() => new HubViewModel());
    private static readonly Lazy<GithubViewModel> _github = new(() => new GithubViewModel());
    private static readonly Lazy<ScrcpyViewModel> _scrcpy = new(() => new ScrcpyViewModel());
    private static readonly Lazy<YtDlpViewModel> _ytdlp = new(() => new YtDlpViewModel());
    private static readonly Lazy<DesktopCommanderViewModel> _desktopCommander = new(() => new DesktopCommanderViewModel());
    private static readonly Lazy<SettingsViewModel> _settings = new(() => new SettingsViewModel());

    public static DashboardViewModel Dashboard => _dashboard.Value;
    public static SpielerplusViewModel Spielerplus => _spielerplus.Value;
    public static HubViewModel Hub => _hub.Value;
    public static GithubViewModel Github => _github.Value;
    public static ScrcpyViewModel Scrcpy => _scrcpy.Value;
    public static YtDlpViewModel YtDlp => _ytdlp.Value;
    public static DesktopCommanderViewModel DesktopCommander => _desktopCommander.Value;
    public static SettingsViewModel Settings => _settings.Value;

    public static void ShutDown()
    {
        if (_dashboard.IsValueCreated) _dashboard.Value.Dispose();
        if (_ytdlp.IsValueCreated) _ytdlp.Value.Dispose();
        if (_scrcpy.IsValueCreated) _scrcpy.Value.Dispose();
        if (_hub.IsValueCreated) _hub.Value.Dispose();
        if (_desktopCommander.IsValueCreated) _desktopCommander.Value.Dispose();
    }
}

/// <summary>Snackbar replacement: a single InfoBar hosted by the main window.</summary>
public sealed class Notifier
{
    public event Action<string, InfoBarSeverity, string?, Action?>? Requested;

    public void Show(string message, InfoBarSeverity severity = InfoBarSeverity.Informational, string? actionLabel = null, Action? action = null) =>
        Requested?.Invoke(message, severity, actionLabel, action);

    public void Success(string message, string? actionLabel = null, Action? action = null) =>
        Show(message, InfoBarSeverity.Success, actionLabel, action);

    public void Error(string message) => Show(message, InfoBarSeverity.Error);
}
