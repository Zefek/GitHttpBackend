using Microsoft.AspNetCore.Authentication;

namespace GitHttpBackend.AspNetCore.Authentication;

/// <summary>Registration helpers for the Git Basic authentication scheme.</summary>
public static class GitBasicAuthenticationExtensions
{
    /// <summary>
    /// Adds the Git-friendly HTTP Basic authentication scheme. Configure
    /// <see cref="GitBasicAuthenticationOptions.ValidateCredentialsAsync"/> to check credentials.
    /// </summary>
    public static AuthenticationBuilder AddGitBasicAuthentication(
        this AuthenticationBuilder builder,
        Action<GitBasicAuthenticationOptions> configure,
        string scheme = GitBasicAuthenticationHandler.SchemeName)
        => builder.AddScheme<GitBasicAuthenticationOptions, GitBasicAuthenticationHandler>(scheme, configure);
}
