using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Core.SpielerPlus;
using FluentDesktop.Helpers;
using Microsoft.UI.Xaml.Media;

namespace FluentDesktop.ViewModels;

public sealed class FixtureRow(SpielerplusFixture fixture, string homeMarker)
{
    public string Date => fixture.DateIso;
    public string Time => fixture.TimeText.Length == 0 ? "TBD" : fixture.TimeText;
    public bool IsHome { get; } = fixture.IsHome(homeMarker);
    public string Type => IsHome ? "HOME" : "AWAY";
    public Brush TypeBrush => IsHome ? Bind.Success : Bind.Neutral;
    public string Matchup => fixture.MatchupLabel;
    public string Competition => fixture.CompetitionLabel;
    public string Venue => fixture.Venue;
}

public sealed partial class SpielerplusViewModel : ViewModelBase
{
    private readonly SpielerplusService _service = new();
    private bool _initialized;

    public SpielerplusConfig Config { get; private set; } = new();
    public ObservableCollection<FixtureRow> Fixtures { get; } = [];
    public LogBuffer Logs { get; } = new(800);

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private int _sourceIndex;
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private string _csvPath = "";
    [ObservableProperty] private string _xlsxPath = "";
    [ObservableProperty] private bool _scriptsAvailable;
    [ObservableProperty] private bool _pythonAvailable;
    [ObservableProperty] private string _pythonLabel = "Python 3";
    [ObservableProperty] private bool _dryRun = true;
    [ObservableProperty] private bool _headed;

    public bool IsFussball => SourceIndex == 0;
    public bool IsDfbnet => SourceIndex == 1;
    public string FetchLabel => IsFussball ? "Fetch FUSSBALL.DE → CSV & XLSX" : "Fetch DFBnet → CSV & XLSX";
    public bool HasResult => CsvPath.Length > 0 || StatusMessage.Length > 0;
    public bool HasCsv => CsvPath.Length > 0;
    public bool PipelineMissing => !IsLoading && (!ScriptsAvailable || !PythonAvailable);
    public string PipelineMessage =>
        $"Pipeline unavailable: {(!PythonAvailable ? $"{PythonLabel} missing (winget install Python.Python.3.12). " : "")}{(!ScriptsAvailable ? $"scripts missing at {_service.RuntimeDir}." : "")}";
    public string FixtureCount => $"{Fixtures.Count} fixtures";
    public bool NoFixtures => Fixtures.Count == 0;
    public string ImportLabel => DryRun ? "Run Import (Dry-Run)" : "Run Import";
    public string CsvLine => $"CSV: {CsvPath}";
    public string XlsxLine => $"XLSX: {XlsxPath}";

    partial void OnSourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsFussball));
        OnPropertyChanged(nameof(IsDfbnet));
        OnPropertyChanged(nameof(FetchLabel));
    }

    partial void OnCsvPathChanged(string value) => RaiseResult();
    partial void OnStatusMessageChanged(string value) => RaiseResult();
    partial void OnDryRunChanged(bool value) => OnPropertyChanged(nameof(ImportLabel));
    partial void OnIsLoadingChanged(bool value) => RaisePipeline();
    partial void OnScriptsAvailableChanged(bool value) => RaisePipeline();
    partial void OnPythonAvailableChanged(bool value) => RaisePipeline();

    private void RaiseResult()
    {
        OnPropertyChanged(nameof(HasResult));
        OnPropertyChanged(nameof(HasCsv));
        OnPropertyChanged(nameof(CsvLine));
        OnPropertyChanged(nameof(XlsxLine));
    }

    private void RaisePipeline()
    {
        OnPropertyChanged(nameof(PipelineMissing));
        OnPropertyChanged(nameof(PipelineMessage));
    }

    public async Task InitAsync()
    {
        if (_initialized) return;
        _initialized = true;
        IsLoading = true;
        try
        {
            Config = await _service.LoadConfigAsync();
            OnPropertyChanged(nameof(Config));
            PythonLabel = await SpielerplusService.PythonLabelAsync();
            PythonAvailable = await SpielerplusService.ResolvePythonCommandAsync() != null;
            ScriptsAvailable = await _service.VerifyAssetsAsync();
            AppendLog($"SpielerPlus importer initialized. {PythonLabel}={(PythonAvailable ? "found" : "missing")}, scripts={(ScriptsAvailable ? "found" : "missing")}.");
            if (!ScriptsAvailable) AppendLog($"Expected python pipeline at {_service.RuntimeDir}.");
        }
        finally
        {
            IsLoading = false;
        }
    }

    public Task SaveConfigAsync() => _service.SaveConfigAsync(Config);

    public void AppendLog(string line) => OnUI(() => Logs.Append($"[{TimeStamp.Now()}] {line}"));

    public void ClearLogs() => Logs.Clear();

    private async Task<bool> RunAsync(Func<Task<bool>> work)
    {
        if (IsRunning) return false;
        await SaveConfigAsync();
        IsRunning = true;
        try
        {
            return await work();
        }
        catch (Exception e)
        {
            StatusMessage = e.Message;
            AppendLog(e.Message);
            return false;
        }
        finally
        {
            IsRunning = false;
        }
    }

    public Task<bool> FetchFixturesAsync() => RunAsync(async () =>
    {
        StatusMessage = "";
        var source = IsFussball ? FixtureSource.Fussball : FixtureSource.Dfbnet;
        AppendLog($"Fetching fixtures via {(IsFussball ? "FUSSBALL.DE" : "DFBnet")}...");
        if (source == FixtureSource.Fussball && Config.FussballUrl.Trim().Length == 0)
        {
            StatusMessage = "Enter a valid FUSSBALL.DE team page URL.";
            AppendLog(StatusMessage);
            return false;
        }
        var result = await Task.Run(() => _service.FetchFixturesAsync(Config, source, AppendLog));
        if (!result.Ok)
        {
            StatusMessage = result.Error.Length > 0 ? result.Error : $"Fetch failed with exit code {result.ExitCode}.";
            AppendLog(StatusMessage);
            return false;
        }
        Fixtures.Clear();
        foreach (var f in result.FixtureList) Fixtures.Add(new FixtureRow(f, Config.HomeMarker));
        OnPropertyChanged(nameof(FixtureCount));
        OnPropertyChanged(nameof(NoFixtures));
        CsvPath = result.CsvPath;
        XlsxPath = result.XlsxPath;
        StatusMessage = $"{Fixtures.Count} fixture(s) exported to {Config.OutputDir}.";
        AppendLog($"Created CSV: {CsvPath}");
        AppendLog($"Created XLSX: {XlsxPath}");
        return true;
    });

    public Task<bool> ProbeExistingAsync() => RunAsync(async () =>
    {
        AppendLog("Checking existing games on SpielerPlus via probe...");
        var output = SpielerplusService.NewTempJson("spielerplus_existing");
        var res = await Task.Run(() => _service.ProbeExistingAsync(Config, output, Headed, AppendLog));
        StatusMessage = res.Ok ? $"Probe completed: {output}" : $"Probe failed with exit code {res.ExitCode}.";
        AppendLog(StatusMessage);
        return res.Ok;
    });

    public Task<bool> ExportParticipationAsync() => RunAsync(async () =>
    {
        var csv = _service.ParticipationCsvPath(Config);
        AppendLog("Exporting SpielerPlus participation status...");
        var res = await Task.Run(() => _service.ExportParticipationAsync(Config, csv, Headed, AppendLog));
        StatusMessage = res.Ok ? $"Participation exported to {csv}" : $"Participation export failed ({res.ExitCode}).";
        AppendLog(StatusMessage);
        return res.Ok;
    });

    public async Task<bool> FetchIcsAsync()
    {
        if (Config.IcsUrl.Trim().Length == 0)
        {
            StatusMessage = "Enter a public Google ICS feed URL.";
            AppendLog(StatusMessage);
            return false;
        }
        return await RunAsync(async () =>
        {
            var output = SpielerplusService.NewTempJson("spielerplus_events");
            AppendLog("Fetching ICS feed...");
            var res = await Task.Run(() => _service.FetchIcsAsync(Config, output, AppendLog));
            StatusMessage = res.Ok ? $"ICS events written to {output}" : $"ICS fetch failed ({res.ExitCode}).";
            AppendLog(StatusMessage);
            return res.Ok;
        });
    }

    public async Task<bool> RunFullImportAsync()
    {
        if (Config.IcsUrl.Trim().Length == 0)
        {
            StatusMessage = "Enter a public Google ICS feed URL.";
            AppendLog(StatusMessage);
            return false;
        }
        return await RunAsync(async () =>
        {
            AppendLog($"Running end-to-end ICS import{(DryRun ? " (dry-run)" : "")}...");
            var res = await Task.Run(() => _service.RunFullImportAsync(Config, DryRun, 0, Headed, AppendLog));
            StatusMessage = res.Ok ? "Import pipeline completed." : $"Import pipeline failed ({res.ExitCode}).";
            AppendLog(StatusMessage);
            return res.Ok;
        });
    }

    public void Open(string target)
    {
        try { ProcessRunner.Open(target); }
        catch (Exception e) { Services.Notifier.Error(e.Message); }
    }
}
