using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FluentDesktop.Core.Mcp;

/// <summary>An AI CLI tool MCP servers can be synced to (cc-switch's app set).</summary>
public enum McpApp { Claude, Codex, Gemini, Grok, OpenCode, Hermes, Pi, Mcode }

public static class McpAppInfo
{
    public static readonly McpApp[] All =
        [McpApp.Claude, McpApp.Codex, McpApp.Gemini, McpApp.Grok, McpApp.OpenCode, McpApp.Hermes, McpApp.Pi, McpApp.Mcode];

    public static string Id(this McpApp app) => app switch
    {
        McpApp.Claude => "claude",
        McpApp.Codex => "codex",
        McpApp.Gemini => "gemini",
        McpApp.Grok => "grok",
        McpApp.OpenCode => "opencode",
        McpApp.Hermes => "hermes",
        McpApp.Pi => "pi",
        _ => "mcode",
    };

    public static string Label(this McpApp app) => app switch
    {
        McpApp.Claude => "Claude",
        McpApp.Codex => "Codex",
        McpApp.Gemini => "Gemini",
        McpApp.Grok => "Grok",
        McpApp.OpenCode => "OpenCode",
        McpApp.Hermes => "Hermes",
        McpApp.Pi => "Pi",
        _ => "MiniMax",
    };

    /// <summary>The live config file a toggle writes to.</summary>
    public static string ConfigNote(this McpApp app) => app switch
    {
        McpApp.Claude => "~/.claude.json",
        McpApp.Codex => "~/.codex/config.toml",
        McpApp.Gemini => "~/.gemini/settings.json",
        McpApp.Grok => "~/.grok/config.toml",
        McpApp.OpenCode => "opencode.json",
        McpApp.Hermes => "~/.hermes/config.yaml",
        McpApp.Pi => "mcp.json (Pi agent dir)",
        _ => "~/.minimax/mcp.json",
    };
}

/// <summary>Per-app enablement flags, stored with DesktopCC's key names.</summary>
public sealed class McpApps
{
    [JsonPropertyName("claude")] public bool Claude { get; set; }
    [JsonPropertyName("codex")] public bool Codex { get; set; }
    [JsonPropertyName("gemini")] public bool Gemini { get; set; }
    [JsonPropertyName("grokbuild")] public bool Grok { get; set; }
    [JsonPropertyName("opencode")] public bool OpenCode { get; set; }
    [JsonPropertyName("hermes")] public bool Hermes { get; set; }
    [JsonPropertyName("pi")] public bool Pi { get; set; }
    [JsonPropertyName("mcode")] public bool Mcode { get; set; }

    public bool Get(McpApp app) => app switch
    {
        McpApp.Claude => Claude,
        McpApp.Codex => Codex,
        McpApp.Gemini => Gemini,
        McpApp.Grok => Grok,
        McpApp.OpenCode => OpenCode,
        McpApp.Hermes => Hermes,
        McpApp.Pi => Pi,
        _ => Mcode,
    };

    public void Set(McpApp app, bool enabled)
    {
        switch (app)
        {
            case McpApp.Claude: Claude = enabled; break;
            case McpApp.Codex: Codex = enabled; break;
            case McpApp.Gemini: Gemini = enabled; break;
            case McpApp.Grok: Grok = enabled; break;
            case McpApp.OpenCode: OpenCode = enabled; break;
            case McpApp.Hermes: Hermes = enabled; break;
            case McpApp.Pi: Pi = enabled; break;
            default: Mcode = enabled; break;
        }
    }

    public McpApps Clone() => (McpApps)MemberwiseClone();
}

/// <summary>
/// One managed MCP server. <see cref="Server"/> is the flat connection spec
/// (type/command/args/env/cwd/url/headers plus pass-through fields).
/// </summary>
public sealed class McpServer
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("server")] public JsonObject Server { get; set; } = new();
    [JsonPropertyName("apps")] public McpApps Apps { get; set; } = new();
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("homepage")] public string? Homepage { get; set; }
    [JsonPropertyName("docs")] public string? Docs { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }

    public static McpServer Create(string id, JsonObject spec, McpApps apps) =>
        new() { Id = id, Name = id, Server = spec, Apps = apps };
}

public enum McpTransport { Stdio, Http, Sse }

public static class McpSpec
{
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string? Str(this JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static JsonObject CloneObject(this JsonObject obj) => (JsonObject)obj.DeepClone();

    /// <summary>A missing type means stdio unless the spec only has a URL.</summary>
    public static McpTransport TransportOf(JsonObject spec) => spec.Str("type") switch
    {
        "http" => McpTransport.Http,
        "sse" => McpTransport.Sse,
        _ when !spec.ContainsKey("type") && spec.Str("url") != null && !spec.ContainsKey("command") => McpTransport.Http,
        _ => McpTransport.Stdio,
    };

    /// <summary>Second-line summary for list rows. Never includes env/header values.</summary>
    public static string Summary(JsonObject spec)
    {
        if (TransportOf(spec) == McpTransport.Stdio)
        {
            var parts = new List<string>();
            if (spec.Str("command") is { } cmd) parts.Add(cmd);
            if (spec["args"] is JsonArray args)
            {
                foreach (var a in args) if (a is JsonValue v && v.TryGetValue<string>(out var s)) parts.Add(s);
            }
            return string.Join(' ', parts);
        }
        var url = spec.Str("url") ?? "";
        return url.StartsWith("https://") ? url[8..] : url.StartsWith("http://") ? url[7..] : url;
    }

    public static string ToPrettyJson(JsonNode node) => node.ToJsonString(Pretty);
}

public sealed record McpPreset(string Name, string Description, string[] Tags, string Homepage, string Docs, string SpecJson)
{
    public JsonObject Spec => (JsonObject)JsonNode.Parse(SpecJson)!;

    public static readonly McpPreset[] All =
    [
        new("fetch", "Fetch web content as markdown for the model", ["stdio", "http", "web"],
            "https://github.com/modelcontextprotocol/servers", "https://github.com/modelcontextprotocol/servers/tree/main/src/fetch",
            """{"type":"stdio","command":"uvx","args":["mcp-server-fetch"]}"""),
        new("time", "Current time and timezone conversion (via uvx)", ["stdio", "time", "utility"],
            "https://github.com/modelcontextprotocol/servers", "https://github.com/modelcontextprotocol/servers/tree/main/src/time",
            """{"type":"stdio","command":"uvx","args":["mcp-server-time"]}"""),
        new("memory", "Knowledge-graph memory the model can store and recall", ["stdio", "memory", "graph"],
            "https://github.com/modelcontextprotocol/servers", "https://github.com/modelcontextprotocol/servers/tree/main/src/memory",
            """{"type":"stdio","command":"npx","args":["-y","@modelcontextprotocol/server-memory"]}"""),
        new("sequential-thinking", "Step-by-step reasoning traces for hard problems", ["stdio", "thinking", "reasoning"],
            "https://github.com/modelcontextprotocol/servers", "https://github.com/modelcontextprotocol/servers/tree/main/src/sequentialthinking",
            """{"type":"stdio","command":"npx","args":["-y","@modelcontextprotocol/server-sequential-thinking"]}"""),
        new("context7", "Up-to-date library docs search", ["stdio", "docs", "search"],
            "https://context7.com", "https://github.com/upstash/context7/blob/master/README.md",
            """{"type":"stdio","command":"npx","args":["-y","@upstash/context7-mcp"]}"""),
    ];
}

public sealed class McpException(string message) : Exception(message);

/// <summary>Validation rules mirror cc-switch.</summary>
public static class McpValidate
{
    public static void Server(string id, JsonObject spec, bool isNew, IEnumerable<string> existing)
    {
        Name(id, isNew, existing);
        Spec(spec);
    }

    public static void Name(string id, bool isNew, IEnumerable<string> existing)
    {
        var trimmed = id.Trim();
        if (trimmed.Length == 0) throw new McpException("Name is required");
        if (trimmed.Any(char.IsWhiteSpace)) throw new McpException($"Name “{trimmed}” must not contain spaces");
        if (isNew && existing.Contains(trimmed)) throw new McpException($"Name “{trimmed}” already exists");
    }

    public static void Spec(JsonObject spec)
    {
        if (spec.Str("type") is { } t && t is not ("stdio" or "http" or "sse"))
            throw new McpException("Type must be stdio, http, or sse");
        if (McpSpec.TransportOf(spec) == McpTransport.Stdio)
        {
            if (string.IsNullOrWhiteSpace(spec.Str("command")))
                throw new McpException("Local command servers need a command (e.g. npx or uvx)");
        }
        else
        {
            var url = spec.Str("url") ?? "";
            if (url.Trim().Length == 0) throw new McpException("HTTP/SSE servers need a URL");
            if (!url.StartsWith("http://") && !url.StartsWith("https://")) throw new McpException("URL must start with http:// or https://");
        }
    }

    public static bool IsValid(JsonObject spec)
    {
        try { Spec(spec); return true; } catch (McpException) { return false; }
    }

    /// <summary>Per-app guardrails applied at sync time (Pi rejects SSE).</summary>
    public static void AppSupport(McpApp app, JsonObject spec)
    {
        if (app == McpApp.Pi && McpSpec.TransportOf(spec) == McpTransport.Sse)
            throw new McpException("Pi does not support SSE servers — use HTTP (streamable HTTP) instead");
    }
}
