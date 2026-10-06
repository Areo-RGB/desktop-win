using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.DesktopCommander;

/// <summary>Process abstraction so the service can be tested without npx.</summary>
public interface IProcessHandle
{
    Task<int> WaitAsync();
    bool Kill();
}

public interface IDesktopCommanderProcessRunner
{
    Task<bool> CommandExistsAsync(string name);
    IProcessHandle Start(string executable, IReadOnlyList<string> arguments, Action<string>? onLine, IReadOnlyDictionary<string, string>? environment = null);
    void Open(string target);
}

public sealed class DefaultDesktopCommanderProcessRunner : IDesktopCommanderProcessRunner
{
    public Task<bool> CommandExistsAsync(string name) => ProcessRunner.CommandExistsAsync(name);

    public IProcessHandle Start(string executable, IReadOnlyList<string> arguments, Action<string>? onLine, IReadOnlyDictionary<string, string>? environment = null) =>
        new Handle(ProcessRunner.StartStreaming(executable, arguments, onLine, onLine, new ProcessOptions { Environment = environment }));

    public void Open(string target) => ProcessRunner.Open(target);

    private sealed class Handle(ManagedProcess process) : IProcessHandle
    {
        public Task<int> WaitAsync() => process.WaitAsync();
        public bool Kill() => process.Kill();
    }
}

/// <summary>
/// Drives <c>npx @wonderwhy-er/desktop-commander remote</c>: start, parse the
/// pairing output, optionally verify in the browser via agent-browser.
/// </summary>
public sealed class DesktopCommanderService : IDisposable
{
    private const string Package = "@wonderwhy-er/desktop-commander@latest";

    private readonly IDesktopCommanderProcessRunner _runner;
    private readonly BrowserVerifier _verifier;
    private readonly object _gate = new();

    private DesktopCommanderState _state = new();
    private IProcessHandle? _active;
    private IProcessHandle? _operation;
    private DesktopCommanderSettings _activeSettings = new();
    private bool _waitingForAuthorization;
    private bool _verificationStarted;
    private bool _disposed;
    private int _generation;

    public DesktopCommanderService(IDesktopCommanderProcessRunner? runner = null)
    {
        _runner = runner ?? new DefaultDesktopCommanderProcessRunner();
        _verifier = new BrowserVerifier(_runner);
    }

    public event Action<DesktopCommanderState>? StateChanged;
    public event Action? ReauthorizingChanged;

    public DesktopCommanderState State { get { lock (_gate) return _state; } }
    public bool IsReauthorizing { get; private set; }

    public async Task StartAsync(DesktopCommanderSettings settings)
    {
        int generation;
        lock (_gate)
        {
            if (_active != null || _state.ProcessActive)
            {
                AppendLogLocked("[Runner] Process is already running.");
                Publish();
                return;
            }
            generation = ++_generation;
            _activeSettings = settings.Normalized();
            _waitingForAuthorization = false;
            _verificationStarted = false;
            _state = _state with
            {
                Status = DesktopCommanderStatus.Starting,
                StatusMessage = "Starting Desktop Commander remote process...",
                AuthUrl = null,
                AuthCode = null,
                DeviceId = null,
                ProcessActive = false,
            };
        }
        Publish();

        if (!await _runner.CommandExistsAsync("npx").ConfigureAwait(false))
        {
            SetError("npx was not found. Install Node.js/npm and try again.");
            return;
        }

        AppendLog($"Running: npx -y {Package} remote -y");
        try
        {
            var process = _runner.Start("npx", ["-y", Package, "remote", "-y"], line => HandleOutput(line, generation));
            lock (_gate)
            {
                if (generation != _generation || _disposed)
                {
                    process.Kill();
                    return;
                }
                _active = process;
                _state = _state with { ProcessActive = true };
            }
            Publish();
            _ = WatchAsync(process, generation);
        }
        catch (Exception e)
        {
            if (generation != _generation) return;
            lock (_gate) _active = null;
            AppendLog($"[Runner] Process error: {e.Message}");
            SetError($"Could not start Desktop Commander: {e.Message}");
        }
    }

    public async Task StopAsync()
    {
        lock (_gate)
        {
            if (_active != null || _operation != null)
            {
                AppendLogLocked("[Runner] Terminating Desktop Commander process...");
            }
            ++_generation;
            _active?.Kill();
            _operation?.Kill();
            _active = null;
            _operation = null;
            _waitingForAuthorization = false;
            _verificationStarted = false;
        }
        await _verifier.CancelAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _state = _state with { Status = DesktopCommanderStatus.Stopped, StatusMessage = "Disconnected", ProcessActive = false };
        }
        Publish();
    }

    public async Task LogoutAndRestartAsync(DesktopCommanderSettings settings)
    {
        if (IsReauthorizing) return;
        IsReauthorizing = true;
        ReauthorizingChanged?.Invoke();
        AppendLog("[Runner] Logging out existing session...");
        try
        {
            await StopAsync().ConfigureAwait(false);
            if (!await _runner.CommandExistsAsync("npx").ConfigureAwait(false))
            {
                SetError("npx was not found. Install Node.js/npm and try again.");
                return;
            }

            string? last = null;
            var process = _runner.Start("npx", ["-y", Package, "remote", "--logout"], line =>
            {
                last = line;
                AppendLog(line);
            });
            lock (_gate) _operation = process;
            var exitCode = await process.WaitAsync().ConfigureAwait(false);
            lock (_gate) if (ReferenceEquals(_operation, process)) _operation = null;

            AppendLog(exitCode == 0
                ? "[Runner] Logged out successfully. Starting fresh authentication..."
                : $"[Runner] Logout warning: process exited with code {exitCode}{(last == null ? "" : ": " + last)}");
            await StartAsync(settings).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            lock (_gate) _operation = null;
            AppendLog($"[Runner] Logout warning: {e.Message}");
            await StartAsync(settings).ConfigureAwait(false);
        }
        finally
        {
            IsReauthorizing = false;
            ReauthorizingChanged?.Invoke();
        }
    }

    public void ClearLogs()
    {
        lock (_gate) _state = _state with { Logs = [], TotalLogged = 0 };
        Publish();
    }

    public void OpenAuthorizationUrl()
    {
        var url = State.AuthUrl;
        if (url != null) _runner.Open(url);
    }

    private async Task WatchAsync(IProcessHandle process, int generation)
    {
        try
        {
            var code = await process.WaitAsync().ConfigureAwait(false);
            lock (_gate)
            {
                if (generation != _generation || _disposed) return;
                if (ReferenceEquals(_active, process)) _active = null;
                AppendLogLocked($"[Runner] Process exited with code {code}");
                _state = _state with
                {
                    Status = DesktopCommanderStatus.Stopped,
                    StatusMessage = $"Disconnected (exit code: {code})",
                    ProcessActive = false,
                };
            }
            Publish();
        }
        catch (Exception e)
        {
            lock (_gate)
            {
                if (generation != _generation || _disposed) return;
                if (ReferenceEquals(_active, process)) _active = null;
            }
            AppendLog($"[Runner] Process error: {e.Message}");
            SetError($"Desktop Commander process failed: {e.Message}");
        }
    }

    internal void HandleOutput(string rawLine, int generation)
    {
        var shouldVerify = false;
        lock (_gate)
        {
            if (generation != _generation || _disposed) return;
            var ev = DesktopCommanderOutputParser.Parse(rawLine);
            if (ev == null) return;

            var next = _state.AppendLog(ev.Line);
            if (ev.AuthUrl != null) next = next with { AuthUrl = ev.AuthUrl };
            if (ev.AuthCode != null) next = next with { AuthCode = ev.AuthCode };
            if (ev.DeviceId != null) next = next with { DeviceId = ev.DeviceId };
            if (ev.WaitingForAuthorization) _waitingForAuthorization = true;
            if (ev.RestoredSession) next = next with { StatusMessage = "Persisted device session restored" };
            if (ev.Online)
            {
                next = next with { Status = DesktopCommanderStatus.Online, StatusMessage = "Online & Active (Remote MCP Ready)" };
            }
            _state = next;

            if (!_verificationStarted && _waitingForAuthorization && _state.AuthUrl != null && _state.AuthCode != null)
            {
                _verificationStarted = true;
                _state = _state with { Status = DesktopCommanderStatus.Authorizing, StatusMessage = $"Pairing code: {_state.AuthCode}" };
                shouldVerify = _activeSettings.AutoVerify;
            }
        }
        Publish();
        if (shouldVerify) _ = VerifyInBrowserAsync(generation);
    }

    private async Task VerifyInBrowserAsync(int generation)
    {
        if (!await _runner.CommandExistsAsync("agent-browser").ConfigureAwait(false))
        {
            if (generation != _generation) return;
            AppendLog("[BrowserVerifier] agent-browser was not found. Use Open Link to authorize manually.");
            Update(s => s with { Status = DesktopCommanderStatus.Error, StatusMessage = "agent-browser not found; open the verification link manually." });
            return;
        }

        var snapshot = State;
        Update(s => s with { Status = DesktopCommanderStatus.Verifying, StatusMessage = $"Verifying in browser with code {s.AuthCode}..." });

        var (success, message) = await _verifier.VerifyAsync(
            snapshot.AuthUrl!,
            snapshot.AuthCode!,
            _activeSettings,
            onLog: m => { if (generation == _generation) AppendLog(m); },
            onProgress: m => { if (generation == _generation) Update(s => s with { StatusMessage = m }); }).ConfigureAwait(false);

        if (generation != _generation || _disposed) return;
        Update(s => success
            ? s with { Status = DesktopCommanderStatus.Verifying, StatusMessage = "Browser verified; awaiting remote connection..." }
            : s with { Status = DesktopCommanderStatus.Error, StatusMessage = $"Verification failed: {message}" });
    }

    private void AppendLog(string line)
    {
        lock (_gate) AppendLogLocked(line);
        Publish();
    }

    private void AppendLogLocked(string line) => _state = _state.AppendLog(line);

    private void SetError(string message) =>
        Update(s => s with { Status = DesktopCommanderStatus.Error, StatusMessage = message, ProcessActive = _active != null });

    private void Update(Func<DesktopCommanderState, DesktopCommanderState> change)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _state = change(_state);
        }
        Publish();
    }

    private void Publish()
    {
        if (_disposed) return;
        StateChanged?.Invoke(State);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ++_generation;
            _active?.Kill();
            _operation?.Kill();
            _active = null;
            _operation = null;
        }
        _verifier.DisposeNow();
    }
}

/// <summary>Submits the pairing code on the verification page via agent-browser.</summary>
internal sealed class BrowserVerifier(IDesktopCommanderProcessRunner runner)
{
    private IProcessHandle? _active;
    private int _generation;

    public async Task<(bool Success, string Message)> VerifyAsync(
        string url, string code, DesktopCommanderSettings settings, Action<string> onLog, Action<string> onProgress)
    {
        var generation = Interlocked.Increment(ref _generation);
        var normalized = settings.Normalized();
        var env = new Dictionary<string, string>
        {
            ["AGENT_BROWSER_DEFAULT_TIMEOUT"] = $"{normalized.VerificationTimeoutSeconds * 1000}",
        };
        onLog($"[BrowserVerifier] Starting with profile \"{normalized.ChromeProfile}\" (timeout: {normalized.VerificationTimeoutSeconds}s).");

        try
        {
            onProgress("Launching browser...");
            onLog($"[BrowserVerifier] Opening verification URL: {url}");
            await RunAsync(generation, ["--profile", normalized.ChromeProfile, "open", url], env, onLog);

            onProgress("Waiting for verification page...");
            onLog("[BrowserVerifier] Waiting for \"Verify your device\".");
            await RunAsync(generation, ["wait", "--text", "Verify your device"], env, onLog);

            onProgress($"Entering code: {code}...");
            onLog($"[BrowserVerifier] Filling pairing code {code}.");
            await RunAsync(generation, ["find", "placeholder", "XXXX-XXXX", "fill", code], env, onLog);

            onProgress("Submitting verification...");
            onLog("[BrowserVerifier] Clicking \"Verify Device\".");
            await RunAsync(generation, ["find", "role", "button", "click", "--name", "Verify Device"], env, onLog);

            onProgress("Confirming authorization...");
            onLog("[BrowserVerifier] Waiting for \"Device verified\".");
            await RunAsync(generation, ["wait", "--text", "Device verified"], env, onLog);

            onLog("[BrowserVerifier] Verification confirmed by browser.");
            onProgress("Browser verification successful!");
            return (true, "Device authorization verified via agent-browser.");
        }
        catch (Exception e)
        {
            onLog($"[BrowserVerifier] Error: {e.Message}");
            onProgress("Browser verification failed");
            return (false, e.Message);
        }
        finally
        {
            await CloseBrowserAsync(env, onLog);
        }
    }

    public async Task CancelAsync()
    {
        Interlocked.Increment(ref _generation);
        _active?.Kill();
        _active = null;
        await CloseBrowserAsync(null, _ => { });
    }

    public void DisposeNow()
    {
        Interlocked.Increment(ref _generation);
        _active?.Kill();
        _active = null;
    }

    private async Task RunAsync(int generation, string[] args, IReadOnlyDictionary<string, string> env, Action<string> onLog)
    {
        if (generation != _generation) throw new InvalidOperationException("Browser verification cancelled.");
        string? last = null;
        var process = runner.Start("agent-browser", args, line =>
        {
            last = line;
            onLog($"[agent-browser] {line}");
        }, env);
        _active = process;
        var code = await process.WaitAsync().ConfigureAwait(false);
        if (ReferenceEquals(_active, process)) _active = null;
        if (generation != _generation) throw new InvalidOperationException("Browser verification cancelled.");
        if (code != 0) throw new InvalidOperationException($"agent-browser exited with code {code}{(last == null ? "" : ": " + last)}");
    }

    private async Task CloseBrowserAsync(IReadOnlyDictionary<string, string>? env, Action<string> onLog)
    {
        try
        {
            var process = runner.Start("agent-browser", ["close"], null, env);
            _active = process;
            await process.WaitAsync().ConfigureAwait(false);
            if (ReferenceEquals(_active, process)) _active = null;
        }
        catch (Exception e)
        {
            onLog($"[BrowserVerifier] Browser close warning: {e.Message}");
        }
    }
}
