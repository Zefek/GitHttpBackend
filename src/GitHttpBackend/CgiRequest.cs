namespace GitHttpBackend;

/// <summary>
/// Host-agnostic view of an incoming Git Smart HTTP request. The ASP.NET Core adapter
/// builds this from an <c>HttpContext</c>; a different host would map its own primitives.
/// </summary>
public sealed class CgiRequest
{
    /// <summary>HTTP method, <c>GET</c> or <c>POST</c>. Maps to <c>REQUEST_METHOD</c>.</summary>
    public required string Method { get; init; }

    /// <summary>
    /// Path relative to the project root, with a leading slash,
    /// e.g. <c>/projekt.git/info/refs</c>. Maps to <c>PATH_INFO</c>.
    /// </summary>
    public required string PathInfo { get; init; }

    /// <summary>Query string without the leading <c>?</c>. Maps to <c>QUERY_STRING</c>.</summary>
    public string QueryString { get; init; } = "";

    /// <summary>Request content type. Maps to <c>CONTENT_TYPE</c>.</summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// Request body length when known. Maps to <c>CONTENT_LENGTH</c>. When <c>null</c>
    /// (chunked upload), the backend reads the body stream until EOF.
    /// </summary>
    public long? ContentLength { get; init; }

    /// <summary>
    /// Value of the request <c>Content-Encoding</c> header (e.g. <c>gzip</c>).
    /// Maps to <c>HTTP_CONTENT_ENCODING</c>; git-http-backend inflates the body itself.
    /// </summary>
    public string? ContentEncoding { get; init; }

    /// <summary>
    /// Value of the request <c>Git-Protocol</c> header (e.g. <c>version=2</c>).
    /// Maps to <c>GIT_PROTOCOL</c>. Required for protocol v2 to engage.
    /// </summary>
    public string? GitProtocol { get; init; }

    /// <summary>Client address. Maps to <c>REMOTE_ADDR</c>.</summary>
    public string RemoteAddr { get; init; } = "";

    /// <summary>Authenticated user name, if any. Maps to <c>REMOTE_USER</c>.</summary>
    public string? RemoteUser { get; init; }

    /// <summary>The request body stream. Empty (but non-null) for GET requests.</summary>
    public required Stream Body { get; init; }
}
