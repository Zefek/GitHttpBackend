namespace GitHttpBackend.Configuration;

internal sealed class ConfigRepositoryCache
{
    readonly string _directory;

    public ConfigRepositoryCache(string directory)
    {
        _directory = directory;
    }

    public string Directory => _directory;

    string FilePath => Path.Combine(_directory, "snapshot.json");

    public static string DefaultDirectory(string url, string path)
    {
        var uri = new Uri(url);
        var name = $"{uri.Host}{uri.AbsolutePath}_{path}";
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim('_');

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GitHttpBackend.Configuration",
            safe);
    }

    public byte[]? Read()
        => File.Exists(FilePath) ? File.ReadAllBytes(FilePath) : null;

    public void Write(byte[] snapshot)
    {
        System.IO.Directory.CreateDirectory(_directory);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, snapshot);
        File.Move(temp, FilePath, overwrite: true);
    }
}
