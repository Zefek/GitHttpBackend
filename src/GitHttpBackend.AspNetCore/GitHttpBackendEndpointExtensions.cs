using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GitHttpBackend.AspNetCore;

/// <summary>
/// Endpoint routing extensions that expose Git repositories over Smart HTTP.
/// </summary>
public static class GitHttpBackendEndpointExtensions
{
    /// <summary>
    /// Maps Git Smart HTTP endpoints (clone / fetch / push) under <paramref name="prefix"/>.
    /// A client then uses e.g. <c>{prefix}/projekt.git</c> as the remote URL.
    /// </summary>
    public static IEndpointConventionBuilder MapGitHttpBackend(
        this IEndpointRouteBuilder endpoints, string prefix, GitBackendOptions options)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(options);

        // Constructed once: resolves and validates the backend path up front.
        return endpoints.MapGitHttpBackend(prefix, new GitHttpBackendInvoker(options));
    }

    /// <summary>
    /// Maps Git Smart HTTP endpoints under <paramref name="prefix"/> using an invoker the
    /// caller already built.
    /// </summary>
    /// <remarks>
    /// Resolving <c>git-http-backend</c> starts a <c>git --exec-path</c> process and checks the
    /// filesystem, so a host that also wants the resolved path — to log it at startup, say —
    /// can build the invoker itself, read <see cref="GitHttpBackendInvoker.BackendPath"/>, and
    /// hand the same instance here rather than paying for the lookup twice. It also means a
    /// bad backend path fails before that log line rather than after it.
    /// </remarks>
    public static IEndpointConventionBuilder MapGitHttpBackend(
        this IEndpointRouteBuilder endpoints, string prefix, GitHttpBackendInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(invoker);

        var normalizedPrefix = "/" + prefix.Trim('/');
        var pattern = (normalizedPrefix == "/" ? "" : normalizedPrefix) + "/{**gitPath}";

        return endpoints.MapMethods(pattern, new[] { HttpMethods.Get, HttpMethods.Post },
            (HttpContext ctx) => HandleAsync(ctx, invoker));
    }

    static async Task HandleAsync(HttpContext ctx, GitHttpBackendInvoker invoker)
    {
        var options = invoker.Options;

        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GitHttpBackend.AspNetCore");

        var gitPath = ctx.Request.RouteValues["gitPath"] as string ?? "";
        var pathInfo = "/" + gitPath;

        // Rejected here, before the Authorize hook and before any process starts: a path the
        // validator and git could resolve differently is precisely the one an authorization
        // decision must never be made about.
        if (!GitRepositoryPath.TryParse(pathInfo, out _, out _))
        {
            logger.LogWarning("Git request rejected, malformed repository path: {Method} {PathInfo}",
                ForLog(ctx.Request.Method), ForLog(pathInfo));
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var request = new CgiRequest
        {
            Method = ctx.Request.Method,
            PathInfo = pathInfo,
            QueryString = ctx.Request.QueryString.Value?.TrimStart('?') ?? "",
            ContentType = ctx.Request.ContentType,
            ContentLength = ctx.Request.ContentLength,
            ContentEncoding = ctx.Request.Headers.ContentEncoding.ToString() is { Length: > 0 } ce ? ce : null,
            GitProtocol = ctx.Request.Headers["Git-Protocol"].ToString() is { Length: > 0 } gp ? gp : null,
            RemoteAddr = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
            RemoteUser = ctx.User.Identity?.IsAuthenticated == true ? ctx.User.Identity.Name : null,
            Body = ctx.Request.Body,
        };

        if (options.Authorize is not null && !await options.Authorize(request))
        {
            logger.LogWarning("Git request denied by Authorize hook: {Method} {PathInfo} (user {User})",
                ForLog(request.Method), ForLog(request.PathInfo), ForLog(request.RemoteUser) ?? "-");
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        // After Authorize, so authorization is what gates creation: a caller can only create a
        // repository it would have been allowed to push to.
        if (options.AllowCreateOnPush)
        {
            try
            {
                if (await invoker.EnsureRepositoryForPushAsync(request, ctx.RequestAborted))
                {
                    logger.LogInformation("Created repository on push: {PathInfo} (user {User})",
                        ForLog(request.PathInfo), ForLog(request.RemoteUser) ?? "-");
                }
            }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
            {
                throw;   // client went away; not a server error
            }
            catch (Exception ex)
            {
                // Falling through to the backend would produce a bare, reasonless 500 — the
                // exact failure mode the stderr logging below was added to eliminate.
                logger.LogError(ex, "Failed to create repository on push: {PathInfo} (user {User})",
                    ForLog(request.PathInfo), ForLog(request.RemoteUser) ?? "-");
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return;
            }
        }

        logger.LogDebug("Invoking git-http-backend: {Method} PATH_INFO={PathInfo} QUERY_STRING={QueryString}",
            ForLog(request.Method), ForLog(request.PathInfo), ForLog(request.QueryString));

        await using var response = await invoker.InvokeAsync(request, ctx.RequestAborted);

        ctx.Response.StatusCode = response.StatusCode;
        foreach (var header in response.Headers)
        {
            ctx.Response.Headers[header.Key] = header.Value;
        }

        // Packs can be large and are produced incrementally — stream, don't buffer.
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await response.Body.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);

        // The branch a push carries is only known once the pack has been relayed, so a
        // repository created by this push gets its HEAD straightened out here. Best effort:
        // the response is already on the wire, and a clone that checks out nothing is a
        // smaller failure than one that never gets the objects.
        if (options.AllowCreateOnPush && response.StatusCode < 400)
        {
            try
            {
                await invoker.AlignHeadAfterPushAsync(request, ctx.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not point HEAD at the pushed branch for {PathInfo}",
                    ForLog(request.PathInfo));
            }
        }

        // git-http-backend reports failures (die -> "Status: 500", empty body) only on stderr.
        // Without this the caller sees a bare 500 and the reason is lost.
        if (response.StatusCode >= 400)
        {
            var stderr = (await response.ReadErrorOutputAsync()).Trim();
            logger.LogError(
                "git-http-backend failed with {StatusCode} {Reason} for {Method} {PathInfo}?{QueryString}. stderr: {StdErr}",
                response.StatusCode, response.ReasonPhrase, ForLog(request.Method), ForLog(request.PathInfo),
                ForLog(request.QueryString), stderr.Length > 0 ? ForLog(stderr) : "(empty)");
        }
        else if (logger.IsEnabled(LogLevel.Debug))
        {
            var stderr = (await response.ReadErrorOutputAsync()).Trim();
            if (stderr.Length > 0)
            {
                logger.LogDebug("git-http-backend stderr: {StdErr}", ForLog(stderr));
            }
        }
    }

    // Text taken from the request reaches most log providers verbatim, so a CR or LF inside a
    // path, query string or user name could forge extra log entries (CWE-117). Line breaks are
    // flattened to spaces; everything else — including non-ASCII repository names — is kept.
    // git's stderr goes through this too: it quotes the requested path back in its error
    // messages, which is the same request data taking a detour through the child process.
    // Replace(char, char) returns the same instance when there is nothing to replace, so the
    // common case allocates nothing.
    static string? ForLog(string? value)
        => value?.Replace('\r', ' ').Replace('\n', ' ');
}
