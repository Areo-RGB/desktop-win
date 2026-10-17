using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.Mcp;

/// <summary>
/// Locations of every app's live MCP config. One object so tests can point the
/// whole matrix at a temp dir; production honors cc-switch's env overrides.
/// </summary>
public sealed class McpPaths(string home, string piAgentDir, string minimaxDir, string hermesDir)
{
    public static McpPaths FromEnvironment()
    {
        var home = AppPaths.Home;
        return new McpPaths(
            home,
            Env("PI_CODING_AGENT_DIR") ?? Path.Combine(home, ".pi", "agent"),
            Env("MINIMAX_DATA_DIR") ?? Env("MAVIS_DATA_DIR") ?? Path.Combine(home, ".minimax"),
            Env("HERMES_HOME") ?? Path.Combine(home, ".hermes"));
    }

    /// <summary>Every app resolves under <paramref name="root"/>.</summary>
    public static McpPaths Under(string root) =>
        new(root, Path.Combine(root, ".pi", "agent"), Path.Combine(root, ".minimax"), Path.Combine(root, ".hermes"));

    private static string? Env(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : null;

    public string ClaudeDir => Path.Combine(home, ".claude");
    public string ClaudeJson => Path.Combine(home, ".claude.json");
    public string CodexDir => Path.Combine(home, ".codex");
    public string CodexConfig => Path.Combine(CodexDir, "config.toml");
    public string GeminiDir => Path.Combine(home, ".gemini");
    public string GeminiSettings => Path.Combine(GeminiDir, "settings.json");
    public string GrokDir => Path.Combine(home, ".grok");
    public string GrokConfig => Path.Combine(GrokDir, "config.toml");
    public string OpenCodeDir => Path.Combine(home, ".config", "opencode");

    /// <summary><c>opencode.jsonc</c> wins when both exist.</summary>
    public string OpenCodeConfig
    {
        get
        {
            var jsonc = Path.Combine(OpenCodeDir, "opencode.jsonc");
            return File.Exists(jsonc) ? jsonc : Path.Combine(OpenCodeDir, "opencode.json");
        }
    }

    public string HermesDir => hermesDir;
    public string HermesConfig => Path.Combine(hermesDir, "config.yaml");
    public string PiMcpJson => Path.Combine(piAgentDir, "mcp.json");
    public string MinimaxMcpJson => Path.Combine(minimaxDir, "mcp.json");

    /// <summary>Temp file + rename so a crash never leaves a half-written config.</summary>
    public static void WriteAtomic(string path, string text)
    {
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir) Directory.CreateDirectory(dir);
        var tmp = path + ".tmp-desktopcc";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Missing file reads as empty text.</summary>
    public static string ReadOrEmpty(string path) => File.Exists(path) ? File.ReadAllText(path) : "";
}
