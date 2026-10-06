using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.Hub;

public sealed record HubStatus(
    bool IsBackendRunning,
    bool IsTunnelRunning,
    bool IsSshRunning,
    int Port,
    string TunnelTokenPath,
    string SshPassword,
    string DashboardUrl,
    string LocalMcpUrl,
    string LocalSshMcpUrl,
    string PublicMcpUrl);

/// <summary>
/// Controls the local MCPHub server, the Cloudflare tunnel and the SSH MCP
/// daemon through scripts/hubctl.ps1, plus port inspection / kill helpers.
/// </summary>
public sealed partial class HubService
{
    /// <summary>Hostname served by the Cloudflare Tunnel (mcp.h03.uk -> localhost:3000).</summary>
    public const string PublicTunnelUrl = "https://mcp.h03.uk/mcp";

    public int Port { get; set; } = 3000;
    public string TunnelTokenPath { get; set; } = Path.Combine(AppPaths.Home, ".cloudflared", "mcp-hub.token");

    public static string ProjectRoot
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("FLUENT_DESKTOP_PROJECT_ROOT");
            if (!string.IsNullOrWhiteSpace(env)) return env;

            var current = Directory.GetCurrentDirectory();
            if (IsHubCheckout(current)) return current;

            // Walk up from the executable (bin\x64\Release\...\FluentDesktop.exe).
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (var i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            {
                if (IsHubCheckout(dir.FullName)) return dir.FullName;
            }

            foreach (var name in new[] { "desktop-win", "flutter-desktop" })
            {
                var fallback = Path.Combine(AppPaths.Home, "Projects", name);
                if (IsHubCheckout(fallback)) return fallback;
            }
            return current;
        }
    }

    private static bool IsHubCheckout(string dir) =>
        File.Exists(Path.Combine(dir, "scripts", "hubctl.ps1")) ||
        File.Exists(Path.Combine(dir, "scripts", "hubctl.sh")) ||
        Directory.Exists(Path.Combine(dir, "servers", "mcphub_fork"));

    public static string McphubDir =>
        Environment.GetEnvironmentVariable("MCPHUB_DIR") ?? Path.Combine(ProjectRoot, "servers", "mcphub_fork");

    public static string SshMcpDir =>
        Environment.GetEnvironmentVariable("SSH_MCP_DIR") ?? Path.Combine(ProjectRoot, "servers", "ssh-mcp");

    public static string TempLogPath(string name) => AppPaths.TempFile(name);

    private Dictionary<string, string> HubEnvironment => new()
    {
        ["MCPHUB_DIR"] = McphubDir,
        ["SSH_MCP_DIR"] = SshMcpDir,
        ["PORT"] = Port.ToString(),
        ["TUNNEL_TOKEN_FILE"] = TunnelTokenPath,
    };

    private async Task HubCtlAsync(string action)
    {
        if (OperatingSystem.IsWindows())
        {
            var script = Path.Combine(ProjectRoot, "scripts", "hubctl.ps1");
            var output = new StringBuilder();
            // start-hub launches a detached node that inherits the pipes, so
            // EOF never arrives while it lives: wait on the process handle.
            using var process = ProcessRunner.StartStreaming(
                "powershell.exe",
                ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, action],
                l => { lock (output) output.AppendLine(l); },
                l => { lock (output) output.AppendLine(l); },
                new ProcessOptions { Environment = HubEnvironment });
            var wait = process.WaitAsync();
            if (await Task.WhenAny(wait, Task.Delay(TimeSpan.FromSeconds(60))).ConfigureAwait(false) != wait)
            {
                process.Kill();
                throw new TimeoutException($"hubctl.ps1 {action} timed out after 60s");
            }
            var code = await wait.ConfigureAwait(false);
            if (code != 0) throw new InvalidOperationException($"hubctl.ps1 {action} failed ({code}): {output.ToString().Trim()}");
            return;
        }

        var sh = Path.Combine(ProjectRoot, "scripts", "hubctl.sh");
        var result = await ProcessRunner.RunAsync("bash", [sh, action], new ProcessOptions { Environment = HubEnvironment }).ConfigureAwait(false);
        if (!result.Ok) throw new InvalidOperationException($"hubctl.sh {action} failed ({result.ExitCode}): {result.CombinedOutput}");
    }

    public static async Task<bool> IsPortOpenAsync(int port, int timeoutMs = 600)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(timeoutMs);
            await client.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> IsTunnelRunningAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Only our tunnel counts: other cloudflared instances may run too.
                var pidFile = TempLogPath("cloudflared-mcp.pid");
                if (!File.Exists(pidFile)) return false;
                var pid = (await File.ReadAllTextAsync(pidFile).ConfigureAwait(false)).Trim();
                if (!int.TryParse(pid, out _)) return false;
                var result = await ProcessRunner.RunAsync(
                    "tasklist.exe", ["/FI", $"PID eq {pid}", "/FO", "CSV", "/NH"],
                    new ProcessOptions { Timeout = TimeSpan.FromSeconds(5) }).ConfigureAwait(false);
                return result.Ok && result.Stdout.Contains("cloudflared.exe", StringComparison.OrdinalIgnoreCase);
            }
            var pg = await ProcessRunner.RunAsync("pgrep", ["-f", $"cloudflared.*--token-file {TunnelTokenPath}"]).ConfigureAwait(false);
            return pg.Ok;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> ReadSshPasswordAsync()
    {
        try
        {
            foreach (var file in new[] { Path.Combine(McphubDir, "data", "mcp_settings.dev.json"), Path.Combine(McphubDir, "mcp_settings.json") })
            {
                if (!File.Exists(file)) continue;
                var m = JsonPassword().Match(await File.ReadAllTextAsync(file).ConfigureAwait(false));
                if (m.Success) return m.Groups[1].Value;
            }
            var envFile = Path.Combine(SshMcpDir, ".env");
            if (File.Exists(envFile))
            {
                var m = EnvPassword().Match(await File.ReadAllTextAsync(envFile).ConfigureAwait(false));
                if (m.Success) return m.Groups[1].Value.Trim();
            }
        }
        catch
        {
            // Fall through to default.
        }
        return "1234";
    }

    public async Task<HubStatus> GetStatusAsync()
    {
        var backend = IsPortOpenAsync(Port);
        var tunnel = IsTunnelRunningAsync();
        var ssh = IsPortOpenAsync(22);
        var password = ReadSshPasswordAsync();
        await Task.WhenAll(backend, tunnel, ssh, password).ConfigureAwait(false);
        return new HubStatus(
            backend.Result, tunnel.Result, ssh.Result, Port, TunnelTokenPath, password.Result,
            $"http://localhost:{Port}",
            $"http://localhost:{Port}/mcp",
            $"http://localhost:{Port}/mcp/ssh-mcp",
            PublicTunnelUrl);
    }

    public async Task StartSshDaemonAsync()
    {
        // The SSH daemon is a Linux/systemd service; Windows uses OpenSSH Server.
        if (OperatingSystem.IsWindows() || await IsPortOpenAsync(22).ConfigureAwait(false)) return;
        await HubCtlAsync("start-ssh").ConfigureAwait(false);
        await Task.Delay(800).ConfigureAwait(false);
    }

    public async Task StartMcphubAsync()
    {
        if (await IsPortOpenAsync(Port).ConfigureAwait(false)) return;
        await HubCtlAsync("start-hub").ConfigureAwait(false);
        await Task.Delay(2000).ConfigureAwait(false);
    }

    public async Task StopMcphubAsync()
    {
        await HubCtlAsync("stop-hub").ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false);
    }

    public async Task StartTunnelAsync()
    {
        if (await IsTunnelRunningAsync().ConfigureAwait(false)) return;
        await HubCtlAsync("start-tunnel").ConfigureAwait(false);
        await Task.Delay(2000).ConfigureAwait(false);
    }

    public async Task StopTunnelAsync()
    {
        await HubCtlAsync("stop-tunnel").ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false);
    }

    public async Task StartAllAsync()
    {
        await HubCtlAsync("start-all").ConfigureAwait(false);
        await Task.Delay(2000).ConfigureAwait(false);
    }

    public async Task StopAllAsync()
    {
        await HubCtlAsync("stop-all").ConfigureAwait(false);
        await Task.Delay(500).ConfigureAwait(false);
    }

    /// <summary>Reads the SSH banner on :22 to verify the daemon answers.</summary>
    public static async Task<string> TestSshConnectionAsync(int sshPort = 22)
    {
        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            await client.ConnectAsync("127.0.0.1", sshPort, cts.Token).ConfigureAwait(false);
            var buffer = new byte[256];
            var read = await client.GetStream().ReadAsync(buffer, cts.Token).ConfigureAwait(false);
            var banner = Encoding.UTF8.GetString(buffer, 0, read).Split('\n')[0].Trim();
            return banner.StartsWith("SSH-", StringComparison.Ordinal)
                ? $"SSH OK on :{sshPort} — {banner}"
                : $"Port {sshPort} answered but sent no SSH banner: {banner}";
        }
        catch (OperationCanceledException)
        {
            return $"SSH test timed out — is the SSH daemon running on :{sshPort}?";
        }
        catch (Exception e)
        {
            return $"SSH connection to :{sshPort} failed ({e.Message})";
        }
    }

    public async Task<string> InspectPortAsync(int port)
    {
        var buf = new StringBuilder();
        buf.AppendLine($"Inspecting TCP :{port} ...");
        var pids = await ListenerPidsAsync(port, buf).ConfigureAwait(false);
        buf.AppendLine(pids.Count > 0 ? $"PIDs listening: {string.Join(", ", pids)}" : "(no listeners)");
        buf.AppendLine($"TCP connect 127.0.0.1:{port}: {(await IsPortOpenAsync(port).ConfigureAwait(false) ? "OPEN" : "CLOSED")}");
        return buf.ToString().Trim();
    }

    public async Task<string> KillProcessOnPortAsync(int port)
    {
        var buf = new StringBuilder();
        buf.AppendLine($"Killing processes on TCP :{port} ...");
        var pids = await ListenerPidsAsync(port, buf).ConfigureAwait(false);
        if (pids.Count == 0)
        {
            buf.AppendLine($"No PIDs listening on :{port}.");
        }
        else
        {
            buf.AppendLine($"Found PIDs: {string.Join(", ", pids)}");
            foreach (var pid in pids)
            {
                try
                {
                    var (exe, args) = OperatingSystem.IsWindows()
                        ? ("taskkill.exe", new[] { "/F", "/PID", pid })
                        : ("kill", new[] { "-9", pid });
                    var kill = await ProcessRunner.RunAsync(exe, args, new ProcessOptions { Timeout = TimeSpan.FromSeconds(10) }).ConfigureAwait(false);
                    var output = kill.CombinedOutput;
                    buf.AppendLine($"{exe} {string.Join(' ', args)} (exit {kill.ExitCode}){(output.Length == 0 ? "" : ": " + output)}");
                }
                catch (Exception e)
                {
                    buf.AppendLine($"kill {pid} failed: {e.Message}");
                }
            }
        }

        await Task.Delay(500).ConfigureAwait(false);
        buf.AppendLine(await IsPortOpenAsync(port).ConfigureAwait(false)
            ? $"Result: port {port} still OPEN."
            : $"Result: port {port} is now FREE.");
        try
        {
            await File.AppendAllTextAsync(
                TempLogPath("portkill.log"),
                $"{DateTime.Now:O} :{port}\n{buf}\n---\n").ConfigureAwait(false);
        }
        catch
        {
            // Logging is best effort.
        }
        return buf.ToString().Trim();
    }

    private static async Task<List<string>> ListenerPidsAsync(int port, StringBuilder buf)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var netstat = await ProcessRunner.RunAsync("netstat.exe", ["-ano", "-p", "TCP"], new ProcessOptions { Timeout = TimeSpan.FromSeconds(5) }).ConfigureAwait(false);
                if (!netstat.Ok)
                {
                    buf.AppendLine($"netstat failed: {netstat.CombinedOutput}");
                    return [];
                }
                buf.AppendLine("— netstat -ano -p TCP (LISTENING) —");
                var (pids, lines) = ParseNetstatListeners(netstat.Stdout, port);
                buf.AppendLine(lines.Count == 0 ? "(none)" : string.Join('\n', lines));
                return pids;
            }

            var ss = await ProcessRunner.RunAsync("ss", ["-ltnp", $"sport = :{port}"], new ProcessOptions { Timeout = TimeSpan.FromSeconds(5) }).ConfigureAwait(false);
            buf.AppendLine("— ss -ltnp —");
            buf.AppendLine(ss.CombinedOutput.Length == 0 ? "(no output)" : ss.CombinedOutput);
            return SsPid().Matches(ss.Stdout).Select(m => m.Groups[1].Value).Distinct().ToList();
        }
        catch (Exception e)
        {
            buf.AppendLine($"listener lookup failed: {e.Message}");
            return [];
        }
    }

    /// <summary>Parses <c>netstat -ano -p TCP</c> output for LISTENING sockets on a port.</summary>
    public static (List<string> Pids, List<string> Lines) ParseNetstatListeners(string output, int port)
    {
        var pids = new List<string>();
        var lines = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var m = NetstatListening().Match(raw);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out var p) || p != port) continue;
            lines.Add(raw.Trim());
            if (!pids.Contains(m.Groups[2].Value)) pids.Add(m.Groups[2].Value);
        }
        return (pids, lines);
    }

    public static Task<string> ReadMcphubLogsAsync() => TailAsync(TempLogPath("mcphub.log"));

    public static Task<string> ReadTunnelLogsAsync() => TailAsync(TempLogPath("cloudflared.log"));

    private static async Task<string> TailAsync(string path, int lines = 50)
    {
        try
        {
            if (File.Exists(path))
            {
                // The hub keeps the file open for writing; share the handle.
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                var all = (await reader.ReadToEndAsync().ConfigureAwait(false)).Split('\n');
                return string.Join('\n', all.TakeLast(lines)).TrimEnd();
            }
        }
        catch
        {
            // Fall through.
        }
        return $"No logs found at {path}";
    }

    [GeneratedRegex(@"TCP\s+\S+:(\d+)\s+\S+\s+LISTENING\s+(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex NetstatListening();

    [GeneratedRegex(@"pid=(\d+)")]
    private static partial Regex SsPid();

    [GeneratedRegex("\"SSH_MCP_PASSWORD\":\\s*\"([^\"]+)\"")]
    private static partial Regex JsonPassword();

    [GeneratedRegex("^SSH_MCP_PASSWORD=(.*)$", RegexOptions.Multiline)]
    private static partial Regex EnvPassword();
}
