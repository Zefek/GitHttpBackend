using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Encodings.Web;

namespace GitHttpBackend.AspNetCore.Authentication;

/// <summary>
/// HTTP Basic authentication handler tuned for Git Smart HTTP: it issues a proper
/// <c>401 WWW-Authenticate: Basic</c> challenge so git prompts, uses its credential
/// helper, or accepts <c>https://user:token@host/…</c> URLs.
/// </summary>
public sealed class GitBasicAuthenticationHandler : AuthenticationHandler<GitBasicAuthenticationOptions>
{
    public const string SchemeName = "Basic";

    public GitBasicAuthenticationHandler(
        IOptionsMonitor<GitBasicAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authValues))
        {
            return AuthenticateResult.NoResult();
        }

        var header = authValues.ToString();
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var encoded = header["Basic ".Length..].Trim();
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return AuthenticateResult.Fail("Malformed Basic authorization header.");
        }

        int sep = decoded.IndexOf(':');
        if (sep < 0)
        {
            return AuthenticateResult.Fail("Malformed Basic credentials.");
        }

        var credentials = new BasicCredentials(decoded[..sep], decoded[(sep + 1)..]);

        var validate = Options.ValidateCredentialsAsync
            ?? throw new InvalidOperationException(
                $"{nameof(GitBasicAuthenticationOptions)}.{nameof(GitBasicAuthenticationOptions.ValidateCredentialsAsync)} must be set.");

        var principal = await validate(credentials);
        if (principal is null)
        {
            return AuthenticateResult.Fail("Invalid credentials.");
        }

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"Basic realm=\"{Options.Realm}\", charset=\"UTF-8\"";
        return Task.CompletedTask;
    }
}
