using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluentDesktop.Core.Mcp;

/// <summary>
/// Read/write helpers for the JSON-backed app configs (Claude, Gemini,
/// OpenCode). Writes splice only the managed top-level key into the existing
/// text, so comments, key order and unrelated settings survive byte-for-byte.
/// </summary>
public static class JsonConfigFile
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = false };
    private static readonly JsonDocumentOptions DocOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Parse JSON that may contain // and /* */ comments.</summary>
    public static JsonNode? ParseJsonc(string text)
    {
        try
        {
            return JsonNode.Parse(text, NodeOptions, DocOptions);
        }
        catch (JsonException e)
        {
            throw new McpException($"config file is not valid JSON: {e.Message}");
        }
    }

    /// <summary>The named top-level object ({id: spec}); missing file or key means none.</summary>
    public static JsonObject ReadServersMap(string path, string key)
    {
        var text = McpPaths.ReadOrEmpty(path);
        if (text.Trim().Length == 0) return new JsonObject();
        if (ParseJsonc(text) is not JsonObject root) throw new McpException($"{path} root must be an object");
        return root[key] switch
        {
            null => new JsonObject(),
            JsonObject map => (JsonObject)map.DeepClone(),
            _ => throw new McpException($"{path}: “{key}” must be an object"),
        };
    }

    /// <summary>Replace (or insert) one top-level key; everything else keeps its bytes.</summary>
    public static void SpliceTopLevelKey(string path, string key, JsonObject servers)
    {
        var old = McpPaths.ReadOrEmpty(path);
        var rendered = RenderKeyBlock(key, servers);
        string next;
        if (LocateTopLevelValue(old, key) is var (start, end))
        {
            next = old[..start] + rendered + old[end..];
        }
        else
        {
            next = InsertTopLevelKey(old, rendered);
        }
        // Fail loudly rather than write a file we cannot read back.
        try { ParseJsonc(next); }
        catch (McpException) { throw new McpException("rewrote invalid JSON"); }
        McpPaths.WriteAtomic(path, next);
    }

    private static string RenderKeyBlock(string key, JsonObject servers)
    {
        var pretty = servers.ToJsonString(McpSpec.Pretty).Replace("\r\n", "\n");
        var lines = pretty.Split('\n').Select((line, ix) => ix == 0 || line.Trim().Length == 0 ? line : "  " + line);
        return $"\"{key}\": " + string.Join("\n", lines);
    }

    private static string InsertTopLevelKey(string old, string rendered)
    {
        if (old.Trim().Length == 0 || FindRootCloseBrace(old) is not { } close) return $"{{\n  {rendered}\n}}\n";
        var before = old[..close].TrimEnd();
        var needsComma = before.Length > 0 && !before.EndsWith('{');
        var sb = new StringBuilder(before);
        if (needsComma) sb.Append(',');
        sb.Append("\n  ").Append(rendered).Append('\n').Append(old[close..]);
        return sb.ToString();
    }

    private static int SkipComment(string t, int i)
    {
        if (t[i + 1] == '/')
        {
            while (i < t.Length && t[i] != '\n') i++;
            return i;
        }
        i += 2;
        while (i + 1 < t.Length && !(t[i] == '*' && t[i + 1] == '/')) i++;
        return i + 2;
    }

    private static bool IsComment(string t, int i) => t[i] == '/' && i + 1 < t.Length && (t[i + 1] == '/' || t[i + 1] == '*');

    private static int? FindRootCloseBrace(string t)
    {
        int depth = 0, i = 0;
        int? last = null;
        bool inString = false, escaped = false;
        while (i < t.Length)
        {
            var c = t[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                i++;
                continue;
            }
            if (IsComment(t, i)) { i = SkipComment(t, i); continue; }
            switch (c)
            {
                case '"': inString = true; break;
                case '{' or '[': depth++; break;
                case '}' or ']':
                    depth = Math.Max(0, depth - 1);
                    if (c == '}' && depth == 0) last = i;
                    break;
            }
            i++;
        }
        return last;
    }

    /// <summary>Span from the opening quote of top-level "key" through its value.</summary>
    internal static (int Start, int End)? LocateTopLevelValue(string t, string key)
    {
        int depth = 0, i = 0;
        bool inString = false, escaped = false;
        while (i < t.Length)
        {
            var c = t[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                i++;
                continue;
            }
            if (IsComment(t, i)) { i = SkipComment(t, i); continue; }
            switch (c)
            {
                case '"':
                    if (depth == 1 && ScanString(t, i) is { } end && t[(i + 1)..end] == key)
                    {
                        var j = SkipWsComments(t, end + 1);
                        if (j < t.Length && t[j] == ':')
                        {
                            j = SkipWsComments(t, j + 1);
                            return ScanValue(t, j) is { } valueEnd ? (i, valueEnd) : null;
                        }
                    }
                    inString = true;
                    break;
                case '{' or '[': depth++; break;
                case '}' or ']': depth = Math.Max(0, depth - 1); break;
            }
            i++;
        }
        return null;
    }

    private static int? ScanString(string t, int start)
    {
        var i = start + 1;
        while (i < t.Length)
        {
            if (t[i] == '\\') i += 2;
            else if (t[i] == '"') return i;
            else i++;
        }
        return null;
    }

    private static int SkipWsComments(string t, int i)
    {
        while (true)
        {
            while (i < t.Length && char.IsWhiteSpace(t[i])) i++;
            if (i + 1 < t.Length && IsComment(t, i)) { i = SkipComment(t, i); continue; }
            return i;
        }
    }

    private static int? ScanValue(string t, int start)
    {
        if (start >= t.Length) return null;
        var first = t[start];
        if (first == '"') return ScanString(t, start) + 1;
        if (first is '{' or '[')
        {
            var close = first == '{' ? '}' : ']';
            int depth = 0;
            bool inString = false, escaped = false;
            for (var i = start; i < t.Length; i++)
            {
                var c = t[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inString = true; break;
                    case '{' or '[': depth++; break;
                    case '}' or ']':
                        depth--;
                        if (depth == 0) return c == close ? i + 1 : null;
                        break;
                }
            }
            return null;
        }
        var k = start;
        while (k < t.Length && t[k] is not (',' or '}' or ']')) k++;
        return k;
    }
}
