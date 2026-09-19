using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GitHttpBackend.AspNetCore.Tests;

/// <summary>
/// A real Kestrel host on a loopback port with <c>MapGitHttpBackend</c> over a throwaway
/// project root. Kestrel rather than <c>TestServer</c> on purpose: what these tests are for
/// is the behaviour of an external <c>git-http-backend</c> process reached over a socket,
/// and an in-memory transport would quietly change the part under test.
/// </summary>
sealed class GitTestServer : IAsyncDisposable
{
    readonly WebApplication _app;

    GitTestServer(WebApplication app, string projectRoot, HttpClient client)
    {
        _app = app;
        ProjectRoot = projectRoot;
        Client = client;
    }

    /// <summary>Throwaway directory handed to git as <c>GIT_PROJECT_ROOT</c>.</summary>
    public string ProjectRoot { get; }

    /// <summary>Client whose base address is the running server.</summary>
    public HttpClient Client { get; }

    /// <summary>
    /// Starts a server over a fresh project root. <paramref name="configure"/> receives that
    /// root and returns the options to map, so a test can set its own <c>Authorize</c> hook.
    /// </summary>
    public static Task<GitTestServer> StartAsync(Func<string, GitBackendOptions> configure)
        => StartAsync(configure, configureBuilder: null, configurePipeline: null);

    /// <summary>
    /// Same, with hooks to register services and middleware ahead of the git endpoint — for
    /// the cases where what is under test is how the host's pipeline changes what the library
    /// sees.
    /// </summary>
    public static Task<GitTestServer> StartAsync(
        Func<string, GitBackendOptions> configure,
        Action<WebApplicationBuilder>? configureBuilder,
        Action<WebApplication>? configurePipeline)
        => StartCoreAsync((app, root) => app.MapGitHttpBackend("/", configure(root)), configureBuilder, configurePipeline);

    /// <summary>
    /// Same, but the caller builds the invoker — the overload a host uses when it wants the
    /// resolved backend path for itself.
    /// </summary>
    public static Task<GitTestServer> StartWithInvokerAsync(Func<string, GitHttpBackendInvoker> configure)
        => StartCoreAsync((app, root) => app.MapGitHttpBackend("/", configure(root)), configureBuilder: null, configurePipeline: null);

    static async Task<GitTestServer> StartCoreAsync(
        Action<WebApplication, string> map,
        Action<WebApplicationBuilder>? configureBuilder,
        Action<WebApplication>? configurePipeline)
    {
        var projectRoot = Path.Combine(
            Path.GetTempPath(), "githttpbackend-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(projectRoot);

        WebApplication? app = null;
        try
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");   // port 0: the OS picks a free one
            builder.Logging.ClearProviders();
            builder.Services.AddLogging();
            configureBuilder?.Invoke(builder);

            app = builder.Build();
            configurePipeline?.Invoke(app);
            map(app, projectRoot);
            await app.StartAsync();

            var address = app.Urls.First();
            var client = new HttpClient { BaseAddress = new Uri(address) };
            return new GitTestServer(app, projectRoot, client);
        }
        catch
        {
            if (app is not null)
            {
                await app.DisposeAsync();
            }
            DeleteDirectory(projectRoot);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try { await _app.StopAsync(TimeSpan.FromSeconds(10)); } catch { /* shutting down anyway */ }
        await _app.DisposeAsync();
        DeleteDirectory(ProjectRoot);
    }

    // Git marks pack files read-only, which Directory.Delete refuses to remove. Cleanup runs
    // even when a test failed, so it must not throw and mask the real failure.
    internal static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { /* best effort */ }
            }
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A leftover directory under TEMP is a smaller problem than a test run that
            // reports a cleanup error instead of the assertion that actually failed.
        }
    }
}
