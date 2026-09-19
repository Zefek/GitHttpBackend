using System.Diagnostics;

namespace GitHttpBackend.AspNetCore.Tests;

/// <summary>
/// The backup script from <c>samples/</c>. It lives in this project because this is where the
/// real-git harness is, and it is tested rather than merely documented for the reason the
/// script itself gives: a backup nobody has restored is a hypothesis.
/// </summary>
public class BackupScriptTests
{
    static string ScriptPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "GitHttpBackend.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "samples", "Backup-GitRepositories.ps1");
        Assert.True(File.Exists(path), $"the backup script was not found at {path}");
        return path;
    }

    static (int ExitCode, string Output) RunScript(string projectRoot, string backupRoot, bool verify = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(ScriptPath());
        psi.ArgumentList.Add("-ProjectRoot");
        psi.ArgumentList.Add(projectRoot);
        psi.ArgumentList.Add("-BackupRoot");
        psi.ArgumentList.Add(backupRoot);
        if (verify)
        {
            psi.ArgumentList.Add("-Verify");
        }

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit(120_000);

        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    [RequiresPowerShellFact]
    public void Mirrors_every_repository_then_keeps_them_current()
    {
        using var git = new GitClient();
        var projectRoot = Path.Combine(git.Root, "repos");
        var backupRoot = Path.Combine(git.Root, "backup");
        Directory.CreateDirectory(projectRoot);

        var work = git.CreateWorkingRepository("work");
        foreach (var name in new[] { "prvni.git", "druhy.git" })
        {
            var bare = Path.Combine(projectRoot, name);
            git.RunOk(projectRoot, "init", "--bare", "--", bare);
            git.RunOk(work, "push", bare, "main");
            git.RunOk(bare, "symbolic-ref", "HEAD", "refs/heads/main");
        }

        // First run: clone.
        var first = RunScript(projectRoot, backupRoot);
        Assert.True(first.ExitCode == 0, first.Output);

        var mirror = Path.Combine(backupRoot, "prvni.git");
        Assert.True(Directory.Exists(mirror), first.Output);
        Assert.True(Directory.Exists(Path.Combine(backupRoot, "druhy.git")), first.Output);

        var expected = git.RunOk(Path.Combine(projectRoot, "prvni.git"), "rev-parse", "main").StdOut;
        Assert.Equal(expected, git.RunOk(mirror, "rev-parse", "main").StdOut);

        // Second run: update. A new commit upstream has to reach the existing mirror.
        File.WriteAllText(Path.Combine(work, "druhy.txt"), "second");
        git.RunOk(work, "add", "--", "druhy.txt");
        git.RunOk(work, "commit", "-m", "second");
        git.RunOk(work, "push", Path.Combine(projectRoot, "prvni.git"), "main");

        var second = RunScript(projectRoot, backupRoot, verify: true);
        Assert.True(second.ExitCode == 0, second.Output);

        var updated = git.RunOk(Path.Combine(projectRoot, "prvni.git"), "rev-parse", "main").StdOut;
        Assert.NotEqual(expected, updated);
        Assert.Equal(updated, git.RunOk(mirror, "rev-parse", "main").StdOut);
    }

    [RequiresPowerShellFact]
    public void The_mirror_is_a_working_clone_source()
    {
        using var git = new GitClient();
        var projectRoot = Path.Combine(git.Root, "repos");
        var backupRoot = Path.Combine(git.Root, "backup");
        Directory.CreateDirectory(projectRoot);

        var work = git.CreateWorkingRepository("work", content: "data to recover");
        var bare = Path.Combine(projectRoot, "projekt.git");
        git.RunOk(projectRoot, "init", "--bare", "--", bare);
        git.RunOk(work, "push", bare, "main");
        git.RunOk(bare, "symbolic-ref", "HEAD", "refs/heads/main");

        var result = RunScript(projectRoot, backupRoot);
        Assert.True(result.ExitCode == 0, result.Output);

        // Restore is documented as a plain clone from the mirror, so that is what is asserted.
        var restored = Path.Combine(git.Root, "restored");
        git.RunOk(git.Root, "clone", Path.Combine(backupRoot, "projekt.git"), restored);

        Assert.Equal("data to recover", File.ReadAllText(Path.Combine(restored, "README.md")));
    }

    [RequiresPowerShellFact]
    public void An_empty_project_root_is_not_treated_as_a_failure()
    {
        using var git = new GitClient();
        var projectRoot = Path.Combine(git.Root, "repos");
        var backupRoot = Path.Combine(git.Root, "backup");
        Directory.CreateDirectory(projectRoot);

        var result = RunScript(projectRoot, backupRoot);

        Assert.Equal(0, result.ExitCode);
    }

    [RequiresPowerShellFact]
    public void A_missing_project_root_fails_loudly()
    {
        using var git = new GitClient();
        var missing = Path.Combine(git.Root, "neexistuje");

        var result = RunScript(missing, Path.Combine(git.Root, "backup"));

        // A scheduled task that reports success while backing up nothing is the failure mode
        // worth ruling out.
        Assert.NotEqual(0, result.ExitCode);
    }
}
