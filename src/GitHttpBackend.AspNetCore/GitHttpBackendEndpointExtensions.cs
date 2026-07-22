using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

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
        var invoker = new GitHttpBackendInvoker(options);

        var normalizedPrefix = "/" + prefix.Trim('/');
        var pattern = (normalizedPrefix == "/" ? "" : normalizedPrefix) + "/{**gitPath}";

        return endpoints.MapMethods(pattern, new[] { HttpMethods.Get, HttpMethods.Post },
            (HttpContext ctx) => HandleAsync(ctx, invoker, options));
    }

    static async Task HandleAsync(HttpContext ctx, GitHttpBackendInvoker invoker, GitBackendOptions options)
    {
        var gitPath = ctx.Request.RouteValues["gitPath"] as string ?? "";

        var request = new CgiRequest
        {
            Method = ctx.Request.Method,
            PathInfo = "/" + gitPath,
            QueryString = ctx.Request.QueryString.HasValue
                ? ctx.Request.QueryString.Value!.TrimStart('?')
                : "",
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
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await using var response = await invoker.InvokeAsync(request, ctx.RequestAborted);

        ctx.Response.StatusCode = response.StatusCode;
        foreach (var header in response.Headers)
        {
            ctx.Response.Headers[header.Key] = header.Value;
        }

        // Packs can be large and are produced incrementally — stream, don't buffer.
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        await response.Body.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }
}
