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
    }

    /// <summary>The resolved path to the git-http-backend executable.</summary>
    public string BackendPath => _backendPath;

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

    static void AppendSafeDirectories(IDictionary<string, string?> env, IReadOnlyList<string> safeDirectories)
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
