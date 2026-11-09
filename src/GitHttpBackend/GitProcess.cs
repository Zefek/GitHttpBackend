using System.Diagnostics;

namespace GitHttpBackend;

internal sealed record GitProcessResult(int ExitCode, byte[] StdOut, string StdErr);

internal sealed class GitProcess
{
    readonly string _gitPath;
    readonly string _execDir;
    readonly GitBackendOptions _options;

    public GitProcess(string gitPath, string execDir, GitBackendOptions options)
    {
        _gitPath = gitPath;
        _execDir = execDir;
        _options = options;
    }

    public async Task<GitProcessResult> RunAsync(CancellationToken ct, params string[] arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _gitPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _execDir,
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        psi.Environment["GIT_EXEC_PATH"] = _execDir;
        if (_options.ExtraEnvironment is not null)
        {
            foreach (var kv in _options.ExtraEnvironment)
            {
                psi.Environment[kv.Key] = kv.Value;
            }
        }
        if (_options.SafeDirectories is { Count: > 0 } safeDirectories)
        {
            GitHttpBackendInvoker.AppendSafeDirectories(psi.Environment, safeDirectories);
        }

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start '{_gitPath}'.");

        try
        {
            var stderrTask = process.StandardError.ReadToEndAsync(ct);
            using var stdout = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(stdout, ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            return new GitProcessResult(
                process.ExitCode, stdout.ToArray(), (await stderrTask.ConfigureAwait(false)).Trim());
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch { }
            throw;
        }
    }
}
