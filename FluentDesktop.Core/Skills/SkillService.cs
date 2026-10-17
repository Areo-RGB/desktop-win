using System.Text.RegularExpressions;
using FluentDesktop.Core.Infrastructure;
using YamlDotNet.RepresentationModel;

namespace FluentDesktop.Core.Skills;

public enum SkillApp { Pi, Claude, Codex, OpenCode }

public sealed record Skill(string Id, string Name, string Description, bool[] Enabled);

public sealed class SkillException(string message) : Exception(message);

/// <summary>
/// Git-backed skills store shared with DesktopCC: master copies live in
/// <c>~/Projects/skills/skills/&lt;id&gt;</c> (SKILLS_DIR overrides), and each app
/// gets a DesktopCC-owned link (a junction on Windows) to the master copy.
/// Backups live in <c>.git/desktopcc-backups</c>, never in the remote tree.
/// All methods block; call them off the UI thread.
/// </summary>
public sealed partial class SkillService
{
    public const string Remote = "https://github.com/Areo-RGB/skill.repo.git";

    public static readonly SkillApp[] AllApps = [SkillApp.Pi, SkillApp.Claude, SkillApp.Codex, SkillApp.OpenCode];

    private readonly string _repo;
    private readonly string[] _apps;
    private readonly object _lock = new();

    public SkillService(string? repo = null, IReadOnlyList<string>? appDirs = null)
    {
        _repo = Path.GetFullPath(repo ?? Env("SKILLS_DIR") ?? Path.Combine(AppPaths.Home, "Projects", "skills"));
        _apps = appDirs?.ToArray() ?? AllApps.Select(SkillsDir).ToArray();
    }

    public string RepoDir => _repo;

    public static string Label(SkillApp app) => app switch
    {
        SkillApp.Pi => "Pi",
        SkillApp.Claude => "Claude",
        SkillApp.Codex => "Codex",
        _ => "OpenCode",
    };

    private static string? Env(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : null;

    public static string SkillsDir(SkillApp app)
    {
        var home = AppPaths.Home;
        return app switch
        {
            SkillApp.Pi => Path.Combine(Env("PI_CODING_AGENT_DIR") ?? Path.Combine(home, ".pi", "agent"), "skills"),
            SkillApp.Claude => Path.Combine(Env("CLAUDE_CONFIG_DIR") ?? Path.Combine(home, ".claude"), "skills"),
            SkillApp.Codex => Path.Combine(Env("CODEX_HOME") ?? Path.Combine(home, ".codex"), "skills"),
            _ => Path.Combine(Env("XDG_CONFIG_HOME") ?? Path.Combine(home, ".config"), "opencode", "skills"),
        };
    }

    private string SkillsRoot => Path.Combine(_repo, "skills");
    private string BackupDir => Path.Combine(_repo, ".git", "desktopcc-backups");
    public bool Exists => Directory.Exists(_repo);

    // ---- git --------------------------------------------------------------

    private CommandResult GitRaw(string? cwd, params string[] args)
    {
        var all = new List<string> { "-c", "core.hooksPath=/dev/null", "-c", "commit.gpgsign=false" };
        all.AddRange(args);
        return ProcessRunner.RunAsync("git", all, new ProcessOptions
        {
            WorkingDirectory = cwd ?? _repo,
            Timeout = TimeSpan.FromMinutes(2),
            Environment = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0", ["GIT_SSH_COMMAND"] = "ssh -oBatchMode=yes" },
        }).GetAwaiter().GetResult();
    }

    private string Git(params string[] args)
    {
        var result = GitRaw(null, args);
        if (!result.Ok) throw new SkillException($"Git {string.Join(' ', args)}: {result.Stderr.Trim()}");
        return result.Stdout;
    }

    private void Ready()
    {
        SafePath(_repo);
        if (!Directory.Exists(Path.Combine(_repo, ".git"))) throw new SkillException("Initialize the skills repository first");
        SafePath(Path.Combine(_repo, ".git"));
        SafePath(SkillsRoot);
    }

    private string SkillDir(string id)
    {
        Ready();
        ValidId(id);
        var dir = Path.Combine(SkillsRoot, id);
        SafePath(dir);
        if (Directory.Exists(dir)) ValidateTree(dir);
        return dir;
    }

    /// <summary>Clone the remote (or init with it as origin) when the folder is missing.</summary>
    public void Initialize()
    {
        lock (_lock)
        {
            SafePath(_repo);
            if (Directory.Exists(_repo))
            {
                if (!Directory.Exists(Path.Combine(_repo, ".git")))
                {
                    GitRaw(_repo, "init", "-b", "main");
                    GitRaw(_repo, "remote", "add", "origin", Remote);
                }
            }
            else
            {
                var parent = Path.GetDirectoryName(_repo)!;
                SafePath(parent);
                Directory.CreateDirectory(parent);
                if (!GitRaw(parent, "clone", "--", Remote, _repo).Ok)
                {
                    Directory.CreateDirectory(_repo);
                    GitRaw(_repo, "init", "-b", "main");
                    GitRaw(_repo, "remote", "add", "origin", Remote);
                }
            }
            Ready();
            Directory.CreateDirectory(SkillsRoot);
            ValidateTree(SkillsRoot);
        }
    }

    private void Pull()
    {
        Ready();
        if (Directory.Exists(SkillsRoot)) ValidateTree(SkillsRoot);
        if (Git("status", "--porcelain").Trim().Length > 0) throw new SkillException("Dirty repository: preserve changes before pulling");
        Git("fetch", "origin");
        var branch = Git("symbolic-ref", "--short", "HEAD").Trim();
        var remoteRef = $"refs/remotes/origin/{branch}";
        if (!GitRaw(null, "show-ref", "--verify", "--quiet", remoteRef).Ok) return;
        // Inspect incoming skills before checkout: no symlinks or special files.
        foreach (var line in Git("ls-tree", "-r", remoteRef, "--", "skills").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!line.StartsWith("100644 ") && !line.StartsWith("100755 ")) throw new SkillException($"Unsafe incoming skill entry: {line}");
        }
        Git("merge", "--ff-only", remoteRef);
        if (Directory.Exists(SkillsRoot)) ValidateTree(SkillsRoot);
        // Drop our links whose master copy disappeared upstream.
        foreach (var parent in _apps)
        {
            SafePath(parent);
            if (!Directory.Exists(parent)) continue;
            foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
            {
                var id = Path.GetFileName(entry);
                if (!IsValidId(id)) continue;
                var target = Path.Combine(SkillsRoot, id);
                if (Owned(entry, target) && !Directory.Exists(target)) Directory.Delete(entry);
            }
        }
    }

    private void ValidateOutgoing()
    {
        if (!GitRaw(null, "rev-parse", "--verify", "HEAD").Ok) return;
        foreach (var commit in Git("rev-list", "HEAD", "--not", "--remotes=origin").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var paths = Git("diff-tree", "--root", "--no-commit-id", "--name-only", "-r", "-m", "-z", commit.Trim());
            if (!paths.Split('\0', StringSplitOptions.RemoveEmptyEntries).All(p => p.StartsWith("skills/")))
                throw new SkillException($"Unpublished commit contains non-skills changes: {commit}");
        }
    }

    private void Push()
    {
        Ready();
        ValidateTree(SkillsRoot);
        if (Git("diff", "--name-only", "--diff-filter=U").Trim().Length > 0) throw new SkillException("Resolve Git conflicts first");
        var staged = Git("diff", "--cached", "--name-only", "-z");
        if (!staged.Split('\0', StringSplitOptions.RemoveEmptyEntries).All(p => p.StartsWith("skills/")))
            throw new SkillException("Preserve unrelated staged changes before pushing skills");
        ValidateOutgoing();
        Git("add", "-A", "--", "skills");
        if (Git("diff", "--cached", "--name-only").Trim().Length > 0)
        {
            Git("-c", "user.name=DesktopCC", "-c", "user.email=desktopcc@localhost", "commit", "-m", "Update skills", "--", "skills");
        }
        if (!GitRaw(null, "rev-parse", "--verify", "HEAD").Ok) throw new SkillException("No skills to push yet");
        ValidateOutgoing();
        Git("push", "--set-upstream", "origin", "HEAD");
    }

    /// <summary>Pull then push, best effort (offline keeps local changes).</summary>
    public string? Sync()
    {
        lock (_lock)
        {
            if (!Directory.Exists(_repo)) return null;
            Ready();
            string? error = null;
            try { Pull(); } catch (SkillException e) { error = e.Message; }
            try { Push(); } catch (SkillException e) { error ??= e.Message; }
            return error;
        }
    }

    // ---- listing / toggles ------------------------------------------------

    public List<Skill> List()
    {
        lock (_lock)
        {
            SafePath(_repo);
            if (!Directory.Exists(_repo)) return [];
            Ready();
            var result = new List<Skill>();
            if (!Directory.Exists(SkillsRoot)) return result;
            foreach (var path in Directory.EnumerateDirectories(SkillsRoot))
            {
                var id = Path.GetFileName(path);
                if (id.StartsWith('.') || !IsValidId(id) || !File.Exists(Path.Combine(path, "SKILL.md"))) continue;
                var dir = SkillDir(id);
                var (name, description) = Metadata(id, File.ReadAllText(Path.Combine(dir, "SKILL.md")));
                var enabled = new bool[_apps.Length];
                for (var i = 0; i < _apps.Length; i++) enabled[i] = NativeEnabled(id, i, dir);
                result.Add(new Skill(id, name, description, enabled));
            }
            result.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return result;
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    /// <summary>A link we created: a symlink or junction pointing exactly at the master copy.</summary>
    private static bool Owned(string path, string dir)
    {
        try
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists && !File.Exists(path)) return false;
            if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint)) return false;
            var target = info.LinkTarget;
            if (target == null) return false;
            if (!Path.IsPathRooted(target)) target = Path.Combine(Path.GetDirectoryName(path)!, target);
            return string.Equals(Normalize(target), Normalize(dir), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool PathExists(string path) => Directory.Exists(path) || File.Exists(path) || IsReparsePoint(path);

    private static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private bool NativeEnabled(string id, int app, string dir)
    {
        var parent = _apps[app];
        SafePath(parent);
        var path = Path.Combine(parent, id);
        if (Owned(path, dir)) return true;
        if (!PathExists(path)) return false;
        // Probe only the manifest; never traverse or claim an unmanaged tree.
        return File.Exists(Path.Combine(path, "SKILL.md"));
    }

    public void SetEnabled(string id, SkillApp app, bool enabled)
    {
        lock (_lock)
        {
            var dir = SkillDir(id);
            if (!File.Exists(Path.Combine(dir, "SKILL.md"))) throw new SkillException($"Unknown skill: {id}");
            Deploy(id, Array.IndexOf(AllApps, app), enabled, dir);
        }
    }

    private void Deploy(string id, int app, bool enabled, string dir)
    {
        var parent = _apps[app];
        SafePath(parent);
        var path = Path.Combine(parent, id);
        if (Owned(path, dir))
        {
            if (!enabled) Directory.Delete(path);
            return;
        }
        if (PathExists(path)) throw new SkillException($"Unmanaged {Label(AllApps[app])} skill collision: {id}");
        if (!enabled) return;
        Directory.CreateDirectory(parent);
        CreateLink(path, dir);
    }

    /// <summary>Symlink when allowed (developer mode), else a directory junction (no privilege needed).</summary>
    private static void CreateLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            // Fall through to a junction.
        }
        var result = ProcessRunner.RunAsync("cmd.exe", ["/d", "/c", "mklink", "/J", link, target]).GetAwaiter().GetResult();
        if (!result.Ok) throw new SkillException($"Couldn't link {link}: {result.CombinedOutput}");
    }

    // ---- import / backups / edit -----------------------------------------

    public void ImportFolder(string folder)
    {
        lock (_lock)
        {
            Ready();
            Import(Path.GetFullPath(folder));
        }
    }

    private void Import(string folder)
    {
        SafePath(folder);
        ValidateTree(folder);
        if (!Directory.Exists(folder) || !File.Exists(Path.Combine(folder, "SKILL.md")))
            throw new SkillException("Import requires a folder containing SKILL.md");
        var id = Path.GetFileName(folder.TrimEnd('\\', '/'));
        var target = SkillDir(id);
        if (Directory.Exists(target)) throw new SkillException($"Skill already exists: {id}");
        Metadata(id, File.ReadAllText(Path.Combine(folder, "SKILL.md")));
        CopyTree(folder, target);
    }

    /// <summary>Copies unmanaged app skill folders into the store; originals are untouched.</summary>
    public int ImportFromApps()
    {
        lock (_lock)
        {
            Ready();
            var count = 0;
            foreach (var parent in _apps)
            {
                SafePath(parent);
                if (!Directory.Exists(parent)) continue;
                foreach (var entry in Directory.EnumerateDirectories(parent))
                {
                    var id = Path.GetFileName(entry);
                    if (!IsValidId(id) || IsReparsePoint(entry) || !File.Exists(Path.Combine(entry, "SKILL.md"))) continue;
                    var target = SkillDir(id);
                    if (Directory.Exists(target)) continue;
                    ValidateTree(entry);
                    Import(entry);
                    count++;
                }
            }
            return count;
        }
    }

    private string Backup(string id, string dir)
    {
        SafePath(BackupDir);
        Directory.CreateDirectory(BackupDir);
        var key = $"{id}--{DateTime.UtcNow.Ticks}00";
        CopyTree(dir, Path.Combine(BackupDir, key, id));
        return key;
    }

    public List<string> Backups()
    {
        lock (_lock)
        {
            SafePath(_repo);
            if (!Directory.Exists(_repo)) return [];
            Ready();
            if (!Directory.Exists(BackupDir)) return [];
            var result = Directory.EnumerateDirectories(BackupDir).Select(d => { ValidateTree(d); return Path.GetFileName(d); }).ToList();
            result.Sort(StringComparer.Ordinal);
            return result;
        }
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            var dir = SkillDir(id);
            if (!Directory.Exists(dir)) throw new SkillException($"Unknown skill: {id}");
            foreach (var parent in _apps) SafePath(parent);
            Backup(id, dir);
            foreach (var parent in _apps)
            {
                var path = Path.Combine(parent, id);
                if (Owned(path, dir)) Directory.Delete(path);
            }
            ValidateTree(dir);
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Restore a backup ID from <see cref="Backups"/>, backing up the current version first.</summary>
    public void Restore(string backupId)
    {
        lock (_lock)
        {
            Ready();
            ValidId(backupId);
            var source = Path.Combine(BackupDir, backupId);
            SafePath(source);
            ValidateTree(source);
            var entries = Directory.GetFileSystemEntries(source);
            if (entries.Length != 1) throw new SkillException("Invalid backup layout");
            var folder = entries[0];
            var id = Path.GetFileName(folder);
            var target = SkillDir(id);
            if (!File.Exists(Path.Combine(folder, "SKILL.md"))) throw new SkillException("Backup missing SKILL.md");
            var stage = Path.Combine(_repo, ".git", "desktopcc-staging", DateTime.UtcNow.Ticks.ToString());
            SafePath(stage);
            Directory.CreateDirectory(stage);
            var replacement = Path.Combine(stage, "new");
            CopyTree(folder, replacement);
            if (Directory.Exists(target))
            {
                Backup(id, target);
                var previous = Path.Combine(stage, "old");
                Directory.Move(target, previous);
                try
                {
                    Directory.Move(replacement, target);
                }
                catch (IOException)
                {
                    Directory.Move(previous, target);
                    throw;
                }
                Directory.Delete(previous, recursive: true);
            }
            else
            {
                Directory.CreateDirectory(SkillsRoot);
                Directory.Move(replacement, target);
            }
            Directory.Delete(stage);
        }
    }

    /// <summary>Create without overwriting an existing ID.</summary>
    public void CreateSkill(string id, string text)
    {
        lock (_lock)
        {
            if (Directory.Exists(SkillDir(id))) throw new SkillException($"Skill already exists: {id}");
            Save(id, text);
        }
    }

    public void SaveSkill(string id, string text)
    {
        lock (_lock) Save(id, text);
    }

    private void Save(string id, string text)
    {
        var dir = SkillDir(id);
        Metadata(id, text);
        if (Directory.Exists(dir)) Backup(id, dir);
        Directory.CreateDirectory(dir);
        var temp = Path.Combine(dir, $".desktopcc-{DateTime.UtcNow.Ticks}");
        File.WriteAllText(temp, text);
        File.Move(temp, Path.Combine(dir, "SKILL.md"), overwrite: true);
    }

    public string ReadSkill(string id)
    {
        lock (_lock) return File.ReadAllText(Path.Combine(SkillDir(id), "SKILL.md"));
    }

    // ---- safety -----------------------------------------------------------

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex IdRegex();

    public static bool IsValidId(string id) => id.Length > 0 && !id.StartsWith('.') && IdRegex().IsMatch(id);

    private static void ValidId(string id)
    {
        if (!IsValidId(id)) throw new SkillException($"Unsafe skill ID: {id}");
    }

    /// <summary>Reject links (symlinks/junctions) in every existing ancestor.</summary>
    internal static void SafePath(string path)
    {
        if (!Path.IsPathRooted(path)) throw new SkillException($"Expected absolute path: {path}");
        var parts = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(p => p is "." or "..")) throw new SkillException($"Escaping path: {path}");
        var current = Path.GetPathRoot(path) ?? "";
        foreach (var part in parts.Skip(current.Length > 0 ? CountRootParts(current) : 0))
        {
            current = Path.Combine(current, part);
            if (IsReparsePoint(current)) throw new SkillException($"Symlink not allowed: {current}");
        }
    }

    private static int CountRootParts(string root) => root.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Length;

    private static void ValidateTree(string path)
    {
        SafePath(path);
        if (Directory.Exists(path))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                if (Path.GetFileName(entry) == ".git") throw new SkillException("Nested Git metadata not allowed");
                ValidateTree(entry);
            }
        }
        else if (!File.Exists(path))
        {
            throw new SkillException($"Special file not allowed: {path}");
        }
    }

    private static void CopyTree(string source, string target)
    {
        ValidateTree(source);
        SafePath(target);
        if (PathExists(target)) throw new SkillException($"Copy destination exists: {target}");
        if (Directory.Exists(source))
        {
            Directory.CreateDirectory(target);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source)) CopyTree(entry, Path.Combine(target, Path.GetFileName(entry)));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }
    }

    /// <summary>name/description from YAML frontmatter; defaults to the ID.</summary>
    public static (string Name, string Description) Metadata(string id, string text)
    {
        var name = id;
        var description = "";
        var normalized = text.Replace("\r\n", "\n");
        if (normalized.StartsWith("---\n"))
        {
            var rest = normalized[4..];
            var end = rest.IndexOf("\n---", StringComparison.Ordinal);
            if (end < 0) throw new SkillException("Unclosed skill frontmatter");
            try
            {
                var yaml = new YamlStream();
                yaml.Load(new StringReader(rest[..end]));
                if (yaml.Documents.Count > 0 && yaml.Documents[0].RootNode is YamlMappingNode map)
                {
                    foreach (var (k, v) in map.Children)
                    {
                        if (k is YamlScalarNode { Value: "name" } && v is YamlScalarNode { Value: { } n }) name = n;
                        if (k is YamlScalarNode { Value: "description" } && v is YamlScalarNode { Value: { } d }) description = d;
                    }
                }
            }
            catch (YamlDotNet.Core.YamlException)
            {
                throw new SkillException("Invalid skill frontmatter");
            }
        }
        return (name, description);
    }
}
