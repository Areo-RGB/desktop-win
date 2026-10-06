namespace FluentDesktop.Core.Infrastructure;

public static class TimeStamp
{
    public static string Now() => DateTime.Now.ToString("HH:mm:ss");
}

/// <summary>Formatting helpers shared by several features.</summary>
public static class Format
{
    public static string Bytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        string[] units = ["KB", "MB", "GB", "TB"];
        double value = bytes / 1024.0;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return value >= 10 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
    }

    public static string Date(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd");

    public static string Date(DateTime value) => value.ToLocalTime().ToString("yyyy-MM-dd");

    /// <summary>"5m ago", "3h ago", "2d ago" within a week, else yyyy-MM-dd.</summary>
    public static string Relative(DateTimeOffset value, DateTimeOffset? now = null)
    {
        var diff = (now ?? DateTimeOffset.Now) - value;
        if (diff.TotalDays < 1)
        {
            return diff.TotalHours < 1 ? $"{(int)diff.TotalMinutes}m ago" : $"{(int)diff.TotalHours}h ago";
        }
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
        return Date(value);
    }

    public static string Count(long value)
    {
        if (value >= 1_000_000) return $"{value / 1_000_000.0:0.0}M";
        if (value >= 1_000) return $"{value / 1_000.0:0.0}K";
        return value.ToString();
    }
}
