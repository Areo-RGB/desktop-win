namespace FluentDesktop.Core.Infrastructure;

/// <summary>
/// Well-known locations. Config files keep the names the Flutter app used on
/// Windows (%APPDATA%\fluent_desktop\*.json) so existing settings carry over.
/// </summary>
public static class AppPaths
{
    public static string Home =>
        NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
        ?? Environment.GetEnvironmentVariable("USERPROFILE")
        ?? Environment.GetEnvironmentVariable("HOME")
        ?? Directory.GetCurrentDirectory();

    public static string RoamingAppData =>
        NonEmpty(Environment.GetEnvironmentVariable("APPDATA"))
        ?? NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))
        ?? Path.Combine(Home, "AppData", "Roaming");

    public static string LocalAppData =>
        NonEmpty(Environment.GetEnvironmentVariable("LOCALAPPDATA"))
        ?? NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
        ?? Path.Combine(Home, "AppData", "Local");

    /// <summary>Directory holding the app's JSON config files.</summary>
    public static string ConfigDir
    {
        get
        {
            var overrideDir = Environment.GetEnvironmentVariable("FLUENT_DESKTOP_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDir)) return overrideDir;
            return Path.Combine(RoamingAppData, "fluent_desktop");
        }
    }

    public static string ConfigFile(string name) => Path.Combine(ConfigDir, name);

    public static string Desktop =>
        NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
        ?? Path.Combine(Home, "Desktop");

    public static string Downloads => Path.Combine(Home, "Downloads");

    public static string Videos =>
        NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))
        ?? Path.Combine(Home, "Videos");

    public static string Pictures =>
        NonEmpty(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures))
        ?? Path.Combine(Home, "Pictures");

    public static string Temp => Path.GetTempPath();

    public static string TempFile(string name) => Path.Combine(Temp, name);

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
