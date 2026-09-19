using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace GitHttpBackend.AspNetCore.Tests;

/// <summary>
/// The overload that takes an invoker the host already built. What matters is that the
/// mapping uses that instance — not a second one built from the same options — so the
/// resolved backend path the host logged is the one actually serving requests.
/// </summary>
public class CallerOwnedInvokerTests
{
    [RequiresGitFact]
    public void Options_expose_what_the_invoker_was_constructed_with()
    {
        var options = new GitBackendOptions { ProjectRoot = Path.GetTempPath() };
        var invoker = new GitHttpBackendInvoker(options);

        Assert.Same(options, invoker.Options);
        Assert.True(File.Exists(invoker.BackendPath));
    }

    [RequiresGitFact]
    public async Task A_caller_owned_invoker_serves_the_repository()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartWithInvokerAsync(
            root => new GitHttpBackendInvoker(new GitBackendOptions { ProjectRoot = root }));

        var work = git.CreateWorkingRepository("work");
        var bare = Path.Combine(server.ProjectRoot, "projekt.git");
        git.RunOk(server.ProjectRoot, "init", "--bare", "--", bare);
        git.RunOk(work, "push", bare, "main");
        git.RunOk(bare, "symbolic-ref", "HEAD", "refs/heads/main");

        var clone = Path.Combine(git.Root, "clone");
        git.RunOk(git.Root, "clone", new Uri(server.Client.BaseAddress!, "projekt.git").ToString(), clone);

        Assert.True(File.Exists(Path.Combine(clone, "README.md")));
    }

    [RequiresGitFact]
    public async Task The_mapping_uses_the_invokers_own_options()
    {
        // The hook only runs if HandleAsync read the options off the instance it was handed,
        // which is what stops the two overloads from drifting apart.
        var authorizeCalls = 0;
        await using var server = await GitTestServer.StartWithInvokerAsync(
            root => new GitHttpBackendInvoker(new GitBackendOptions
            {
                ProjectRoot = root,
                Authorize = _ =>
                {
                    Interlocked.Increment(ref authorizeCalls);
                    return ValueTask.FromResult(false);
                },
            }));

        var response = await server.Client.GetAsync("/projekt.git/info/refs?service=git-upload-pack");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(1, authorizeCalls);
    }

    [Fact]
    public async Task Both_overloads_reject_a_null_argument()
    {
        await using var app = WebApplication.CreateSlimBuilder().Build();

        Assert.Throws<ArgumentNullException>(
            () => app.MapGitHttpBackend("/", (GitBackendOptions)null!));
        Assert.Throws<ArgumentNullException>(
            () => app.MapGitHttpBackend("/", (GitHttpBackendInvoker)null!));
        Assert.Throws<ArgumentNullException>(
            () => ((IEndpointRouteBuilder)null!).MapGitHttpBackend("/", new GitBackendOptions
            {
                ProjectRoot = Path.GetTempPath(),
            }));
    }
}
