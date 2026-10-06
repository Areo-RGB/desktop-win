using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentDesktop.Core.DeviceMirror;
using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Helpers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace FluentDesktop.ViewModels;

public sealed partial class DeviceItem(AdbDevice device) : ObservableObject
{
    public AdbDevice Device { get; } = device;
    public string Serial => Device.Serial;
    public string DisplayName => Device.DisplayName;
    public bool IsOnline => Device.IsOnline;
    public bool IsOffline => !Device.IsOnline;
    public string State => Device.State.ToUpperInvariant();
    public Brush StateBrush => Device.IsOnline ? Bind.Success : Bind.Danger;
    public string SerialText => $"Serial: {Device.Serial}";
    public string Details => string.Join("  •  ", new[]
    {
        Device.AndroidVersion != null ? $"Android {Device.AndroidVersion} (API {Device.SdkVersion ?? "?"})" : null,
        Device.ScreenWidth != null && Device.ScreenHeight != null ? $"{Device.ScreenWidth}x{Device.ScreenHeight} ({Device.ScreenDensity?.ToString() ?? "?"} dpi)" : null,
        Device.IpAddress != null ? $"IP: {Device.IpAddress}" : null,
    }.Where(s => s != null));
    public bool HasBattery => Device.BatteryLevel != null;
    public string Battery => $"{Device.BatteryLevel}%";
    public string BatteryGlyph => Device.IsCharging ? "" : "";
    public string ComboLabel => $"{Device.DisplayName} ({Device.Serial}) • {Device.State}";

    public string OfflineHint => Device.State switch
    {
        "unauthorized" => "Device is unauthorized. Unlock the phone and accept the “Allow USB debugging” prompt, then hit Refresh.",
        "offline" => "Device is offline. Reconnect USB / re-run “adb reconnect”, then hit Refresh.",
        _ => $"Device is in state “{Device.State}”. Only “device” state can mirror or capture.",
    };

    public ObservableCollection<ScreenshotItem> Screenshots { get; } = [];

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private ScreenshotItem? _activeScreenshot;

    public bool HasScreenshots => Screenshots.Count > 0;

    public void NotifyScreenshots() => OnPropertyChanged(nameof(HasScreenshots));
}

public sealed record ScreenshotItem(string Id, string Serial, byte[] Bytes, DateTime Timestamp, string SavedPath, BitmapImage Thumbnail, BitmapImage Full)
{
    public string Time => Timestamp.ToString("HH:mm:ss");
    public string Caption => $"Captured at {Time} ({Format.Bytes(Bytes.LongLength)})";
}

public sealed partial class RunningScrcpy(Process process, string deviceName) : ObservableObject
{
    public Process Process { get; } = process;
    public int Pid { get; } = process.Id;
    public string Title => $"PID: {Pid} • Device: {deviceName}";
    public string Started { get; } = $"Started: {DateTime.Now:HH:mm:ss}";
}

public sealed partial class ScrcpyViewModel : ViewModelBase, IDisposable
{
    public ObservableCollection<DeviceItem> Devices { get; } = [];
    public ObservableCollection<RunningScrcpy> Running { get; } = [];
    public ScrcpyConfig Config { get; } = new();

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private bool _hasScanned;
    [ObservableProperty] private string? _scanError;
    [ObservableProperty] private bool _checkingBinaries = true;
    [ObservableProperty] private BinaryStatus? _binaries;
    [ObservableProperty] private DeviceItem? _selectedDevice;

    public string Subtitle => CheckingBinaries
        ? "Checking ADB / scrcpy…"
        : $"{Devices.Count} device(s) detected via ADB{(Binaries?.AdbVersion is { } v ? $" • {v}" : "")}";

    public bool ShowBinaryBanner => !CheckingBinaries && Binaries is { } b && (!b.AdbOk || !b.ScrcpyOk);

    public string BinaryBannerTitle => Binaries is { } b
        ? $"{string.Join(" & ", new[] { b.AdbOk ? null : "ADB", b.ScrcpyOk ? null : "scrcpy" }.Where(x => x != null))} not found"
        : "";

    public string BinaryBannerText => Binaries is { } b
        ? string.Join("\n", new[]
        {
            b.AdbOk ? null : $"ADB: {b.AdbPath}\nInstall it, then hit Refresh.",
            b.ScrcpyOk ? null : $"scrcpy: {b.ScrcpyPath}\nInstall it to enable mirroring (screenshots still work with ADB alone).",
        }.Where(x => x != null))
        : "";

    public string InstallInstructions => AdbService.InstallInstructions;

    public bool NoDevices => Devices.Count == 0 && !IsScanning;
    public string EmptyTitle => HasScanned ? "No Android Devices Found" : "Device Scan Not Started";
    public string EmptyText => HasScanned
        ? "Connect an Android device with USB Debugging enabled, or connect via Wi-Fi ADB."
        : "Hit Refresh to scan for devices via ADB. Nothing runs until you do.";

    public bool CanLaunchSelected => SelectedDevice?.IsOnline == true;
    public bool HasRunning => Running.Count > 0;

    public string ScrcpyPathText => Binaries?.ScrcpyPath ?? "checking…";
    public string ScrcpyVersionText => Binaries?.ScrcpyVersion ?? "not detected";
    public string AdbPathText => Binaries?.AdbPath ?? "checking…";
    public string AdbVersionText => Binaries?.AdbVersion ?? "not detected";

    partial void OnCheckingBinariesChanged(bool value) => RaiseDerived();
    partial void OnBinariesChanged(BinaryStatus? value) => RaiseDerived();
    partial void OnIsScanningChanged(bool value) => RaiseDerived();
    partial void OnHasScannedChanged(bool value) => RaiseDerived();

    partial void OnSelectedDeviceChanged(DeviceItem? value)
    {
        foreach (var d in Devices) d.IsSelected = ReferenceEquals(d, value);
        OnPropertyChanged(nameof(CanLaunchSelected));
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(ShowBinaryBanner));
        OnPropertyChanged(nameof(BinaryBannerTitle));
        OnPropertyChanged(nameof(BinaryBannerText));
        OnPropertyChanged(nameof(NoDevices));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyText));
        OnPropertyChanged(nameof(ScrcpyPathText));
        OnPropertyChanged(nameof(ScrcpyVersionText));
        OnPropertyChanged(nameof(AdbPathText));
        OnPropertyChanged(nameof(AdbVersionText));
    }

    public async Task CheckBinariesAsync()
    {
        CheckingBinaries = true;
        try { Binaries = await Task.Run(AdbService.GetBinaryStatusAsync); }
        finally { CheckingBinaries = false; }
    }

    public async Task RefreshDevicesAsync()
    {
        if (IsScanning) return;
        IsScanning = true;
        HasScanned = true;
        ScanError = null;
        _ = CheckBinariesAsync();
        try
        {
            var devices = await Task.Run(AdbService.GetDevicesAsync);
            var previous = SelectedDevice?.Serial;
            var oldShots = Devices.ToDictionary(d => d.Serial, d => d.Screenshots.ToList());
            Devices.Clear();
            foreach (var d in devices)
            {
                var item = new DeviceItem(d);
                if (oldShots.TryGetValue(d.Serial, out var shots))
                {
                    foreach (var s in shots) item.Screenshots.Add(s);
                    item.ActiveScreenshot = shots.FirstOrDefault();
                }
                Devices.Add(item);
            }
            SelectedDevice = Devices.FirstOrDefault(d => d.Serial == previous)
                ?? Devices.FirstOrDefault(d => d.IsOnline)
                ?? Devices.FirstOrDefault();
            if (devices.Count == 0 && AdbService.LastError is { } err)
            {
                ScanError = err;
                Services.Notifier.Error($"ADB scan failed: {err}");
            }
        }
        catch (Exception e)
        {
            ScanError = e.Message;
        }
        finally
        {
            IsScanning = false;
        }
    }

    public async Task CaptureAsync(DeviceItem device)
    {
        if (!device.IsOnline || device.IsCapturing) return;
        SelectedDevice = device;
        device.IsCapturing = true;
        try
        {
            var bytes = await Task.Run(() => AdbService.CaptureScreenAsync(device.Serial));
            if (bytes == null)
            {
                Services.Notifier.Error(AdbService.LastError ?? "Failed to capture screenshot. Make sure device is unlocked.");
                return;
            }
            var now = DateTime.Now;
            Directory.CreateDirectory(AdbService.ScreenshotsDir);
            var path = Path.Combine(AdbService.ScreenshotsDir, $"screenshot_{SafeName(device.Serial)}_{DateTimeOffset.Now.ToUnixTimeMilliseconds()}.png");
            await File.WriteAllBytesAsync(path, bytes);
            if (!await ClipboardHelper.SetImageAsync(bytes, path)) ClipboardHelper.SetText(path);

            var shot = new ScreenshotItem(
                $"{device.Serial}_{now.Ticks}", device.Serial, bytes, now, path,
                await Images.FromBytesAsync(bytes, 150),
                await Images.FromBytesAsync(bytes));
            device.Screenshots.Insert(0, shot);
            device.ActiveScreenshot = shot;
            device.NotifyScreenshots();
            Services.Notifier.Success("Saved to Screenshots & copied to clipboard!", "Open Image", () => ProcessRunner.Open(path));
        }
        finally
        {
            device.IsCapturing = false;
        }
    }

    private static string SafeName(string serial) => string.Concat(serial.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));

    public void DeleteScreenshot(DeviceItem device, ScreenshotItem shot)
    {
        device.Screenshots.Remove(shot);
        if (ReferenceEquals(device.ActiveScreenshot, shot)) device.ActiveScreenshot = device.Screenshots.FirstOrDefault();
        device.NotifyScreenshots();
    }

    public void ClearScreenshots(DeviceItem device)
    {
        device.Screenshots.Clear();
        device.ActiveScreenshot = null;
        device.NotifyScreenshots();
    }

    public async Task LaunchAsync(DeviceItem? device = null)
    {
        device ??= SelectedDevice;
        if (device is not { IsOnline: true })
        {
            Services.Notifier.Error("Please select an online device first.");
            return;
        }
        SelectedDevice = device;
        if (Binaries is { ScrcpyOk: false })
        {
            await CheckBinariesAsync();
            if (Binaries is { ScrcpyOk: false })
            {
                Services.Notifier.Error($"scrcpy not found ({Binaries.ScrcpyPath}). Install it first:\n{AdbService.InstallInstructions}");
                return;
            }
        }

        string? recordPath = null;
        if (Config.RecordVideo)
        {
            Directory.CreateDirectory(AdbService.VideosDir);
            recordPath = Path.Combine(AdbService.VideosDir, $"scrcpy_{SafeName(device.Serial)}_{DateTimeOffset.Now.ToUnixTimeMilliseconds()}.mp4");
        }

        var process = AdbService.LaunchScrcpy(device.Serial, Config, recordPath);
        if (process == null)
        {
            Services.Notifier.Error(AdbService.LastError ?? "Failed to launch scrcpy process. Check settings & logs.");
            return;
        }
        var info = new RunningScrcpy(process, device.DisplayName);
        Running.Add(info);
        OnPropertyChanged(nameof(HasRunning));
        process.Exited += (_, _) => OnUI(() =>
        {
            Running.Remove(info);
            OnPropertyChanged(nameof(HasRunning));
        });
        Services.Notifier.Show($"Launched SCRCPY mirror for {device.DisplayName} (PID: {process.Id})");
    }

    public void Kill(RunningScrcpy info)
    {
        try { info.Process.Kill(entireProcessTree: true); } catch { /* already exited */ }
        Running.Remove(info);
        OnPropertyChanged(nameof(HasRunning));
        Services.Notifier.Show($"Terminated SCRCPY process {info.Pid}");
    }

    public async Task EnableTcpIpAsync(DeviceItem device)
    {
        var ok = await Task.Run(() => AdbService.EnableTcpIpAsync(device.Serial));
        Services.Notifier.Show(ok ? $"Wireless ADB enabled on port 5555 for {device.DisplayName}" : "Failed to enable Wireless ADB");
    }

    public async Task RebootAsync(DeviceItem device, string mode)
    {
        await Task.Run(() => AdbService.RebootAsync(device.Serial, mode));
        await RefreshDevicesAsync();
    }

    public async Task ConnectWifiAsync(string ip, int port)
    {
        var result = await Task.Run(() => AdbService.ConnectWifiAsync(ip, port));
        Services.Notifier.Show(result ?? $"Connecting to {ip}:{port}...");
        await RefreshDevicesAsync();
    }

    public void Dispose()
    {
        foreach (var p in Running)
        {
            try { p.Process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }
    }
}
