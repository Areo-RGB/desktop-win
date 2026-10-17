using System.Text.Json;
using System.Text.Json.Nodes;
using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.Mcp;

/// <summary>What one live-config write did; Written=false means the app isn't installed.</summary>
public sealed record McpAppNote(McpApp App, bool Written);

public sealed record McpImportReport(int Changed, IReadOnlyList<(McpApp App, int Count)> PerApp, IReadOnlyList<string> Errors);

/// <summary>
/// Store (mcp_servers.json, shared with DesktopCC) is the source of truth;
/// every mutation fans out to the enabled apps' live configs, and imports only
/// ever flip an app flag on an existing entry (never overwrite its spec).
/// Not thread-safe; callers serialize access.
/// </summary>
public sealed class McpService
{
    public const string StoreFile = "mcp_servers.json";

    private readonly McpPaths _paths;
    private readonly string _storePath;
    private readonly List<McpServer> _servers;
    private static readonly TomlAdapter Codex = new(p => p.CodexDir, p => p.CodexConfig, codex: true);
    private static readonly TomlAdapter Grok = new(p => p.GrokDir, p => p.GrokConfig, codex: false);

    public McpService(McpPaths? paths = null, string? storePath = null)
    {
        _paths = paths ?? McpPaths.FromEnvironment();
        _storePath = storePath ?? AppPaths.ConfigFile(StoreFile);
        _servers = Load(_storePath);
    }

    private static List<McpServer> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var map = JsonSerializer.Deserialize<Dictionary<string, McpServer>>(File.ReadAllText(path));
            return map?.Values.OrderBy(s => s.Id, StringComparer.Ordinal).ToList() ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public IReadOnlyList<McpServer> Servers => _servers;

    public McpServer? Get(string id) => _servers.Find(s => s.Id == id);

    public List<string> ExistingIds => _servers.Select(s => s.Id).ToList();

    private void Save()
    {
        var map = new SortedDictionary<string, McpServer>(StringComparer.Ordinal);
        foreach (var s in _servers) map[s.Id] = s;
        var options = new JsonSerializerOptions(McpSpec.Pretty) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
        McpPaths.WriteAtomic(_storePath, JsonSerializer.Serialize(map, options));
    }

    private bool WriteApp(McpApp app, string id, JsonObject spec)
    {
        McpValidate.AppSupport(app, spec);
        switch (app)
        {
            case McpApp.Claude: return ClaudeAdapter.Upsert(_paths, id, spec);
            case McpApp.Codex: return Codex.Upsert(_paths, id, spec);
            case McpApp.Gemini: return GeminiAdapter.Upsert(_paths, id, spec);
            case McpApp.Grok: return Grok.Upsert(_paths, id, spec);
            case McpApp.OpenCode: return OpenCodeAdapter.Upsert(_paths, id, spec);
            case McpApp.Hermes: return HermesAdapter.Upsert(_paths, id, spec);
            case McpApp.Pi: McpJsonAdapter.PiSetEnabled(_paths, id, spec); return true;
            default: McpJsonAdapter.MinimaxSetEnabled(_paths, id, spec); return true;
        }
    }

    private void RemoveApp(McpApp app, string id)
    {
        switch (app)
        {
            case McpApp.Claude: ClaudeAdapter.Remove(_paths, id); break;
            case McpApp.Codex: Codex.Remove(_paths, id); break;
            case McpApp.Gemini: GeminiAdapter.Remove(_paths, id); break;
            case McpApp.Grok: Grok.Remove(_paths, id); break;
            case McpApp.OpenCode: OpenCodeAdapter.Remove(_paths, id); break;
            case McpApp.Hermes: HermesAdapter.Remove(_paths, id); break;
            case McpApp.Pi: McpJsonAdapter.PiRemove(_paths, id); break;
            default: McpJsonAdapter.MinimaxSetEnabled(_paths, id, null); break;
        }
    }

    /// <summary>Add or update, then sync enabled apps and scrub newly-disabled ones. Names are immutable.</summary>
    public List<McpAppNote> Upsert(McpServer server)
    {
        var existing = Get(server.Id);
        McpValidate.Server(server.Id, server.Server, existing == null, ExistingIds);
        var prev = existing?.Apps ?? new McpApps();
        var notes = new List<McpAppNote>();
        foreach (var app in McpAppInfo.All)
        {
            if (server.Apps.Get(app))
            {
                notes.Add(new(app, WriteApp(app, server.Id, server.Server)));
            }
            else if (prev.Get(app))
            {
                RemoveApp(app, server.Id);
                notes.Add(new(app, true));
            }
        }
        var ix = _servers.FindIndex(s => s.Id == server.Id);
        if (ix >= 0) _servers[ix] = server; else _servers.Add(server);
        _servers.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        Save();
        return notes;
    }

    public void Delete(string id)
    {
        if (Get(id) is not { } server) return;
        Exception? first = null;
        foreach (var app in McpAppInfo.All)
        {
            // A Pi entry left enabled:false by an earlier toggle is still ours.
            if (!server.Apps.Get(app) && app != McpApp.Pi) continue;
            try { RemoveApp(app, id); }
            catch (Exception e) when (e is McpException or IOException or UnauthorizedAccessException) { first ??= e; }
        }
        _servers.RemoveAll(s => s.Id == id);
        Save();
        if (first != null) throw new McpException($"deleted “{id}”, but one app config failed: {first.Message}");
    }

    /// <summary>Flip one cell of the matrix. Returns false when the app isn't installed.</summary>
    public bool Toggle(string id, McpApp app, bool enabled)
    {
        var server = Get(id) ?? throw new McpException($"Unknown server “{id}”");
        if (server.Apps.Get(app) == enabled) return true;
        var written = true;
        if (enabled) written = WriteApp(app, id, server.Server);
        else RemoveApp(app, id);
        server.Apps.Set(app, enabled);
        Save();
        return written;
    }

    /// <summary>Rewrite every installed app's config from the current flags.</summary>
    public List<(McpApp App, string? Error, bool Written)> RewriteAll()
    {
        var results = new List<(McpApp, string?, bool)>();
        foreach (var app in McpAppInfo.All)
        {
            var enabled = _servers.Where(s => s.Apps.Get(app)).Select(s => (s.Id, s.Server)).ToList();
            try
            {
                var written = app switch
                {
                    McpApp.Claude => ClaudeAdapter.Rewrite(_paths, enabled),
                    McpApp.Codex => Codex.Rewrite(_paths, enabled),
                    McpApp.Gemini => GeminiAdapter.Rewrite(_paths, enabled),
                    McpApp.Grok => Grok.Rewrite(_paths, enabled),
                    McpApp.OpenCode => OpenCodeAdapter.Rewrite(_paths, enabled),
                    McpApp.Hermes => HermesAdapter.Rewrite(_paths, enabled),
                    _ => RewriteEnableOnly(app, enabled),
                };
                results.Add((app, null, written));
            }
            catch (Exception e) when (e is McpException or IOException or UnauthorizedAccessException)
            {
                results.Add((app, e.Message, false));
            }
        }
        return results;
    }

    /// <summary>
    /// Pi/MiniMax: a false flag also covers unmanaged entries, so a rewrite only
    /// (re-)enables flag-on servers, best effort.
    /// </summary>
    private bool RewriteEnableOnly(McpApp app, List<(string Id, JsonObject Server)> enabled)
    {
        McpException? first = null;
        foreach (var (id, spec) in enabled)
        {
            try
            {
                if (app == McpApp.Pi) McpJsonAdapter.PiSetEnabled(_paths, id, spec);
                else McpJsonAdapter.MinimaxSetEnabled(_paths, id, spec);
            }
            catch (McpException e) { first ??= e; }
        }
        if (first != null) throw first;
        return true;
    }

    /// <summary>
    /// Read every app's live config once. New names are added (enabled only for
    /// the app they came from); known names just gain that app's flag.
    /// </summary>
    public McpImportReport ImportAll()
    {
        var errors = new List<string>();
        var found = new List<(McpApp App, string Id, JsonObject Spec, bool Enabled)>();

        void Collect(McpApp app, Func<IEnumerable<(string Id, JsonObject Spec, bool Enabled)>> read)
        {
            try { found.AddRange(read().Select(e => (app, e.Id, e.Spec, e.Enabled))); }
            catch (Exception e) when (e is McpException or IOException or UnauthorizedAccessException)
            {
                errors.Add($"{app.Label()} couldn't be read: {e.Message}");
            }
        }

        static IEnumerable<(string, JsonObject, bool)> Objects(JsonObject map, Func<JsonObject, JsonObject> convert, bool honorEnabled) =>
            map.Where(kv => kv.Value is JsonObject).Select(kv =>
            {
                var native = (JsonObject)kv.Value!;
                var enabled = !honorEnabled || native["enabled"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;
                return (kv.Key, convert(native), enabled);
            });

        Collect(McpApp.Claude, () => Objects(ClaudeAdapter.ReadMap(_paths), o => o.CloneObject(), false));
        Collect(McpApp.Codex, () => Codex.ImportMap(_paths).Select(kv => (kv.Key, kv.Value, true)));
        Collect(McpApp.Gemini, () => Objects(GeminiAdapter.ReadMap(_paths), o => o, false));
        Collect(McpApp.Grok, () => Grok.ImportMap(_paths).Select(kv => (kv.Key, kv.Value, true)));
        Collect(McpApp.OpenCode, () => OpenCodeAdapter.ImportMap(_paths).Select(e => (e.Id, e.Spec, true)));
        Collect(McpApp.Hermes, () => HermesAdapter.ImportMap(_paths).Select(e => (e.Id, e.Spec, true)));
        Collect(McpApp.Pi, () => Objects(McpJsonAdapter.ReadMap(_paths.PiMcpJson), McpJsonAdapter.FromLive, true));
        Collect(McpApp.Mcode, () => Objects(McpJsonAdapter.ReadMap(_paths.MinimaxMcpJson), o => o.CloneObject(), true));

        var changed = 0;
        var counts = new Dictionary<McpApp, int>();
        foreach (var (app, id, rawSpec, enabled) in found)
        {
            var spec = rawSpec;
            if (app is McpApp.Mcode or McpApp.Pi) spec.Remove("enabled");
            if (!McpValidate.IsValid(spec))
            {
                errors.Add($"{app.Label()}: “{id}” is invalid, skipped");
                continue;
            }
            if (Get(id) is { } slot)
            {
                var conflict = app is McpApp.Pi or McpApp.Mcode && !McpJsonAdapter.SameTransport(slot.Server, spec, app == McpApp.Pi);
                if (conflict)
                {
                    errors.Add($"{app.Label()}: “{id}” conflicts with the stored server, skipped");
                    continue;
                }
                if (!slot.Apps.Get(app) && enabled)
                {
                    slot.Apps.Set(app, true);
                    changed++;
                    counts[app] = counts.GetValueOrDefault(app) + 1;
                }
            }
            else
            {
                var apps = new McpApps();
                if (enabled) apps.Set(app, true);
                _servers.Add(McpServer.Create(id, spec, apps));
                changed++;
                counts[app] = counts.GetValueOrDefault(app) + 1;
            }
        }
        _servers.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        Save();
        return new McpImportReport(changed, McpAppInfo.All.Select(a => (a, counts.GetValueOrDefault(a))).ToList(), errors);
    }

    /// <summary>Everything as {id: spec}, for "Copy all as JSON".</summary>
    public string ExportJson()
    {
        var map = new JsonObject();
        foreach (var s in _servers) map[s.Id] = s.Server.DeepClone();
        return McpSpec.ToPrettyJson(map);
    }

    public string? RowJson(string id) =>
        Get(id) is { } s ? McpSpec.ToPrettyJson(new JsonObject { [s.Id] = s.Server.DeepClone() }) : null;
}
