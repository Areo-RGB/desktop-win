using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Core.Skills;
using FluentDesktop.Helpers;
using FluentDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentDesktop.Views;

public sealed partial class SkillsPage : Page
{
    private const string SkillTemplate = "---\nname: my-skill\ndescription: What this skill does and when to use it\n---\n\n# My skill\n\nInstructions for the agent.\n";

    public SkillsPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.EnsureStartedAsync();
    }

    public SkillsViewModel ViewModel { get; } = Services.Skills;

    private static T? TagOf<T>(object sender) where T : class => (sender as FrameworkElement)?.Tag as T;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshAsync();

    private async void Column_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<SkillColumn>(sender) is { } column) await ViewModel.ToggleColumnAsync(column.App);
    }

    private async void Cell_Click(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.Tag is SkillCell cell) await ViewModel.ToggleAsync(cell, box.IsChecked == true);
    }

    private async void New_Click(object sender, RoutedEventArgs e) => await EditAsync(null, SkillTemplate);

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<SkillRow>(sender) is { } row && await ViewModel.ReadAsync(row.Id) is { } text) await EditAsync(row.Id, text);
    }

    /// <summary>SKILL.md editor. Existing IDs can't be renamed here.</summary>
    private async Task EditAsync(string? id, string text)
    {
        var idBox = new TextBox
        {
            Header = "Directory name",
            PlaceholderText = "my-skill",
            Text = id ?? "",
            IsEnabled = id == null,
            Description = "Letters, numbers, dots, hyphens and underscores.",
        };
        var content = new TextBox
        {
            Header = "SKILL.md",
            Text = text.Replace("\n", "\r"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            MinHeight = 360,
        };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var panel = new StackPanel { Spacing = 10, MinWidth = 620, Children = { idBox, content, error } };
        var dialog = Dialogs.Create(id == null ? "New skill" : $"Edit {id}", new ScrollViewer { Content = panel, MaxHeight = 640 }, "Save locally");
        dialog.PrimaryButtonClick += (_, a) =>
        {
            var newId = idBox.Text.Trim();
            string? problem = null;
            if (!SkillService.IsValidId(newId)) problem = "Enter a valid directory name";
            else
            {
                try { SkillService.Metadata(newId, content.Text.Replace("\r", "\n")); }
                catch (SkillException ex) { problem = ex.Message; }
            }
            if (problem != null)
            {
                error.Message = problem;
                error.IsOpen = true;
                a.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await ViewModel.SaveAsync(idBox.Text.Trim(), content.Text.Replace("\r", "\n"), id != null);
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<SkillRow>(sender) is not { } row) return;
        if (await Dialogs.ConfirmAsync($"Uninstall {row.Name}?",
                "A backup is kept in .git/desktopcc-backups. App links DesktopCC owns are removed, then the master copy is deleted and the removal is synced.",
                "Uninstall", danger: true))
        {
            await ViewModel.RemoveAsync(row.Id);
        }
    }

    private async void ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickFolderAsync() is { } folder) await ViewModel.ImportFolderAsync(folder);
    }

    private async void ImportApps_Click(object sender, RoutedEventArgs e) => await ViewModel.ImportFromAppsAsync();

    private void OpenRepo_Click(object sender, RoutedEventArgs e)
    {
        if (Directory.Exists(ViewModel.RepoDir)) ProcessRunner.Open(ViewModel.RepoDir);
    }

    private async void Backups_Click(object sender, RoutedEventArgs e)
    {
        var list = new ListView { ItemsSource = ViewModel.Backups, SelectionMode = ListViewSelectionMode.Single, MaxHeight = 420, MinWidth = 480 };
        var empty = new TextBlock { Text = "No backups yet.", Visibility = ViewModel.Backups.Count == 0 ? Visibility.Visible : Visibility.Collapsed };
        var dialog = Dialogs.Create("Backups", new StackPanel { Spacing = 8, Children = { empty, list } }, "Restore", "Close");
        dialog.IsPrimaryButtonEnabled = false;
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedItem != null;
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is string backup)
        {
            await ViewModel.RestoreAsync(backup);
        }
    }
}
