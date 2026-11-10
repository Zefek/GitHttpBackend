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

        if (path.Any(c => c is '\\' or ':' || char.IsControl(c)))
            return false;

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
        var name = ValidateArguments(repository, reference, path);

        if (ExportedRepository(name) is not { } repositoryDir)
            return null;

        if (await ResolveCommitAsync(repositoryDir, reference, ct).ConfigureAwait(false) is not { } commit)
            return null;

        var treeish = path.Length == 0 ? commit : $"{commit}:{path}";
        var tree = await _git.RunAsync(ct, "-C", repositoryDir, "ls-tree", "-z", treeish).ConfigureAwait(false);
        if (tree.ExitCode != 0)
            return null;

        var files = new List<GitSnapshotFile>();
        foreach (var entry in Encoding.UTF8.GetString(tree.StdOut).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParseRegularBlob(entry, out var fileName, out var objectId))
                continue;

            var blob = await _git.RunAsync(ct, "-C", repositoryDir, "cat-file", "blob", objectId).ConfigureAwait(false);
            if (blob.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"git cat-file exited with {blob.ExitCode}: {blob.StdErr}");
            }

            files.Add(new GitSnapshotFile(fileName, blob.StdOut));
        }

        return new GitSnapshot(commit, files);
    }

    static string ValidateArguments(string repository, string reference, string path)
    {
        if (!GitRepositoryPath.TryParse(repository, out var name, out var rest) || rest.Length != 0)
            throw new ArgumentException("Not a valid repository name.", nameof(repository));
        if (!IsValidReference(reference))
            throw new ArgumentException("Not a valid reference.", nameof(reference));
        if (!IsValidPath(path))
            throw new ArgumentException("Not a valid path.", nameof(path));

        return name;
    }

    string? ExportedRepository(string name)
    {
        var rootFull = Path.GetFullPath(_options.ProjectRoot);
        if (GitRepositoryCreator.ExistingRepository(rootFull, name) is not { } repositoryDir)
            return null;

        if (!_options.ExportAll && !File.Exists(Path.Combine(repositoryDir, "git-daemon-export-ok")))
            return null;

        return repositoryDir;
    }

    async Task<string?> ResolveCommitAsync(string repositoryDir, string reference, CancellationToken ct)
    {
        var resolved = await _git.RunAsync(ct,
            "-C", repositoryDir, "rev-parse", "--verify", "--quiet", reference + "^{commit}").ConfigureAwait(false);
        if (resolved.ExitCode == 0)
            return Encoding.ASCII.GetString(resolved.StdOut).Trim();

        if (resolved.StdErr.Length == 0)
            return null;

        throw new InvalidOperationException(
            $"git rev-parse exited with {resolved.ExitCode}: {resolved.StdErr}");
    }

    static bool TryParseRegularBlob(string entry, out string fileName, out string objectId)
    {
        fileName = "";
        objectId = "";

        var tab = entry.IndexOf('\t');
        if (tab < 0)
            return false;

        var meta = entry[..tab].Split(' ');
        if (meta.Length != 3 || meta[1] != "blob" || meta[0] is not ("100644" or "100755"))
            return false;

        fileName = entry[(tab + 1)..];
        objectId = meta[2];
        return true;
    }
}
