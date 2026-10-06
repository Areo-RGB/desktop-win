using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentDesktop.Core.Dashboard;
using FluentDesktop.Helpers;
using Microsoft.UI.Xaml.Media;

namespace FluentDesktop.ViewModels;

public sealed partial class DashboardCommandItem : ObservableObject
{
    public DashboardCommandItem(DashboardCommand command) => Command = command;

    public DashboardCommand Command { get; }
    public string Title => Command.Title;
    public string Description => Command.Description;
    public string CommandString => Command.CommandString;
    public ImageSource? Icon => Images.FromAsset(Command.BrandIcon);
    public bool HasIcon => Command.BrandIcon != null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private bool _isRunning;

    [ObservableProperty]
    private bool _isSelected;

    public bool CanRun => !IsRunning;
}

public sealed partial class DashboardViewModel : ViewModelBase, IDisposable
{
    private readonly DashboardCommandStore _store = new();
    private List<DashboardCommand> _commands = [];
    private CommandSession? _session;
    private bool _loaded;

    public ObservableCollection<DashboardCommandItem> Commands { get; } = [];
    public LogBuffer Logs { get; } = new(500);

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSession), nameof(NoSession))]
    private string? _sessionTitle;

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>0 running, 1 completed, 2 failed.</summary>
    [ObservableProperty]
    private int _statusKind;

    public bool HasSession => SessionTitle != null;
    public bool NoSession => SessionTitle == null;

    public async Task EnsureLoadedAsync()
    {
        if (_loaded) return;
        _loaded = true;
        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            _commands = await _store.LoadAsync();
            Rebuild();
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Rebuild()
    {
        Commands.Clear();
        foreach (var c in _commands)
        {
            Commands.Add(new DashboardCommandItem(c)
            {
                IsRunning = IsRunning && _session?.Command.Id == c.Id,
                IsSelected = !IsRunning && _session?.Command.Id == c.Id,
            });
        }
    }

    public async Task RunAsync(DashboardCommand command)
    {
        if (IsRunning) return;
        var session = new CommandSession(command);
        _session = session;
        Logs.Clear();
        SessionTitle = command.Title;
        IsRunning = true;
        StatusKind = 0;
        StatusText = "Running";
        Rebuild();

        session.Line += line => OnUI(() => { if (ReferenceEquals(_session, session)) Logs.Append(line); });
        session.Completed += code => OnUI(() =>
        {
            if (!ReferenceEquals(_session, session)) return;
            IsRunning = false;
            StatusKind = code == 0 ? 1 : 2;
            StatusText = code == 0 ? "Completed" : $"Failed (code {code})";
            Rebuild();
        });
        await Task.Run(session.RunAsync);
    }

    public void Cancel() => _session?.Cancel();

    public void ClearConsole()
    {
        if (IsRunning) return;
        _session = null;
        SessionTitle = null;
        Logs.Clear();
        Rebuild();
    }

    public async Task AddAsync(DashboardCommand command)
    {
        _commands.Add(command);
        await _store.SaveAsync(_commands);
        Rebuild();
    }

    public async Task UpdateAsync(DashboardCommand command)
    {
        var idx = _commands.FindIndex(c => c.Id == command.Id);
        if (idx < 0) return;
        _commands[idx] = command;
        await _store.SaveAsync(_commands);
        Rebuild();
    }

    public async Task DeleteAsync(string id)
    {
        _commands.RemoveAll(c => c.Id == id);
        await _store.SaveAsync(_commands);
        Rebuild();
    }

    public void Dispose() => _session?.Cancel();
}
