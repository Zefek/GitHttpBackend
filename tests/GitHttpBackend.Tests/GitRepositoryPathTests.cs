namespace GitHttpBackend.Tests;

/// <summary>
/// The parser is the single place that answers "which repository is this request about?".
/// Authorization rests on that answer matching the one git-http-backend's enter_repo()
/// arrives at, so the rejection cases below are security tests, not input hygiene.
/// </summary>
public class GitRepositoryPathTests
{
    [Theory]
    // The shapes a git client actually produces.
    [InlineData("/projekt.git/info/refs", "projekt.git", "/info/refs")]
    [InlineData("/projekt/info/refs", "projekt", "/info/refs")]
    [InlineData("/projekt.git/git-upload-pack", "projekt.git", "/git-upload-pack")]
    [InlineData("/projekt.git/git-receive-pack", "projekt.git", "/git-receive-pack")]
    [InlineData("/projekt.git/objects/info/packs", "projekt.git", "/objects/info/packs")]
    // Non-ASCII names work today and the library's logging is written for them.
    [InlineData("/pokusný-projekt.git/info/refs", "pokusný-projekt.git", "/info/refs")]
    [InlineData("/проект.git/info/refs", "проект.git", "/info/refs")]
    // The repository on its own, with and without the trailing separator.
    [InlineData("/projekt.git", "projekt.git", "")]
    [InlineData("/projekt.git/", "projekt.git", "")]
    // A leading slash is what PATH_INFO carries, but not requiring one costs nothing.
    [InlineData("projekt.git/info/refs", "projekt.git", "/info/refs")]
    // Dots inside a name are ordinary characters; only whole-segment dots traverse.
    [InlineData("/my.config.repo.git/info/refs", "my.config.repo.git", "/info/refs")]
    public void Accepts_plain_repository_paths(string pathInfo, string expectedRepository, string expectedRest)
    {
        Assert.True(GitRepositoryPath.TryParse(pathInfo, out var repository, out var rest));
        Assert.Equal(expectedRepository, repository);
        Assert.Equal(expectedRest, rest);
    }

    [Theory]
    // Traversal, in the forms that reach a Windows filesystem.
    [InlineData("/../etc/passwd")]
    [InlineData("/projekt.git/../../secrets.git/info/refs")]
    [InlineData("/..")]
    [InlineData("/.")]
    [InlineData("/.../info/refs")]
    // Percent-encoding: the validator and git must not sit on opposite sides of a decode.
    [InlineData("/%2e%2e/secrets.git/info/refs")]
    [InlineData("/projekt%2fother/info/refs")]
    [InlineData("/projekt%5cother/info/refs")]
    [InlineData("/projekt%00.git/info/refs")]
    // Backslash is a separator on Windows, so these are traversal too.
    [InlineData("/projekt.git\\..\\secrets.git/info/refs")]
    [InlineData("\\\\host\\share\\repo.git")]
    // Absolute and drive-qualified paths must never become a repository name.
    [InlineData("//host/share/repo.git")]
    [InlineData("/C:/git-repos/secrets.git")]
    [InlineData("C:\\git-repos\\secrets.git")]
    // Null bytes truncate strings in the C code on the other side of the process boundary.
    [InlineData("/\0projekt.git/info/refs")]
    [InlineData("/projekt\0.git/info/refs")]
    // Windows strips trailing dots and spaces, which would give one repository two names.
    [InlineData("/projekt.git./info/refs")]
    [InlineData("/projekt.git /info/refs")]
    // Nothing to serve.
    [InlineData("")]
    [InlineData("/")]
    [InlineData("//")]
    [InlineData(null)]
    // A CR or LF in a name would forge log entries and is never part of a real one.
    [InlineData("/projekt\r\n.git/info/refs")]
    public void Rejects_unsafe_paths(string? pathInfo)
    {
        Assert.False(GitRepositoryPath.TryParse(pathInfo, out var repository, out var rest));
        Assert.Equal("", repository);
        Assert.Equal("", rest);
    }

    [Fact]
    public void Rest_keeps_the_service_path_intact()
    {
        Assert.True(GitRepositoryPath.TryParse("/projekt.git/objects/pack/pack-abc.pack", out _, out var rest));
        Assert.Equal("/objects/pack/pack-abc.pack", rest);
    }
}
