using System.Diagnostics;
using System.Globalization;

namespace GitHttpBackend;

/// <summary>
/// Runs Git's <c>git-http-backend</c> CGI for a single request: sets up the CGI
/// environment, pumps the request body to its stdin, and parses its stdout into a
/// <see cref="GitBackendResponse"/>. Thread-safe and reusable across requests.
/// </summary>
public sealed class GitHttpBackendInvoker
{
    readonly GitBackendOptions _options;
    readonly string _backendPath;
    readonly string _execDir;
    readonly GitRepositoryCreator? _creator;

    public GitHttpBackendInvoker(GitBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrEmpty(options.ProjectRoot);

        _options = options;
        _backendPath = options.BackendPath
            ?? GitBackendLocator.Locate()
            ?? throw new InvalidOperationException(
                "git-http-backend was not found. Install Git, or set GitBackendOptions.BackendPath explicitly.");

        if (!File.Exists(_backendPath))
            throw new FileNotFoundException("git-http-backend was not found at the configured path.", _backendPath);

        _execDir = Path.GetDirectoryName(_backendPath)
            ?? throw new InvalidOperationException(
                $"The git-http-backend path '{_backendPath}' has no parent directory.");

        // Resolved up front rather than on the first push, so a host that asked for
        // create-on-push finds out at startup that it cannot do it.
        if (options.AllowCreateOnPush)
        {
            _creator = new GitRepositoryCreator(ResolveGitClient(_execDir), _execDir, options);
        }
    }

    /// <summary>The resolved path to the git-http-backend executable.</summary>
    public string BackendPath => _backendPath;

    /// <summary>
    /// Creates the target repository when <see cref="GitBackendOptions.AllowCreateOnPush"/>
    /// is set, <paramref name="request"/> is a push, and the repository does not exist yet.
    /// A clone or fetch never creates anything.
    /// <para>
    /// Call this after the authorization decision and before <see cref="InvokeAsync"/>:
    /// it creates whatever the caller asked for, so authorization is what gates it.
    /// </para>
    /// </summary>
    /// <returns><c>true</c> when a repository was created.</returns>
    /// <exception cref="InvalidOperationException">Creation was attempted and failed.</exception>
    public async Task<bool> EnsureRepositoryForPushAsync(CgiRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_creator is null)
            return false;

        // The name comes from the one parser the routing and the authorization hook also use;
        // an invalid path never gets as far as touching the filesystem.
        if (!GitRepositoryPath.TryParse(request.PathInfo, out var repository, out var rest))
            return false;

        if (!GitRepositoryCreator.IsPush(request.Method, rest, request.QueryString))
            return false;

        return await _creator.EnsureExistsAsync(repository, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Call after a <c>git-receive-pack</c> POST has been relayed: points <c>HEAD</c> at the
    /// branch that was pushed, when the repository still has the unborn <c>HEAD</c> that
    /// <c>git init --bare</c> left behind. Without it, a repository created by a push of
    /// <c>main</c> clones out empty. No-op unless
    /// <see cref="GitBackendOptions.AllowCreateOnPush"/> is set.
    /// </summary>
    public async Task AlignHeadAfterPushAsync(CgiRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_creator is null)
            return;

        if (!string.Equals(request.Method, "POST", StringComparison.OrdinalIgnoreCase))
            return;

        if (!GitRepositoryPath.TryParse(request.PathInfo, out var repository, out var rest)
            || !string.Equals(rest, "/git-receive-pack", StringComparison.Ordinal))
        {
            return;
        }

        await _creator.AlignHeadAsync(repository, ct).ConfigureAwait(false);
    }

    // Git for Windows ships git.exe inside libexec/git-core next to git-http-backend, and so
    // do the Linux packages. Taking it from there keeps the client and the backend on one
    // installation; the PATH search is only a fallback for layouts that split them.
    static string ResolveGitClient(string execDir)
    {
        var exe = OperatingSystem.IsWindows() ? "git.exe" : "git";
        var candidate = Path.Combine(execDir, exe);
        if (File.Exists(candidate))
            return candidate;

        return GitBackendLocator.LocateGit()
            ?? throw new InvalidOperationException(
                "AllowCreateOnPush is enabled but the git client executable was not found. "
                + "Install Git, or leave AllowCreateOnPush off.");
    }

    public async Task<GitBackendResponse> InvokeAsync(CgiRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var psi = new ProcessStartInfo
        {
            FileName = _backendPath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _execDir,
        };

        PopulateEnvironment(psi.Environment, request);

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.Start();

        // Pump request body -> stdin and drain stderr concurrently with reading stdout,
        // so a large body or verbose diagnostics can't deadlock on a full pipe buffer.
        var stdinTask = PumpStdinAsync(process, request.Body, ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            var stdout = process.StandardOutput.BaseStream;
            var parsed = await CgiHeaderParser.ReadAsync(stdout, ct).ConfigureAwait(false);

            var body = new ConcatStream(parsed.Leftover, stdout);
            return new GitBackendResponse(
                parsed.StatusCode, parsed.ReasonPhrase, parsed.Headers, body,
                process, stdinTask, stderrTask);
        }
        catch
        {
            try 
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { /* ignore */ }
            process.Dispose();
            throw;
        }
    }

    // Builds the CGI environment git-http-backend expects for this request.
    void PopulateEnvironment(IDictionary<string, string?> env, CgiRequest request)
    {
        env["GIT_PROJECT_ROOT"] = _options.ProjectRoot;
        if (_options.ExportAll)
        {
            env["GIT_HTTP_EXPORT_ALL"] = "1";
        }
        // Help the backend locate git-upload-pack / git-receive-pack.
        env["GIT_EXEC_PATH"] = _execDir;

        env["REQUEST_METHOD"] = request.Method;
        env["PATH_INFO"] = request.PathInfo;
        env["QUERY_STRING"] = request.QueryString;
        env["REMOTE_ADDR"] = request.RemoteAddr;

        if (request.ContentType is not null)
        {
            env["CONTENT_TYPE"] = request.ContentType;
        }

        if (request.ContentLength is { } len)
        {
            env["CONTENT_LENGTH"] = len.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(request.ContentEncoding))
        {
            env["HTTP_CONTENT_ENCODING"] = request.ContentEncoding;
        }

        if (!string.IsNullOrEmpty(request.GitProtocol))
        {
            env["GIT_PROTOCOL"] = request.GitProtocol;
        }

        if (!string.IsNullOrEmpty(request.RemoteUser))
        {
            env["REMOTE_USER"] = request.RemoteUser;
        }

        if (_options.ExtraEnvironment is not null)
        {
            foreach (var kv in _options.ExtraEnvironment)
            {
                env[kv.Key] = kv.Value;
            }
        }

        // Appended after ExtraEnvironment so a caller-supplied GIT_CONFIG_COUNT is extended,
        // not overwritten. safe.directory is only honoured from system/global/env config —
        // it cannot be set from the repository itself.
        if (_options.SafeDirectories is { Count: > 0 } safeDirectories)
        {
            AppendSafeDirectories(env, safeDirectories);
        }
    }

    internal static void AppendSafeDirectories(IDictionary<string, string?> env, IReadOnlyList<string> safeDirectories)
    {
        var index = env.TryGetValue("GIT_CONFIG_COUNT", out var existing)
            && int.TryParse(existing, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count)
            && count > 0
                ? count
                : 0;

        foreach (var directory in safeDirectories)
        {
            env[$"GIT_CONFIG_KEY_{index}"] = "safe.directory";
            env[$"GIT_CONFIG_VALUE_{index}"] = directory;
            index++;
        }

        env["GIT_CONFIG_COUNT"] = index.ToString(CultureInfo.InvariantCulture);
    }

    static async Task PumpStdinAsync(Process process, Stream body, CancellationToken ct)
    {
        try
        {
            var stdin = process.StandardInput.BaseStream;
            await body.CopyToAsync(stdin, ct).ConfigureAwait(false);
            await stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (Exception) when (process.HasExited)
        {
            // Backend already closed its input (e.g. GET, or it stopped reading). Not fatal.
        }
        finally
        {
            try { process.StandardInput.Close(); } catch { /* ignore */ }
        }
    }
}
