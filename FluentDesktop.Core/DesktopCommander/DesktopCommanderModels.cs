using System.Collections.Immutable;
using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.DesktopCommander;

public enum DesktopCommanderStatus
{
    Idle,
    Starting,
    Authorizing,
    Verifying,
    Online,
    Stopped,
    Error,
}

public sealed record DesktopCommanderSettings
{
    public bool AutoVerify { get; init; } = true;
    public string ChromeProfile { get; init; } = "Default";
    public int VerificationTimeoutSeconds { get; init; } = 10;

    public DesktopCommanderSettings Normalized() => this with
    {
        ChromeProfile = string.IsNullOrWhiteSpace(ChromeProfile) ? "Default" : ChromeProfile.Trim(),
        VerificationTimeoutSeconds = Math.Clamp(VerificationTimeoutSeconds, 1, 300),
    };
}

public sealed record DesktopCommanderState
{
    public const int MaxLogs = 500;

    public DesktopCommanderStatus Status { get; init; } = DesktopCommanderStatus.Idle;
    public string StatusMessage { get; init; } = "Ready to connect";
    public string? AuthUrl { get; init; }
    public string? AuthCode { get; init; }
    public string? DeviceId { get; init; }
    public bool ProcessActive { get; init; }
    public ImmutableList<string> Logs { get; init; } = [];

    /// <summary>Lines appended since the last clear (keeps counting past the cap).</summary>
    public long TotalLogged { get; init; }

    public DesktopCommanderState AppendLog(string line)
    {
        var logs = Logs.Add(line);
        if (logs.Count > MaxLogs) logs = logs.RemoveRange(0, logs.Count - MaxLogs);
        return this with { Logs = logs, TotalLogged = TotalLogged + 1 };
    }

    /// <summary>Short label for the navigation badge, or null when idle.</summary>
    public string? Badge => Status switch
    {
        DesktopCommanderStatus.Starting => "START",
        DesktopCommanderStatus.Authorizing => "AUTH",
        DesktopCommanderStatus.Verifying => "VERIFY",
        DesktopCommanderStatus.Online => "ONLINE",
        DesktopCommanderStatus.Error => "ERROR",
        _ => null,
    };
}

public sealed class DesktopCommanderSettingsStore(string? filePath = null)
{
    public string FilePath { get; } = filePath ?? AppPaths.ConfigFile("desktop_commander.json");

    public async Task<DesktopCommanderSettings> LoadAsync() =>
        ((await JsonStore.LoadAsync<DesktopCommanderSettings>(FilePath).ConfigureAwait(false)) ?? new()).Normalized();

    public Task SaveAsync(DesktopCommanderSettings settings) => JsonStore.SaveAsync(FilePath, settings.Normalized());
}
