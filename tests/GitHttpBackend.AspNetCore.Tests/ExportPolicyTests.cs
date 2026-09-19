using System.Net;

namespace GitHttpBackend.AspNetCore.Tests;

/// <summary>
/// What <c>ExportAll</c> actually gates. The README tells readers that the permissive default
/// is an accepted exposure they can close with one setting, so that setting has to genuinely
/// close it — and the marker has to genuinely open it again, or the escape hatch is a trap of
/// its own.
/// </summary>
public class ExportPolicyTests
{
    static string SeedBareRepository(GitClient git, string projectRoot, string name, bool exported)
    {
        var work = git.CreateWorkingRepository($"work-{name}");
        var bare = Path.Combine(projectRoot, name);
        git.RunOk(projectRoot, "init", "--bare", "--", bare);
        git.RunOk(work, "push", bare, "main");
        git.RunOk(bare, "symbolic-ref", "HEAD", "refs/heads/main");
        if (exported)
        {
            File.WriteAllBytes(Path.Combine(bare, "git-daemon-export-ok"), []);
        }
        return bare;
    }

    [RequiresGitFact]
    public async Task With_the_default_every_repository_is_served_without_a_marker()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(
            root => new GitBackendOptions { ProjectRoot = root });

        SeedBareRepository(git, server.ProjectRoot, "projekt.git", exported: false);

        var response = await server.Client.GetAsync("/projekt.git/info/refs?service=git-upload-pack");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [RequiresGitFact]
    public async Task With_export_all_off_an_unmarked_repository_is_not_served()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(
            root => new GitBackendOptions { ProjectRoot = root, ExportAll = false });

        SeedBareRepository(git, server.ProjectRoot, "projekt.git", exported: false);

        var response = await server.Client.GetAsync("/projekt.git/info/refs?service=git-upload-pack");

        // 404, the same answer as for a repository that does not exist — which is exactly why
        // the README warns that a forgotten marker fails invisibly.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var clone = git.Run(git.Root, "clone",
            new Uri(server.Client.BaseAddress!, "projekt.git").ToString(), Path.Combine(git.Root, "clone"));
        Assert.NotEqual(0, clone.ExitCode);
    }

    [RequiresGitFact]
    public async Task With_export_all_off_a_marked_repository_is_served()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(
            root => new GitBackendOptions { ProjectRoot = root, ExportAll = false });

        SeedBareRepository(git, server.ProjectRoot, "projekt.git", exported: true);

        var clone = Path.Combine(git.Root, "clone");
        git.RunOk(git.Root, "clone", new Uri(server.Client.BaseAddress!, "projekt.git").ToString(), clone);

        Assert.True(File.Exists(Path.Combine(clone, "README.md")));
    }

    [RequiresGitFact]
    public async Task Create_on_push_marks_the_repository_so_it_works_under_either_setting()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(
            root => new GitBackendOptions
            {
                ProjectRoot = root,
                ExportAll = false,
                AllowCreateOnPush = true,
            });

        var work = git.CreateWorkingRepository("work");
        var url = new Uri(server.Client.BaseAddress!, "nove.git").ToString();
        git.RunOk(work, "push", url, "main");

        Assert.True(File.Exists(Path.Combine(server.ProjectRoot, "nove.git", "git-daemon-export-ok")));

        // Reachable straight away: a repository created by a push that then 404s would be the
        // worst of both defaults.
        var clone = Path.Combine(git.Root, "clone");
        git.RunOk(git.Root, "clone", url, clone);
        Assert.True(File.Exists(Path.Combine(clone, "README.md")));
    }
}
