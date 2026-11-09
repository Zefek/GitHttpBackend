using System.Text;

namespace GitHttpBackend;

public sealed class GitSnapshotReader
{
    readonly GitBackendOptions _options;
    readonly GitProcess _git;

    public GitSnapshotReader(GitHttpBackendInvoker invoker)
    {
        ArgumentNullException.ThrowIfNull(invoker);

        _options = invoker.Options;
        _git = new GitProcess(GitHttpBackendInvoker.ResolveGitClient(invoker.ExecDir), invoker.ExecDir, _options);
    }

    public GitBackendOptions Options => _options;

    public static bool IsValidReference(string? reference)
    {
        if (string.IsNullOrEmpty(reference) || reference.Length > 255)
            return false;

        if (reference[0] is '-' or '/' || reference[^1] is '/' or '.')
            return false;

        if (reference.Contains("..", StringComparison.Ordinal) || reference.Contains("//", StringComparison.Ordinal))
            return false;

        foreach (var c in reference)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/'))
                return false;
        }

        return true;
    }

    public static bool IsValidPath(string? path)
    {
        if (path is null)
            return false;

        if (path.Length == 0)
            return true;

        foreach (var c in path)
        {
            if (c is '\\' or ':' || char.IsControl(c))
                return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment.All(c => c == '.'))
                return false;
        }

        return true;
    }

    public async Task<GitSnapshot?> ReadAsync(
        string repository, string reference, string path, CancellationToken ct = default)
    {
        if (!GitRepositoryPath.TryParse(repository, out var name, out var rest) || rest.Length != 0)
            throw new ArgumentException("Not a valid repository name.", nameof(repository));
        if (!IsValidReference(reference))
            throw new ArgumentException("Not a valid reference.", nameof(reference));
        if (!IsValidPath(path))
            throw new ArgumentException("Not a valid path.", nameof(path));

        var rootFull = Path.GetFullPath(_options.ProjectRoot);
        if (GitRepositoryCreator.ExistingRepository(rootFull, name) is not { } repositoryDir)
            return null;

        if (!_options.ExportAll && !File.Exists(Path.Combine(repositoryDir, "git-daemon-export-ok")))
            return null;

        var resolved = await _git.RunAsync(ct,
            "-C", repositoryDir, "rev-parse", "--verify", "--quiet", reference + "^{commit}").ConfigureAwait(false);
        if (resolved.ExitCode != 0)
        {
            if (resolved.StdErr.Length == 0)
                return null;
            throw new InvalidOperationException(
                $"git rev-parse exited with {resolved.ExitCode}: {resolved.StdErr}");
        }

        var commit = Encoding.ASCII.GetString(resolved.StdOut).Trim();
        var treeish = path.Length == 0 ? commit : $"{commit}:{path}";

        var tree = await _git.RunAsync(ct, "-C", repositoryDir, "ls-tree", "-z", treeish).ConfigureAwait(false);
        if (tree.ExitCode != 0)
            return null;

        var files = new List<GitSnapshotFile>();
        foreach (var entry in Encoding.UTF8.GetString(tree.StdOut).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = entry.IndexOf('\t');
            if (tab < 0)
                continue;

            var meta = entry[..tab].Split(' ');
            if (meta.Length != 3 || meta[1] != "blob" || meta[0] is not ("100644" or "100755"))
                continue;

            var blob = await _git.RunAsync(ct, "-C", repositoryDir, "cat-file", "blob", meta[2]).ConfigureAwait(false);
            if (blob.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git cat-file exited with {blob.ExitCode}: {blob.StdErr}");
            }

            files.Add(new GitSnapshotFile(entry[(tab + 1)..], blob.StdOut));
        }

        return new GitSnapshot(commit, files);
    }
}
