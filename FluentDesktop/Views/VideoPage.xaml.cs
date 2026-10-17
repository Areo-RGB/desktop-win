using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Core.Video;
using FluentDesktop.Helpers;
using FluentDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace FluentDesktop.Views;

public sealed partial class VideoPage : Page
{
    private bool _scanned;

    public VideoPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            if (_scanned) return;
            _scanned = true;
            await ViewModel.ScanLocalAsync();
        };
    }

    public VideoViewModel ViewModel { get; } = Services.Video;

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var r2 = sender.SelectedItem == sender.Items[1];
        EditPanel.Visibility = r2 ? Visibility.Collapsed : Visibility.Visible;
        R2Panel.Visibility = r2 ? Visibility.Visible : Visibility.Collapsed;
        if (r2 && ViewModel.Objects.Count == 0 && !ViewModel.IsListing) _ = ViewModel.ListR2Async();
    }

    private async void Scan_Click(object sender, RoutedEventArgs e) => await ViewModel.ScanLocalAsync();

    private async void Local_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is LocalVideoItem item) await ViewModel.SelectLocalAsync(item);
    }

    private async void Read_Click(object sender, RoutedEventArgs e) => await ViewModel.LoadAsync();

    private async void File_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) await ViewModel.LoadAsync();
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickVideoAsync() is { } path)
        {
            ViewModel.FilePath = path;
            await ViewModel.LoadAsync();
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e) => ViewModel.OpenInPlayer();

    private void Template_Click(object sender, RoutedEventArgs e) => ClipboardHelper.Copy(VideoChapters.Template, "Chapter template");

    private void Reset_Click(object sender, RoutedEventArgs e) => ViewModel.ResetEditor();

    private async void Write_Click(object sender, RoutedEventArgs e) => await ViewModel.WriteMetadataAsync();

    private async void Encode_Click(object sender, RoutedEventArgs e) => await ViewModel.EncodeAsync();

    private void Cancel_Click(object sender, RoutedEventArgs e) => ViewModel.Cancel();

    // ---- R2 ---------------------------------------------------------------

    private async void List_Click(object sender, RoutedEventArgs e) => await ViewModel.ListR2Async();

    private async void Prefix_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) await ViewModel.ListR2Async();
    }

    private void ConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        ProcessRunner.Open(File.Exists(TrainingBucket.PropertiesPath) ? TrainingBucket.PropertiesPath : AppPaths.ConfigDir);
    }

    private async void BrowseUpload_Click(object sender, RoutedEventArgs e)
    {
        if (await Pickers.PickVideoAsync() is { } path) ViewModel.UploadFile = path;
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        var file = ViewModel.UploadFile.Trim().Trim('"');
        if (file.Length == 0) return;
        var key = ViewModel.UploadKey;
        if (ViewModel.KeyExists(key) && !await Dialogs.ConfirmAsync($"Replace “{key}”?",
                "The file in the bucket is overwritten. Devices download the new version on their next refresh.", "Replace", danger: true))
        {
            return;
        }
        await ViewModel.UploadAsync([(file, key)]);
    }

    private async void Batch_Click(object sender, RoutedEventArgs e)
    {
        var items = ViewModel.BatchItems();
        var replacing = items.Where(i => ViewModel.KeyExists(i.Key)).Select(i => i.Key).ToList();
        if (replacing.Count > 0)
        {
            var title = replacing.Count == 1 ? $"Replace “{replacing[0]}”?" : $"Replace {replacing.Count} files in the bucket?";
            if (!await Dialogs.ConfirmAsync(title,
                    "Existing files with the same name are overwritten. Devices download the new versions on their next refresh.", "Replace", danger: true))
            {
                return;
            }
        }
        await ViewModel.UploadAsync(items);
    }

    private void CancelUpload_Click(object sender, RoutedEventArgs e) => ViewModel.CancelUpload();

    private async void DeleteObject_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not R2ObjectItem item) return;
        if (await Dialogs.ConfirmAsync($"Delete “{item.Key}”?", "The file is removed from the bucket. Devices drop it on their next refresh.", "Delete", danger: true))
        {
            await ViewModel.DeleteObjectAsync(item.Key);
        }
    }
}
