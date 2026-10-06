using System.Runtime.CompilerServices;
using FluentDesktop.Core.DeviceMirror;
using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Helpers;
using FluentDesktop.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace FluentDesktop.Views;

public sealed partial class ScrcpyPage : Page
{
    private static readonly (string Label, int Value)[] MaxSizes =
        [("Original (No Scaling)", 0), ("1080p+ (1920px max)", 1920), ("1080p (1080px max)", 1080), ("720p (720px max)", 720), ("480p (480px max)", 480)];

    private static readonly (string Label, int Value)[] Fps =
        [("Unlimited", 0), ("30 FPS (Power Saver)", 30), ("60 FPS (Standard)", 60), ("90 FPS", 90), ("120 FPS (Smooth Gaming)", 120)];

    private static readonly (string Label, string Value)[] BitRates =
        [("2 Mbps (Very Low)", "2M"), ("4 Mbps (Low)", "4M"), ("8 Mbps (Standard)", "8M"), ("16 Mbps (High)", "16M"), ("24 Mbps (Ultra)", "24M")];

    private static readonly (string Label, string Value)[] Codecs =
        [("H.264 / AVC (Most Compatible)", "h264"), ("H.265 / HEVC (High Efficiency)", "h265"), ("AV1", "av1")];

    private readonly ConditionalWeakTable<DeviceItem, ScrollViewer> _zoomViewers = new();

    public ScrcpyPage()
    {
        InitializeComponent();
        var cfg = ViewModel.Config;
        Fill(MaxSizeBox, MaxSizes.Select(m => m.Label), Array.FindIndex(MaxSizes, m => m.Value == cfg.MaxSize));
        Fill(MaxFpsBox, Fps.Select(m => m.Label), Array.FindIndex(Fps, m => m.Value == cfg.MaxFps));
        Fill(BitRateBox, BitRates.Select(m => m.Label), Array.FindIndex(BitRates, m => m.Value == cfg.BitRate));
        Fill(CodecBox, Codecs.Select(m => m.Label), Array.FindIndex(Codecs, m => m.Value == cfg.VideoCodec));
        // Lazy: no ADB call until Refresh; only probe whether the binaries exist.
        Loaded += async (_, _) =>
        {
            if (ViewModel.Binaries == null) await ViewModel.CheckBinariesAsync();
        };
    }

    public ScrcpyViewModel ViewModel { get; } = Services.Scrcpy;

    private static void Fill(ComboBox box, IEnumerable<string> labels, int selected)
    {
        box.ItemsSource = labels.ToList();
        box.SelectedIndex = Math.Max(0, selected);
    }

    private void MaxSize_Changed(object sender, SelectionChangedEventArgs e) => ViewModel.Config.MaxSize = MaxSizes[Math.Max(0, MaxSizeBox.SelectedIndex)].Value;
    private void MaxFps_Changed(object sender, SelectionChangedEventArgs e) => ViewModel.Config.MaxFps = Fps[Math.Max(0, MaxFpsBox.SelectedIndex)].Value;
    private void BitRate_Changed(object sender, SelectionChangedEventArgs e) => ViewModel.Config.BitRate = BitRates[Math.Max(0, BitRateBox.SelectedIndex)].Value;
    private void Codec_Changed(object sender, SelectionChangedEventArgs e) => ViewModel.Config.VideoCodec = Codecs[Math.Max(0, CodecBox.SelectedIndex)].Value;

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (SettingsPanel == null) return; // raised while InitializeComponent is still building the page
        var index = sender.Items.IndexOf(sender.SelectedItem);
        DevicesPanel.Visibility = Bind.Visible(index == 0);
        SettingsPanel.Visibility = Bind.Visible(index == 1);
    }

    private static T? TagOf<T>(object sender) where T : class => (sender as FrameworkElement)?.Tag as T;

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ViewModel.RefreshDevicesAsync();

    private async void Recheck_Click(object sender, RoutedEventArgs e) => await ViewModel.CheckBinariesAsync();

    private void CopyInstall_Click(object sender, RoutedEventArgs e) => ClipboardHelper.Copy(AdbService.InstallInstructions, "Install command");

    private void OpenDownload_Click(object sender, RoutedEventArgs e) =>
        ProcessRunner.Open(ViewModel.Binaries?.ScrcpyOk == true ? AdbService.AdbDownloadUrl : AdbService.ScrcpyDownloadUrl);

    private void ScreenshotsFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AdbService.ScreenshotsDir);
        ProcessRunner.Open(AdbService.ScreenshotsDir);
    }

    private void Device_Tapped(object sender, TappedRoutedEventArgs e)
    {
        if (TagOf<DeviceItem>(sender) is { } d) ViewModel.SelectedDevice = d;
    }

    private async void Launch_Click(object sender, RoutedEventArgs e) => await ViewModel.LaunchAsync(TagOf<DeviceItem>(sender));

    private async void LaunchSelected_Click(object sender, RoutedEventArgs e) => await ViewModel.LaunchAsync();

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<DeviceItem>(sender) is { } d) await ViewModel.CaptureAsync(d);
    }

    private async void TcpIp_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<DeviceItem>(sender) is { } d) await ViewModel.EnableTcpIpAsync(d);
    }

    private async void Reboot_Click(object sender, RoutedEventArgs e) => await RebootAsync(sender, "normal");

    private async void RebootRecovery_Click(object sender, RoutedEventArgs e) => await RebootAsync(sender, "recovery");

    private async void RebootBootloader_Click(object sender, RoutedEventArgs e) => await RebootAsync(sender, "bootloader");

    private async Task RebootAsync(object sender, string mode)
    {
        if (TagOf<DeviceItem>(sender) is { } d) await ViewModel.RebootAsync(d, mode);
    }

    private void Kill_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<RunningScrcpy>(sender) is { } p) ViewModel.Kill(p);
    }

    private void ClearShots_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<DeviceItem>(sender) is { } d) ViewModel.ClearScreenshots(d);
    }

    private async void CopyShot_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<DeviceItem>(sender)?.ActiveScreenshot is not { } shot) return;
        if (!await ClipboardHelper.SetImageAsync(shot.Bytes, shot.SavedPath)) ClipboardHelper.SetText(shot.SavedPath);
        Services.Notifier.Success("Screenshot copied to clipboard!");
    }

    private void OpenShot_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<DeviceItem>(sender)?.ActiveScreenshot is { } shot) ProcessRunner.Open(shot.SavedPath);
    }

    private void DeleteShot_Click(object sender, RoutedEventArgs e)
    {
        if (TagOf<DeviceItem>(sender) is { ActiveScreenshot: { } shot } d) ViewModel.DeleteScreenshot(d, shot);
    }

    private void Zoom_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer viewer && viewer.Tag is DeviceItem d) _zoomViewers.AddOrUpdate(d, viewer);
    }

    private void Zoom(object sender, Func<float, float> change)
    {
        if (TagOf<DeviceItem>(sender) is { } d && _zoomViewers.TryGetValue(d, out var viewer))
        {
            viewer.ChangeView(null, null, Math.Clamp(change(viewer.ZoomFactor), viewer.MinZoomFactor, viewer.MaxZoomFactor));
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Zoom(sender, z => z * 1.25f);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Zoom(sender, z => z * 0.8f);

    private void ZoomReset_Click(object sender, RoutedEventArgs e) => Zoom(sender, _ => 1f);

    private async void Wifi_Click(object sender, RoutedEventArgs e)
    {
        var ip = new TextBox { Header = "Device IP Address", PlaceholderText = "e.g. 192.168.1.100", Text = ViewModel.SelectedDevice?.Device.IpAddress ?? "192.168.1." };
        var port = new NumberBox { Header = "Port (Default: 5555)", Value = 5555, Minimum = 1, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(ip);
        panel.Children.Add(port);
        if (await Dialogs.Create("Connect to Device over Wi-Fi", panel, "Connect").ShowAsync() != ContentDialogResult.Primary) return;
        await ViewModel.ConnectWifiAsync(ip.Text.Trim(), double.IsNaN(port.Value) ? 5555 : (int)port.Value);
    }
}

public static class ScrcpyBind
{
    public static bool HasText(string? value) => !string.IsNullOrWhiteSpace(value);

    public static Brush SelectionBrush(bool selected) =>
        (Brush)Application.Current.Resources[selected ? "AccentFillColorDefaultBrush" : "CardStrokeColorDefaultBrush"];

    public static Thickness SelectionThickness(bool selected) => new(selected ? 2 : 1);
}
