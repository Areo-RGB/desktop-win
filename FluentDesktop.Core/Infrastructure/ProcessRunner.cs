using System.Diagnostics;
using System.Text;

namespace FluentDesktop.Core.Infrastructure;

/// <summary>Result of a command that ran to completion.</summary>
public sealed record CommandResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
    public string CombinedOutput => $"{Stdout}\n{Stderr}".Trim();
}

/// <summary>Result of a command whose stdout is binary (e.g. adb screencap).</summary>
public sealed record BinaryCommandResult(int ExitCode, byte[] Stdout, string Stderr)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>Options shared by every way of starting a process.</summary>
public sealed class ProcessOptions
{
    public string? WorkingDirectory { get; init; }

    /// <summary>Extra variables layered over the (enriched) parent environment.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public TimeSpan? Timeout { get; init; }

    /// <summary>Don't create a console window for the child (default on).</summary>
    public bool NoWindow { get; init; } = true;

    /// <summary>
    /// Run through the system shell (cmd.exe /c on Windows, /bin/sh -c
    /// elsewhere) so shell built-ins and PATHEXT lookups work, like Dart's
    /// <c>runInShell: true</c>.
    /// </summary>
    public bool UseShell { get; init; }

    public static readonly ProcessOptions Default = new();
}

/// <summary>
/// A long-running child process whose stdout/stderr are delivered line by line.
/// </summary>
public sealed class ManagedProcess : IDisposable
{
    private readonly Process _process;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _openStreams = 2;

    internal ManagedProcess(Process process, Action<string>? onStdout, Action<string>? onStderr)
    {
        _process = process;
        _process.EnableRaisingEvents = true;
        _process.OutputDataReceived += (_, e) => Handle(e.Data, onStdout);
        _process.ErrorDataReceived += (_, e) => Handle(e.Data, onStderr);
        _process.Exited += (_, _) => TryComplete();
    }

    public int Pid { get; private set; }

    internal void Start()
    {
        _process.Start();
        Pid = _process.Id;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void Handle(string? line, Action<string>? sink)
    {
        if (line is null)
        {
            // End of stream.
            if (Interlocked.Decrement(ref _openStreams) == 0) TryComplete();
            return;
        }
        sink?.Invoke(line);
    }

    private void TryComplete()
    {
        if (!_process.HasExited) return;
        if (Volatile.Read(ref _openStreams) > 0)
        {
            // Streams can stay open forever when a detached grandchild inherits
            // the pipes. Give them a moment, then report the exit anyway.
            _ = Task.Delay(500).ContinueWith(_ => _exited.TrySetResult(SafeExitCode()));
            return;
        }
        _exited.TrySetResult(SafeExitCode());
    }

    private int SafeExitCode()
    {
        try { return _process.ExitCode; } catch { return -1; }
    }

    public bool HasExited
    {
        get { try { return _process.HasExited; } catch { return true; } }
    }

    /// <summary>Waits for exit and for both output streams to drain.</summary>
    public Task<int> WaitAsync() => _exited.Task;

    public bool Kill()
    {
        try
        {
            if (_process.HasExited) return false;
            _process.Kill(entireProcessTree: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose() => _process.Dispose();
}

/// <summary>
/// Runs external commands. This is the whole "backend": every feature is a
/// thin layer over CLI tools (adb, scrcpy, gh, git, yt-dlp, npx, python...).
/// </summary>
public static class ProcessRunner
{
    public static async Task<CommandResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        ProcessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= ProcessOptions.Default;
        using var process = Create(executable, arguments, options, redirectInput: false);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (stdout) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await WaitForExitAsync(process, options.Timeout, cancellationToken).ConfigureAwait(false);
        return new CommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static async Task<BinaryCommandResult> RunBytesAsync(
        string executable,
        IEnumerable<string> arguments,
        ProcessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= ProcessOptions.Default;
        using var process = Create(executable, arguments, options, redirectInput: false);
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (stderr) stderr.AppendLine(e.Data); };

        process.Start();
        process.BeginErrorReadLine();
        using var buffer = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(buffer, cancellationToken);

        await WaitForExitAsync(process, options.Timeout, cancellationToken).ConfigureAwait(false);
        await copy.ConfigureAwait(false);
        return new BinaryCommandResult(process.ExitCode, buffer.ToArray(), stderr.ToString());
    }

    /// <summary>Starts a process and streams its output line by line.</summary>
    public static ManagedProcess StartStreaming(
        string executable,
        IEnumerable<string> arguments,
        Action<string>? onStdout = null,
        Action<string>? onStderr = null,
        ProcessOptions? options = null)
    {
        options ??= ProcessOptions.Default;
        var process = Create(executable, arguments, options, redirectInput: false);
        var managed = new ManagedProcess(process, onStdout, onStderr);
        managed.Start();
        return managed;
    }

    /// <summary>Fire-and-forget launch (e.g. a terminal window) without redirection.</summary>
    public static Process StartDetached(string executable, IEnumerable<string> arguments, ProcessOptions? options = null)
    {
        options ??= ProcessOptions.Default;
        var psi = BuildStartInfo(executable, arguments, options);
        psi.RedirectStandardOutput = false;
        psi.RedirectStandardError = false;
        psi.CreateNoWindow = false;
        return Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {executable}");
    }

    /// <summary>Opens a file, folder or URL with the default handler.</summary>
    public static void Open(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(target.Trim()) { UseShellExecute = true });
            return;
        }
        var opener = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        Process.Start(new ProcessStartInfo(opener) { ArgumentList = { target.Trim() }, UseShellExecute = false });
    }

    public static Task<bool> CommandExistsAsync(string executable) =>
        Task.Run(() =>
        {
            try { return ExecutableResolver.Resolve(executable) != null; }
            catch { return false; }
        });

    private static Process Create(string executable, IEnumerable<string> arguments, ProcessOptions options, bool redirectInput)
    {
        var psi = BuildStartInfo(executable, arguments, options);
        psi.RedirectStandardInput = redirectInput;
        return new Process { StartInfo = psi, EnableRaisingEvents = true };
    }

    internal static ProcessStartInfo BuildStartInfo(string executable, IEnumerable<string> arguments, ProcessOptions options)
    {
        var env = EnvironmentEnricher.Build(options.Environment);
        var resolved = ExecutableResolver.Resolve(executable, env["PATH"]) ?? executable;
        var args = arguments.ToList();

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = options.NoWindow,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrWhiteSpace(options.WorkingDirectory))
        {
            psi.WorkingDirectory = options.WorkingDirectory;
        }

        if (options.UseShell)
        {
            if (OperatingSystem.IsWindows())
            {
                psi.FileName = System.Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
                psi.Arguments = "/d /s /c \"" + WindowsCommandLine.Join(new[] { executable }.Concat(args), forCmd: true) + "\"";
            }
            else
            {
                psi.FileName = "/bin/sh";
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add(string.Join(' ', new[] { executable }.Concat(args).Select(PosixQuote)));
            }
        }
        else if (OperatingSystem.IsWindows() && ExecutableResolver.IsBatchFile(resolved))
        {
            // .cmd/.bat (npx.cmd, agent-browser.cmd...) must go through cmd.exe.
            psi.FileName = System.Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = "/d /s /c \"" + WindowsCommandLine.Join(new[] { resolved }.Concat(args), forCmd: true) + "\"";
        }
        else
        {
            psi.FileName = resolved;
            foreach (var a in args) psi.ArgumentList.Add(a);
        }

        psi.Environment.Clear();
        foreach (var (k, v) in env) psi.Environment[k] = v;
        return psi;
    }

    private static string PosixQuote(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || "-_./=:@%+,".Contains(c))
            ? arg
            : "'" + arg.Replace("'", "'\\''") + "'";

    private static async Task WaitForExitAsync(Process process, TimeSpan? timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            if (!ct.IsCancellationRequested)
            {
                throw new TimeoutException($"Timed out after {timeout?.TotalSeconds:0}s: {process.StartInfo.FileName}");
            }
            throw;
        }
    }
}
