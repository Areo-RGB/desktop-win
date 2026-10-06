using FluentDesktop.Core.Infrastructure;
using FluentDesktop.Helpers;
using Microsoft.UI.Xaml;

namespace FluentDesktop;

public partial class App : Application
{
    public App()
    {
        // WebView2 defaults its user data folder to next to the exe, which may
        // be read-only once installed; keep it under LOCALAPPDATA instead.
        Environment.SetEnvironmentVariable(
            "WEBVIEW2_USER_DATA_FOLDER",
            Path.Combine(AppPaths.LocalAppData, "FluentDesktop", "WebView2"));
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"Unhandled: {e.Exception}");
            SmokeErrors.Add(e.Exception.ToString());
            Services.Notifier.Error(e.Exception.Message);
            e.Handled = true;
        };
    }

    public static MainWindow MainWindow { get; private set; } = null!;

    /// <summary>Errors seen during a CI smoke run (FLUENT_DESKTOP_SMOKE=&lt;result file&gt;).</summary>
    public static List<string> SmokeErrors { get; } = [];

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
