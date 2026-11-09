using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GitHttpBackend.Configuration.Tests;

public sealed class ConfigRepositoryTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "githttpbackend-config-tests", Guid.NewGuid().ToString("n"));
    readonly X509Certificate2 _certificate = CreateCertificate();

    public void Dispose()
    {
        _certificate.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    static X509Certificate2 CreateCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=ConfigRepositoryTests", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DataEncipherment, critical: false));
        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        return X509CertificateLoader.LoadPkcs12(created.Export(X509ContentType.Pkcs12), null);
    }

    string Encrypt(string plaintext, bool armored = true)
    {
        var cms = new EnvelopedCms(new ContentInfo(Encoding.UTF8.GetBytes(plaintext)));
        cms.Encrypt(new CmsRecipient(_certificate));
        var base64 = Convert.ToBase64String(cms.Encode(), Base64FormattingOptions.InsertLineBreaks);
        return armored ? $"-----BEGIN CMS-----\r\n{base64}\r\n-----END CMS-----\r\n" : base64.ReplaceLineEndings("");
    }

    static byte[] Snapshot(string commit, params (string Name, string Content)[] files)
        => JsonSerializer.SerializeToUtf8Bytes(new
        {
            commit,
            files = files.Select(f => new { name = f.Name, content = Encoding.UTF8.GetBytes(f.Content) }),
        });

    CmsDecryptor Decryptor(string? _) => new([_certificate]);

    HostApplicationBuilder Builder(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        settings.TryAdd("ConfigRepo:Url", "https://git.home/Home");
        settings.TryAdd("ConfigRepo:Path", "OdectyMVC");
        settings.TryAdd("ConfigRepo:CacheDirectory", Path.Combine(_root, "cache"));
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }

    ConfigRepositoryInfo? Load(HostApplicationBuilder builder, FakeHandler handler, string[]? args = null)
        => builder.AddConfigRepository(args ?? [], ConfigRepositoryExtensions.DefaultSectionName, handler, Decryptor, TimeSpan.Zero);

    [Fact]
    public void Without_a_url_nothing_is_loaded()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var handler = new FakeHandler();

        var info = builder.AddConfigRepository([], ConfigRepositoryExtensions.DefaultSectionName, handler, Decryptor);

        Assert.Null(info);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public void Merges_plain_and_encrypted_json_and_ignores_other_files()
    {
        var handler = new FakeHandler(_ => FakeHandler.Ok(Snapshot("abc123",
            ("appsettings.json", """{ "Plain": { "A": "1" } }"""),
            ("appSettings.Production.json.cms", Encrypt("""{ "ConnectionStrings": { "Odecty": "Host=db;Password=x" } }""")),
            ("deploy.ps1", "param()"),
            ("GaugeList._json", """{ "Plain": { "A": "2" } }"""))));
        var builder = Builder([]);

        var info = Load(builder, handler);

        Assert.Equal("1", builder.Configuration["Plain:A"]);
        Assert.Equal("Host=db;Password=x", builder.Configuration.GetConnectionString("Odecty"));
        Assert.NotNull(info);
        Assert.Equal("abc123", info.Commit);
        Assert.Equal(ConfigRepositorySource.Remote, info.Source);
        Assert.Equal(["appsettings.json", "appSettings.Production.json.cms"], info.Files);
        Assert.Null(info.Warning);
        Assert.Equal("https://git.home/Home/-/snapshot?ref=HEAD&path=OdectyMVC", handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public void Environment_variables_and_command_line_still_win_over_the_repository()
    {
        var handler = new FakeHandler(_ => FakeHandler.Ok(Snapshot("abc", ("appsettings.json", """{ "X": "repo", "Y": "repo" }"""))));
        var builder = Builder([]);

        Load(builder, handler, ["--Y=cmd"]);

        Assert.Equal("repo", builder.Configuration["X"]);
        Assert.Equal("cmd", builder.Configuration["Y"]);
    }

    [Fact]
    public void A_key_defined_in_two_files_fails_and_names_both_files_but_not_the_value()
    {
        var handler = new FakeHandler(_ => FakeHandler.Ok(Snapshot("abc",
            ("a.json", """{ "Db": { "Password": "secret-one" } }"""),
            ("b.json.cms", Encrypt("""{ "db": { "password": "secret-two" } }""")))));

        var ex = Assert.Throws<InvalidOperationException>(() => Load(Builder([]), handler));

        Assert.Contains("a.json", ex.Message);
        Assert.Contains("b.json.cms", ex.Message);
        Assert.DoesNotContain("secret", ex.Message);
    }

    [Fact]
    public void Sends_basic_auth_with_a_cms_encrypted_password()
    {
        var handler = new FakeHandler(_ => FakeHandler.Ok(Snapshot("abc")));
        var builder = Builder(new()
        {
            ["ConfigRepo:User"] = "cfg-reader",
            ["ConfigRepo:PasswordCms"] = Encrypt("﻿s3cr3t\r\n", armored: false),
            ["ConfigRepo:Ref"] = "master",
        });

        Load(builder, handler);

        var request = handler.Requests.Single();
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal("cfg-reader:s3cr3t", Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!)));
        Assert.Contains("ref=master", request.RequestUri!.Query);
    }

    [Fact]
    public void Falls_back_to_the_cached_snapshot_when_the_server_is_unreachable()
    {
        var up = new FakeHandler(_ => FakeHandler.Ok(Snapshot("good", ("appsettings.json", """{ "X": "cached" }"""))));
        Load(Builder([]), up);

        var down = new FakeHandler(_ => throw new HttpRequestException("connection refused"));
        var builder = Builder([]);
        var info = Load(builder, down);

        Assert.Equal("cached", builder.Configuration["X"]);
        Assert.Equal(ConfigRepositorySource.Cache, info!.Source);
        Assert.Equal("good", info.Commit);
        Assert.Contains("connection refused", info.Warning);
        Assert.Equal(3, down.Requests.Count);
    }

    [Fact]
    public void Rejected_credentials_are_not_retried_and_fall_back_to_the_cache()
    {
        Load(Builder([]), new FakeHandler(_ => FakeHandler.Ok(Snapshot("good", ("appsettings.json", """{ "X": "1" }""")))));
        var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var info = Load(Builder([]), handler);

        Assert.Single(handler.Requests);
        Assert.Equal(ConfigRepositorySource.Cache, info!.Source);
        Assert.Contains("401", info.Warning);
    }

    [Fact]
    public void Without_server_and_cache_startup_fails()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("connection refused"));

        var ex = Assert.Throws<InvalidOperationException>(() => Load(Builder([]), handler));

        Assert.Contains("no cached snapshot", ex.Message);
    }

    [Fact]
    public void A_snapshot_that_cannot_be_decrypted_fails_and_does_not_replace_the_cache()
    {
        Load(Builder([]), new FakeHandler(_ => FakeHandler.Ok(Snapshot("good", ("appsettings.json", """{ "X": "1" }""")))));
        string foreignCiphertext;
        using (var foreign = new ConfigRepositoryTests())
        {
            foreignCiphertext = foreign.Encrypt("""{ "X": "2" }""");
        }
        var bad = new FakeHandler(_ => FakeHandler.Ok(Snapshot("bad", ("appsettings.json.cms", foreignCiphertext))));

        Assert.Throws<InvalidOperationException>(() => Load(Builder([]), bad));

        var info = Load(Builder([]), new FakeHandler(_ => throw new HttpRequestException("down")));
        Assert.Equal("good", info!.Commit);
    }

    [Fact]
    public void Registers_the_info_for_the_application()
    {
        var builder = Builder([]);
        Load(builder, new FakeHandler(_ => FakeHandler.Ok(Snapshot("abc"))));

        using var host = builder.Build();

        Assert.Equal("abc", host.Services.GetRequiredService<ConfigRepositoryInfo>().Commit);
    }

    sealed class FakeHandler : HttpMessageHandler
    {
        readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
        {
            _respond = respond ?? (_ => throw new InvalidOperationException("No request expected."));
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        public static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request));
        }
    }
}
