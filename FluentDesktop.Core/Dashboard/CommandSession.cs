using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.Dashboard;

/// <summary>
/// Runs one dashboard command and streams its output. Events fire on
/// background threads; the UI layer marshals them.
/// </summary>
public sealed class CommandSession
{
    public const string Separator = "----------------------------------------";

    private ManagedProcess? _process;
    private bool _cancelled;

    public CommandSession(DashboardCommand command) => Command = command;

    public DashboardCommand Command { get; }
    public bool IsRunning { get; private set; }
    public int? ExitCode { get; private set; }

    public event Action<string>? Line;
    public event Action<int>? Completed;

    public async Task RunAsync()
    {
        IsRunning = true;
        Line?.Invoke($"[{TimeStamp.Now()}] Executing: {Command.CommandString}");
        Line?.Invoke(Separator);
        int code;
        try
        {
            _process = ProcessRunner.StartStreaming(
                Command.Executable,
                Command.Arguments,
                onStdout: l => Line?.Invoke(l),
                onStderr: l => Line?.Invoke($"[ERR] {l}"),
                options: new ProcessOptions { UseShell = true });
            code = await _process.WaitAsync().ConfigureAwait(false);
            if (_cancelled) return;
            Line?.Invoke(Separator);
            Line?.Invoke($"[{TimeStamp.Now()}] Exited with code: {code}");
        }
        catch (Exception e)
        {
            code = -1;
            Line?.Invoke($"[ERROR] Could not start process: {e.Message}");
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
        Finish(code);
    }

    public void Cancel()
    {
        if (!IsRunning || _process == null) return;
        _cancelled = true;
        _process.Kill();
        Line?.Invoke("[SYSTEM] Command terminated by user.");
        Finish(-9);
    }

    private void Finish(int code)
    {
        if (!IsRunning) return;
        IsRunning = false;
        ExitCode = code;
        Completed?.Invoke(code);
    }
}
