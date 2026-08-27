using System.Diagnostics;

namespace GitHttpBackend;

/// <summary>
/// Locates the <c>git-http-backend</c> executable on the current machine.
/// </summary>
public static class GitBackendLocator
{
    static string ExeName =>
        OperatingSystem.IsWindows() ? "git-http-backend.exe" : "git-http-backend";

    /// <summary>
    /// Returns the full path to git-http-backend, or <c>null</c> if it cannot be found.
    /// Tries, in order: <c>git --exec-path</c>, then well-known install locations.
    /// </summary>
    public static string? Locate()
    {
        // 1) Ask git itself where its core commands live. Robust and cross-platform.
        var execPath = TryGitExecPath();
        if (execPath is not null)
        {
            var candidate = Path.Combine(execPath, ExeName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // 2) Fall back to common Git for Windows install locations.
        foreach (var root in WindowsGitRoots())
        {
            foreach (var arch in new[] { "mingw64", "mingw32" })
            {
                var candidate = Path.Combine(root, arch, "libexec", "git-core", ExeName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    static string? TryGitExecPath()
    {
        // Resolved to a full path first: with UseShellExecute = false a bare "git" would be
        // looked up by the OS starting at the application and current directories, so a
        // stray git.exe next to the host process could win over the installed one.
        var gitExe = ResolveGitExecutable();
        if (gitExe is null)
        {
            return null;
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = gitExe,
                Arguments = "--exec-path",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null)
            {
                return null;
            }

            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch
        {
            return null;
        }
    }

    // Walks PATH explicitly, then the well-known Git for Windows install roots, and returns
    // the first existing git executable as an absolute path.
    static string? ResolveGitExecutable()
    {
        var exe = OperatingSystem.IsWindows() ? "git.exe" : "git";

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry (invalid path characters) — skip it, keep scanning.
            }
        }

        foreach (var root in WindowsGitRoots())
        {
            foreach (var sub in new[] { "cmd", "bin" })
            {
                var candidate = Path.Combine(root, sub, exe);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    static IEnumerable<string> WindowsGitRoots()
    {
        foreach (var env in new[] { "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432" })
        {
            var pf = Environment.GetEnvironmentVariable(env);
            if (!string.IsNullOrEmpty(pf))
            {
                yield return Path.Combine(pf, "Git");
            }
        }
    }
}
