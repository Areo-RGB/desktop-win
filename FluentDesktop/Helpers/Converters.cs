using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace FluentDesktop.Helpers;

internal static class ThemeBrush
{
    public static Brush Get(string key) => (Brush)Application.Current.Resources[key];
}

/// <summary>Dashboard console: [ERR]/[ERROR] lines in red.</summary>
public sealed class DashboardLogBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is string s && (s.StartsWith("[ERR]", StringComparison.Ordinal) || s.StartsWith("[ERROR]", StringComparison.Ordinal))
            ? ThemeBrush.Get("ConsoleErrorBrush")
            : ThemeBrush.Get("ConsoleTextBrush");

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// <summary>Desktop Commander console: errors red, success green, runner lines blue.</summary>
public sealed class DesktopCommanderLogBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var lower = (value as string ?? "").ToLowerInvariant();
        if (lower.Contains("error") || lower.Contains("failed") || lower.Contains("warning")) return ThemeBrush.Get("ConsoleErrorBrush");
        if (lower.Contains("online") || lower.Contains("verified") || lower.Contains("success")) return ThemeBrush.Get("ConsoleSuccessBrush");
        if (lower.Contains("running:") || lower.Contains("browserverifier")) return ThemeBrush.Get("ConsoleInfoBrush");
        return ThemeBrush.Get("ConsoleMutedBrush");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// <summary>App log lines (<see cref="AppLogs"/>): colored by their level marker.</summary>
public sealed class LevelLogBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var s = value as string ?? "";
        var body = s.Length > 10 && s[2] == ':' ? s[10..] : s;
        if (body.StartsWith("✗ ") || body.StartsWith("[ERR")) return ThemeBrush.Get("ConsoleErrorBrush");
        if (body.StartsWith("✓ ")) return ThemeBrush.Get("ConsoleSuccessBrush");
        if (body.StartsWith("$ ")) return ThemeBrush.Get("ConsoleInfoBrush");
        if (body.StartsWith("! ")) return Bind.Warning;
        return ThemeBrush.Get("ConsoleMutedBrush");
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
