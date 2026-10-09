using System.Text.Json;
using System.Text.Json.Nodes;
using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.Hub;

public sealed record SshMcpSettings
{
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 22;
    public string Username { get; init; } = Environment.UserName;
    public string Password { get; init; } = "";
    public string SudoPassword { get; init; } = "";
    public string KeyPath { get; init; } = "";
    public string ApprovalPolicy { get; init; } = "ask-destructive";
    public string HostKeyMode { get; init; } = "tofu";
}

/// <summary>Persists native connection settings and registers ssh-mcp's stdio server with MCPHub.</summary>
public sealed class SshMcpSettingsStore(string hubDir, string sshDir, string? settingsPath = null)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public string SettingsPath { get; } = settingsPath ?? AppPaths.ConfigFile("ssh_mcp.json");
    public string ProfilePath => Path.Combine(Path.GetDirectoryName(SettingsPath)!, "ssh-mcp", "config.toml");

    public async Task<SshMcpSettings> LoadAsync()
    {
        if (!File.Exists(SettingsPath)) return new SshMcpSettings();
        var json = await File.ReadAllTextAsync(SettingsPath).ConfigureAwait(false);
        return JsonSerializer.Deserialize<SshMcpSettings>(json, Options)
            ?? throw new InvalidOperationException("SSH MCP settings are empty.");
    }

    public async Task SaveAsync(SshMcpSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.Host) || string.IsNullOrWhiteSpace(settings.Username) || settings.Port is < 1 or > 65535)
            throw new ArgumentException("Enter an SSH host, username, and port between 1 and 65535.");
        if (settings.ApprovalPolicy is not ("auto" or "ask-destructive" or "ask-all" or "deny") ||
            settings.HostKeyMode is not ("tofu" or "strict" or "insecure"))
            throw new ArgumentException("Invalid SSH approval or host key policy.");
        var entrypoint = Path.Combine(sshDir, "build", "index.js");
        if (!File.Exists(entrypoint))
            throw new FileNotFoundException("Build SSH MCP first: npm --prefix <mcp-hub>/ssh-mcp ci", entrypoint);
        var hubPath = Path.Combine(hubDir, "mcp_settings.json");
        var document = File.Exists(hubPath)
            ? JsonNode.Parse(await File.ReadAllTextAsync(hubPath).ConfigureAwait(false)) as JsonObject
                ?? throw new InvalidOperationException("MCPHub settings must be a JSON object.")
            : new JsonObject();
        if (document["mcpServers"] is null) document["mcpServers"] = new JsonObject();
        var servers = document["mcpServers"] as JsonObject
            ?? throw new InvalidOperationException("MCPHub mcpServers must be an object.");
        // Preserve ownership, tool customizations, and unrelated environment values on existing registrations.
        var entry = servers["ssh-mcp"] as JsonObject ?? new JsonObject();
        var environment = entry["env"] as JsonObject ?? new JsonObject();
        environment["SSH_MCP_PASSWORD"] = settings.Password;
        environment["SSH_MCP_SUDO_PASSWORD"] = settings.SudoPassword;
        entry["type"] = "stdio";
        entry["command"] = "node";
        entry["args"] = new JsonArray(entrypoint, "--config=" + ProfilePath, "--hostKeyMode=" + settings.HostKeyMode);
        entry["enabled"] = true;
        if (entry["env"] is null) entry["env"] = environment;
        if (servers["ssh-mcp"] is null) servers["ssh-mcp"] = entry;

        // JSON basic strings are also TOML basic strings with the default JSON encoder.
        static string Quote(string value) => JsonSerializer.Serialize(value);
        var profile = $"""
            [defaults]
            defaultProfile = "winui"
            approvalMode = {Quote(settings.ApprovalPolicy)}

            [[profiles]]
            name = "winui"
            host = {Quote(settings.Host)}
            port = {settings.Port}
            user = {Quote(settings.Username)}
            auth = {Quote(string.IsNullOrWhiteSpace(settings.KeyPath) ? "password" : "key")}
            keyRef = {Quote(settings.KeyPath)}
            role = "admin"
            group = "dev"
            approvalPolicy = {Quote(settings.ApprovalPolicy)}
            """;
        await WriteAsync(ProfilePath, profile + Environment.NewLine).ConfigureAwait(false);
        await WriteAsync(hubPath, document.ToJsonString(Options)).ConfigureAwait(false);
        await WriteAsync(SettingsPath, JsonSerializer.Serialize(settings, Options)).ConfigureAwait(false);
    }

    private static async Task WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await File.WriteAllTextAsync(temporary, content).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }
}
