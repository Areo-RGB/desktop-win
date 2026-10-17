using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace FluentDesktop.Core.Mcp;

internal static class Ui
{
    /// <summary>UI-only metadata never written to app configs.</summary>
    public static readonly string[] Fields = ["enabled", "source", "id", "name", "description", "tags", "homepage", "docs"];

    public static JsonObject Strip(JsonObject spec)
    {
        var obj = spec.CloneObject();
        foreach (var f in Fields) obj.Remove(f);
        return obj;
    }

    public static IEnumerable<(string Id, JsonObject Spec)> Sorted(IEnumerable<(string Id, JsonObject Spec)> servers) =>
        servers.OrderBy(s => s.Id, StringComparer.Ordinal);

    public static bool IsEmpty(JsonNode? v) => v is JsonArray { Count: 0 } or JsonObject { Count: 0 };
}

/// <summary>Claude Code: <c>~/.claude.json</c> mcpServers.</summary>
internal static class ClaudeAdapter
{
    private const string Key = "mcpServers";
    private static readonly string[] WindowsWrapCommands = ["npx", "npm", "yarn", "pnpm", "node", "bun", "deno"];

    public static bool Installed(McpPaths p) => Directory.Exists(p.ClaudeDir) || File.Exists(p.ClaudeJson);

    /// <summary>On Windows, <c>npx …</c> becomes <c>cmd /c npx …</c> (Claude Code warns otherwise).</summary>
    internal static void WrapForWindows(JsonObject obj, bool windows)
    {
        if (!windows || (obj.Str("type") ?? "stdio") != "stdio") return;
        if (obj.Str("command") is not { } cmd || cmd.Equals("cmd", StringComparison.OrdinalIgnoreCase) || cmd.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase)) return;
        var stem = Path.GetFileNameWithoutExtension(cmd);
        if (!WindowsWrapCommands.Any(c => c.Equals(stem, StringComparison.OrdinalIgnoreCase))) return;
        var args = new JsonArray("/c", cmd);
        if (obj["args"] is JsonArray existing) foreach (var a in existing) args.Add(a?.DeepClone());
        obj["command"] = "cmd";
        obj["args"] = args;
    }

    public static JsonObject ToLive(JsonObject spec)
    {
        var obj = Ui.Strip(spec);
        WrapForWindows(obj, OperatingSystem.IsWindows());
        return obj;
    }

    public static JsonObject ReadMap(McpPaths p) => JsonConfigFile.ReadServersMap(p.ClaudeJson, Key);

    public static bool Upsert(McpPaths p, string id, JsonObject spec)
    {
        if (!Installed(p)) return false;
        var map = ReadMap(p);
        map[id] = ToLive(spec);
        JsonConfigFile.SpliceTopLevelKey(p.ClaudeJson, Key, map);
        return true;
    }

    public static bool Remove(McpPaths p, string id)
    {
        if (!Installed(p)) return false;
        var map = ReadMap(p);
        if (!map.Remove(id)) return false;
        JsonConfigFile.SpliceTopLevelKey(p.ClaudeJson, Key, map);
        return true;
    }

    public static bool Rewrite(McpPaths p, IEnumerable<(string Id, JsonObject Spec)> servers)
    {
        if (!Installed(p)) return false;
        var map = new JsonObject();
        foreach (var (id, spec) in Ui.Sorted(servers)) map[id] = ToLive(spec);
        JsonConfigFile.SpliceTopLevelKey(p.ClaudeJson, Key, map);
        return true;
    }
}

/// <summary>Gemini CLI: <c>~/.gemini/settings.json</c>; HTTP uses httpUrl, one ms timeout.</summary>
internal static class GeminiAdapter
{
    private const string Key = "mcpServers";

    public static bool Installed(McpPaths p) => Directory.Exists(p.GeminiDir);

    public static JsonObject ToLive(JsonObject spec)
    {
        var obj = spec.CloneObject();
        if (obj.Str("type") == "http" && obj["url"] is { } url)
        {
            obj.Remove("url");
            obj["httpUrl"] = url;
        }
        obj.Remove("type");
        foreach (var f in Ui.Fields) obj.Remove(f);
        var startup = TakeMs(obj, "startup_timeout_sec", 1000) ?? TakeMs(obj, "startup_timeout_ms", 1) ?? 10_000;
        var tool = TakeMs(obj, "tool_timeout_sec", 1000) ?? TakeMs(obj, "tool_timeout_ms", 1) ?? 60_000;
        obj["timeout"] = Math.Max(startup, tool);
        return obj;
    }

    private static long? TakeMs(JsonObject obj, string key, long multiplier)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is not JsonValue v) return null;
        obj.Remove(key);
        if (v.TryGetValue<long>(out var l)) return l * multiplier;
        if (v.TryGetValue<double>(out var d)) return (long)(d * multiplier);
        return null;
    }

    public static JsonObject FromLive(JsonObject spec)
    {
        var obj = spec.CloneObject();
        if (obj["httpUrl"] is { } http)
        {
            obj.Remove("httpUrl");
            obj["url"] = http;
            obj["type"] = "http";
        }
        if (!obj.ContainsKey("type"))
        {
            if (obj.ContainsKey("command")) obj["type"] = "stdio";
            else if (obj.ContainsKey("url")) obj["type"] = "sse";
        }
        return obj;
    }

    public static JsonObject ReadMap(McpPaths p)
    {
        var result = new JsonObject();
        foreach (var (id, spec) in JsonConfigFile.ReadServersMap(p.GeminiSettings, Key))
        {
            if (spec is JsonObject o) result[id] = FromLive(o);
        }
        return result;
    }

    private static void Write(McpPaths p, IEnumerable<(string Id, JsonObject Spec)> unified)
    {
        var live = new JsonObject();
        foreach (var (id, spec) in unified) live[id] = ToLive(spec);
        JsonConfigFile.SpliceTopLevelKey(p.GeminiSettings, Key, live);
    }

    private static List<(string, JsonObject)> Entries(JsonObject map) =>
        map.Where(kv => kv.Value is JsonObject).Select(kv => (kv.Key, (JsonObject)kv.Value!)).ToList();

    public static bool Upsert(McpPaths p, string id, JsonObject spec)
    {
        if (!Installed(p)) return false;
        var map = ReadMap(p);
        map[id] = spec.CloneObject();
        Write(p, Entries(map));
        return true;
    }

    public static bool Remove(McpPaths p, string id)
    {
        if (!Installed(p)) return false;
        var map = ReadMap(p);
        if (!map.Remove(id)) return false;
        Write(p, Entries(map));
        return true;
    }

    public static bool Rewrite(McpPaths p, IEnumerable<(string Id, JsonObject Spec)> servers)
    {
        if (!Installed(p)) return false;
        Write(p, Ui.Sorted(servers));
        return true;
    }
}

/// <summary>OpenCode: <c>opencode.json[c]</c> mcp; stdio ↔ local, http/sse ↔ remote.</summary>
internal static class OpenCodeAdapter
{
    private const string Key = "mcp";

    public static bool Installed(McpPaths p) => Directory.Exists(p.OpenCodeDir);

    public static JsonObject FromLive(JsonObject obj)
    {
        var type = obj.Str("type") ?? "local";
        var output = new JsonObject();
        switch (type)
        {
            case "local":
                output["type"] = "stdio";
                if (obj["command"] is JsonArray arr)
                {
                    var parts = arr.OfType<JsonValue>().Where(v => v.TryGetValue<string>(out _)).Select(v => v.GetValue<string>()).ToList();
                    if (parts.Count > 0)
                    {
                        output["command"] = parts[0];
                        if (parts.Count > 1) output["args"] = new JsonArray(parts.Skip(1).Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
                    }
                }
                else if (obj.Str("command") is { } cmd) output["command"] = cmd;
                if ((obj["environment"] ?? obj["env"]) is JsonObject { Count: > 0 } env) output["env"] = env.DeepClone();
                break;
            case "remote":
                output["type"] = "sse";
                if (obj["url"] is { } url) output["url"] = url.DeepClone();
                if (obj["headers"] is JsonObject { Count: > 0 } headers) output["headers"] = headers.DeepClone();
                break;
            case "stdio" or "http" or "sse":
                return obj.CloneObject();
            default:
                throw new McpException($"Unknown OpenCode MCP type: {type}");
        }
        return output;
    }

    public static JsonObject ToLive(JsonObject obj)
    {
        var type = obj.Str("type") ?? "stdio";
        var output = new JsonObject();
        switch (type)
        {
            case "stdio":
                output["type"] = "local";
                var command = new JsonArray(obj.Str("command") ?? "");
                if (obj["args"] is JsonArray args) foreach (var a in args) command.Add(a?.DeepClone());
                output["command"] = command;
                if (obj["env"] is JsonObject { Count: > 0 } env) output["environment"] = env.DeepClone();
                break;
            case "http" or "sse":
                output["type"] = "remote";
                if (obj["url"] is { } url) output["url"] = url.DeepClone();
                if (obj["headers"] is JsonObject { Count: > 0 } headers) output["headers"] = headers.DeepClone();
                break;
            default:
                throw new McpException($"Unknown MCP type: {type}");
        }
        output["enabled"] = true;
        return output;
    }

    private static JsonObject ReadLive(McpPaths p) => JsonConfigFile.ReadServersMap(p.OpenCodeConfig, Key);

    public static bool Upsert(McpPaths p, string id, JsonObject spec)
    {
        if (!Installed(p)) return false;
        var map = ReadLive(p);
        map[id] = ToLive(spec);
        JsonConfigFile.SpliceTopLevelKey(p.OpenCodeConfig, Key, map);
        return true;
    }

    public static bool Remove(McpPaths p, string id)
    {
        if (!Installed(p)) return false;
        var map = ReadLive(p);
        if (!map.Remove(id)) return false;
        JsonConfigFile.SpliceTopLevelKey(p.OpenCodeConfig, Key, map);
        return true;
    }

    public static bool Rewrite(McpPaths p, IEnumerable<(string Id, JsonObject Spec)> servers)
    {
        if (!Installed(p)) return false;
        var map = new JsonObject();
        foreach (var (id, spec) in Ui.Sorted(servers)) map[id] = ToLive(spec);
        JsonConfigFile.SpliceTopLevelKey(p.OpenCodeConfig, Key, map);
        return true;
    }

    public static List<(string Id, JsonObject Spec)> ImportMap(McpPaths p) =>
        ReadLive(p).Where(kv => kv.Value is JsonObject).Select(kv => (kv.Key, FromLive((JsonObject)kv.Value!))).ToList();
}

/// <summary>
/// Hermes: <c>~/.hermes/config.yaml</c> mcp_servers. Hermes-owned fields
/// (timeout, tools, auth, …) survive writes; comments do not (YAML round-trip).
/// </summary>
internal static class HermesAdapter
{
    private static readonly string[] ExtraFields = ["enabled", "timeout", "connect_timeout", "tools", "sampling", "roots", "auth"];

    public static bool Installed(McpPaths p) => Directory.Exists(p.HermesDir);

    private static JsonObject ReadRoot(McpPaths p)
    {
        var text = McpPaths.ReadOrEmpty(p.HermesConfig);
        if (text.Trim().Length == 0) return new JsonObject();
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0) return new JsonObject();
            return YamlToJson(stream.Documents[0].RootNode) as JsonObject ?? new JsonObject();
        }
        catch (YamlDotNet.Core.YamlException e)
        {
            throw new McpException($"parsing {p.HermesConfig}: {e.Message}");
        }
    }

    private static JsonNode? YamlToJson(YamlNode node) => node switch
    {
        YamlMappingNode map => new JsonObject(map.Children.Select(kv =>
            new KeyValuePair<string, JsonNode?>(((YamlScalarNode)kv.Key).Value ?? "", YamlToJson(kv.Value)))),
        YamlSequenceNode seq => new JsonArray(seq.Children.Select(YamlToJson).ToArray()),
        YamlScalarNode scalar => ScalarToJson(scalar),
        _ => null,
    };

    private static JsonNode? ScalarToJson(YamlScalarNode scalar)
    {
        var v = scalar.Value;
        if (v == null) return null;
        if (scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted or YamlDotNet.Core.ScalarStyle.Literal or YamlDotNet.Core.ScalarStyle.Folded)
            return JsonValue.Create(v);
        if (v is "~" or "null" or "Null" or "NULL" or "") return null;
        if (v is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (v is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return JsonValue.Create(d);
        return JsonValue.Create(v);
    }

    private static object? JsonToPlain(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => obj.ToDictionary(kv => kv.Key, kv => JsonToPlain(kv.Value)),
        JsonArray arr => arr.Select(JsonToPlain).ToList(),
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => v.GetValue<string>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetValue<long>(out var l) ? l : v.GetValue<double>(),
            _ => null,
        },
        _ => null,
    };

    private static void WriteRoot(McpPaths p, JsonObject root)
    {
        var yaml = new SerializerBuilder().Build().Serialize(JsonToPlain(root));
        McpPaths.WriteAtomic(p.HermesConfig, yaml);
    }

    private static JsonObject ServersOf(JsonObject root) => root["mcp_servers"] is JsonObject s ? s.CloneObject() : new JsonObject();

    public static JsonObject FromLive(string id, JsonNode? spec)
    {
        if (spec is not JsonObject obj) throw new McpException($"Hermes MCP server '{id}' must be an object");
        var output = new JsonObject();
        string[] keys;
        if (obj.ContainsKey("command")) { output["type"] = "stdio"; keys = ["command", "args", "env"]; }
        else if (obj.ContainsKey("url")) { output["type"] = "sse"; keys = ["url", "headers"]; }
        else throw new McpException($"Hermes MCP server '{id}' has neither 'command' nor 'url'");
        foreach (var k in keys)
        {
            if (obj[k] is { } v && !Ui.IsEmpty(v)) output[k] = v.DeepClone();
        }
        return output;
    }

    public static JsonObject ToLive(JsonObject obj)
    {
        var type = obj.Str("type") ?? "stdio";
        string[] keys = type switch
        {
            "stdio" => ["command", "args", "env"],
            "http" or "sse" => ["url", "headers"],
            _ => throw new McpException($"Unknown MCP type: {type}"),
        };
        var output = new JsonObject();
        foreach (var k in keys)
        {
            if (obj[k] is { } v && !Ui.IsEmpty(v)) output[k] = v.DeepClone();
        }
        output["enabled"] = true;
        return output;
    }

    private static JsonObject Merge(JsonNode? existing, JsonObject fresh)
    {
        var output = new JsonObject();
        if (existing is JsonObject old)
        {
            foreach (var f in ExtraFields) if (old.TryGetPropertyValue(f, out var v)) output[f] = v?.DeepClone();
        }
        foreach (var (k, v) in fresh)
        {
            if (ExtraFields.Contains(k) && output.ContainsKey(k)) continue;
            output[k] = v?.DeepClone();
        }
        return output;
    }

    public static bool Upsert(McpPaths p, string id, JsonObject spec)
    {
        if (!Installed(p)) return false;
        var root = ReadRoot(p);
        var servers = ServersOf(root);
        servers[id] = Merge(servers[id], ToLive(spec));
        root["mcp_servers"] = SortedObject(servers);
        WriteRoot(p, root);
        return true;
    }

    public static bool Remove(McpPaths p, string id)
    {
        if (!Installed(p)) return false;
        var root = ReadRoot(p);
        var servers = ServersOf(root);
        if (!servers.Remove(id)) return false;
        root["mcp_servers"] = SortedObject(servers);
        WriteRoot(p, root);
        return true;
    }

    public static bool Rewrite(McpPaths p, IEnumerable<(string Id, JsonObject Spec)> servers)
    {
        if (!Installed(p)) return false;
        var root = ReadRoot(p);
        var existing = ServersOf(root);
        var map = new JsonObject();
        foreach (var (id, spec) in Ui.Sorted(servers)) map[id] = Merge(existing[id], ToLive(spec));
        root["mcp_servers"] = map;
        WriteRoot(p, root);
        return true;
    }

    private static JsonObject SortedObject(JsonObject obj) =>
        new(obj.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value?.DeepClone())));

    public static List<(string Id, JsonObject Spec)> ImportMap(McpPaths p) =>
        ServersOf(ReadRoot(p)).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, FromLive(kv.Key, kv.Value))).ToList();
}

/// <summary>
/// Pi (<c>&lt;agent-dir&gt;/mcp.json</c>) and MiniMax (<c>~/.minimax/mcp.json</c>).
/// Written unconditionally. Pi disables with <c>enabled: false</c>; MiniMax removes.
/// </summary>
internal static class McpJsonAdapter
{
    private static readonly string[] PiTransportFields = ["command", "args", "env", "cwd", "url", "headers", "type"];
    private static readonly string[] MinimaxTransportFields = ["command", "args", "env", "url", "headers", "type"];

    private static JsonObject ReadDoc(string path)
    {
        if (!File.Exists(path)) return new JsonObject { ["mcpServers"] = new JsonObject() };
        JsonNode? doc;
        try { doc = JsonNode.Parse(File.ReadAllText(path)); }
        catch (JsonException e) { throw new McpException($"parsing {path}: {e.Message}"); }
        if (doc is not JsonObject obj || (obj.ContainsKey("mcpServers") && obj["mcpServers"] is not JsonObject))
            throw new McpException($"{path}: invalid mcpServers object");
        return obj;
    }

    private static void WriteDoc(string path, JsonObject doc) => McpPaths.WriteAtomic(path, doc.ToJsonString(McpSpec.Pretty));

    public static JsonObject ReadMap(string path) => ReadDoc(path)["mcpServers"] is JsonObject s ? s.CloneObject() : new JsonObject();

    /// <summary>Native → unified: streamable-http reads as http; typeless entries are inferred.</summary>
    public static JsonObject FromLive(JsonObject native)
    {
        var spec = native.CloneObject();
        var typeless = !spec.ContainsKey("type");
        if (spec.Str("type") == "streamable-http" || (typeless && spec.ContainsKey("url"))) spec["type"] = "http";
        else if (typeless && spec.ContainsKey("command")) spec["type"] = "stdio";
        return spec;
    }

    public static JsonObject TransportOnly(JsonObject spec, bool pi)
    {
        var fields = pi ? PiTransportFields : MinimaxTransportFields;
        var output = FromLive(spec);
        foreach (var key in output.Select(kv => kv.Key).ToList()) if (!fields.Contains(key)) output.Remove(key);
        return output;
    }

    public static bool SameTransport(JsonObject a, JsonObject b, bool pi) =>
        JsonNode.DeepEquals(TransportOnly(a, pi), TransportOnly(b, pi));

    private static void ValidatePiName(string id)
    {
        if (id.Length == 0 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new McpException($"Pi server names may only contain letters, digits, _ and -: “{id}”");
    }

    public static void PiSetEnabled(McpPaths p, string id, JsonObject? spec)
    {
        var doc = ReadDoc(p.PiMcpJson);
        if (doc["mcpServers"] is not JsonObject servers) doc["mcpServers"] = servers = new JsonObject();
        if (spec != null)
        {
            McpValidate.Spec(spec);
            if (McpSpec.TransportOf(spec) == McpTransport.Sse)
                throw new McpException("Pi does not support SSE servers — use HTTP (streamable HTTP) instead");
            ValidatePiName(id);
            var key = id.Replace('-', '_');
            if (servers.Select(kv => kv.Key).FirstOrDefault(n => n != id && n.Replace('-', '_') == key) is { } other)
                throw new McpException($"Pi treats “{id}” and “{other}” as the same server (only - vs _ differs)");
            var merged = servers[id] switch
            {
                null => new JsonObject(),
                JsonObject o => o.CloneObject(),
                _ => throw new McpException($"Pi MCP entry “{id}” is not an object"),
            };
            foreach (var f in PiTransportFields) merged.Remove(f);
            foreach (var (k, v) in TransportOnly(spec, pi: true)) merged[k] = v?.DeepClone();
            merged["enabled"] = true;
            servers[id] = merged;
        }
        else
        {
            if (servers[id] is not JsonObject entry) return;
            entry["enabled"] = false;
        }
        WriteDoc(p.PiMcpJson, doc);
    }

    /// <summary>Delete; also drops a leftover enabled:false entry with the same transport.</summary>
    public static void PiRemove(McpPaths p, string id)
    {
        var doc = ReadDoc(p.PiMcpJson);
        if (doc["mcpServers"] is not JsonObject servers || !servers.Remove(id)) return;
        WriteDoc(p.PiMcpJson, doc);
    }

    public static void MinimaxSetEnabled(McpPaths p, string id, JsonObject? spec)
    {
        var doc = ReadDoc(p.MinimaxMcpJson);
        if (doc["mcpServers"] is not JsonObject servers) doc["mcpServers"] = servers = new JsonObject();
        if (spec != null)
        {
            McpValidate.Spec(spec);
            var merged = servers[id] switch
            {
                null => new JsonObject(),
                JsonObject o => o.CloneObject(),
                _ => throw new McpException($"MiniMax MCP entry “{id}” is not an object"),
            };
            foreach (var f in MinimaxTransportFields) merged.Remove(f);
            foreach (var (k, v) in spec) merged[k] = v?.DeepClone();
            merged["enabled"] = true;
            servers[id] = merged;
        }
        else
        {
            servers.Remove(id);
        }
        WriteDoc(p.MinimaxMcpJson, doc);
    }
}
