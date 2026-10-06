using FluentDesktop.Helpers;
using FluentDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FluentDesktop.Views;

public sealed partial class SpielerplusPage : Page
{
    public SpielerplusPage()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            await ViewModel.InitAsync();
            Bindings.Update(); // Config is replaced after loading from disk
        };
        Unloaded += async (_, _) => await ViewModel.SaveConfigAsync();
    }

    public SpielerplusViewModel ViewModel { get; } = Services.Spielerplus;

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (LogsPanel == null) return; // raised while InitializeComponent is still building the page
        var index = sender.Items.IndexOf(sender.SelectedItem);
        FixturesPanel.Visibility = Bind.Visible(index == 0);
        IcsPanel.Visibility = Bind.Visible(index == 1);
        LogsPanel.Visibility = Bind.Visible(index == 2);
    }

    private static void Report(bool ok, string success, string failure) =>
        Services.Notifier.Show(ok ? success : failure, ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);

    private async void Fetch_Click(object sender, RoutedEventArgs e)
    {
        var ok = await ViewModel.FetchFixturesAsync();
        Report(ok, ViewModel.StatusMessage, "Fetch failed — see Logs");
    }

    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        var ok = await ViewModel.ProbeExistingAsync();
        Report(ok, "Probe completed", "Probe failed — see Logs");
    }

    private async void Participation_Click(object sender, RoutedEventArgs e)
    {
        var ok = await ViewModel.ExportParticipationAsync();
        Report(ok, ViewModel.StatusMessage, "Participation export failed — see Logs");
    }

    private async void FetchIcs_Click(object sender, RoutedEventArgs e)
    {
        var ok = await ViewModel.FetchIcsAsync();
        Report(ok, ViewModel.StatusMessage, "ICS fetch failed");
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var ok = await ViewModel.RunFullImportAsync();
        Report(ok, "Import completed", "Import failed — see Logs");
    }

    private void OpenImport_Click(object sender, RoutedEventArgs e) => ViewModel.Open("https://www.spielerplus.de/import/create-game");

    private void OpenCsv_Click(object sender, RoutedEventArgs e) => ViewModel.Open(ViewModel.CsvPath);

    private void OpenXlsx_Click(object sender, RoutedEventArgs e) => ViewModel.Open(ViewModel.XlsxPath);

    private void OpenFolder_Click(object sender, RoutedEventArgs e) => ViewModel.Open(ViewModel.Config.OutputDir);

    private void CopyCsv_Click(object sender, RoutedEventArgs e) => ClipboardHelper.Copy(ViewModel.CsvPath, "CSV path");

    private void CopyXlsx_Click(object sender, RoutedEventArgs e) => ClipboardHelper.Copy(ViewModel.XlsxPath, "XLSX path");

    private void CopyLogs_Click(object sender, RoutedEventArgs e) => ClipboardHelper.Copy(ViewModel.Logs.Text, "Logs");

    private void ClearLogs_Click(object sender, RoutedEventArgs e) => ViewModel.ClearLogs();
}
