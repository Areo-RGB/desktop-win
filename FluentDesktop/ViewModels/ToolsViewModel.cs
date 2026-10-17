using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentDesktop.Core.Tools;
using FluentDesktop.Helpers;

namespace FluentDesktop.ViewModels;

public sealed record InfoRow(string Key, string Value);

/// <summary>Greeter + system info and CLI tool versions (DesktopCC's Tools page).</summary>
public sealed partial class ToolsViewModel : ViewModelBase
{
    private bool _probed;

    public ObservableCollection<InfoRow> Rows { get; } = new(EnvironmentProbe.SystemInfo().Select(r => new InfoRow(r.Key, r.Value)));

    [ObservableProperty]
    private string _name = "";

    [ObservableProperty]
    private string _greeting = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NotProbing))]
    private bool _isProbing;

    public bool NotProbing => !IsProbing;

    public void Greet()
    {
        var n = Name.Trim();
        Greeting = n.Length == 0 ? "Hello! Type your name above." : $"Hello {n}, welcome to Fluent Desktop!";
    }

    public async Task EnsureProbedAsync()
    {
        if (_probed) return;
        _probed = true;
        await ProbeAsync();
    }

    public async Task ProbeAsync()
    {
        if (IsProbing) return;
        IsProbing = true;
        try
        {
            foreach (var (key, value) in await EnvironmentProbe.ToolVersionsAsync())
            {
                var ix = Rows.ToList().FindIndex(r => r.Key == key);
                if (ix >= 0) Rows[ix] = new InfoRow(key, value);
                else Rows.Add(new InfoRow(key, value));
            }
        }
        finally
        {
            IsProbing = false;
        }
    }
}
