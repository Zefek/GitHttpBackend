using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace GitHttpBackend.Configuration;

internal sealed record SnapshotFile(string Name, byte[] Content);

internal sealed record Snapshot(string Commit, List<SnapshotFile> Files);

internal sealed class ConfigRepositoryLoader
{
    readonly ConfigRepositoryOptions _options;
    readonly HttpMessageHandler _handler;
    readonly Lazy<CmsDecryptor> _decryptor;
    readonly ConfigRepositoryCache _cache;

    public ConfigRepositoryLoader(
        ConfigRepositoryOptions options, HttpMessageHandler handler, Func<CmsDecryptor> decryptor, string cacheDirectory)
    {
        if (string.IsNullOrWhiteSpace(options.Url))
            throw new ArgumentException("ConfigRepository Url is not set.", nameof(options));

        _options = options;
        _handler = handler;
        _decryptor = new Lazy<CmsDecryptor>(decryptor);
        _cache = new ConfigRepositoryCache(cacheDirectory);
    }

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    public (Dictionary<string, string?> Data, ConfigRepositoryInfo Info) Load()
    {
        byte[] raw;
        var source = ConfigRepositorySource.Remote;
        string? warning = null;

        try
        {
            raw = Fetch();
        }
        catch (Exception ex) when (ex is not ConfigRepositoryDecryptionException)
        {
            raw = _cache.Read() ?? throw new InvalidOperationException(
                $"Configuration repository {SafeUrl()} could not be read and there is no cached snapshot in {_cache.Directory}.",
                ex);
            source = ConfigRepositorySource.Cache;
            warning = $"Configuration repository {SafeUrl()} could not be read ({ex.GetBaseException().Message}); "
                + $"using the cached snapshot from {_cache.Directory}.";
        }

        var snapshot = Parse(raw);
        var (data, files) = Flatten(snapshot);

        if (source == ConfigRepositorySource.Remote)
        {
            try
            {
                _cache.Write(raw);
            }
            catch (Exception ex)
            {
                warning = $"The snapshot could not be cached in {_cache.Directory}: {ex.Message}";
            }
        }

        return (data, new ConfigRepositoryInfo(
            SafeUrl(), _options.Ref, _options.Path, snapshot.Commit, source, files, warning));
    }

    byte[] Fetch()
    {
        using var client = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)),
        };

        var url = $"{_options.Url!.TrimEnd('/')}/-/snapshot"
            + $"?ref={Uri.EscapeDataString(_options.Ref)}&path={Uri.EscapeDataString(_options.Path.Trim('/'))}";
        var authorization = Authorization();
        var attempts = Math.Max(1, _options.Attempts);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = authorization;
                using var response = client.SendAsync(request).GetAwaiter().GetResult();

                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                    or HttpStatusCode.Forbidden or HttpStatusCode.NotFound)
                {
                    throw new InvalidOperationException(
                        $"{SafeUrl()} answered {(int)response.StatusCode} {response.StatusCode} for ref '{_options.Ref}' and path '{_options.Path}'.");
                }

                response.EnsureSuccessStatusCode();
                return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && attempt < attempts)
            {
                Thread.Sleep(RetryDelay * attempt);
            }
        }
    }

    AuthenticationHeaderValue? Authorization()
    {
        if (string.IsNullOrEmpty(_options.User))
            return null;

        var password = string.IsNullOrWhiteSpace(_options.PasswordCms)
            ? _options.Password ?? ""
            : DecryptPassword();

        return new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_options.User}:{password}")));
    }

    string DecryptPassword()
    {
        try
        {
            return _decryptor.Value.DecryptString(_options.PasswordCms!, "ConfigRepo PasswordCms");
        }
        catch (Exception ex)
        {
            throw new ConfigRepositoryDecryptionException(ex.Message, ex);
        }
    }

    static Snapshot Parse(byte[] raw)
    {
        var snapshot = JsonSerializer.Deserialize<Snapshot>(raw, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (snapshot is null || string.IsNullOrEmpty(snapshot.Commit) || snapshot.Files is null)
            throw new InvalidOperationException("The configuration snapshot is malformed.");
        return snapshot;
    }

    (Dictionary<string, string?> Data, List<string> Files) Flatten(Snapshot snapshot)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var origin = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var used = new List<string>();

        foreach (var file in snapshot.Files
                     .Where(f => IsJson(f.Name) || IsEncryptedJson(f.Name))
                     .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
        {
            var json = IsEncryptedJson(file.Name) ? _decryptor.Value.Decrypt(file.Content, file.Name) : file.Content;

            IConfigurationRoot parsed;
            try
            {
                parsed = new ConfigurationBuilder().AddJsonStream(new MemoryStream(json)).Build();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"{file.Name} at commit {snapshot.Commit} is not valid JSON.", ex);
            }

            foreach (var (key, value) in parsed.AsEnumerable())
            {
                if (value is null)
                    continue;

                if (origin.TryGetValue(key, out var other))
                {
                    throw new InvalidOperationException(
                        $"Configuration key '{key}' is defined in both {other} and {file.Name} at commit {snapshot.Commit}.");
                }

                origin[key] = file.Name;
                data[key] = value;
            }

            used.Add(file.Name);
        }

        return (data, used);
    }

    static bool IsJson(string name) => name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    static bool IsEncryptedJson(string name) => name.EndsWith(".json.cms", StringComparison.OrdinalIgnoreCase);

    string SafeUrl()
    {
        var builder = new UriBuilder(_options.Url!) { UserName = "", Password = "" };
        return builder.Uri.ToString().TrimEnd('/');
    }
}

internal sealed class ConfigRepositoryDecryptionException(string message, Exception inner)
    : InvalidOperationException(message, inner);
