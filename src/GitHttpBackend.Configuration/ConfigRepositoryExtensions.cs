using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GitHttpBackend.Configuration;

public static class ConfigRepositoryExtensions
{
    public const string DefaultSectionName = "ConfigRepo";

    public static ConfigRepositoryInfo? AddConfigRepository(
        this IHostApplicationBuilder builder, string[] args, string sectionName = DefaultSectionName)
    {
        using var handler = new SocketsHttpHandler();
        return builder.AddConfigRepository(args, sectionName, handler, CmsDecryptor.FromStore);
    }

    internal static ConfigRepositoryInfo? AddConfigRepository(
        this IHostApplicationBuilder builder,
        string[] args,
        string sectionName,
        HttpMessageHandler handler,
        Func<string?, CmsDecryptor> decryptor,
        TimeSpan? retryDelay = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(args);

        var options = builder.Configuration.GetSection(sectionName).Get<ConfigRepositoryOptions>();
        if (string.IsNullOrWhiteSpace(options?.Url))
            return null;

        var cacheDirectory = string.IsNullOrWhiteSpace(options.CacheDirectory)
            ? ConfigRepositoryCache.DefaultDirectory(options)
            : options.CacheDirectory;

        var loader = new ConfigRepositoryLoader(options, handler, () => decryptor(options.Thumbprint), cacheDirectory)
        {
            RetryDelay = retryDelay ?? TimeSpan.FromSeconds(2),
        };
        var (data, info) = loader.Load();

        builder.Configuration.AddInMemoryCollection(data);
        builder.Configuration.AddEnvironmentVariables();
        builder.Configuration.AddCommandLine(args);

        builder.Services.AddSingleton(info);
        builder.Services.AddHostedService<ConfigRepositoryStartupLog>();

        return info;
    }
}

internal sealed class ConfigRepositoryStartupLog : IHostedService
{
    readonly ConfigRepositoryInfo _info;
    readonly ILogger<ConfigRepositoryStartupLog> _logger;

    public ConfigRepositoryStartupLog(ConfigRepositoryInfo info, ILogger<ConfigRepositoryStartupLog> logger)
    {
        _info = info;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Configuration loaded from {ConfigUrl} ref={ConfigRef} path={ConfigPath} commit={ConfigCommit} source={ConfigSource} files={ConfigFiles}",
            _info.Url, _info.Ref, _info.Path, _info.Commit, _info.Source, string.Join(", ", _info.Files));

        if (_info.Warning is not null)
            _logger.LogWarning("{ConfigWarning}", _info.Warning);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
