using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Core.YtDlp;
using FluentDesktop.Helpers;
using FluentDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluentDesktop.Views;

public sealed partial class YtDlpPage : Page
{
    public YtDlpPage()
    {
        InitializeComponent();
        Loaded += async (_, _) => await ViewModel.InitAsync();
    }

    public YtDlpViewModel ViewModel { get; } = Services.YtDlp;

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (CloudPanel == null) return; // raised while InitializeComponent is still building the page
        var index = sender.Items.IndexOf(sender.SelectedItem);
        DownloaderPanel.Visibility = Bind.Visible(index == 0);
        LocalPanel.Visibility = Bind.Visible(index == 1);
        CloudPanel.Visibility = Bind.Visible(index == 2);
    }

    private static MediaEntry? EntryOf(object sender) => (sender as FrameworkElement)?.Tag as MediaEntry;

    // ── Downloader ──────────────────────────────────────────────

    private async void Url_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) await ViewModel.ParseAsync();
    }

    private async void Parse_Click(object sender, RoutedEventArgs e) => await ViewModel.ParseAsync();

    private async void Paste_Click(object sender, RoutedEventArgs e) => await ViewModel.PasteFromClipboardAsync();

    private async void Download_Click(object sender, RoutedEventArgs e) => await ViewModel.StartDownloadAsync();

    private void CancelDownload_Click(object sender, RoutedEventArgs e) => ViewModel.CancelDownload();

    private async void OutputFolder_Click(object sender, RoutedEventArgs e) => await ViewModel.SelectOutputFolderAsync();

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        var list = new ListView
        {
            ItemsSource = ViewModel.History,
            SelectionMode = ListViewSelectionMode.None,
            MaxHeight = 460,
            ItemTemplate = (DataTemplate)Resources["HistoryTemplate"],
        };
        var empty = new TextBlock
        {
            Text = "No recent downloads.",
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 28, 0, 28),
            Visibility = Bind.Visible(ViewModel.History.Count == 0),
        };
        var panel = new Grid { Width = 640 };
        panel.Children.Add(list);
        panel.Children.Add(empty);

        var dialog = Dialogs.Create("Download history", panel, ViewModel.History.Count > 0 ? "Clear history" : "", close: "Done");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.ClearHistoryAsync();
    }

    private void OpenHistory_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is HistoryEntry entry) ViewModel.OpenPath(OpenTarget(entry.Item.FilePath));
    }

    /// <summary>Opens the containing folder for a file path (explorer /select would need a shell call).</summary>
    private static string OpenTarget(string path) =>
        File.Exists(path) ? Path.GetDirectoryName(path) ?? path : path;

    // ── Local files ─────────────────────────────────────────────

    private async void RefreshLocal_Click(object sender, RoutedEventArgs e) => await ViewModel.LoadLocalFilesAsync();

    private async void ChooseRoot_Click(object sender, RoutedEventArgs e) => await ViewModel.ChooseLocalRootAsync();

    private void OpenLocalFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedLocalFolder is { } f) ViewModel.OpenPath(f.Path);
    }

    private async void UploadFolder_Click(object sender, RoutedEventArgs e) => await ViewModel.UploadSelectedLocalFolderAsync();

    private async void DeleteLocalFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedLocalFolder is not { } f) return;
        if (await Dialogs.ConfirmAsync($"Delete folder \"{f.Name}\"?", $"This permanently deletes {f.Path} and everything in it.", "Delete", danger: true))
        {
            await ViewModel.DeleteSelectedLocalFolderAsync();
        }
    }

    private void LocalMedia_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaEntry { Local: { } v }) ViewModel.OpenPath(v.Path);
    }

    private void CopyLocalPath_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender)?.Local is { } v) ViewModel.CopyText(v.Path);
    }

    private async void UploadLocal_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender)?.Local is { } v) await ViewModel.UploadLocalVideoAsync(v);
    }

    private async void DeleteLocal_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender)?.Local is not { } v) return;
        if (await Dialogs.ConfirmAsync($"Delete \"{v.Name}\"?", "The file is deleted from disk.", "Delete", danger: true))
        {
            await ViewModel.DeleteLocalVideoAsync(v);
        }
    }

    // ── Cloud files ─────────────────────────────────────────────

    private async void RefreshCloud_Click(object sender, RoutedEventArgs e) => await ViewModel.LoadCloudFilesAsync();

    private void CloudMedia_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaEntry { Cloud: { } v }) ViewModel.OpenPath(v.Url);
    }

    private void CopyCloudUrl_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender)?.Cloud is { } v) ViewModel.CopyText(v.Url);
    }

    private async void DeleteCloud_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender)?.Cloud is not { } v) return;
        if (await Dialogs.ConfirmAsync($"Delete \"{v.Name}\" from R2?", v.Key, "Delete", danger: true))
        {
            await ViewModel.DeleteCloudVideoAsync(v);
        }
    }

    private async void DeleteCloudFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCloudFolder is not { } f) return;
        if (await Dialogs.ConfirmAsync($"Delete all objects in \"{f.Name}\"?", $"{f.Count} object(s) will be removed from the bucket.", "Delete", danger: true))
        {
            await ViewModel.DeleteSelectedCloudFolderAsync();
        }
    }

    private async void R2Settings_Click(object sender, RoutedEventArgs e)
    {
        var c = ViewModel.R2Config;
        var account = new TextBox { Header = "Account ID", Text = c.AccountId };
        var key = new TextBox { Header = "Access key ID", Text = c.AccessKeyId };
        var secret = new PasswordBox { Header = "Secret access key", Password = c.SecretAccessKey };
        var endpoint = new TextBox { Header = "Endpoint", Text = c.Endpoint };
        var bucket = new TextBox { Header = "Bucket", Text = c.Bucket };
        var publicBase = new TextBox { Header = "Public base URL", Text = c.PublicBaseUrl };
        var panel = new StackPanel { Spacing = 10, Width = 520 };
        foreach (var el in new FrameworkElement[] { account, key, secret, endpoint, bucket, publicBase }) panel.Children.Add(el);

        if (await Dialogs.Create("Cloudflare R2 settings", new ScrollViewer { Content = panel }, "Save & load").ShowAsync() != ContentDialogResult.Primary) return;
        ViewModel.R2Config = new YtR2Config(
            account.Text.Trim(), key.Text.Trim(), secret.Password.Trim(), endpoint.Text.Trim(), bucket.Text.Trim(), publicBase.Text.Trim());
        await ViewModel.LoadCloudFilesAsync();
    }
}

public static class YtBind
{
    public static Brush StatusBrush(bool available) =>
        available ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    public static InfoBarSeverity Severity(bool isError) => isError ? InfoBarSeverity.Error : InfoBarSeverity.Success;

    public static string Percent(double value) => $"{value:0.0}%";
}
