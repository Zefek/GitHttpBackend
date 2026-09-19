using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;

namespace GitHttpBackend.AspNetCore.Tests;

/// <summary>
/// What the sample's forwarded-headers configuration buys, seen from where it matters: the
/// client address the library hands to git as <c>REMOTE_ADDR</c>. The pipeline here mirrors
/// the sample's wiring, including the deliberately narrow <c>KnownProxies</c> — trusting
/// these headers from an untrusted source would not blank the audit trail, it would falsify
/// it, so the rejection case is the one worth pinning.
/// </summary>
public class ForwardedHeadersTests
{
    /// <summary>
    /// Runs one request through a pipeline configured the way the sample configures it, and
    /// returns the address the git handler saw.
    /// </summary>
    static async Task<(string RemoteAddr, string Scheme)> ObserveAsync(
        bool enabled, string[] knownProxies, Action<HttpRequestMessage> addHeaders)
    {
        string remoteAddr = "";
        var scheme = "";

        await using var server = await GitTestServer.StartAsync(
            root => new GitBackendOptions
            {
                ProjectRoot = root,
                Authorize = request =>
                {
                    remoteAddr = request.RemoteAddr;
                    return ValueTask.FromResult(false);   // stop before the backend runs
                },
            },
            builder =>
            {
                if (!enabled)
                    return;

                builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
                {
                    forwarded.ForwardedHeaders =
                        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                    forwarded.KnownProxies.Clear();
                    forwarded.KnownNetworks.Clear();
                    foreach (var proxy in knownProxies)
                    {
                        forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
                    }
                });
            },
            app =>
            {
                if (enabled)
                {
                    app.UseForwardedHeaders();
                }

                // Records the scheme the rest of the pipeline sees, which is what the sample's
                // home page builds its clone URLs from.
                app.Use(async (ctx, next) =>
                {
                    scheme = ctx.Request.Scheme;
                    await next();
                });
            });

        var request = new HttpRequestMessage(HttpMethod.Get, "/projekt.git/info/refs?service=git-upload-pack");
        addHeaders(request);
        var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        return (remoteAddr, scheme);
    }

    [Fact]
    public async Task The_real_client_address_reaches_the_handler()
    {
        var (remoteAddr, _) = await ObserveAsync(
            enabled: true,
            knownProxies: ["127.0.0.1", "::1"],
            request => request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.7"));

        Assert.Equal("203.0.113.7", remoteAddr);
    }

    [Fact]
    public async Task The_forwarded_scheme_reaches_the_handler()
    {
        var (_, scheme) = await ObserveAsync(
            enabled: true,
            knownProxies: ["127.0.0.1", "::1"],
            request => request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https"));

        Assert.Equal("https", scheme);
    }

    [Fact]
    public async Task A_forged_header_from_an_untrusted_source_is_ignored()
    {
        // The connection comes from loopback; the only trusted proxy is somewhere else. The
        // header must count for nothing, or any client could write its own audit trail.
        var (remoteAddr, scheme) = await ObserveAsync(
            enabled: true,
            knownProxies: ["10.0.0.1"],
            request =>
            {
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.7");
                request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            });

        Assert.Equal("127.0.0.1", remoteAddr);
        Assert.Equal("http", scheme);
    }

    [Fact]
    public async Task With_the_feature_off_the_headers_change_nothing()
    {
        var (remoteAddr, scheme) = await ObserveAsync(
            enabled: false,
            knownProxies: [],
            request =>
            {
                request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.7");
                request.Headers.TryAddWithoutValidation("X-Forwarded-Proto", "https");
            });

        Assert.Equal("127.0.0.1", remoteAddr);
        Assert.Equal("http", scheme);
    }
}
