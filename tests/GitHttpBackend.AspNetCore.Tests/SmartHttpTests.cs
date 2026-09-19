using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace GitHttpBackend.AspNetCore.Tests;

/// <summary>
/// Clone and push driven by the real git client against a real Kestrel host. This library's
/// correctness rests on the observable behaviour of git-http-backend, a binary upgraded on
/// its own schedule — a build that compiles proves nothing about whether a clone still works
/// after the next Git update. Only these do.
/// </summary>
public class SmartHttpTests
{
    static GitBackendOptions Plain(string root) => new() { ProjectRoot = root };

    /// <summary>
    /// Creates a bare repository under the project root, seeded from a local working copy over
    /// the file transport so the HTTP path is never part of a test's setup.
    /// </summary>
    static string SeedBareRepository(
        GitClient git, string projectRoot, string name, string workingCopy, bool allowPush = false)
    {
        var bare = Path.Combine(projectRoot, name);
        git.RunOk(projectRoot, "init", "--bare", "--", bare);
        git.RunOk(workingCopy, "push", bare, "main");
        git.RunOk(bare, "symbolic-ref", "HEAD", "refs/heads/main");
        if (allowPush)
        {
            git.RunOk(bare, "config", "http.receivepack", "true");
        }
        return bare;
    }

    static int BackendProcessCount()
    {
        var processes = Process.GetProcessesByName("git-http-backend");
        try
        {
            return processes.Length;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    [RequiresGitFact]
    public async Task Clone_over_http_returns_the_repository_contents()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(Plain);

        var work = git.CreateWorkingRepository("work", content: "obsah repozitáře");
        SeedBareRepository(git, server.ProjectRoot, "projekt.git", work);

        var clone = Path.Combine(git.Root, "clone");
        git.RunOk(git.Root, "clone", new Uri(server.Client.BaseAddress!, "projekt.git").ToString(), clone);

        Assert.Equal("obsah repozitáře", File.ReadAllText(Path.Combine(clone, "README.md")));
    }

    [RequiresGitFact]
    public async Task A_non_ascii_repository_name_can_be_cloned()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(Plain);

        var work = git.CreateWorkingRepository("work");
        SeedBareRepository(git, server.ProjectRoot, "pokusný-projekt.git", work);

        var clone = Path.Combine(git.Root, "clone");
        git.RunOk(git.Root, "clone",
            new Uri(server.Client.BaseAddress!, Uri.EscapeDataString("pokusný-projekt.git")).ToString(), clone);

        Assert.True(File.Exists(Path.Combine(clone, "README.md")));
    }

    [RequiresGitFact]
    public async Task Push_over_http_lands_in_the_repository()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(Plain);

        var work = git.CreateWorkingRepository("work");
        SeedBareRepository(git, server.ProjectRoot, "projekt.git", work, allowPush: true);

        File.WriteAllText(Path.Combine(work, "druhy.txt"), "second");
        git.RunOk(work, "add", "--", "druhy.txt");
        git.RunOk(work, "commit", "-m", "second commit");

        var url = new Uri(server.Client.BaseAddress!, "projekt.git").ToString();
        git.RunOk(work, "push", url, "main");

        var clone = Path.Combine(git.Root, "clone");
        git.RunOk(git.Root, "clone", url, clone);
        Assert.Equal("second", File.ReadAllText(Path.Combine(clone, "druhy.txt")));
    }

    [RequiresGitFact]
    public async Task Push_is_refused_when_receivepack_is_not_enabled()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(Plain);

        var work = git.CreateWorkingRepository("work");
        SeedBareRepository(git, server.ProjectRoot, "projekt.git", work, allowPush: false);

        File.WriteAllText(Path.Combine(work, "druhy.txt"), "second");
        git.RunOk(work, "add", "--", "druhy.txt");
        git.RunOk(work, "commit", "-m", "second commit");

        var url = new Uri(server.Client.BaseAddress!, "projekt.git").ToString();
        var result = git.Run(work, "push", url, "main");

        // Refused, not silently accepted: the commit must not be reachable afterwards.
        Assert.NotEqual(0, result.ExitCode);
        var bare = Path.Combine(server.ProjectRoot, "projekt.git");
        Assert.NotEqual(0, git.Run(bare, "cat-file", "-e", "main:druhy.txt").ExitCode);
    }

    [RequiresGitFact]
    public async Task A_large_binary_file_survives_the_header_body_boundary()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(Plain);

        // Incompressible and far larger than the parser's 4 KB read buffer, so the packfile is
        // streamed across many reads and the header/body split is exercised for real.
        var payload = new byte[6 * 1024 * 1024];
        Random.Shared.NextBytes(payload);

        var work = git.CreateWorkingRepository("work");
        File.WriteAllBytes(Path.Combine(work, "velky.bin"), payload);
        git.RunOk(work, "add", "--", "velky.bin");
        git.RunOk(work, "commit", "-m", "binary");
        SeedBareRepository(git, server.ProjectRoot, "projekt.git", work);

        var clone = Path.Combine(git.Root, "clone");
        git.RunOk(git.Root, "clone", new Uri(server.Client.BaseAddress!, "projekt.git").ToString(), clone);

        Assert.Equal(
            SHA256.HashData(payload),
            SHA256.HashData(File.ReadAllBytes(Path.Combine(clone, "velky.bin"))));
    }

    [RequiresGitFact]
    public async Task The_git_protocol_header_reaches_the_backend()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(Plain);

        var work = git.CreateWorkingRepository("work");
        SeedBareRepository(git, server.ProjectRoot, "projekt.git", work);

        var request = new HttpRequestMessage(HttpMethod.Get, "/projekt.git/info/refs?service=git-upload-pack");
        request.Headers.TryAddWithoutValidation("Git-Protocol", "version=2");
        var advertisement = await (await server.Client.SendAsync(request)).Content.ReadAsStringAsync();

        // Protocol v2 only engages when GIT_PROTOCOL reaches the backend; its advertisement
        // opens with a "version 2" pkt-line, where v0 goes straight to the ref list.
        Assert.Contains("version 2", advertisement, StringComparison.Ordinal);

        var v0 = await server.Client.GetStringAsync("/projekt.git/info/refs?service=git-upload-pack");
        Assert.DoesNotContain("version 2", v0, StringComparison.Ordinal);
    }

    [RequiresGitFact]
    public async Task An_unknown_repository_returns_404()
    {
        await using var server = await GitTestServer.StartAsync(Plain);

        var response = await server.Client.GetAsync("/neexistuje.git/info/refs?service=git-upload-pack");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_denied_request_never_starts_the_backend_process()
    {
        // BackendPath points at a file that exists but is not a runnable image. Starting it
        // would throw and surface as a 500, so a clean 403 is proof that nothing was started —
        // a stronger guarantee than counting processes, which can only ever sample.
        var fakeBackend = Path.Combine(
            Path.GetTempPath(), "githttpbackend-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(fakeBackend);
        var backendPath = Path.Combine(
            fakeBackend, OperatingSystem.IsWindows() ? "git-http-backend.exe" : "git-http-backend");
        await File.WriteAllTextAsync(backendPath, "not an executable");

        try
        {
            var authorizeCalls = 0;
            await using var server = await GitTestServer.StartAsync(root => new GitBackendOptions
            {
                ProjectRoot = root,
                BackendPath = backendPath,
                Authorize = _ =>
                {
                    Interlocked.Increment(ref authorizeCalls);
                    return ValueTask.FromResult(false);
                },
            });

            var response = await server.Client.GetAsync("/projekt.git/info/refs?service=git-upload-pack");

            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Equal(1, authorizeCalls);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        }
        finally
        {
            GitTestServer.DeleteDirectory(fakeBackend);
        }
    }

    [RequiresGitFact]
    public async Task No_backend_process_is_left_behind_after_a_clone()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(Plain);

        var work = git.CreateWorkingRepository("work");
        SeedBareRepository(git, server.ProjectRoot, "projekt.git", work);

        var baseline = BackendProcessCount();

        var clone = Path.Combine(git.Root, "clone");
        git.RunOk(git.Root, "clone", new Uri(server.Client.BaseAddress!, "projekt.git").ToString(), clone);

        // GitBackendResponse.DisposeAsync owns that lifetime; give it a moment to run and then
        // insist the count is back where it started.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (BackendProcessCount() > baseline && DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
        }

        Assert.True(BackendProcessCount() <= baseline,
            "a git-http-backend process outlived the request that started it");
    }

    [RequiresGitFact]
    public async Task Content_type_from_the_backend_reaches_the_client()
    {
        using var git = new GitClient();
        await using var server = await GitTestServer.StartAsync(Plain);

        var work = git.CreateWorkingRepository("work");
        SeedBareRepository(git, server.ProjectRoot, "projekt.git", work);

        var response = await server.Client.GetAsync("/projekt.git/info/refs?service=git-upload-pack");

        Assert.Equal(
            new MediaTypeHeaderValue("application/x-git-upload-pack-advertisement"),
            response.Content.Headers.ContentType);
    }
}
