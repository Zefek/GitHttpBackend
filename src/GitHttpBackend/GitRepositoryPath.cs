namespace GitHttpBackend;

/// <summary>
/// Splits a CGI <c>PATH_INFO</c> into the repository name and the remaining git service
/// path, rejecting anything that is not a plain repository name followed by such a path.
/// <para>
/// This is the single place where "which repository is this request about?" is decided.
/// Authorization and <c>git-http-backend</c>'s own <c>enter_repo()</c> must agree on that
/// answer — where they can be made to disagree, a caller gets authorized for repository A
/// while git serves repository B. Every caller deriving a repository name from a request
/// path is expected to use this type rather than parse the path again.
/// </para>
/// </summary>
public static class GitRepositoryPath
{
    /// <summary>
    /// Attempts to split <paramref name="pathInfo"/> (e.g. <c>/projekt.git/info/refs</c>)
    /// into <paramref name="repository"/> (<c>projekt.git</c>) and <paramref name="rest"/>
    /// (<c>/info/refs</c>, or an empty string when the path is just the repository).
    /// </summary>
    /// <returns>
    /// <c>true</c> when the path is a safe repository path; <c>false</c> when it traverses,
    /// is encoded, or is otherwise not a plain name. Both out parameters are empty on failure.
    /// </returns>
    /// <remarks>
    /// The character rules cover separators, traversal and encoding only. Non-ASCII
    /// repository names are deliberately accepted — they work today and the library's
    /// logging code is written for them.
    /// </remarks>
    public static bool TryParse(string? pathInfo, out string repository, out string rest)
    {
        repository = "";
        rest = "";

        if (string.IsNullOrEmpty(pathInfo))
            return false;

        // A percent sign means the path is still (or again) encoded. Accepting it would put
        // this validator and git's own resolution on opposite sides of a decoding step, which
        // is exactly the disagreement this type exists to prevent — %2e%2e%2f is the classic
        // shape. A repository name containing a literal '%' is refused as a consequence; that
        // is a deliberate trade and no configuration repository needs one.
        // Backslashes are path separators on Windows, so repo\..\other matters as much as the
        // forward-slash form and is rejected outright rather than normalised.
        foreach (var c in pathInfo)
        {
            if (c is '%' or '\\' or ':' || char.IsControl(c))
                return false;
        }

        var path = pathInfo;
        if (path[0] == '/')
            path = path[1..];

        // One trailing slash is how a client writes "the repository itself"; more than one
        // segment separator in a row is not something a git client produces.
        if (path.EndsWith('/'))
            path = path[..^1];

        if (path.Length == 0)
            return false;

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            // Empty: a doubled separator, or a leading one that survived the strip above —
            // "//host/share" is a UNC path once the leading slashes are read as separators.
            if (segment.Length == 0)
                return false;

            // "." and ".." traverse; "..." and longer runs are not legal names on Windows
            // and only ever show up in probing.
            if (IsAllDots(segment))
                return false;
        }

        var name = segments[0];

        // Windows silently strips trailing dots and spaces from file names, so "projekt.git."
        // and "projekt.git" would be the same directory on disk but different strings here.
        // Two names for one repository is the aliasing this type is meant to rule out.
        if (name[^1] is '.' or ' ')
            return false;

        repository = name;
        rest = segments.Length > 1 ? "/" + string.Join('/', segments, 1, segments.Length - 1) : "";
        return true;
    }

    static bool IsAllDots(string segment)
    {
        foreach (var c in segment)
        {
            if (c != '.')
                return false;
        }
        return true;
    }
}
