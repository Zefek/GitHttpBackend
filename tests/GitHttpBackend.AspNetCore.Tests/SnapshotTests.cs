using System.Net;
using System.Net.Http.Json;
using System.Text;

namespace GitHttpBackend.AspNetCore.Tests;

public class SnapshotTests
{
    static GitBackendOptions Plain(string root) => new() { ProjectRoot = root };

    static string SeedConfigRepository(GitClient git, string projectRoot, string name = "Home.git")
    {
        var work = Path.Combine(git.Root, "work-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path.Combine(work, "OdectyMVC", "nested"));
        Directory.CreateDirectory(Path.Combine(work, "Odecty"));
        git.RunOk(work, "init", "--initial-branch=main");
        File.WriteAllText(Path.Combine(work, "OdectyMVC", "appsettings.json.cms"), "-----BEGIN CMS-----\nAAAA\n-----END CMS-----\n");
        File.WriteAllText(Path.Combine(work, "OdectyMVC", "deploy.ps1"), "param()");
        File.WriteAllText(Path.Combine(work, "OdectyMVC", "nested", "ignored.json"), "{}");
        File.WriteAllText(Path.Combine(work, "Odecty", "appsettings.json"), "{\"a\":1}");
        git.RunOk(work, "add", "--all");
        git.RunOk(work, "commit", "-m", "initial");

        var bare = Path.Combine(projectRoot, name);
        git.RunOk(projectRoot, "init", "--bare", "--", bare);
        git.RunOk(work, "push", bare, "main");
        git.RunOk(bare, "symbolic-ref", "HEAD", "refs/heads/main");
        return work;
    }

    static async Task<GitSnapshot> ReadSnapshot(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<GitSnapshot>())!;

    [RequiresGitFact]
    public async Task Returns_the_files_of_the_directory_at_the_resolved_commit()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartWithSnapshotAsync(Plain);
        var work = SeedConfigRepository(git, server.ProjectRoot);
        var head = git.RunOk(work, "rev-parse", "HEAD").StdOut;

        using var response = await server.Client.GetAsync("Home/-/snapshot?ref=main&path=OdectyMVC");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal($"\"{head}\"", response.Headers.ETag?.Tag);
        var snapshot = await ReadSnapshot(response);
        Assert.Equal(head, snapshot.Commit);
        Assert.Equal(["appsettings.json.cms", "deploy.ps1"], snapshot.Files.Select(f => f.Name).Order());
        Assert.Equal("-----BEGIN CMS-----\nAAAA\n-----END CMS-----\n",
            Encoding.UTF8.GetString(snapshot.Files.Single(f => f.Name == "appsettings.json.cms").Content));
    }

    [RequiresGitFact]
    public async Task Missing_ref_defaults_to_HEAD_and_repository_name_may_omit_the_git_suffix()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartWithSnapshotAsync(Plain);
        SeedConfigRepository(git, server.ProjectRoot);

        using var response = await server.Client.GetAsync("Home/-/snapshot?path=Odecty");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var snapshot = await ReadSnapshot(response);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(snapshot.Files.Single().Content));
    }

    [RequiresGitFact]
    public async Task A_commit_can_be_requested_by_its_object_name()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartWithSnapshotAsync(Plain);
        var work = SeedConfigRepository(git, server.ProjectRoot);
        var first = git.RunOk(work, "rev-parse", "HEAD").StdOut;
        File.WriteAllText(Path.Combine(work, "Odecty", "appsettings.json"), "{\"a\":2}");
        git.RunOk(work, "commit", "-am", "second");
        git.RunOk(work, "push", Path.Combine(server.ProjectRoot, "Home.git"), "main");

        using var response = await server.Client.GetAsync($"Home.git/-/snapshot?ref={first}&path=Odecty");

        var snapshot = await ReadSnapshot(response);
        Assert.Equal(first, snapshot.Commit);
        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(snapshot.Files.Single().Content));
    }

    [RequiresGitFact]
    public async Task A_matching_If_None_Match_returns_304()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartWithSnapshotAsync(Plain);
        var work = SeedConfigRepository(git, server.ProjectRoot);
        var head = git.RunOk(work, "rev-parse", "HEAD").StdOut;

        using var request = new HttpRequestMessage(HttpMethod.Get, "Home/-/snapshot?path=OdectyMVC");
        request.Headers.TryAddWithoutValidation("If-None-Match", $"\"{head}\"");
        using var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
    }

    [RequiresGitTheory]
    [InlineData("Missing/-/snapshot?path=OdectyMVC")]
    [InlineData("Home/-/snapshot?ref=nonexistent&path=OdectyMVC")]
    [InlineData("Home/-/snapshot?path=Nonexistent")]
    [InlineData("Home/-/snapshot?path=OdectyMVC/deploy.ps1")]
    public async Task Unknown_repository_ref_or_directory_returns_404(string url)
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartWithSnapshotAsync(Plain);
        SeedConfigRepository(git, server.ProjectRoot);

        using var response = await server.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [RequiresGitTheory]
    [InlineData("Home/-/snapshot?ref=--upload-pack=calc&path=OdectyMVC")]
    [InlineData("Home/-/snapshot?ref=main..HEAD&path=OdectyMVC")]
    [InlineData("Home/-/snapshot?ref=main%3AOdecty&path=OdectyMVC")]
    [InlineData("Home/-/snapshot?path=../OdectyMVC")]
    [InlineData("Home/-/snapshot?path=OdectyMVC/../Odecty")]
    [InlineData("Home/-/snapshot?path=C:%5CWindows")]
    public async Task Malformed_ref_or_path_returns_400(string url)
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartWithSnapshotAsync(Plain);
        SeedConfigRepository(git, server.ProjectRoot);

        using var response = await server.Client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [RequiresGitFact]
    public async Task Authorize_sees_a_fetch_of_the_repository_and_can_deny_it()
    {
        using var git = new GitClient();
        CgiRequest? seen = null;
        await using var server = await GitTestServer.StartWithSnapshotAsync(root => new GitBackendOptions
        {
            ProjectRoot = root,
            Authorize = req =>
            {
                seen = req;
                return ValueTask.FromResult(false);
            },
        });
        SeedConfigRepository(git, server.ProjectRoot);

        using var response = await server.Client.GetAsync("Home/-/snapshot?path=OdectyMVC");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.NotNull(seen);
        Assert.Equal("/Home/info/refs", seen.PathInfo);
        Assert.False(seen.IsPush);
    }

    [RequiresGitFact]
    public async Task A_repository_without_the_export_marker_is_not_served_when_ExportAll_is_off()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartWithSnapshotAsync(root => new GitBackendOptions
        {
            ProjectRoot = root,
            ExportAll = false,
        });
        SeedConfigRepository(git, server.ProjectRoot);

        using var hidden = await server.Client.GetAsync("Home/-/snapshot?path=OdectyMVC");
        File.WriteAllBytes(Path.Combine(server.ProjectRoot, "Home.git", "git-daemon-export-ok"), []);
        using var exported = await server.Client.GetAsync("Home/-/snapshot?path=OdectyMVC");

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, exported.StatusCode);
    }

    [RequiresGitFact]
    public async Task Clone_still_works_next_to_the_snapshot_endpoint()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartWithSnapshotAsync(Plain);
        SeedConfigRepository(git, server.ProjectRoot);

        var clone = Path.Combine(git.Root, "clone");
        git.RunOk(git.Root, "clone", new Uri(server.Client.BaseAddress!, "Home.git").ToString(), clone);

        Assert.True(File.Exists(Path.Combine(clone, "Odecty", "appsettings.json")));
    }
}
