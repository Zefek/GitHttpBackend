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
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "git",
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
