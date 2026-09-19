using System.Net;

namespace GitHttpBackend.AspNetCore.Tests;

/// <summary>
/// Create-on-push, driven by the real git client. The negative cases matter as much as the
/// positive one: the option must not turn a clone, an unauthorized caller or a malformed
/// name into a directory on disk.
/// </summary>
public class CreateOnPushTests
{
    static GitBackendOptions Options(string root, bool allowCreateOnPush,
        Func<CgiRequest, ValueTask<bool>>? authorize = null) => new()
        {
            ProjectRoot = root,
            AllowCreateOnPush = allowCreateOnPush,
            Authorize = authorize,
        };

    [RequiresGitFact]
    public async Task Push_to_unknown_name_creates_the_repository_and_completes()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(root => Options(root, allowCreateOnPush: true));

        var work = git.CreateWorkingRepository("work");
        var url = new Uri(server.Client.BaseAddress!, "nove.git").ToString();

        git.RunOk(work, "push", url, "main");

        var created = Path.Combine(server.ProjectRoot, "nove.git");
        Assert.True(Directory.Exists(created), "the bare repository was not created");

        // The push is only proven by getting the commit back out again.
        var clone = Path.Combine(git.Root, "clone");
        git.RunOk(git.Root, "clone", url, clone);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(clone, "README.md")));
    }

    [RequiresGitFact]
    public async Task Created_repository_has_receivepack_enabled()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(root => Options(root, allowCreateOnPush: true));

        var work = git.CreateWorkingRepository("work");
        git.RunOk(work, "push", new Uri(server.Client.BaseAddress!, "nove.git").ToString(), "main");

        var created = Path.Combine(server.ProjectRoot, "nove.git");
        var receivePack = git.RunOk(created, "config", "--get", "http.receivepack");
        Assert.Equal("true", receivePack.StdOut);
    }

    [RequiresGitFact]
    public async Task Created_repository_points_HEAD_at_the_pushed_branch()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(root => Options(root, allowCreateOnPush: true));

        var work = git.CreateWorkingRepository("work");
        git.RunOk(work, "push", new Uri(server.Client.BaseAddress!, "nove.git").ToString(), "main");

        // git init --bare left HEAD on refs/heads/master; the push was on main.
        var created = Path.Combine(server.ProjectRoot, "nove.git");
        Assert.Equal("refs/heads/main", git.RunOk(created, "symbolic-ref", "HEAD").StdOut);
    }

    [RequiresGitFact]
    public async Task A_HEAD_that_already_resolves_is_left_alone()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(root => Options(root, allowCreateOnPush: true));

        var work = git.CreateWorkingRepository("work");
        var url = new Uri(server.Client.BaseAddress!, "nove.git").ToString();
        git.RunOk(work, "push", url, "main");

        // A second branch must not move HEAD away from the one the repository settled on.
        git.RunOk(work, "checkout", "-b", "feature");
        File.WriteAllText(Path.Combine(work, "feature.txt"), "x");
        git.RunOk(work, "add", "--", "feature.txt");
        git.RunOk(work, "commit", "-m", "feature");
        git.RunOk(work, "push", url, "feature");

        var created = Path.Combine(server.ProjectRoot, "nove.git");
        Assert.Equal("refs/heads/main", git.RunOk(created, "symbolic-ref", "HEAD").StdOut);
    }

    [RequiresGitFact]
    public async Task Clone_of_an_unknown_name_never_creates_anything()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(root => Options(root, allowCreateOnPush: true));

        var url = new Uri(server.Client.BaseAddress!, "neexistuje.git").ToString();
        var result = git.Run(git.Root, "clone", url, Path.Combine(git.Root, "clone"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(Directory.GetDirectories(server.ProjectRoot));

        // Same through the ref advertisement the clone starts with.
        var response = await server.Client.GetAsync("/neexistuje.git/info/refs?service=git-upload-pack");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(Directory.GetDirectories(server.ProjectRoot));
    }

    [RequiresGitFact]
    public async Task With_the_option_off_a_push_to_an_unknown_name_still_fails()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(root => Options(root, allowCreateOnPush: false));

        var work = git.CreateWorkingRepository("work");
        var url = new Uri(server.Client.BaseAddress!, "nove.git").ToString();

        var result = git.Run(work, "push", url, "main");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(Directory.GetDirectories(server.ProjectRoot));
    }

    [RequiresGitFact]
    public async Task Creation_happens_only_after_authorization_succeeds()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(root =>
            Options(root, allowCreateOnPush: true, authorize: _ => ValueTask.FromResult(false)));

        var work = git.CreateWorkingRepository("work");
        var url = new Uri(server.Client.BaseAddress!, "nove.git").ToString();

        var result = git.Run(work, "push", url, "main");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(Directory.GetDirectories(server.ProjectRoot));

        var response = await server.Client.GetAsync("/nove.git/info/refs?service=git-receive-pack");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(Directory.GetDirectories(server.ProjectRoot));
    }

    [RequiresGitFact]
    public async Task A_malformed_name_is_rejected_before_any_filesystem_access()
    {
        await using var server = await GitTestServer.StartAsync(root => Options(root, allowCreateOnPush: true));

        var response = await server.Client.GetAsync("/.../info/refs?service=git-receive-pack");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(Directory.GetDirectories(server.ProjectRoot));
    }

    [RequiresGitFact]
    public async Task An_existing_repository_is_never_reinitialised()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(root => Options(root, allowCreateOnPush: true));

        var existing = Path.Combine(server.ProjectRoot, "stare.git");
        git.RunOk(server.ProjectRoot, "init", "--bare", "--", existing);
        git.RunOk(existing, "config", "http.receivepack", "true");
        git.RunOk(existing, "config", "custom.marker", "keep-me");

        var work = git.CreateWorkingRepository("work");
        git.RunOk(work, "push", new Uri(server.Client.BaseAddress!, "stare.git").ToString(), "main");

        // A re-init would have rewritten config; the marker surviving proves it did not happen.
        Assert.Equal("keep-me", git.RunOk(existing, "config", "--get", "custom.marker").StdOut);
        Assert.Single(Directory.GetDirectories(server.ProjectRoot));
    }

    [RequiresGitFact]
    public async Task A_repository_reachable_under_the_other_suffix_is_not_duplicated()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(root => Options(root, allowCreateOnPush: true));

        // git-http-backend's enter_repo() finds "projekt.git" for a request naming "projekt",
        // so creating a second directory for it would split one repository in two.
        var existing = Path.Combine(server.ProjectRoot, "projekt.git");
        git.RunOk(server.ProjectRoot, "init", "--bare", "--", existing);
        git.RunOk(existing, "config", "http.receivepack", "true");

        var work = git.CreateWorkingRepository("work");
        git.RunOk(work, "push", new Uri(server.Client.BaseAddress!, "projekt").ToString(), "main");

        Assert.Single(Directory.GetDirectories(server.ProjectRoot));
    }

    [RequiresGitFact]
    public async Task Concurrent_pushes_to_the_same_new_name_create_it_once()
    {
        var root = Path.Combine(Path.GetTempPath(), "githttpbackend-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var invoker = new GitHttpBackendInvoker(Options(root, allowCreateOnPush: true));

            static CgiRequest Advertisement() => new()
            {
                Method = "GET",
                PathInfo = "/soubeh.git/info/refs",
                QueryString = "service=git-receive-pack",
                Body = Stream.Null,
            };

            var created = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => invoker.EnsureRepositoryForPushAsync(Advertisement()))));

            Assert.Equal(1, created.Count(c => c));
            Assert.Single(Directory.GetDirectories(root));
        }
        finally
        {
            GitTestServer.DeleteDirectory(root);
        }
    }

    [RequiresGitFact]
    public async Task A_fetch_request_is_not_treated_as_a_push()
    {
        var root = Path.Combine(Path.GetTempPath(), "githttpbackend-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var invoker = new GitHttpBackendInvoker(Options(root, allowCreateOnPush: true));

            var upload = new CgiRequest
            {
                Method = "GET",
                PathInfo = "/klon.git/info/refs",
                QueryString = "service=git-upload-pack",
                Body = Stream.Null,
            };
            Assert.False(await invoker.EnsureRepositoryForPushAsync(upload));

            var uploadPost = new CgiRequest
            {
                Method = "POST",
                PathInfo = "/klon.git/git-upload-pack",
                Body = Stream.Null,
            };
            Assert.False(await invoker.EnsureRepositoryForPushAsync(uploadPost));

            Assert.Empty(Directory.GetDirectories(root));
        }
        finally
        {
            GitTestServer.DeleteDirectory(root);
        }
    }
}
