using System.Text.Json;
using System.Text.RegularExpressions;
using FluentDesktop.Core.Infrastructure;

namespace FluentDesktop.Core.GitHub;

public delegate Task<CommandResult> GithubRunFn(string executable, IReadOnlyList<string> args, string? workingDirectory);

/// <summary>`gh` / `git` operations plus the local repo cache (github_repos.json).</summary>
public sealed class GithubCliService
{
    private const string RepoFields =
        "name,nameWithOwner,description,isPrivate,isArchived,updatedAt,pushedAt,createdAt,visibility,url,stargazerCount,forkCount,primaryLanguage";

    private readonly GithubRunFn _run;

    public GithubCliService(string? reposFilePath = null, GithubRunFn? run = null)
    {
        ReposFilePath = reposFilePath ?? AppPaths.ConfigFile("github_repos.json");
        _run = run ?? ((exe, args, wd) => ProcessRunner.RunAsync(exe, args, new ProcessOptions { WorkingDirectory = wd }));
    }

    public string ReposFilePath { get; }

    public async Task<bool> CheckAuthAsync()
    {
        try { return (await _run("gh", ["auth", "status"], null).ConfigureAwait(false)).Ok; }
        catch { return false; }
    }

    public async Task<List<GithubRepo>> LoadLocalReposAsync() =>
        await JsonStore.LoadAsync<List<GithubRepo>>(ReposFilePath).ConfigureAwait(false) ?? [];

    public Task SaveLocalReposAsync(IEnumerable<GithubRepo> repos) => JsonStore.SaveAsync(ReposFilePath, repos.ToList());

    public async Task<List<GithubRepo>> FetchRemoteReposAsync(int limit = 1000)
    {
        var res = await _run("gh", ["repo", "list", "--limit", $"{limit}", "--json", RepoFields], null).ConfigureAwait(false);
        if (!res.Ok) throw new InvalidOperationException($"`gh repo list` failed: {res.Stderr.Trim()}");
        return JsonSerializer.Deserialize<List<GithubRepo>>(res.Stdout, JsonStore.Options) ?? [];
    }

    /// <summary>Fetches from GitHub and refreshes the cache.</summary>
    public async Task<List<GithubRepo>> SyncRemoteReposAsync(int limit = 1000)
    {
        var repos = await FetchRemoteReposAsync(limit).ConfigureAwait(false);
        await SaveLocalReposAsync(repos).ConfigureAwait(false);
        return repos;
    }

    public Task<CommandResult> DeleteRepoAsync(string repo) => _run("gh", ["repo", "delete", repo, "--yes"], null);

    public Task<CommandResult> ArchiveRepoAsync(string repo) => _run("gh", ["repo", "archive", repo, "--yes"], null);

    public Task<CommandResult> SetVisibilityAsync(string repo, bool makePrivate) =>
        _run("gh", ["repo", "edit", repo, "--visibility", makePrivate ? "private" : "public", "--accept-visibility-change-consequences"], null);

    public async IAsyncEnumerable<BatchProgress> ExecuteBatchAsync(IReadOnlyList<string> repos, GithubBatchAction action)
    {
        var results = new List<BatchItemResult>();
        for (var i = 0; i < repos.Count; i++)
        {
            var repo = repos[i];
            yield return new BatchProgress(i, repos.Count, repo, results.ToList(), false);
            BatchItemResult result;
            try
            {
                var res = action switch
                {
                    GithubBatchAction.Delete => await DeleteRepoAsync(repo).ConfigureAwait(false),
                    GithubBatchAction.Archive => await ArchiveRepoAsync(repo).ConfigureAwait(false),
                    GithubBatchAction.SetPrivate => await SetVisibilityAsync(repo, true).ConfigureAwait(false),
                    _ => await SetVisibilityAsync(repo, false).ConfigureAwait(false),
                };
                var err = res.Stderr.Trim();
                result = res.Ok
                    ? new BatchItemResult(repo, true, "Success")
                    : new BatchItemResult(repo, false, err.Length > 0 ? err : $"Exit code {res.ExitCode}");
            }
            catch (Exception e)
            {
                result = new BatchItemResult(repo, false, e.Message);
            }
            results.Add(result);
            yield return new BatchProgress(i + 1, repos.Count, repo, results.ToList(), i == repos.Count - 1);
        }
    }

    public static List<string> BuildCreateArgs(CreateRepoRequest r)
    {
        var args = new List<string> { "repo", "create", r.Name, r.IsPrivate ? "--private" : "--public" };
        if (r.Description.Length > 0) args.AddRange(["--description", r.Description]);
        if (r.AddReadme) args.Add("--add-readme");
        var gi = r.GitignoreTemplate?.Trim() ?? "";
        if (gi.Length > 0 && !gi.Equals("none", StringComparison.OrdinalIgnoreCase)) args.AddRange(["--gitignore", gi]);
        var lic = r.LicenseTemplate?.Trim() ?? "";
        if (lic.Length > 0 && !lic.Equals("none", StringComparison.OrdinalIgnoreCase)) args.AddRange(["--license", lic]);
        if (r.CloneAfterCreate) args.Add("--clone");
        return args;
    }

    public async Task<GithubOperationResult> CreateRepoAsync(CreateRepoRequest request)
    {
        var name = request.Name.Trim();
        if (!GithubRules.IsValidRepoName(name))
        {
            return GithubOperationResult.Failure($"Invalid repository name \"{name}\". Use letters, digits, \".\", \"-\" or \"_\" (max 100 chars).");
        }
        var logs = new List<string>();
        try
        {
            var args = BuildCreateArgs(request with { Name = name, Description = request.Description.Trim() });
            logs.Add($"$ gh {string.Join(' ', args)}");
            var res = await _run("gh", args, request.CloneDirectory).ConfigureAwait(false);
            AddOutput(logs, res);
            if (!res.Ok)
            {
                var err = res.Stderr.Trim();
                return GithubOperationResult.Failure(err.Length > 0 ? err : $"gh exited with code {res.ExitCode}", logs);
            }
            await TryRefreshCacheAsync().ConfigureAwait(false);
            return GithubOperationResult.Ok($"Repository \"{name}\" created.", logs);
        }
        catch (Exception e)
        {
            return GithubOperationResult.Failure($"Failed to create repo: {e.Message}", logs);
        }
    }

    public async Task<LocalRepoInfo> InspectLocalDirAsync(string rawPath)
    {
        var path = rawPath.Trim();
        if (path.Length == 0 || !Directory.Exists(path)) return new LocalRepoInfo(path, false, false);
        try
        {
            var top = await _run("git", ["rev-parse", "--show-toplevel"], path).ConfigureAwait(false);
            if (!top.Ok) return new LocalRepoInfo(path, true, false);

            var branchRes = await _run("git", ["branch", "--show-current"], path).ConfigureAwait(false);
            var branch = branchRes.Ok && branchRes.Stdout.Trim() is { Length: > 0 } b ? b : null;

            var remotes = new Dictionary<string, string>();
            var remoteRes = await _run("git", ["remote", "-v"], path).ConfigureAwait(false);
            if (remoteRes.Ok)
            {
                foreach (var line in remoteRes.Stdout.Split('\n'))
                {
                    var trimmed = line.Trim();
                    if (trimmed.Length == 0 || !trimmed.Contains("(fetch)")) continue;
                    var parts = Regex.Split(trimmed, @"\s+");
                    if (parts.Length >= 2) remotes[parts[0]] = parts[1];
                }
            }

            var status = await _run("git", ["status", "--porcelain"], path).ConfigureAwait(false);
            var dirty = status.Ok && status.Stdout.Trim().Length > 0;
            var topLevel = top.Stdout.Trim();
            return new LocalRepoInfo(path, true, true, branch, remotes, dirty, topLevel.Length == 0 ? null : topLevel);
        }
        catch
        {
            return new LocalRepoInfo(path, true, false);
        }
    }

    public async Task<GithubOperationResult> InitLocalRepoAsync(string rawPath)
    {
        var path = rawPath.Trim();
        if (path.Length == 0 || !Directory.Exists(path)) return GithubOperationResult.Failure($"Local directory does not exist: {path}");
        var logs = new List<string>();
        try
        {
            if ((await _run("git", ["rev-parse", "--git-dir"], path).ConfigureAwait(false)).Ok)
            {
                return GithubOperationResult.Ok("Already a git repository.");
            }
            logs.Add($"$ git init  (in {path})");
            var init = await _run("git", ["init"], path).ConfigureAwait(false);
            AddOutput(logs, init);
            if (!init.Ok)
            {
                var err = init.Stderr.Trim();
                return GithubOperationResult.Failure(err.Length > 0 ? $"`git init` failed: {err}" : "`git init` failed", logs);
            }
            return GithubOperationResult.Ok($"Initialized git repository in {path}.", logs);
        }
        catch (Exception e)
        {
            return GithubOperationResult.Failure($"Failed to run `git init`: {e.Message}", logs);
        }
    }

    /// <summary>
    /// New repo name → <c>gh repo create &lt;name&gt; --source .</c>; existing
    /// owner/repo → git init (if needed), add/set remote, optional push.
    /// </summary>
    public async Task<GithubOperationResult> ConnectLocalToRemoteAsync(ConnectLocalRequest request)
    {
        var logs = new List<string>();
        var path = request.LocalPath.Trim();
        if (path.Length == 0 || !Directory.Exists(path)) return GithubOperationResult.Failure($"Local directory does not exist: {path}");
        GithubOperationResult Fail(string msg) => GithubOperationResult.Failure(msg, logs);

        try
        {
            var newName = request.NewRepoName?.Trim() ?? "";
            if (newName.Length > 0)
            {
                if (!GithubRules.IsValidRepoName(newName))
                {
                    return Fail($"Invalid repository name \"{newName}\". Use letters, digits, \".\", \"-\" or \"_\" (max 100 chars).");
                }
                var args = new List<string> { "repo", "create", newName, "--source", ".", request.NewRepoPrivate ? "--private" : "--public" };
                if (request.PushAfterConnect) args.Add("--push");
                if (request.RemoteName != "origin") args.AddRange(["--remote", request.RemoteName]);
                logs.Add($"$ gh {string.Join(' ', args)}  (in {path})");
                var res = await _run("gh", args, path).ConfigureAwait(false);
                AddOutput(logs, res);
                if (!res.Ok)
                {
                    var err = res.Stderr.Trim();
                    return Fail(err.Length > 0 ? err : $"gh exited with code {res.ExitCode}");
                }
                await TryRefreshCacheAsync().ConfigureAwait(false);
                return GithubOperationResult.Ok($"Created \"{newName}\" from {path}.", logs);
            }

            var remote = request.ExistingRemoteName?.Trim() ?? "";
            if (remote.Length == 0 || !remote.Contains('/'))
            {
                return Fail("Enter an existing remote as \"owner/repo\", or provide a new repository name.");
            }
            var remoteName = string.IsNullOrWhiteSpace(request.RemoteName) ? "origin" : request.RemoteName.Trim();
            var url = $"https://github.com/{remote}.git";

            async Task<CommandResult> Git(params string[] args)
            {
                logs.Add($"$ git {string.Join(' ', args)}");
                return await _run("git", args, path).ConfigureAwait(false);
            }

            if (!(await _run("git", ["rev-parse", "--git-dir"], path).ConfigureAwait(false)).Ok)
            {
                var init = await Git("init");
                if (!init.Ok) return Fail($"`git init` failed: {init.Stderr}");
                logs.Add(init.Stdout.Trim());
            }

            var hasRemote = (await _run("git", ["remote", "get-url", remoteName], path).ConfigureAwait(false)).Ok;
            var set = hasRemote ? await Git("remote", "set-url", remoteName, url) : await Git("remote", "add", remoteName, url);
            if (!set.Ok) return Fail($"`git remote {(hasRemote ? "set-url" : "add")}` failed: {set.Stderr}");

            if (request.PushAfterConnect)
            {
                var branch = string.IsNullOrWhiteSpace(request.Branch) ? "HEAD" : request.Branch.Trim();
                var push = await Git("push", "-u", remoteName, branch);
                if (push.Stderr.Trim().Length > 0) logs.Add(push.Stderr.Trim());
                if (!push.Ok) return Fail($"`git push` failed: {push.Stderr}");
            }

            await TryRefreshCacheAsync().ConfigureAwait(false);
            return GithubOperationResult.Ok($"Connected {path} to {remote}.", logs, remote);
        }
        catch (Exception e)
        {
            return GithubOperationResult.Failure($"Failed to connect local repo: {e.Message}", logs);
        }
    }

    private async Task TryRefreshCacheAsync()
    {
        try { await SyncRemoteReposAsync().ConfigureAwait(false); }
        catch { /* best effort; the operation itself succeeded */ }
    }

    private static void AddOutput(List<string> logs, CommandResult res)
    {
        if (res.Stdout.Trim() is { Length: > 0 } o) logs.Add(o);
        if (res.Stderr.Trim() is { Length: > 0 } e) logs.Add(e);
    }
}
