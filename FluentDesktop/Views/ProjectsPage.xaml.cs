using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Helpers;
using FluentDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentDesktop.Views;

public sealed partial class ProjectsPage : Page
{
    public ProjectsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.EnsureLoadedAsync();
    }

    public ProjectsViewModel ViewModel { get; } = Services.Projects;

    private static ProjectItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as ProjectItem;

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ViewModel.RescanAsync();

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Start(item);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ViewModel.Stop(item);
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ProcessRunner.Open(item.Path);
    }

    private void Url_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) ProcessRunner.Open(item.Def.DevUrl);
    }

    private async void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await ViewModel.HideAsync(item);
    }

    private async void ShowAll_Click(object sender, RoutedEventArgs e) => await ViewModel.ShowAllAsync();
}
