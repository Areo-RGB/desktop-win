using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluentDesktop.Core.Mcp;

public sealed record PastedServer(string Name, JsonObject Spec);

public sealed record PasteDoc(string Format, IReadOnlyList<PastedServer> Items);

/// <summary>
/// "Paste a config" recognition: a single server object, a {"name": {...}} map,
/// {"mcpServers": {...}}, Codex mcp_servers (JSON or a [mcp_servers.x] TOML
/// fragment), or OpenCode's mcp entries. Everything is normalized.
/// </summary>
public static class McpPaste
{
    private static bool LooksLikeSpec(JsonNode? value) =>
        value is JsonObject obj && (obj["command"] is JsonArray || obj.Str("command") != null || obj.Str("url") != null);

    private static JsonObject FromOpenCode(JsonObject obj)
    {
        var type = obj.Str("type") ?? "";
        var remote = type == "remote" || (obj.Str("url") != null && obj.Str("command") == null);
        var output = new JsonObject();
        if (remote)
        {
            output["type"] = "http";
            if (obj["url"] is { } url) output["url"] = url.DeepClone();
            if (obj["headers"] is JsonObject headers) output["headers"] = headers.DeepClone();
            return output;
        }
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
        else if (obj.Str("command") is { } cmd)
        {
            output["command"] = cmd;
        }
        if ((obj["environment"] ?? obj["env"]) is JsonObject env) output["env"] = env.DeepClone();
        return output;
    }

    /// <summary>OpenCode shapes, streamable-http, http_headers and missing-type inference.</summary>
    public static JsonObject Normalize(JsonObject value)
    {
        if (value["command"] is JsonArray || value.Str("type") is "local" or "remote") return FromOpenCode(value);
        var spec = value.CloneObject();
        switch (spec.Str("type"))
        {
            case "streamable-http" or "streamableHttp":
                spec["type"] = "http";
                break;
            case null when !spec.ContainsKey("type"):
                spec["type"] = spec.Str("url") != null && spec.Str("command") == null ? "http" : "stdio";
                break;
        }
        if (spec["http_headers"] is { } headers)
        {
            spec.Remove("http_headers");
            if (!spec.ContainsKey("headers")) spec["headers"] = headers;
        }
        return spec;
    }

    private static List<PastedServer> EntriesOf(JsonObject map, Func<JsonObject, JsonObject> convert) =>
        map.Where(kv => kv.Value is JsonObject).Select(kv => new PastedServer(kv.Key, convert((JsonObject)kv.Value!))).ToList();

    private static PasteDoc? RecognizeObject(JsonObject obj)
    {
        if (obj["mcpServers"] is JsonObject servers)
            return EntriesOf(servers, Normalize) is { Count: > 0 } items ? new("mcpServers JSON", items) : null;
        if (obj["mcp"] is JsonObject mcp)
            return EntriesOf(mcp, FromOpenCode) is { Count: > 0 } items ? new("OpenCode mcp", items) : null;
        if (obj["mcp_servers"] is JsonObject codex)
            return EntriesOf(codex, Normalize) is { Count: > 0 } items ? new("Codex mcp_servers", items) : null;
        if (LooksLikeSpec(obj)) return new("single server", [new PastedServer("", Normalize(obj))]);
        if (obj.Count > 0 && obj.All(kv => LooksLikeSpec(kv.Value)))
            return EntriesOf(obj, Normalize) is { Count: > 0 } items ? new("named servers", items) : null;
        return null;
    }

    /// <summary>Empty text → null. Unrecognized → <see cref="McpException"/> with a hint.</summary>
    public static PasteDoc? Recognize(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Contains("[mcp_servers."))
        {
            Tomlyn.Model.TomlTable root;
            try { root = Tomlyn.Toml.ToModel(trimmed); }
            catch (Tomlyn.TomlException e) { throw new McpException($"That TOML did not parse: {e.Message}"); }
            var items = TomlMcp.NativeEntries(root, includeLegacy: false)
                .Select(e => TomlMcp.ImportEntry(e.Entry, "http_headers") is { } spec ? new PastedServer(e.Id, spec) : null)
                .OfType<PastedServer>()
                .ToList();
            if (items.Count == 0) throw new McpException("No [mcp_servers.*] entries found in that TOML");
            return new PasteDoc("Codex TOML", items);
        }
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(trimmed); }
        catch (JsonException) { throw new McpException("Paste JSON, a {\"name\": …} map, or Codex TOML"); }
        if (parsed is not JsonObject obj) throw new McpException("Paste a JSON object");
        return RecognizeObject(obj) ?? throw new McpException("No MCP servers recognized in that text");
    }
}
