<#
.SYNOPSIS
    Mirrors every bare repository under a GitHttpBackend project root to a backup location.

.DESCRIPTION
    Clones each repository as a mirror on the first run and updates the mirror on every run
    afterwards. A mirror is a complete copy including all refs, and is itself a valid clone
    source -- recovery is "git clone" from the backup, with no restore procedure to remember
    and no archive format to stay compatible with.

    Repositories holding configuration exist in one copy on one disk. A disk failure does not
    lose a working tree; it loses every version ever recorded. That is what this guards
    against.

    Safe to send offsite ONLY when the secrets inside the repositories are already encrypted
    at rest and the decryption key is not in the backup. What leaves the machine is then
    ciphertext. Without that, the backup is a plaintext copy of every secret you have.

.PARAMETER ProjectRoot
    Directory holding the bare repositories -- the same path as Git:ProjectRoot.

.PARAMETER BackupRoot
    Where the mirrors go. Created if missing. A second physical disk, a NAS or cloud storage.

.PARAMETER Verify
    Also run "git fsck" on each mirror. A backup nobody has checked is a hypothesis; this
    turns it into a fact. Slower, so it suits a weekly run rather than an hourly one.

.EXAMPLE
    .\Backup-GitRepositories.ps1 -ProjectRoot D:\git-repos -BackupRoot E:\zalohy\git

.EXAMPLE
    Register a nightly run with Task Scheduler:

    $action  = New-ScheduledTaskAction -Execute 'powershell.exe' `
        -Argument '-NoProfile -ExecutionPolicy Bypass -File "D:\scripts\Backup-GitRepositories.ps1" -ProjectRoot D:\git-repos -BackupRoot E:\zalohy\git'
    $trigger = New-ScheduledTaskTrigger -Daily -At 2am
    Register-ScheduledTask -TaskName 'Zaloha git repozitaru' -Action $action -Trigger $trigger `
        -User 'SYSTEM' -RunLevel Highest

.NOTES
    Exits with a non-zero code when any repository fails, so a scheduled task reports the
    failure instead of quietly reporting success.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ProjectRoot,

    [Parameter(Mandatory)]
    [string]$BackupRoot,

    [switch]$Verify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Git {
    param([string[]]$Arguments)

    # git reports ordinary progress on stderr ("Cloning into bare repository..."). Redirecting
    # it wraps each line in an ErrorRecord, which with ErrorActionPreference = 'Stop' would turn
    # a successful clone into a terminating error. The exit code is the only honest signal, so
    # stderr is captured with the preference relaxed and judged by $LASTEXITCODE afterwards.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & git @Arguments 2>&1
    }
    finally {
        $ErrorActionPreference = $previous
    }

    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') selhalo s kodem $LASTEXITCODE`n$($output -join [Environment]::NewLine)"
    }
    return $output
}

# A bare repository is a top-level directory named *.git, or one holding HEAD and objects/ --
# the same shape the server's home page looks for.
function Get-BareRepository {
    param([string]$Root)

    Get-ChildItem -LiteralPath $Root -Directory | Where-Object {
        $_.Name.EndsWith('.git', [StringComparison]::OrdinalIgnoreCase) -or
        ((Test-Path -LiteralPath (Join-Path $_.FullName 'HEAD')) -and
         (Test-Path -LiteralPath (Join-Path $_.FullName 'objects')))
    }
}

if (-not (Test-Path -LiteralPath $ProjectRoot)) {
    throw "ProjectRoot '$ProjectRoot' neexistuje."
}

if (-not (Test-Path -LiteralPath $BackupRoot)) {
    New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
}

$repositories = @(Get-BareRepository -Root $ProjectRoot)
if ($repositories.Count -eq 0) {
    Write-Warning "V '$ProjectRoot' nejsou zadne bare repozitare."
    return
}

$failed = @()

foreach ($repository in $repositories) {
    $mirror = Join-Path $BackupRoot $repository.Name

    try {
        if (Test-Path -LiteralPath $mirror) {
            # "remote update" fetches every ref the mirror tracks, and --prune removes the ones
            # deleted upstream, so the mirror stays a copy rather than an accumulation.
            Write-Host "Aktualizuji $($repository.Name)"
            Invoke-Git @('-C', $mirror, 'remote', 'update', '--prune') | Out-Null
        }
        else {
            Write-Host "Zrcadlim $($repository.Name)"
            Invoke-Git @('clone', '--mirror', '--', $repository.FullName, $mirror) | Out-Null
        }

        if ($Verify) {
            Write-Host "Kontroluji $($repository.Name)"
            Invoke-Git @('-C', $mirror, 'fsck', '--no-progress') | Out-Null
        }
    }
    catch {
        Write-Error "Zaloha '$($repository.Name)' selhala: $_" -ErrorAction Continue
        $failed += $repository.Name
    }
}

if ($failed.Count -gt 0) {
    Write-Error "Selhalo $($failed.Count) z $($repositories.Count) repozitaru: $($failed -join ', ')" -ErrorAction Continue
    exit 1
}

Write-Host "Hotovo: $($repositories.Count) repozitaru zalohovano do $BackupRoot"
