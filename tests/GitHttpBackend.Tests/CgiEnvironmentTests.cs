namespace GitHttpBackend.Tests;

/// <summary>
/// The CGI environment is the entire contract with git-http-backend, and the SafeDirectories
/// ordering carries a stated guarantee: a caller-supplied GIT_CONFIG_COUNT is extended, not
/// overwritten. Silently clobbering it would drop the caller's config with no visible error.
/// </summary>
public class CgiEnvironmentTests
{
    const string ExecDir = @"C:\Program Files\Git\mingw64\libexec\git-core";

    static Dictionary<string, string?> Build(GitBackendOptions options, CgiRequest? request = null)
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        GitHttpBackendInvoker.PopulateEnvironment(env, options, ExecDir, request ?? Request());
        return env;
    }

    static CgiRequest Request(string method = "GET", string pathInfo = "/projekt.git/info/refs")
        => new()
        {
            Method = method,
            PathInfo = pathInfo,
            QueryString = "service=git-upload-pack",
            RemoteAddr = "10.0.0.5",
            Body = Stream.Null,
        };

    [Fact]
    public void Maps_the_request_onto_the_CGI_variables()
    {
        var env = Build(new GitBackendOptions { ProjectRoot = @"C:\git-repos" });

        Assert.Equal(@"C:\git-repos", env["GIT_PROJECT_ROOT"]);
        Assert.Equal("1", env["GIT_HTTP_EXPORT_ALL"]);
        Assert.Equal(ExecDir, env["GIT_EXEC_PATH"]);
        Assert.Equal("GET", env["REQUEST_METHOD"]);
        Assert.Equal("/projekt.git/info/refs", env["PATH_INFO"]);
        Assert.Equal("service=git-upload-pack", env["QUERY_STRING"]);
        Assert.Equal("10.0.0.5", env["REMOTE_ADDR"]);
    }

    [Fact]
    public void Omits_the_export_all_flag_when_it_is_off()
    {
        var env = Build(new GitBackendOptions { ProjectRoot = @"C:\git-repos", ExportAll = false });

        Assert.False(env.ContainsKey("GIT_HTTP_EXPORT_ALL"));
    }

    [Fact]
    public void Omits_optional_variables_that_have_no_value()
    {
        var env = Build(new GitBackendOptions { ProjectRoot = @"C:\git-repos" });

        Assert.False(env.ContainsKey("CONTENT_TYPE"));
        Assert.False(env.ContainsKey("CONTENT_LENGTH"));
        Assert.False(env.ContainsKey("HTTP_CONTENT_ENCODING"));
        Assert.False(env.ContainsKey("GIT_PROTOCOL"));
        Assert.False(env.ContainsKey("REMOTE_USER"));
    }

    [Fact]
    public void Carries_the_post_variables_and_the_protocol_header()
    {
        var env = Build(new GitBackendOptions { ProjectRoot = @"C:\git-repos" }, new CgiRequest
        {
            Method = "POST",
            PathInfo = "/projekt.git/git-upload-pack",
            ContentType = "application/x-git-upload-pack-request",
            ContentLength = 4096,
            ContentEncoding = "gzip",
            GitProtocol = "version=2",
            RemoteUser = "ci",
            Body = Stream.Null,
        });

        Assert.Equal("application/x-git-upload-pack-request", env["CONTENT_TYPE"]);
        Assert.Equal("4096", env["CONTENT_LENGTH"]);
        Assert.Equal("gzip", env["HTTP_CONTENT_ENCODING"]);
        Assert.Equal("version=2", env["GIT_PROTOCOL"]);
        Assert.Equal("ci", env["REMOTE_USER"]);
    }

    [Fact]
    public void Safe_directories_become_numbered_config_pairs()
    {
        var env = Build(new GitBackendOptions
        {
            ProjectRoot = @"C:\git-repos",
            SafeDirectories = ["*", @"D:\other"],
        });

        Assert.Equal("safe.directory", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal("*", env["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("safe.directory", env["GIT_CONFIG_KEY_1"]);
        Assert.Equal(@"D:\other", env["GIT_CONFIG_VALUE_1"]);
        Assert.Equal("2", env["GIT_CONFIG_COUNT"]);
    }

    [Fact]
    public void Safe_directories_extend_a_count_supplied_through_extra_environment()
    {
        var env = Build(new GitBackendOptions
        {
            ProjectRoot = @"C:\git-repos",
            ExtraEnvironment = new Dictionary<string, string>
            {
                ["GIT_CONFIG_COUNT"] = "1",
                ["GIT_CONFIG_KEY_0"] = "core.quotepath",
                ["GIT_CONFIG_VALUE_0"] = "false",
            },
            SafeDirectories = ["*"],
        });

        // The caller's pair survives and ours is appended after it.
        Assert.Equal("core.quotepath", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal("false", env["GIT_CONFIG_VALUE_0"]);
        Assert.Equal("safe.directory", env["GIT_CONFIG_KEY_1"]);
        Assert.Equal("*", env["GIT_CONFIG_VALUE_1"]);
        Assert.Equal("2", env["GIT_CONFIG_COUNT"]);
    }

    [Fact]
    public void A_nonsense_existing_count_is_not_trusted()
    {
        var env = Build(new GitBackendOptions
        {
            ProjectRoot = @"C:\git-repos",
            ExtraEnvironment = new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = "not-a-number" },
            SafeDirectories = ["*"],
        });

        Assert.Equal("safe.directory", env["GIT_CONFIG_KEY_0"]);
        Assert.Equal("1", env["GIT_CONFIG_COUNT"]);
    }

    [Fact]
    public void Extra_environment_wins_over_the_variables_the_library_sets()
    {
        var env = Build(new GitBackendOptions
        {
            ProjectRoot = @"C:\git-repos",
            ExtraEnvironment = new Dictionary<string, string> { ["GIT_PROJECT_ROOT"] = @"D:\elsewhere" },
        });

        Assert.Equal(@"D:\elsewhere", env["GIT_PROJECT_ROOT"]);
    }

    [Fact]
    public void No_config_pairs_appear_without_safe_directories()
    {
        var env = Build(new GitBackendOptions { ProjectRoot = @"C:\git-repos" });

        Assert.False(env.ContainsKey("GIT_CONFIG_COUNT"));
    }
}
