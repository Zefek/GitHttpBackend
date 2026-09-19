using System.Net;

namespace GitHttpBackend.AspNetCore.Tests;

/// <summary>
/// End of the chain for <c>GitRepositoryPath</c>: a path the parser rejects must be refused
/// by the endpoint before the Authorize hook runs, and a path it accepts must still reach
/// git untouched.
/// </summary>
public class RepositoryPathValidationTests
{
    // Paths chosen to survive RFC 3986 dot-segment removal and Kestrel's normalisation, so the
    // request genuinely arrives at the endpoint. That is the point of the issue behind these
    // tests: the guarantee must live here, not in whatever the host happens to normalise away.
    [Theory]
    [InlineData("/.../info/refs")]
    [InlineData("/projekt%5c..%5csecrets.git/info/refs")]
    [InlineData("/projekt.git%5cinfo/refs")]
    public async Task Malformed_path_is_refused_before_authorization(string requestPath)
    {
        var authorizeCalls = 0;
        await using var server = await GitTestServer.StartAsync(root => new GitBackendOptions
        {
            ProjectRoot = root,
            Authorize = _ =>
            {
                Interlocked.Increment(ref authorizeCalls);
                return ValueTask.FromResult(true);
            },
        });

        var response = await server.Client.GetAsync(requestPath);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, authorizeCalls);
    }

    [RequiresGitFact]
    public async Task Valid_path_reaches_git()
    {
        var authorizeCalls = 0;
        await using var server = await GitTestServer.StartAsync(root => new GitBackendOptions
        {
            ProjectRoot = root,
            Authorize = _ =>
            {
                Interlocked.Increment(ref authorizeCalls);
                return ValueTask.FromResult(true);
            },
        });

        // The repository does not exist, so git answers 404 — which is the proof that the
        // request was handed to git rather than rejected as malformed.
        var response = await server.Client.GetAsync("/projekt.git/info/refs?service=git-upload-pack");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, authorizeCalls);
    }
}
