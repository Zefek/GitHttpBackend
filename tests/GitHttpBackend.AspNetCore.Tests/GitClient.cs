using System.Diagnostics;

namespace GitHttpBackend.AspNetCore.Tests;

/// <summary>
/// Drives the real <c>git</c> client in a throwaway workspace. Testing against the actual
/// binary is the whole point: this library parses what <c>git-http-backend</c> emits, and
/// that is upgraded independently of anything here.
/// </summary>
sealed class GitClient : IDisposable
{
    readonly string _home;

    public GitClient()
    {
        Executable = GitBackendLocator.LocateGit()
            ?? throw new InvalidOperationException("The git client was not found.");

        Root = Path.Combine(Path.GetTempPath(), "githttpbackend-tests", Guid.NewGuid().ToString("n"));
        _home = Path.Combine(Root, "home");
        Directory.CreateDirectory(_home);
    }

    /// <summary>Throwaway directory the test may create working copies under.</summary>
    public string Root { get; }

    /// <summary>Resolved path to the git client.</summary>
    public string Executable { get; }

    /// <summary>Runs git and returns the outcome without throwing, for negative tests.</summary>
    public GitResult Run(string workingDirectory, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = Executable,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        // The machine's own git configuration, credential helpers and identity must not decide
        // whether a test passes. HOME/USERPROFILE point at the workspace, so "global" config is
        // this test's config, and the terminal prompt is off so a credential request fails fast
        // instead of hanging the run.
        psi.Environment["HOME"] = _home;
        psi.Environment["USERPROFILE"] = _home;
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["GIT_ASKPASS"] = "";
        psi.Environment["GIT_AUTHOR_NAME"] = "Test";
        psi.Environment["GIT_AUTHOR_EMAIL"] = "test@example.invalid";
        psi.Environment["GIT_COMMITTER_NAME"] = "Test";
        psi.Environment["GIT_COMMITTER_EMAIL"] = "test@example.invalid";

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{Executable}'.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            throw new TimeoutException($"git {string.Join(' ', arguments)} did not finish within 60 s.");
        }

        return new GitResult(process.ExitCode, stdout.Result.Trim(), stderr.Result.Trim());
    }

    /// <summary>Runs git and fails the test with git's own stderr when it does not succeed.</summary>
    public GitResult RunOk(string workingDirectory, params string[] arguments)
    {
        var result = Run(workingDirectory, arguments);
        Assert.True(result.ExitCode == 0,
            $"git {string.Join(' ', arguments)} exited with {result.ExitCode}: {result.StdErr}");
        return result;
    }

    /// <summary>Creates a working repository with one commit and returns its path.</summary>
    public string CreateWorkingRepository(string name, string fileName = "README.md", string content = "hello")
    {
        var path = Path.Combine(Root, name);
        Directory.CreateDirectory(path);
        RunOk(path, "init", "--initial-branch=main");
        File.WriteAllText(Path.Combine(path, fileName), content);
        RunOk(path, "add", "--", fileName);
        RunOk(path, "commit", "-m", "initial");
        return path;
    }

    public void Dispose() => GitTestServer.DeleteDirectory(Root);
}

sealed record GitResult(int ExitCode, string StdOut, string StdErr);
