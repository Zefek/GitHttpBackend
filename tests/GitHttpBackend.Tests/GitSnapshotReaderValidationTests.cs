namespace GitHttpBackend.Tests;

public class GitSnapshotReaderValidationTests
{
    [Theory]
    [InlineData("HEAD")]
    [InlineData("main")]
    [InlineData("master")]
    [InlineData("feature/config-v2")]
    [InlineData("v1.2.3")]
    [InlineData("refs/heads/main")]
    [InlineData("4117c00031fb47475eae731fcfeebf1b7cea9b4d")]
    public void Accepts_branch_tag_and_commit_names(string reference)
        => Assert.True(GitSnapshotReader.IsValidReference(reference));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-x")]
    [InlineData("--upload-pack=calc")]
    [InlineData("/main")]
    [InlineData("main/")]
    [InlineData("main.")]
    [InlineData("main..HEAD")]
    [InlineData("feature//x")]
    [InlineData("main:Odecty")]
    [InlineData("main^{tree}")]
    [InlineData("HEAD~1")]
    [InlineData("main@{1}")]
    [InlineData("main Odecty")]
    [InlineData("větev")]
    public void Rejects_anything_else_as_a_reference(string? reference)
        => Assert.False(GitSnapshotReader.IsValidReference(reference));

    [Theory]
    [InlineData("")]
    [InlineData("OdectyMVC")]
    [InlineData("Home/OdectyMVC")]
    [InlineData("my.config")]
    [InlineData("pokusný adresář")]
    public void Accepts_directory_paths(string path)
        => Assert.True(GitSnapshotReader.IsValidPath(path));

    [Theory]
    [InlineData(null)]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../Odecty")]
    [InlineData("OdectyMVC/../Odecty")]
    [InlineData("./OdectyMVC")]
    [InlineData("OdectyMVC//nested")]
    [InlineData("/OdectyMVC")]
    [InlineData("OdectyMVC\\nested")]
    [InlineData("C:")]
    [InlineData("Odecty\0")]
    public void Rejects_traversal_and_foreign_separators_in_paths(string? path)
        => Assert.False(GitSnapshotReader.IsValidPath(path));

    [Theory]
    [InlineData("POST", "/Home.git/git-receive-pack", "", true)]
    [InlineData("GET", "/Home.git/info/refs", "service=git-receive-pack", true)]
    [InlineData("GET", "/Home.git/info/refs", "service=git-upload-pack", false)]
    [InlineData("POST", "/Home.git/git-upload-pack", "", false)]
    [InlineData("GET", "/../Home.git/git-receive-pack", "", false)]
    public void IsPush_recognises_only_receive_pack(string method, string pathInfo, string query, bool expected)
    {
        var request = new CgiRequest { Method = method, PathInfo = pathInfo, QueryString = query, Body = Stream.Null };

        Assert.Equal(expected, request.IsPush);
    }
}
