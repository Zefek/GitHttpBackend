namespace GitHttpBackend.AspNetCore.Tests;

/// <summary>
/// A fact that drives the Windows PowerShell sample script, so it needs both Git and
/// <c>powershell.exe</c>. Skips elsewhere; CI runs on <c>windows-latest</c>, which has both.
/// </summary>
sealed class RequiresPowerShellFactAttribute : FactAttribute
{
    public RequiresPowerShellFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "The sample backup script is Windows PowerShell.";
        }
        else if (GitBackendLocator.LocateGit() is null)
        {
            Skip = "The git client was not found on this machine.";
        }
    }
}
