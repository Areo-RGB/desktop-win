using CommunityToolkit.Mvvm.ComponentModel;
using FluentDesktop.Core.DesktopCommander;
using FluentDesktop.Helpers;

namespace FluentDesktop.ViewModels;

public sealed partial class DesktopCommanderViewModel : ViewModelBase, IDisposable
{
    private readonly DesktopCommanderService _service = new();
    private readonly DesktopCommanderSettingsStore _store = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _saveTimer;
    private DesktopCommanderSettings _settings = new();
    private long _renderedLogs;

    public DesktopCommanderViewModel()
    {
        _saveTimer = Dispatcher.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(300);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => _ = _store.SaveAsync(_settings);
        _service.StateChanged += s => OnUI(() => Apply(s));
        _service.ReauthorizingChanged += () => OnUI(() =>
        {
            IsReauthorizing = _service.IsReauthorizing;
            Refresh();
        });
    }

    public LogBuffer Logs { get; } = new(DesktopCommanderState.MaxLogs);

    [ObservableProperty] private bool _isInitialized;
    [ObservableProperty] private bool _isReauthorizing;
    [ObservableProperty] private DesktopCommanderStatus _status;
    [ObservableProperty] private string _statusMessage = "Ready to connect";
    [ObservableProperty] private string? _authUrl;
    [ObservableProperty] private string? _authCode;
    [ObservableProperty] private string? _deviceId;
    [ObservableProperty] private bool _processActive;
    [ObservableProperty] private string? _badge;
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canReauthorize;
    [ObservableProperty] private bool _isWorking;
    [ObservableProperty] private string _logCount = "0/500";

    public bool AutoVerify
    {
        get => _settings.AutoVerify;
        set
        {
            if (_settings.AutoVerify == value) return;
            _settings = (_settings with { AutoVerify = value }).Normalized();
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public string ChromeProfile
    {
        get => _settings.ChromeProfile;
        set
        {
            _settings = _settings with { ChromeProfile = value };
            ScheduleSave();
        }
    }

    public double VerificationTimeout
    {
        get => _settings.VerificationTimeoutSeconds;
        set
        {
            if (double.IsNaN(value)) return;
            _settings = (_settings with { VerificationTimeoutSeconds = (int)value }).Normalized();
            ScheduleSave();
        }
    }

    public string SettingsHint => ProcessActive
        ? "Changes apply the next time Desktop Commander starts or re-authorizes."
        : "Requires npx; automatic verification also requires agent-browser.";

    public async Task InitAsync()
    {
        if (IsInitialized) return;
        _settings = await _store.LoadAsync();
        IsInitialized = true;
        OnPropertyChanged(nameof(AutoVerify));
        OnPropertyChanged(nameof(ChromeProfile));
        OnPropertyChanged(nameof(VerificationTimeout));
        Refresh();
    }

    public Task StartAsync() => _service.StartAsync(_settings);

    public Task StopAsync() => _service.StopAsync();

    public Task ReauthorizeAsync() => _service.LogoutAndRestartAsync(_settings);

    public void ClearLogs() => _service.ClearLogs();

    public void OpenAuthorizationUrl()
    {
        try { _service.OpenAuthorizationUrl(); }
        catch (Exception e) { Services.Notifier.Error($"Could not open verification link: {e.Message}"); }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void Apply(DesktopCommanderState s)
    {
        Status = s.Status;
        StatusMessage = s.StatusMessage;
        AuthUrl = s.AuthUrl;
        AuthCode = s.AuthCode;
        DeviceId = s.DeviceId;
        ProcessActive = s.ProcessActive;
        Badge = s.Badge;

        // Append only the new tail instead of rebuilding the list per line.
        var fresh = s.TotalLogged - _renderedLogs;
        if (fresh < 0 || fresh >= s.Logs.Count)
        {
            Logs.ReplaceAll(s.Logs);
        }
        else
        {
            foreach (var line in s.Logs.Skip(s.Logs.Count - (int)fresh)) Logs.Append(line);
        }
        _renderedLogs = s.TotalLogged;
        LogCount = $"{s.Logs.Count}/{DesktopCommanderState.MaxLogs}";
        Refresh();
    }

    private void Refresh()
    {
        CanStart = IsInitialized && !ProcessActive && Status != DesktopCommanderStatus.Starting && !IsReauthorizing;
        CanReauthorize = IsInitialized && !IsReauthorizing;
        IsWorking = Status is DesktopCommanderStatus.Starting or DesktopCommanderStatus.Authorizing or DesktopCommanderStatus.Verifying;
        OnPropertyChanged(nameof(SettingsHint));
    }

    public void Dispose()
    {
        _saveTimer.Stop();
        _ = _store.SaveAsync(_settings);
        _service.Dispose();
    }
}
