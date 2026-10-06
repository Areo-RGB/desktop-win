using FluentDesktop.Core.DesktopCommander;
using FluentDesktop.Helpers;
using FluentDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentDesktop.Views;

public sealed partial class DesktopCommanderPage : Page
{
    public DesktopCommanderPage()
    {
        InitializeComponent();
    }

    public DesktopCommanderViewModel ViewModel { get; } = Services.DesktopCommander;

    private async void Start_Click(object sender, RoutedEventArgs e) => await ViewModel.StartAsync();

    private async void Stop_Click(object sender, RoutedEventArgs e) => await ViewModel.StopAsync();

    private async void Reauthorize_Click(object sender, RoutedEventArgs e) => await ViewModel.ReauthorizeAsync();

    private void OpenLink_Click(object sender, RoutedEventArgs e) => ViewModel.OpenAuthorizationUrl();

    private void ClearLogs_Click(object sender, RoutedEventArgs e) => ViewModel.ClearLogs();
}

public static class DesktopCommanderBind
{
    public static string StatusLabel(DesktopCommanderStatus status) => status.ToString().ToUpperInvariant();

    public static Brush StatusBrush(DesktopCommanderStatus status) => status switch
    {
        DesktopCommanderStatus.Starting => (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
        DesktopCommanderStatus.Authorizing => Bind.Warning,
        DesktopCommanderStatus.Verifying => Bind.Purple,
        DesktopCommanderStatus.Online => Bind.Success,
        DesktopCommanderStatus.Error => Bind.Danger,
        _ => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
    };
}
