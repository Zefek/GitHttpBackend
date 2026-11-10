using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace GitHttpBackend.AspNetCore;

public static class GitSnapshotEndpointExtensions
{
    public static IEndpointConventionBuilder MapGitSnapshot(
        this IEndpointRouteBuilder endpoints, string prefix, GitHttpBackendInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(invoker);

        var reader = new GitSnapshotReader(invoker);
        var normalizedPrefix = "/" + prefix.Trim('/');
        var pattern = (normalizedPrefix == "/" ? "" : normalizedPrefix) + "/{repository}/-/snapshot";

        return endpoints.MapGet(pattern, (HttpContext ctx) => HandleAsync(ctx, reader));
    }

    static async Task HandleAsync(HttpContext ctx, GitSnapshotReader reader)
    {
        var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("GitHttpBackend.AspNetCore.Snapshot");

        var repository = ctx.Request.RouteValues["repository"] as string ?? "";
        var reference = ctx.Request.Query["ref"].ToString() is { Length: > 0 } r ? r : "HEAD";
        var path = ctx.Request.Query["path"].ToString().Trim('/');

        if (!GitRepositoryPath.TryParse(repository, out _, out var rest) || rest.Length != 0
            || !GitSnapshotReader.IsValidReference(reference)
            || !GitSnapshotReader.IsValidPath(path))
        {
            logger.LogWarning("Snapshot request rejected, malformed input: {Repository} ref={Ref} path={Path}",
                GitHttpBackendEndpointExtensions.ForLog(repository),
                GitHttpBackendEndpointExtensions.ForLog(reference),
                GitHttpBackendEndpointExtensions.ForLog(path));
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var user = ctx.User.Identity?.IsAuthenticated == true ? ctx.User.Identity.Name : null;

        if (reader.Options.Authorize is { } authorize)
        {
            var fetch = new CgiRequest
            {
                Method = HttpMethods.Get,
                PathInfo = $"/{repository}/info/refs",
                QueryString = "service=git-upload-pack",
                RemoteAddr = ctx.Connection.RemoteIpAddress?.ToString() ?? "",
                RemoteUser = user,
                Body = Stream.Null,
            };

            if (!await authorize(fetch))
            {
                logger.LogWarning("Snapshot request denied by Authorize hook: {Repository} (user {User})",
                    GitHttpBackendEndpointExtensions.ForLog(repository),
                    GitHttpBackendEndpointExtensions.ForLog(user) ?? "-");
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
        }

        GitSnapshot? snapshot;
        try
        {
            snapshot = await reader.ReadAsync(repository, reference, path, ctx.RequestAborted);
        }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Snapshot failed: {Repository} ref={Ref} path={Path} (user {User})",
                GitHttpBackendEndpointExtensions.ForLog(repository),
                GitHttpBackendEndpointExtensions.ForLog(reference),
                GitHttpBackendEndpointExtensions.ForLog(path),
                GitHttpBackendEndpointExtensions.ForLog(user) ?? "-");
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        if (snapshot is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var etag = $"\"{snapshot.Commit}\"";
        ctx.Response.Headers.ETag = etag;
        ctx.Response.Headers.CacheControl = "no-cache";

        if (ctx.Request.Headers.IfNoneMatch.Any(v => v is not null
                && v.Split(',').Any(t => t.Trim() == etag)))
        {
            ctx.Response.StatusCode = StatusCodes.Status304NotModified;
            return;
        }

        logger.LogInformation("Snapshot served: {Repository} ref={Ref} path={Path} commit={Commit} files={Count} (user {User})",
            GitHttpBackendEndpointExtensions.ForLog(repository), GitHttpBackendEndpointExtensions.ForLog(reference), GitHttpBackendEndpointExtensions.ForLog(path), snapshot.Commit,
            snapshot.Files.Count, GitHttpBackendEndpointExtensions.ForLog(user) ?? "-");

        await ctx.Response.WriteAsJsonAsync(snapshot, ctx.RequestAborted);
    }
}
