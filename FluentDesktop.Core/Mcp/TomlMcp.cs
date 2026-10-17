using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace FluentDesktop.Core.Mcp;

/// <summary>
/// Codex (<c>~/.codex/config.toml</c>) and Grok Build (<c>~/.grok/config.toml</c>)
/// <c>[mcp_servers]</c>. Only the mcp_servers tables are rewritten; every other
/// line of the file (comments, unrelated settings) is kept as-is.
/// </summary>
public static partial class TomlMcp
{
    /// <summary>Fields of the other transport; Codex rejects the file if they're present.</summary>
    private static readonly string[] StdioOnly = ["command", "args", "env", "env_vars", "cwd"];
    private static readonly string[] UrlOnly = ["url", "headers", "http_headers", "env_http_headers", "http_headers_helper", "bearer_token_env_var", "oauth_resource"];

    // ---- reading ----------------------------------------------------------

    public static TomlTable ParseModel(string text, string path)
    {
        if (text.Trim().Length == 0) return new TomlTable();
        try
        {
            return Toml.ToModel(text);
        }
        catch (TomlException e)
        {
            throw new McpException($"parsing {path}: {e.Message}");
        }
    }

    /// <summary>Native entries (as JSON) from [mcp_servers] and, optionally, legacy [mcp.servers].</summary>
    public static List<(string Id, JsonObject Entry)> NativeEntries(TomlTable root, bool includeLegacy)
    {
        var tables = new List<TomlTable>();
        if (includeLegacy && root.TryGetValue("mcp", out var mcp) && mcp is TomlTable mcpTable &&
            mcpTable.TryGetValue("servers", out var legacy) && legacy is TomlTable legacyTable)
        {
            tables.Add(legacyTable);
        }
        if (root.TryGetValue("mcp_servers", out var servers) && servers is TomlTable serversTable) tables.Add(serversTable);
        var result = new List<(string, JsonObject)>();
        foreach (var table in tables)
        {
            foreach (var (id, value) in table)
            {
                if (value is TomlTable entry && result.All(r => r.Item1 != id)) result.Add((id, (JsonObject)ToJson(entry)!));
            }
        }
        return result;
    }

    public static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        long l => JsonValue.Create(l),
        int i => JsonValue.Create(i),
        double d => JsonValue.Create(d),
        bool b => JsonValue.Create(b),
        TomlDateTime dt => JsonValue.Create(dt.ToString()),
        TomlTableArray ta => new JsonArray(ta.Select(t => ToJson(t)).ToArray()),
        TomlArray arr => new JsonArray(arr.Select(ToJson).ToArray()),
        TomlTable t => new JsonObject(t.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, ToJson(kv.Value)))),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    /// <summary>Reader-side transport: explicit type wins, else url implies HTTP.</summary>
    private static string EntryTransport(JsonObject entry) =>
        entry.Str("type") ?? (entry.ContainsKey("url") ? "http" : "stdio");

    /// <summary>One native table → unified spec. <paramref name="headersKey"/> is http_headers (Codex) or headers (Grok).</summary>
    public static JsonObject? ImportEntry(JsonObject entry, string headersKey)
    {
        var type = EntryTransport(entry);
        if (type is not ("stdio" or "http" or "sse")) return null;
        var spec = new JsonObject { ["type"] = type };
        if (type == "stdio")
        {
            if (entry.Str("command") is { } cmd) spec["command"] = cmd;
            if (entry["args"] is JsonArray args)
            {
                var strings = new JsonArray(args.OfType<JsonValue>().Where(v => v.TryGetValue<string>(out _)).Select(v => (JsonNode)JsonValue.Create(v.GetValue<string>())).ToArray());
                if (strings.Count > 0) spec["args"] = strings;
            }
            if (entry.Str("cwd") is { } cwd && cwd.Trim().Length > 0) spec["cwd"] = cwd;
            if (StringMap(entry["env"]) is { Count: > 0 } env) spec["env"] = env;
        }
        else
        {
            if (entry.Str("url") is { } url) spec["url"] = url;
            if ((StringMap(entry[headersKey]) ?? StringMap(entry["headers"])) is { Count: > 0 } headers) spec["headers"] = headers;
        }
        // Scalar extras (timeouts, retry flags, ...) pass through.
        foreach (var (key, value) in entry)
        {
            if (spec.ContainsKey(key) || key == "type" || key == headersKey || value is null) continue;
            if (IsScalarOrFlat(value)) spec[key] = value.DeepClone();
        }
        return spec;
    }

    private static JsonObject? StringMap(JsonNode? node)
    {
        if (node is not JsonObject obj) return null;
        var map = new JsonObject();
        foreach (var (k, v) in obj)
        {
            if (v is JsonValue jv && jv.TryGetValue<string>(out var s)) map[k] = s;
        }
        return map;
    }

    private static bool IsScalarOrFlat(JsonNode node) => node switch
    {
        JsonValue => true,
        JsonArray arr => arr.All(i => i is JsonValue),
        JsonObject obj => obj.All(kv => kv.Value is JsonValue v && v.GetValueKind() == JsonValueKind.String),
        _ => false,
    };

    // ---- writing ----------------------------------------------------------

    /// <summary>Writer-side transport: a typeless/stdio spec with a URL and no command is HTTP.</summary>
    private static string SpecTransport(JsonObject spec)
    {
        bool Has(string key) => spec.Str(key) is { } s && s.Trim().Length > 0;
        var explicitType = spec.Str("type");
        if (explicitType is null or "stdio" && !Has("command") && Has("url")) return "http";
        return explicitType ?? "stdio";
    }

    /// <summary>Unified spec → Codex entry: no type, other transport's fields dropped, headers → http_headers.</summary>
    public static JsonObject CodexEntry(JsonObject spec)
    {
        var table = new JsonObject();
        var transport = SpecTransport(spec);
        if (transport == "stdio")
        {
            table["command"] = spec.Str("command") ?? "";
            if (spec["args"] is JsonArray args)
            {
                var strings = new JsonArray(args.OfType<JsonValue>().Where(v => v.TryGetValue<string>(out _)).Select(v => (JsonNode)JsonValue.Create(v.GetValue<string>())).ToArray());
                if (strings.Count > 0) table["args"] = strings;
            }
            if (spec.Str("cwd") is { } cwd && cwd.Trim().Length > 0) table["cwd"] = cwd;
            if (StringMap(spec["env"]) is { Count: > 0 } env) table["env"] = env;
        }
        else
        {
            table["url"] = spec.Str("url") ?? "";
            if (StringMap(spec["headers"]) is { Count: > 0 } headers) table["http_headers"] = headers;
        }
        foreach (var (key, value) in spec)
        {
            var opposite = transport == "stdio" ? UrlOnly.Contains(key) : StdioOnly.Contains(key);
            if (key == "type" || opposite || table.ContainsKey(key) || value is null) continue;
            if (value is JsonValue || (value is JsonArray arr && arr.Count > 0 && arr.All(i => i is JsonValue))) table[key] = value.DeepClone();
        }
        return table;
    }

    /// <summary>Grok: same as Codex but headers keep their name.</summary>
    public static JsonObject GrokEntry(JsonObject spec)
    {
        var table = CodexEntry(spec);
        table.Remove("type");
        if (table["http_headers"] is { } headers)
        {
            table.Remove("http_headers");
            table["headers"] = headers;
        }
        return table;
    }

    [GeneratedRegex(@"^\s*\[\[?\s*(?<key>[^\]]*?)\s*\]\]?\s*(#.*)?$")]
    private static partial Regex HeaderRegex();

    /// <summary>Dotted header key split into unquoted segments.</summary>
    private static List<string> KeySegments(string key)
    {
        var segments = new List<string>();
        var sb = new StringBuilder();
        char? quote = null;
        foreach (var c in key)
        {
            if (quote != null)
            {
                if (c == quote) quote = null; else sb.Append(c);
            }
            else if (c is '"' or '\'') quote = c;
            else if (c == '.') { segments.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(c);
        }
        segments.Add(sb.ToString().Trim());
        return segments;
    }

    private static bool IsManagedHeader(List<string> seg) =>
        seg[0] == "mcp_servers" || (seg.Count >= 2 && seg[0] == "mcp" && seg[1] == "servers");

    /// <summary>Drop every [mcp_servers*] (and legacy [mcp.servers*]) table, then append <paramref name="entries"/>.</summary>
    public static string ReplaceServers(string text, IEnumerable<(string Id, JsonObject Entry)> entries)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var kept = new List<string>();
        var skipping = false;
        foreach (var line in lines)
        {
            var header = HeaderRegex().Match(line);
            if (header.Success && !line.TrimStart().StartsWith('#'))
            {
                skipping = IsManagedHeader(KeySegments(header.Groups["key"].Value));
            }
            if (!skipping) kept.Add(line);
        }
        while (kept.Count > 0 && kept[^1].Trim().Length == 0) kept.RemoveAt(kept.Count - 1);

        var sb = new StringBuilder();
        if (kept.Count > 0) sb.Append(string.Join("\n", kept)).Append('\n');
        foreach (var (id, entry) in entries)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append("[mcp_servers.").Append(Key(id)).Append("]\n");
            foreach (var (k, v) in entry)
            {
                if (v is null) continue;
                sb.Append(Key(k)).Append(" = ").Append(Value(v)).Append('\n');
            }
        }
        return sb.ToString();
    }

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex BareKeyRegex();

    private static string Key(string key) => BareKeyRegex().IsMatch(key) ? key : Quote(key);

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when c < 0x20 => $"\\u{(int)c:X4}",
                _ => c.ToString(),
            });
        }
        return sb.Append('"').ToString();
    }

    private static string Value(JsonNode node) => node switch
    {
        JsonObject obj => "{ " + string.Join(", ", obj.Where(kv => kv.Value != null).Select(kv => $"{Key(kv.Key)} = {Value(kv.Value!)}")) + " }",
        JsonArray arr => "[" + string.Join(", ", arr.Where(i => i != null).Select(i => Value(i!))) + "]",
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.String => Quote(v.GetValue<string>()),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => v.TryGetValue<long>(out var l) ? l.ToString(CultureInfo.InvariantCulture) : v.GetValue<double>().ToString("R", CultureInfo.InvariantCulture),
            _ => Quote(v.ToJsonString()),
        },
        _ => "\"\"",
    };
}

/// <summary>Codex/Grok adapter over <see cref="TomlMcp"/>.</summary>
internal sealed class TomlAdapter(Func<McpPaths, string> dir, Func<McpPaths, string> file, bool codex)
{
    private string HeadersKey => codex ? "http_headers" : "headers";

    public bool Installed(McpPaths p) => Directory.Exists(dir(p));

    private JsonObject Native(JsonObject spec) => codex ? TomlMcp.CodexEntry(spec) : TomlMcp.GrokEntry(spec);

    private List<(string Id, JsonObject Entry)> Current(McpPaths p, out string text)
    {
        text = McpPaths.ReadOrEmpty(file(p));
        return TomlMcp.NativeEntries(TomlMcp.ParseModel(text, file(p)), includeLegacy: codex);
    }

    public Dictionary<string, JsonObject> ImportMap(McpPaths p)
    {
        var result = new Dictionary<string, JsonObject>();
        foreach (var (id, entry) in Current(p, out _))
        {
            if (TomlMcp.ImportEntry(entry, HeadersKey) is { } spec) result.TryAdd(id, spec);
        }
        return result;
    }

    public bool Upsert(McpPaths p, string id, JsonObject spec)
    {
        if (!Installed(p)) return false;
        var entries = Current(p, out var text);
        var native = Native(spec);
        var ix = entries.FindIndex(e => e.Id == id);
        if (ix >= 0) entries[ix] = (id, native); else entries.Add((id, native));
        McpPaths.WriteAtomic(file(p), TomlMcp.ReplaceServers(text, entries));
        return true;
    }

    public bool Remove(McpPaths p, string id)
    {
        if (!Installed(p) || !File.Exists(file(p))) return false;
        List<(string Id, JsonObject Entry)> entries;
        string text;
        try { entries = Current(p, out text); }
        catch (McpException) { return false; }
        if (entries.RemoveAll(e => e.Id == id) == 0) return false;
        McpPaths.WriteAtomic(file(p), TomlMcp.ReplaceServers(text, entries));
        return true;
    }

    public bool Rewrite(McpPaths p, IEnumerable<(string Id, JsonObject Spec)> servers)
    {
        if (!Installed(p)) return false;
        Current(p, out var text);
        var entries = servers.OrderBy(s => s.Id, StringComparer.Ordinal).Select(s => (s.Id, Native(s.Spec)));
        McpPaths.WriteAtomic(file(p), TomlMcp.ReplaceServers(text, entries));
        return true;
    }
}
