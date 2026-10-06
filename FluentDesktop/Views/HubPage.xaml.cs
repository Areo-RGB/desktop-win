using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Helpers;
using FluentDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentDesktop.Views;

public sealed partial class HubPage : Page
{
    private bool _webInitialized;

    public HubPage()
    {
        InitializeComponent();
        Loaded += (_, _) => ViewModel.Start();
    }

    public HubViewModel ViewModel { get; } = Services.Hub;

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (DashboardPanel == null) return; // raised while InitializeComponent is still building the page
        var index = sender.Items.IndexOf(sender.SelectedItem);
        ControlPanel.Visibility = Bind.Visible(index == 0);
        EndpointsPanel.Visibility = Bind.Visible(index == 1);
        LogsPanel.Visibility = Bind.Visible(index == 2);
        DashboardPanel.Visibility = Bind.Visible(index == 3);
        if (index == 3) _ = InitWebAsync();
    }

    private async Task InitWebAsync()
    {
        if (_webInitialized) return;
        _webInitialized = true;
        try
        {
            await DashboardWeb.EnsureCoreWebView2Async();
            DashboardWeb.NavigationStarting += (_, _) =>
            {
                WebProgress.Visibility = Visibility.Visible;
                WebError.Visibility = Visibility.Collapsed;
            };
            DashboardWeb.NavigationCompleted += (_, e) =>
            {
                WebProgress.Visibility = Visibility.Collapsed;
                if (e.IsSuccess) return;
                WebErrorText.Text = $"Could not load {ViewModel.DashboardUrl}\n{e.WebErrorStatus}\n\nIs the MCPHub server running?";
                WebError.Visibility = Visibility.Visible;
            };
            DashboardWeb.Source = new Uri(ViewModel.DashboardUrl);
        }
        catch (Exception e)
        {
            WebErrorText.Text = $"WebView2 Runtime is not available: {e.Message}";
            WebError.Visibility = Visibility.Visible;
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private void OpenDashboard_Click(object sender, RoutedEventArgs e) => ProcessRunner.Open(ViewModel.DashboardUrl);

    private async void StartAll_Click(object sender, RoutedEventArgs e) => await ViewModel.StartAllAsync();

    private async void StopAll_Click(object sender, RoutedEventArgs e) => await ViewModel.StopAllAsync();

    private async void StartSsh_Click(object sender, RoutedEventArgs e) => await ViewModel.StartSshAsync();

    private async void TestSsh_Click(object sender, RoutedEventArgs e) => await ViewModel.TestSshAsync();

    private async void ToggleHub_Click(object sender, RoutedEventArgs e) => await ViewModel.ToggleMcphubAsync();

    private async void ToggleTunnel_Click(object sender, RoutedEventArgs e) => await ViewModel.ToggleTunnelAsync();

    private int? KillPort()
    {
        var value = KillPortBox.Value;
        if (double.IsNaN(value) || value < 1 || value > 65535)
        {
            Services.Notifier.Error("Enter a valid port (1–65535).");
            return null;
        }
        return (int)value;
    }

    private async void InspectPort_Click(object sender, RoutedEventArgs e)
    {
        if (KillPort() is { } port) await ViewModel.InspectPortAsync(port);
    }

    private async void KillPort_Click(object sender, RoutedEventArgs e)
    {
        if (KillPort() is not { } port) return;
        var ok = await Dialogs.ConfirmAsync(
            $"Kill process on :{port}?",
            "This runs taskkill /F on every process listening on the port. Use when the port is stuck (e.g. EADDRINUSE).",
            "Kill",
            danger: true);
        if (ok) await ViewModel.KillPortAsync(port);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string value && value.Length > 0) ClipboardHelper.Copy(value, "Value");
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e) => ClipboardHelper.Copy(ViewModel.VisibleLog, "Log");

    private void WebBack_Click(object sender, RoutedEventArgs e)
    {
        if (DashboardWeb.CanGoBack) DashboardWeb.GoBack();
    }

    private void WebReload_Click(object sender, RoutedEventArgs e)
    {
        if (DashboardWeb.CoreWebView2 != null) DashboardWeb.Reload();
        else _ = InitWebAsync();
    }

    private void WebHome_Click(object sender, RoutedEventArgs e) => DashboardWeb.Source = new Uri(ViewModel.DashboardUrl);
}

public static class HubBind
{
    public static bool IsTrue(bool value) => value;

    public static Brush StateBrush(bool running) => running ? Bind.Success : Bind.Neutral;

    public static string StateGlyph(bool running) => running ? "" : "";

    public static string SshLabel(bool running) => running ? "Running" : "Start SSH";

    public static string HubLabel(bool running) => running ? "Stop Server" : "Start Server";

    public static string TunnelLabel(bool running) => running ? "Stop Tunnel" : "Start Tunnel";

    public static string LocalServerDescription(string url) => $"Local web server at {url}";

    public static string PortKillDescription(string logPath) => $"Inspect listeners or force-kill. Logged below & to {logPath}.";
}
