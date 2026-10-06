using FluentDesktop.Core.Settings;
using FluentDesktop.Helpers;
using FluentDesktop.Views;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace FluentDesktop;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> Pages = new()
    {
        ["Dashboard"] = typeof(DashboardPage),
        ["Spielerplus"] = typeof(SpielerplusPage),
        ["Hub"] = typeof(HubPage),
        ["Github"] = typeof(GithubPage),
        ["Scrcpy"] = typeof(ScrcpyPage),
        ["YtDlp"] = typeof(YtDlpPage),
        ["DesktopCommander"] = typeof(DesktopCommanderPage),
    };

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _notificationTimer;

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        if (Images.ExtractAsset("Assets/AppIcon.ico") is { } icon) AppWindow.SetIcon(icon);
        AppTitleBar.IconSource = new ImageIconSource { ImageSource = Images.FromAsset("Assets/logo.png", 32) };
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1400, 900));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;

        _notificationTimer = DispatcherQueue.CreateTimer();
        _notificationTimer.Tick += (_, _) =>
        {
            _notificationTimer.Stop();
            Notification.IsOpen = false;
        };
        Services.Notifier.Requested += ShowNotification;

        // Desktop Commander lives for the whole session so its status badge
        // updates even when the page is not open.
        var dc = Services.DesktopCommander;
        dc.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(dc.Badge)) UpdateDesktopCommanderBadge();
        };
        _ = dc.InitAsync();

        Closed += (_, _) => Services.ShutDown();

        NavView.SelectedItem = NavView.MenuItems[0];
        _ = ApplyStoredSettingsAsync();

        if (Environment.GetEnvironmentVariable("FLUENT_DESKTOP_SMOKE") is { Length: > 0 } smokeFile)
        {
            RootGrid.Loaded += async (_, _) => await RunSmokeAsync(smokeFile);
        }
    }

    /// <summary>CI smoke test: visit every page so runtime XAML / x:Bind errors surface.</summary>
    private async Task RunSmokeAsync(string resultFile)
    {
        var visited = new List<string>();
        foreach (var asset in new[] { "Assets/logo.png", "Assets/AppIcon.ico", "Assets/cc_apps/claude.svg", "Assets/GitHub_Invertocat_Black.png" })
        {
            if (Images.AssetBytes(asset) == null) App.SmokeErrors.Add($"Embedded asset missing: {asset}");
        }
        foreach (var (tag, type) in Pages.Append(new("Settings", typeof(SettingsPage))))
        {
            try
            {
                ContentFrame.Navigate(type);
                await Task.Delay(1500);
                visited.Add(tag);
            }
            catch (Exception e)
            {
                App.SmokeErrors.Add($"{tag}: {e}");
            }
        }
        var report = App.SmokeErrors.Count == 0
            ? $"SMOKE OK: {string.Join(", ", visited)}"
            : "SMOKE FAILED\n" + string.Join("\n---\n", App.SmokeErrors);
        await File.WriteAllTextAsync(resultFile, report);
        Close();
    }

    private async Task ApplyStoredSettingsAsync()
    {
        var settings = await Services.Settings.LoadAsync();
        ApplyTheme(settings.Theme);
        ApplyBackdrop(settings.Backdrop);
        NavView.IsPaneOpen = settings.NavigationPaneOpen;
    }

    public void ApplyTheme(AppTheme theme)
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
        AppWindow.TitleBar.PreferredTheme = theme switch
        {
            AppTheme.Light => TitleBarTheme.Light,
            AppTheme.Dark => TitleBarTheme.Dark,
            _ => TitleBarTheme.UseDefaultAppMode,
        };
    }

    public void ApplyBackdrop(AppBackdrop backdrop)
    {
        SystemBackdrop = backdrop switch
        {
            AppBackdrop.MicaAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            AppBackdrop.Acrylic => new DesktopAcrylicBackdrop(),
            _ => new MicaBackdrop { Kind = MicaKind.Base },
        };
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
        _ = Services.Settings.SetPaneOpenAsync(NavView.IsPaneOpen);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        Type? page = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : args.SelectedItemContainer?.Tag is string tag && Pages.TryGetValue(tag, out var t) ? t : null;
        if (page == null || ContentFrame.CurrentSourcePageType == page) return;
        ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }

    private void UpdateDesktopCommanderBadge()
    {
        var badge = Services.DesktopCommander.Badge;
        DesktopCommanderBadge.Visibility = badge == null ? Visibility.Collapsed : Visibility.Visible;
        DesktopCommanderBadge.Style = (Style)Application.Current.Resources[badge switch
        {
            "ONLINE" => "SuccessIconInfoBadgeStyle",
            "ERROR" => "CriticalIconInfoBadgeStyle",
            "AUTH" => "CautionIconInfoBadgeStyle",
            _ => "AttentionIconInfoBadgeStyle",
        }];
        ToolTipService.SetToolTip(DesktopCommanderItem, badge == null ? null : $"Desktop Commander: {badge}");
    }

    private void ShowNotification(string message, InfoBarSeverity severity, string? actionLabel, Action? action)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            Notification.IsOpen = false;
            Notification.Message = message;
            Notification.Severity = severity;
            if (actionLabel != null && action != null)
            {
                var button = new HyperlinkButton { Content = actionLabel };
                button.Click += (_, _) =>
                {
                    Notification.IsOpen = false;
                    action();
                };
                Notification.ActionButton = button;
            }
            else
            {
                Notification.ActionButton = null;
            }
            Notification.IsOpen = true;
            _notificationTimer.Interval = TimeSpan.FromSeconds(severity == InfoBarSeverity.Error ? 6 : 4);
            _notificationTimer.Start();
        });
    }
}
