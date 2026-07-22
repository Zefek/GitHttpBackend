namespace GitHttpBackend;

/// <summary>
/// Configuration for <see cref="GitHttpBackendInvoker"/>.
/// </summary>
public sealed class GitBackendOptions
{
    /// <summary>
    /// Directory that contains the (bare) git repositories, e.g. <c>C:\git-repos</c>
    /// with <c>C:\git-repos\projekt.git</c> inside. Maps to <c>GIT_PROJECT_ROOT</c>.
    /// </summary>
    public required string ProjectRoot { get; init; }

    /// <summary>
    /// Export every repository without requiring a <c>git-daemon-export-ok</c> marker file.
    /// Maps to <c>GIT_HTTP_EXPORT_ALL</c>. Default <c>true</c>.
    /// </summary>
    public bool ExportAll { get; init; } = true;

    /// <summary>
    /// Full path to <c>git-http-backend(.exe)</c>. When <c>null</c> it is auto-detected
    /// via <see cref="GitBackendLocator.Locate"/>.
    /// </summary>
    public string? BackendPath { get; init; }

    /// <summary>
    /// Extra environment variables handed to the git-http-backend process. Applied last,
    /// so they win over the variables this library sets.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }

    /// <summary>
    /// Optional authorization hook, evaluated before the request reaches git.
    /// Return <c>false</c> to reject with 403. Gating <c>git-receive-pack</c> (push)
    /// is the typical use.
    /// </summary>
    public Func<CgiRequest, ValueTask<bool>>? Authorize { get; init; }
}
