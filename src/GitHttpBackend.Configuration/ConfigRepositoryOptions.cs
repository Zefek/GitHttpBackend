namespace GitHttpBackend.Configuration;

public sealed class ConfigRepositoryOptions
{
    public string? Url { get; set; }
    public string Ref { get; set; } = "HEAD";
    public string Path { get; set; } = "";
    public string? User { get; set; }
    public string? Password { get; set; }
    public string? PasswordCms { get; set; }
    public string? Thumbprint { get; set; }
    public string? CacheDirectory { get; set; }
    public int TimeoutSeconds { get; set; } = 15;
    public int Attempts { get; set; } = 3;
}

public enum ConfigRepositorySource
{
    Remote,
    Cache,
}

public sealed record ConfigRepositoryInfo(
    string Url,
    string Ref,
    string Path,
    string Commit,
    ConfigRepositorySource Source,
    IReadOnlyList<string> Files,
    string? Warning);
