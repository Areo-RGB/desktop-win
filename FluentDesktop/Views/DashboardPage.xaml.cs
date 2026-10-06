using FluentDesktop.Core.Dashboard;
using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Helpers;
using FluentDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace FluentDesktop.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.EnsureLoadedAsync();
    }

    public DashboardViewModel ViewModel { get; } = Services.Dashboard;

    private static DashboardCommandItem? ItemOf(object sender) => (sender as FrameworkElement)?.Tag as DashboardCommandItem;

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { CanRun: true } item) await ViewModel.RunAsync(item.Command);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { } item) await ViewModel.DeleteAsync(item.Command.Id);
    }

    private async void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (await ShowCommandDialogAsync("Edit Command", "Save Changes", item.Command) is { } updated)
        {
            await ViewModel.UpdateAsync(updated);
        }
    }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (await ShowCommandDialogAsync("Add Custom Command", "Save", null) is { } created)
        {
            await ViewModel.AddAsync(created);
        }
    }

    private async void Reload_Click(object sender, RoutedEventArgs e) => await ViewModel.LoadAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    private void Dismiss_Click(object sender, RoutedEventArgs e) => ViewModel.ClearConsole();

    private static async Task<DashboardCommand?> ShowCommandDialogAsync(string title, string primary, DashboardCommand? existing)
    {
        var titleBox = new TextBox { Header = "Command Title *", PlaceholderText = "e.g. System Info", Text = existing?.Title ?? "" };
        var exeBox = new TextBox { Header = "Executable / Binary Path *", PlaceholderText = "e.g. git or C:\\Tools\\tool.exe", Text = existing?.Executable ?? "" };
        var argsBox = new TextBox
        {
            Header = "Arguments (space separated, \"quote\" to group)",
            PlaceholderText = "e.g. status --short",
            Text = existing == null ? "" : WindowsCommandLine.Join(existing.Arguments),
        };
        var descBox = new TextBox { Header = "Description", PlaceholderText = "e.g. Print system information", Text = existing?.Description ?? "" };
        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        foreach (var box in new[] { titleBox, exeBox, argsBox, descBox }) panel.Children.Add(box);

        var dialog = Dialogs.Create(title, panel, primary);
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (titleBox.Text.Trim().Length == 0 || exeBox.Text.Trim().Length == 0) args.Cancel = true;
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;

        var description = descBox.Text.Trim();
        var command = existing ?? new DashboardCommand { Id = $"custom_{DateTimeOffset.Now.ToUnixTimeMilliseconds()}" };
        return command with
        {
            Title = titleBox.Text.Trim(),
            Executable = exeBox.Text.Trim(),
            Arguments = WindowsCommandLine.Split(argsBox.Text),
            Description = description.Length > 0 ? description : "Custom preconfigured command",
        };
    }
}

/// <summary>x:Bind functions for the dashboard.</summary>
public static class DashboardBind
{
    public static Brush BorderFor(bool running, bool selected) =>
        running || selected
            ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
            : (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];

    public static Brush StatusBrush(int kind) => kind switch
    {
        1 => Bind.Success,
        2 => Bind.Danger,
        _ => (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
    };

    public static string StatusGlyph(int kind) => kind switch
    {
        1 => "\uE930",
        2 => "\uEA39",
        _ => "\uE895",
    };
}
