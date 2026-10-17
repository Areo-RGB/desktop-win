using FluentDesktop.Helpers;
using FluentDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FluentDesktop.Views;

public sealed partial class ToolsPage : Page
{
    public ToolsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.EnsureProbedAsync();
    }

    public ToolsViewModel ViewModel { get; } = Services.Tools;

    private void Greet_Click(object sender, RoutedEventArgs e) => ViewModel.Greet();

    private void Name_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) ViewModel.Greet();
    }

    private async void Probe_Click(object sender, RoutedEventArgs e) => await ViewModel.ProbeAsync();
}
