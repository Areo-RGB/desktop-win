namespace FluentDesktop.Helpers;

/// <summary>
/// App-wide page logs, shown together on the Settings page (DesktopCC's Logs
/// card). New pages log here; existing consoles are surfaced as-is.
/// UI thread only.
/// </summary>
public static class AppLogs
{
    public static readonly string[] Sources = ["Video", "Projects", "MCP", "Skills", "Dashboard", "Commander", "SpielerPlus"];

    private static readonly Dictionary<string, LogBuffer> Own = new()
    {
        ["Video"] = new(500),
        ["Projects"] = new(500),
        ["MCP"] = new(500),
        ["Skills"] = new(500),
    };

    public static LogBuffer For(string source) => source switch
    {
        "Dashboard" => Services.Dashboard.Logs,
        "Commander" => Services.DesktopCommander.Logs,
        "SpielerPlus" => Services.Spielerplus.Logs,
        _ => Own[source],
    };

    /// <summary>Appends one timestamped entry per line of <paramref name="text"/>.</summary>
    public static void Push(string source, string text, LogLevel level = LogLevel.Info)
    {
        var prefix = level switch
        {
            LogLevel.Command => "$ ",
            LogLevel.Success => "✓ ",
            LogLevel.Warning => "! ",
            LogLevel.Error => "✗ ",
            _ => "",
        };
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (line.Trim().Length == 0) continue;
            Own[source].Append($"{DateTime.Now:HH:mm:ss}  {prefix}{line}");
        }
    }
}

public enum LogLevel { Info, Command, Success, Warning, Error }
