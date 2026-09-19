using System.Collections.Concurrent;
using System.Diagnostics;

namespace GitHttpBackend;

/// <summary>
/// Creates a bare repository under <c>ProjectRoot</c> for a push that targets a name which
/// does not exist yet. Used only when <see cref="GitBackendOptions.AllowCreateOnPush"/> is set,
/// and only after the caller has been authorized.
/// </summary>
internal sealed class GitRepositoryCreator
{
    // Two simultaneous pushes to the same new name must not race into a half-created
    // repository. The key is the resolved directory, matched the way the filesystem matches it.
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    readonly string _gitPath;
    readonly string _execDir;
    readonly GitBackendOptions _options;

    public GitRepositoryCreator(string gitPath, string execDir, GitBackendOptions options)
    {
        _gitPath = gitPath;
        _execDir = execDir;
        _options = options;
    }

    /// <summary>
    /// True when this request is the ref advertisement or the POST of a push. Both count:
    /// <c>git push</c> asks for the advertisement first, and if that 404s the push never
    /// reaches the POST. A <c>git-upload-pack</c> request (clone, fetch) is never a push.
    /// </summary>
    public static bool IsPush(string method, string rest, string queryString)
    {
        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            return string.Equals(rest, "/git-receive-pack", StringComparison.Ordinal);

        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            return string.Equals(rest, "/info/refs", StringComparison.Ordinal)
                && HasService(queryString, "git-receive-pack");

        return false;
    }

    /// <summary>
    /// Creates <paramref name="repository"/> as a bare repository if no repository of that
    /// name is already reachable. Returns <c>true</c> when one was created.
    /// </summary>
    /// <exception cref="InvalidOperationException">Creation was attempted and failed.</exception>
    public async Task<bool> EnsureExistsAsync(string repository, CancellationToken ct)
    {
        var rootFull = Path.GetFullPath(_options.ProjectRoot);
        var target = Path.GetFullPath(Path.Combine(rootFull, repository));

        // GitRepositoryPath has already rejected everything that could escape, so this is a
        // belt-and-braces check on the one operation that writes to disk.
        if (!target.StartsWith(
                rootFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The repository path resolved outside the configured project root.");
        }

        if (ExistingRepository(rootFull, repository) is not null)
            return false;

        var gate = Locks.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another request may have created it while we waited.
            if (ExistingRepository(rootFull, repository) is not null)
                return false;

            await CreateAsync(target, ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    // The name git-http-backend's enter_repo() would find: what the client asked for, or the
    // same name with the .git suffix added or removed. Creating a second directory for a
    // repository that is already reachable under one of those names must not happen.
    static string? ExistingRepository(string rootFull, string repository)
    {
        foreach (var candidate in Candidates(repository))
        {
            var path = Path.Combine(rootFull, candidate);
            if (Directory.Exists(path))
                return path;
        }
        return null;
    }

    static IEnumerable<string> Candidates(string repository)
    {
        yield return repository;
        yield return repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? repository[..^4]
            : repository + ".git";
    }

    async Task CreateAsync(string target, CancellationToken ct)
    {
        var createdDirectory = false;
        try
        {
            createdDirectory = !Directory.Exists(target);
            Directory.CreateDirectory(target);

            await RunGitAsync(ct, "init", "--bare", "--", target).ConfigureAwait(false);

            // Without this git-http-backend refuses the push regardless of anything this
            // library does. A repository that rejects the very push that created it is a trap.
            await RunGitAsync(ct, "-C", target, "config", "http.receivepack", "true").ConfigureAwait(false);

            // With ExportAll off, publication is the marker file — a repository created by a
            // push that immediately 404s would be worse than not creating it at all.
            if (!_options.ExportAll)
            {
                await File.WriteAllBytesAsync(
                    Path.Combine(target, "git-daemon-export-ok"), [], ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // A half-created directory would keep looking like an existing repository and block
            // every later attempt, so the failed attempt takes its own leftovers with it.
            if (createdDirectory)
            {
                try { Directory.Delete(target, recursive: true); } catch { /* best effort */ }
            }
            throw;
        }
    }

    /// <summary>
    /// Points <c>HEAD</c> at the pushed branch when a freshly created repository was pushed to
    /// under a different branch name than <c>git init</c> chose.
    /// </summary>
    /// <remarks>
    /// <c>git init --bare</c> writes <c>HEAD -&gt; refs/heads/master</c> (or whatever
    /// <c>init.defaultBranch</c> says) and <c>receive-pack</c> does not revise it. Push
    /// <c>main</c> to such a repository and the push succeeds, but every later clone checks
    /// out nothing, because HEAD names a branch that was never created. Fixing that from the
    /// server is the whole point of create-on-push — needing a shell to run
    /// <c>git symbolic-ref</c> afterwards would put back the step this feature removes.
    /// <para>
    /// Only ever touches a HEAD that resolves to nothing, and only when exactly one branch
    /// exists, so there is no case where it overrides a deliberate choice.
    /// </para>
    /// </remarks>
    public async Task AlignHeadAsync(string repository, CancellationToken ct)
    {
        var rootFull = Path.GetFullPath(_options.ProjectRoot);
        if (ExistingRepository(rootFull, repository) is not { } path)
            return;

        // A HEAD that resolves is either the branch just pushed or a deliberate setting.
        if ((await ExecuteGitAsync(ct, "-C", path, "rev-parse", "--verify", "--quiet", "HEAD")
                .ConfigureAwait(false)).ExitCode == 0)
        {
            return;
        }

        var branches = await ExecuteGitAsync(ct,
            "-C", path, "for-each-ref", "--format=%(refname)", "refs/heads/").ConfigureAwait(false);
        if (branches.ExitCode != 0)
            return;

        var refs = branches.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (refs.Length != 1)
            return;

        await ExecuteGitAsync(ct, "-C", path, "symbolic-ref", "HEAD", refs[0]).ConfigureAwait(false);
    }

    async Task RunGitAsync(CancellationToken ct, params string[] arguments)
    {
        var result = await ExecuteGitAsync(ct, arguments).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"git {string.Join(' ', arguments)} exited with {result.ExitCode}: "
                + (result.StdErr.Length > 0 ? result.StdErr : "(no stderr)"));
        }
    }

    async Task<(int ExitCode, string StdOut, string StdErr)> ExecuteGitAsync(
        CancellationToken ct, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gitPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _execDir,
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        // Same environment the backend process gets, for the same reason: without
        // safe.directory a service account hits "dubious ownership" the moment git opens the
        // repository it just initialised.
        psi.Environment["GIT_EXEC_PATH"] = _execDir;
        if (_options.ExtraEnvironment is not null)
        {
            foreach (var kv in _options.ExtraEnvironment)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }
        if (_options.SafeDirectories is { Count: > 0 } safeDirectories)
        {
            GitHttpBackendInvoker.AppendSafeDirectories(psi.Environment, safeDirectories);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{_gitPath}'.");

        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return (process.ExitCode,
            (await stdoutTask.ConfigureAwait(false)).Trim(),
            (await stderrTask.ConfigureAwait(false)).Trim());
    }

    static bool HasService(string queryString, string service)
    {
        foreach (var pair in queryString.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq < 0)
                continue;

            if (pair.AsSpan(0, eq).SequenceEqual("service")
                && pair.AsSpan(eq + 1).SequenceEqual(service))
            {
                return true;
            }
        }
        return false;
    }
}
