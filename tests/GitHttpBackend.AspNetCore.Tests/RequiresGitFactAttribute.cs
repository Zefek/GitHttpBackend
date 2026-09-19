namespace GitHttpBackend.AspNetCore.Tests;

/// <summary>
/// A fact that needs Git installed. Skips rather than fails when <c>git-http-backend</c>
/// cannot be located, so the suite stays usable on a machine without Git; CI runs on
/// <c>windows-latest</c>, which has it, so nothing is silently skipped there.
/// </summary>
sealed class RequiresGitFactAttribute : FactAttribute
{
    public RequiresGitFactAttribute()
    {
        if (GitBackendLocator.Locate() is null)
        {
            Skip = "git-http-backend was not found on this machine.";
        }
    }
}
