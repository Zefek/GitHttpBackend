namespace GitHttpBackend;

public sealed record GitSnapshot(string Commit, IReadOnlyList<GitSnapshotFile> Files);

public sealed record GitSnapshotFile(string Name, byte[] Content);
