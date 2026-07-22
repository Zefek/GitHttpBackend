using System.Diagnostics;

namespace GitHttpBackend;

/// <summary>
/// The response produced by git-http-backend: parsed status and headers, plus a
/// <see cref="Body"/> stream to copy to the client. Owns the backend process;
/// dispose it (ideally with <c>await using</c>) once the body has been consumed.
/// </summary>
public sealed class GitBackendResponse : IAsyncDisposable
{
    readonly Process _process;
    readonly Task _stdinTask;
    readonly Task<string> _stderrTask;

    internal GitBackendResponse(
        int statusCode,
        string reasonPhrase,
        IReadOnlyList<KeyValuePair<string, string>> headers,
        Stream body,
        Process process,
        Task stdinTask,
        Task<string> stderrTask)
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        Headers = headers;
        Body = body;
        _process = process;
        _stdinTask = stdinTask;
        _stderrTask = stderrTask;
    }

    /// <summary>HTTP status code (from the CGI <c>Status:</c> header, or 200).</summary>
    public int StatusCode { get; }

    /// <summary>Reason phrase that accompanied the status, if any.</summary>
    public string ReasonPhrase { get; }

    /// <summary>Response headers emitted by git-http-backend (Content-Type, etc.).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; }

    /// <summary>Response body. Read/copy this to the client, then dispose the response.</summary>
    public Stream Body { get; }

    /// <summary>Everything git-http-backend wrote to stderr (diagnostics).</summary>
    public async Task<string> ReadErrorOutputAsync()
    {
        try 
        { 
            return await _stderrTask.ConfigureAwait(false); 
        }
        catch
        {
            return ""; 
        }
    }

    public async ValueTask DisposeAsync()
    {
        // The stdin pump should be done once the backend has produced its full response,
        // but never let disposal hang on it.
        try { await _stdinTask.ConfigureAwait(false); } catch { /* ignore */ }

        try
        {
            if (!_process.HasExited)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                }
            }
        }
        catch { /* ignore */ }

        _process.Dispose();
    }
}
