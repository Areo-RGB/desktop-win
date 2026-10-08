using System.Diagnostics;
using System.Text.RegularExpressions;
using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.DeviceMirror;

/// <summary>ADB / scrcpy integration. <see cref="LastError"/> carries the last human-readable failure.</summary>
public static partial class AdbService
{
    public const string AdbDownloadUrl = "https://developer.android.com/tools/adb";
    public const string ScrcpyDownloadUrl = "https://github.com/Genymobile/scrcpy";

    private static string? _adbCache;
    private static string? _scrcpyCache;

    public static string? LastError { get; private set; }

    public static string AdbPath => _adbCache ??= ResolveAdb();
    public static string ScrcpyPath => _scrcpyCache ??= ResolveScrcpy();

    public static void ClearCache()
    {
        _adbCache = null;
        _scrcpyCache = null;
    }

    public static string InstallInstructions =>
        OperatingSystem.IsWindows()
            ? "winget install Google.PlatformTools\nwinget install Genymobile.scrcpy"
            : "sudo pacman -S android-tools scrcpy";

    public static string ScreenshotsDir => Path.Combine(AppPaths.Pictures, "Screenshots");
    public static string VideosDir => AppPaths.Videos;

    private static string ResolveAdb()
    {
        var env = Environment.GetEnvironmentVariable("ADB");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        var exe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        var roots = new[]
        {
            Environment.GetEnvironmentVariable("ANDROID_HOME"),
            Environment.GetEnvironmentVariable("ANDROID_SDK_ROOT"),
            Environment.GetEnvironmentVariable("ANDROID_SDK"),
            Path.Combine(AppPaths.LocalAppData, "Android", "Sdk"),
            Path.Combine(AppPaths.Home, "Android", "Sdk"),
        };
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var candidate = Path.Combine(root, "platform-tools", exe);
            if (File.Exists(candidate)) return candidate;
        }
        if (OperatingSystem.IsWindows() && File.Exists(@"C:\platform-tools\adb.exe")) return @"C:\platform-tools\adb.exe";
        return ExecutableResolver.Resolve(exe) ?? exe;
    }

    private static string ResolveScrcpy()
    {
        var env = Environment.GetEnvironmentVariable("SCRCPY");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var exe = OperatingSystem.IsWindows() ? "scrcpy.exe" : "scrcpy";
        return NewestUnpackedScrcpy(exe) ?? ExecutableResolver.Resolve(exe) ?? exe;
    }

    /// <summary>Newest "scrcpy-win64-v*" folder in Documents (scrcpy 5+ has --hwdec; PATH installs may be older).</summary>
    private static string? NewestUnpackedScrcpy(string exe)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Directory.EnumerateDirectories(docs, "scrcpy-win64-v*")
                .Select(d => (Dir: d, Ver: Version.TryParse(Path.GetFileName(d)["scrcpy-win64-v".Length..], out var v) ? v : null))
                .Where(x => x.Ver != null && File.Exists(Path.Combine(x.Dir, exe)))
                .OrderByDescending(x => x.Ver)
                .Select(x => Path.Combine(x.Dir, exe))
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Child environment with ADB set and its directory on PATH (scrcpy needs it).</summary>
    private static Dictionary<string, string> AdbEnvironment
    {
        get
        {
            var env = new Dictionary<string, string> { ["ADB"] = AdbPath };
            var dir = Path.GetDirectoryName(AdbPath);
            if (!string.IsNullOrEmpty(dir))
            {
                var sep = OperatingSystem.IsWindows() ? ";" : ":";
                env["PATH"] = (Environment.GetEnvironmentVariable("PATH") ?? "") + sep + dir;
            }
            return env;
        }
    }

    public static async Task<BinaryStatus> GetBinaryStatusAsync()
    {
        ClearCache();
        string? adbVer = null, scrcpyVer = null;
        bool adbOk = false, scrcpyOk = false;
        try
        {
            var res = await ProcessRunner.RunAsync(AdbPath, ["version"], new ProcessOptions { Timeout = TimeSpan.FromSeconds(6) }).ConfigureAwait(false);
            adbOk = res.Ok;
            if (adbOk) adbVer = res.Stdout.Split('\n')[0].Trim();
        }
        catch { adbOk = false; }
        try
        {
            var res = await ProcessRunner.RunAsync(ScrcpyPath, ["--version"], new ProcessOptions { Timeout = TimeSpan.FromSeconds(6) }).ConfigureAwait(false);
            scrcpyOk = res.Ok;
            if (scrcpyOk)
            {
                var first = (res.Stdout + res.Stderr).Split('\n')[0].Trim();
                scrcpyVer = first.Length == 0 ? "scrcpy" : first;
            }
        }
        catch { scrcpyOk = false; }
        return new BinaryStatus(AdbPath, ScrcpyPath, adbOk, scrcpyOk, adbVer, scrcpyVer);
    }

    private static async Task<string?> RunAdbAsync(IReadOnlyList<string> args, int timeoutSeconds = 6)
    {
        try
        {
            var res = await ProcessRunner.RunAsync(AdbPath, args, new ProcessOptions
            {
                Environment = AdbEnvironment,
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
            }).ConfigureAwait(false);
            if (res.Ok)
            {
                LastError = null;
                return res.Stdout.Trim();
            }
            LastError = res.Stderr.Trim().Length > 0 ? res.Stderr.Trim() : $"adb exited with code {res.ExitCode}: {string.Join(' ', args)}";
            return null;
        }
        catch (TimeoutException e)
        {
            LastError = $"ADB timeout: {e.Message}";
            return null;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            LastError = $"Could not start ADB ({AdbPath}). Is Android Platform-Tools installed and on PATH?\n{e.Message}";
            return null;
        }
        catch (Exception e)
        {
            LastError = $"ADB error: {e.Message}";
            return null;
        }
    }

    public static Task StartServerAsync() => RunAdbAsync(["start-server"], 10);

    public static async Task RestartServerAsync()
    {
        await RunAdbAsync(["kill-server"], 10).ConfigureAwait(false);
        await RunAdbAsync(["start-server"], 10).ConfigureAwait(false);
    }

    public static async Task<List<AdbDevice>> GetDevicesAsync()
    {
        await StartServerAsync().ConfigureAwait(false);
        var output = await RunAdbAsync(["devices", "-l"]).ConfigureAwait(false);
        if (output == null) return [];
        var devices = ParseDevicesList(output);

        foreach (var device in devices.Where(d => d.IsOnline))
        {
            var s = device.Serial;
            var results = await Task.WhenAll(
                RunAdbAsync(["-s", s, "shell", "getprop", "ro.product.manufacturer"]),
                RunAdbAsync(["-s", s, "shell", "getprop", "ro.product.model"]),
                RunAdbAsync(["-s", s, "shell", "getprop", "ro.build.version.release"]),
                RunAdbAsync(["-s", s, "shell", "getprop", "ro.build.version.sdk"]),
                RunAdbAsync(["-s", s, "shell", "dumpsys", "battery"]),
                RunAdbAsync(["-s", s, "shell", "wm", "size"]),
                RunAdbAsync(["-s", s, "shell", "wm", "density"]),
                RunAdbAsync(["-s", s, "shell", "ip", "route"])).ConfigureAwait(false);
            ApplyProperties(device, results);
        }
        return devices;
    }

    internal static void ApplyProperties(AdbDevice device, IReadOnlyList<string?> r)
    {
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        device.Manufacturer = Clean(r[0]);
        device.Model = Clean(r[1]);
        device.AndroidVersion = Clean(r[2]);
        device.SdkVersion = Clean(r[3]);
        if (r[4] is { } battery)
        {
            if (BatteryLevel().Match(battery) is { Success: true } m) device.BatteryLevel = int.Parse(m.Groups[1].Value);
            device.IsCharging = battery.Contains("USB powered: true") || battery.Contains("AC powered: true") || battery.Contains("Wireless powered: true");
        }
        if (r[5] is { } size && PhysicalSize().Match(size) is { Success: true } sm)
        {
            device.ScreenWidth = int.Parse(sm.Groups[1].Value);
            device.ScreenHeight = int.Parse(sm.Groups[2].Value);
        }
        if (r[6] is { } density && PhysicalDensity().Match(density) is { Success: true } dm)
        {
            device.ScreenDensity = int.Parse(dm.Groups[1].Value);
        }
        if (r[7] is { } route && RouteSrc().Match(route) is { Success: true } im)
        {
            device.IpAddress = im.Groups[1].Value;
        }
    }

    /// <summary>Parses <c>adb devices -l</c> output (no I/O).</summary>
    public static List<AdbDevice> ParseDevicesList(string output)
    {
        var parsed = new List<AdbDevice>();
        foreach (var line in output.Split('\n').Skip(1))
        {
            var parts = Regex.Split(line.Trim(), @"\s+");
            if (parts.Length < 2 || parts[0].Length == 0) continue;
            if (parts[0].StartsWith('*')) continue; // "* daemon started successfully"
            parsed.Add(new AdbDevice { Serial = parts[0], State = parts[1] });
        }
        return parsed;
    }

    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];

    /// <summary>Lossless capture via <c>adb exec-out screencap -p</c>.</summary>
    public static async Task<byte[]?> CaptureScreenAsync(string serial)
    {
        try
        {
            var res = await ProcessRunner.RunBytesAsync(AdbPath, ["-s", serial, "exec-out", "screencap", "-p"], new ProcessOptions
            {
                Environment = AdbEnvironment,
                Timeout = TimeSpan.FromSeconds(8),
            }).ConfigureAwait(false);
            if (!res.Ok)
            {
                LastError = res.Stderr.Length > 0 ? res.Stderr : $"screencap failed with exit code {res.ExitCode}";
                return null;
            }
            if (res.Stdout.Length > 8 && res.Stdout.AsSpan(0, 4).SequenceEqual(PngMagic))
            {
                LastError = null;
                return res.Stdout;
            }
            LastError = $"Device returned an invalid screenshot (len={res.Stdout.Length}). Unlock the device and retry.";
            return null;
        }
        catch (Exception e)
        {
            LastError = e is System.ComponentModel.Win32Exception
                ? $"Could not start ADB ({AdbPath}). Is Android Platform-Tools installed?\n{e.Message}"
                : $"captureScreen error: {e.Message}";
            return null;
        }
    }

    /// <summary>Starts an external scrcpy window. Returns null on failure (see <see cref="LastError"/>).</summary>
    public static Process? LaunchScrcpy(string serial, ScrcpyConfig config, string? recordPath = null)
    {
        try
        {
            var psi = ProcessRunner.BuildStartInfo(ScrcpyPath, config.ToArgs(serial, recordPath), new ProcessOptions { Environment = AdbEnvironment });
            // scrcpy logs to stdout; nobody reads it, so don't redirect (a full pipe would block it).
            psi.RedirectStandardOutput = false;
            psi.RedirectStandardError = false;
            var process = Process.Start(psi);
            if (process == null) throw new InvalidOperationException("Process.Start returned null");
            process.EnableRaisingEvents = true;
            LastError = null;
            return process;
        }
        catch (Exception e)
        {
            LastError = $"Could not start scrcpy ({ScrcpyPath}). Install it first:\n{InstallInstructions}\n{e.Message}";
            return null;
        }
    }

    public static async Task<bool> EnableTcpIpAsync(string serial, int port = 5555) =>
        await RunAdbAsync(["-s", serial, "tcpip", $"{port}"]).ConfigureAwait(false) != null;

    public static Task<string?> ConnectWifiAsync(string ip, int port = 5555) => RunAdbAsync(["connect", $"{ip}:{port}"]);

    public static async Task<bool> RebootAsync(string serial, string mode = "normal") =>
        await RunAdbAsync(mode == "normal" ? ["-s", serial, "reboot"] : ["-s", serial, "reboot", mode]).ConfigureAwait(false) != null;

    [GeneratedRegex(@"level:\s*(\d+)")]
    private static partial Regex BatteryLevel();

    [GeneratedRegex(@"Physical size:\s*(\d+)x(\d+)")]
    private static partial Regex PhysicalSize();

    [GeneratedRegex(@"Physical density:\s*(\d+)")]
    private static partial Regex PhysicalDensity();

    [GeneratedRegex(@"src\s+(\d+\.\d+\.\d+\.\d+)")]
    private static partial Regex RouteSrc();
}
