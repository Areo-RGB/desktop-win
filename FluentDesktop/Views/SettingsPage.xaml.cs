using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Helpers;
using FluentDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentDesktop.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        ViewModel.LoadFolders();
    }

    public SettingsViewModel ViewModel { get; } = Services.Settings;

    public string ConfigDir => AppPaths.ConfigDir;

    public Microsoft.UI.Xaml.Media.ImageSource? Logo { get; } = Images.FromAsset("Assets/logo.png", 40);

    private async void RestartAdb_Click(object sender, RoutedEventArgs e) => await ViewModel.RestartAdbServerAsync();

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFolderAsync() is not { } folder) return;
        switch ((sender as FrameworkElement)?.Tag as string)
        {
            case "Projects": ViewModel.ProjectsFolder = folder; break;
            case "Downloads": ViewModel.DownloadsFolder = folder; break;
            default: ViewModel.VideoLibraryFolder = folder; break;
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => ViewModel.ClearLog();

    private void OpenConfig_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        ProcessRunner.Open(AppPaths.ConfigDir);
    }
}
