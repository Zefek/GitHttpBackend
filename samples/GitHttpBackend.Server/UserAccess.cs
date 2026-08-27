namespace GitHttpBackend.Server;

/// <summary>
/// A configured user: password/token plus the repos they may access ("*" = all).
/// Bound from the "Git:Auth:Users" configuration section.
/// </summary>
sealed class UserAccess
{
    public string Password { get; set; } = "";
    public string[] Repos { get; set; } = [];
}
