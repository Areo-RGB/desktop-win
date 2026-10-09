using CommunityToolkit.Mvvm.ComponentModel;
using FluentDesktop.Core.Hub;
using FluentDesktop.Helpers;

namespace FluentDesktop.ViewModels;

public sealed partial class HubViewModel : ViewModelBase, IDisposable
{
    private readonly HubService _service = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private bool _started;
    private SshMcpSettings _sshSettings = new();

    public HubViewModel()
    {
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(5);
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isProcessing;
    [ObservableProperty] private HubStatus? _status;
    [ObservableProperty] private string _logs = "";
    [ObservableProperty] private string _portLog = "";
    [ObservableProperty] private string _sshHost = "127.0.0.1";
    [ObservableProperty] private double _sshPort = 22;
    [ObservableProperty] private string _sshUsername = Environment.UserName;
    [ObservableProperty] private string _sshKeyPath = "";
    [ObservableProperty] private string _sshPassword = "";

    /// <summary>0 = MCPHub log, 1 = tunnel log, 2 = port kill log.</summary>
    [ObservableProperty] private int _logIndex;

    public string DashboardUrl => Status?.DashboardUrl ?? $"http://localhost:{_service.Port}";

    public string PortKillLogPath => HubService.TempLogPath("portkill.log");

    public string VisibleLog => LogIndex == 2
        ? (PortLog.Length == 0 ? "No port action yet. Use Inspect or Kill on Port above." : PortLog)
        : (Logs.Length == 0 ? "No log data available." : Logs);

    partial void OnLogsChanged(string value) => OnPropertyChanged(nameof(VisibleLog));
    partial void OnPortLogChanged(string value) => OnPropertyChanged(nameof(VisibleLog));
    partial void OnStatusChanged(HubStatus? value) => OnPropertyChanged(nameof(DashboardUrl));

    partial void OnLogIndexChanged(int value)
    {
        OnPropertyChanged(nameof(VisibleLog));
        if (value == 0) _ = LoadMcphubLogsAsync();
        else if (value == 1) _ = LoadTunnelLogsAsync();
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        _ = InitializeAsync();
        _timer.Start();
    }

    private async Task InitializeAsync()
    {
        try
        {
            _sshSettings = await _service.SshSettings.LoadAsync();
            SshHost = _sshSettings.Host;
            SshPort = _sshSettings.Port;
            SshUsername = _sshSettings.Username;
            SshKeyPath = _sshSettings.KeyPath;
            SshPassword = _sshSettings.Password;
        }
        catch (Exception e)
        {
            Services.Notifier.Error($"SSH settings failed: {e.Message}");
        }
        await RefreshAsync();
    }

    public Task SaveSshSettingsAsync() => RunActionAsync(async () =>
    {
        if (!double.IsFinite(SshPort) || SshPort != Math.Truncate(SshPort) || SshPort is < 1 or > 65535)
            throw new ArgumentException("Enter an SSH port between 1 and 65535.");
        var settings = _sshSettings with
        {
            Host = SshHost.Trim(), Port = (int)SshPort, Username = SshUsername.Trim(),
            KeyPath = SshKeyPath.Trim(), Password = SshPassword
        };
        await _service.SshSettings.SaveAsync(settings);
        _sshSettings = settings;
        Services.Notifier.Show("SSH MCP settings saved. Restart MCPHub to apply the connection settings.");
    });

    public async Task RefreshAsync()
    {
        try
        {
            Status = await _service.GetStatusAsync();
            if (LogIndex == 0) Logs = await HubService.ReadMcphubLogsAsync();
        }
        catch (Exception e)
        {
            Services.Notifier.Error($"Hub status failed: {e.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        IsProcessing = true;
        try
        {
            await action();
        }
        catch (Exception e)
        {
            Services.Notifier.Error(e.Message);
        }
        finally
        {
            await RefreshAsync();
            IsProcessing = false;
        }
    }

    public Task StartSshAsync() => RunActionAsync(_service.StartSshDaemonAsync);
    public Task ToggleMcphubAsync() => RunActionAsync(Status?.IsBackendRunning == true ? _service.StopMcphubAsync : _service.StartMcphubAsync);
    public Task ToggleTunnelAsync() => RunActionAsync(Status?.IsTunnelRunning == true ? _service.StopTunnelAsync : _service.StartTunnelAsync);
    public Task StartAllAsync() => RunActionAsync(_service.StartAllAsync);
    public Task StopAllAsync() => RunActionAsync(_service.StopAllAsync);

    public Task InspectPortAsync(int port) => RunActionAsync(async () =>
    {
        LogIndex = 2;
        PortLog = await _service.InspectPortAsync(port);
    });

    public Task KillPortAsync(int port) => RunActionAsync(async () =>
    {
        LogIndex = 2;
        PortLog = await _service.KillProcessOnPortAsync(port);
        Services.Notifier.Show($"Kill on :{port} finished — see Port Kill log.");
    });

    public async Task TestSshAsync()
    {
        IsProcessing = true;
        try
        {
            var settings = await _service.SshSettings.LoadAsync();
            Services.Notifier.Show(await HubService.TestSshConnectionAsync(settings.Port, settings.Host));
            await RefreshAsync();
        }
        catch (Exception e)
        {
            Services.Notifier.Error(e.Message);
        }
        finally
        {
            IsProcessing = false;
        }
    }

    public async Task LoadMcphubLogsAsync() => Logs = await HubService.ReadMcphubLogsAsync();

    public async Task LoadTunnelLogsAsync() => Logs = await HubService.ReadTunnelLogsAsync();

    public void Dispose() => _timer.Stop();
}
