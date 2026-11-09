namespace GitHttpBackend.AspNetCore.Tests;

sealed class RequiresGitTheoryAttribute : TheoryAttribute
{
    public RequiresGitTheoryAttribute()
    {
        if (GitBackendLocator.Locate() is null)
        {
            Skip = "git-http-backend was not found on this machine.";
        }
    }
}
