namespace GitHttpBackend.Tests;

/// <summary>
/// Which requests count as a push. Getting this wrong in one direction means a clone can
/// create a repository; in the other it means <c>git push</c> 404s on the ref advertisement
/// and never reaches the POST that would have created anything.
/// </summary>
public class GitRepositoryCreatorTests
{
    [Theory]
    // git push asks for the advertisement first, then posts the pack.
    [InlineData("GET", "/info/refs", "service=git-receive-pack", true)]
    [InlineData("POST", "/git-receive-pack", "", true)]
    [InlineData("get", "/info/refs", "service=git-receive-pack", true)]
    [InlineData("GET", "/info/refs", "foo=bar&service=git-receive-pack", true)]
    // Clone and fetch, which must never create anything.
    [InlineData("GET", "/info/refs", "service=git-upload-pack", false)]
    [InlineData("POST", "/git-upload-pack", "", false)]
    // A bare advertisement with no service is the dumb-protocol probe.
    [InlineData("GET", "/info/refs", "", false)]
    // Near misses that must not be read as a push.
    [InlineData("GET", "/info/refs", "service=git-receive-pack-evil", false)]
    [InlineData("GET", "/info/refs", "noservice=git-receive-pack", false)]
    [InlineData("GET", "/info/refs", "service", false)]
    [InlineData("HEAD", "/git-receive-pack", "", false)]
    [InlineData("POST", "/objects/info/packs", "", false)]
    [InlineData("GET", "", "service=git-receive-pack", false)]
    public void IsPush_recognises_only_receive_pack(string method, string rest, string query, bool expected)
    {
        Assert.Equal(expected, GitRepositoryCreator.IsPush(method, rest, query));
    }
}
