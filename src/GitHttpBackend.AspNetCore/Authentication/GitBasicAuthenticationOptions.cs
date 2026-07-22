using Microsoft.AspNetCore.Authentication;
using System.Security.Claims;

namespace GitHttpBackend.AspNetCore.Authentication;

/// <summary>Username/password pair decoded from a Basic <c>Authorization</c> header.</summary>
public readonly record struct BasicCredentials(string Username, string Password);

/// <summary>
/// Options for <see cref="GitBasicAuthenticationHandler"/>. Git clients (including CI
/// runners) authenticate with HTTP Basic — a username and a password or token — so this
/// is the natural scheme for both humans and automation.
/// </summary>
public sealed class GitBasicAuthenticationOptions : AuthenticationSchemeOptions
{
    /// <summary>Realm reported in the <c>WWW-Authenticate</c> challenge.</summary>
    public string Realm { get; set; } = "Git";

    /// <summary>
    /// Validates the supplied credentials. Return a <see cref="ClaimsPrincipal"/> on
    /// success, or <c>null</c> to reject. Required.
    /// </summary>
    public Func<BasicCredentials, Task<ClaimsPrincipal?>>? ValidateCredentialsAsync { get; set; }
}
