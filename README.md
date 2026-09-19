# GitHttpBackend

Serve Git repositories over **Smart HTTP** (clone / fetch / push) from a .NET host, by
wrapping Git's own `git-http-backend` CGI. Kestrel handles the HTTP; `git-http-backend`
handles the Git wire protocol (pkt-line, ref advertisement, packfile negotiation).

## Projects

| Project | What it is | NuGet candidate |
|---|---|---|
| `src/GitHttpBackend` | Host-agnostic core: runs `git-http-backend`, maps request/response. No ASP.NET dependency. | `GitHttpBackend` |
| `src/GitHttpBackend.AspNetCore` | ASP.NET Core adapter: `MapGitHttpBackend()`. | `GitHttpBackend.AspNetCore` |
| `samples/GitHttpBackend.Server` | Runnable localhost utility. | — |

## Requirements

- .NET 10 SDK
- Git installed (provides `git-http-backend`; auto-detected via `git --exec-path`).

## Usage

```csharp
app.MapGitHttpBackend("/", new GitBackendOptions
{
    ProjectRoot = @"C:\git-repos",   // contains projekt.git\
    ExportAll   = true,
    // BackendPath = null            // auto-detected
    // Authorize = req => ...        // gate push, etc.
});
```

Clone: `git clone http://localhost:5050/projekt.git`

## Enabling push

`git-http-backend` refuses push unless the repo opts in:

```
git -C C:\git-repos\projekt.git config http.receivepack true
```

### Create on push

Creating a repository is otherwise the one step that needs a shell on the server.
`AllowCreateOnPush` closes that gap: a push to a name that does not exist creates the bare
repository, sets `http.receivepack = true` on it, and lets the push complete.

```csharp
var options = new GitBackendOptions
{
    ProjectRoot        = @"C:\git-repos",
    AllowCreateOnPush  = true,     // default false
};
```

In the sample it is `"Git:AllowCreateOnPush": true` in `appsettings.json`.

It is **off by default** and deliberately narrow:

- Creation runs *after* the `Authorize` hook, so only a caller allowed to push to that name
  can trigger it. With Basic auth on, that means the name matches the user's `Repos` list.
- A clone or fetch of an unknown name never creates anything — it keeps returning 404.
- An existing repository is never re-initialised or reconfigured.
- `HEAD` follows the branch you pushed. `git init --bare` writes `HEAD -> refs/heads/master`
  and `receive-pack` never revises it, so pushing `main` would otherwise leave every later
  clone checking out nothing. A `HEAD` that already resolves is left alone.
- Concurrent pushes to the same new name are serialised, so no half-created repository.
- When `ExportAll` is `false`, the new repository gets a `git-daemon-export-ok` marker, so it
  is reachable under either export mode.

With the option off, a push to an unknown name behaves exactly as before: 404.

## Authentication

Auth is **opt-in** and **provider-agnostic** — the library never hardcodes a scheme.
`MapGitHttpBackend` returns an `IEndpointConventionBuilder`, so the host decides.

The sample toggles it via `Git:Auth:Mode`:

- `none` (default) — anonymous. Best for localhost and CI that clones this repo.
- `basic` — HTTP Basic, validated against `Git:Auth:Users`.

Each user carries a password/token and the repos they may access (`"*"` = all):

```json
"Git": {
  "Auth": {
    "Mode": "basic",
    "Users": {
      "ci":    { "Password": "token-ci",    "Repos": [ "*" ] },
      "pavel": { "Password": "heslo-pavel", "Repos": [ "projekt", "WaterSensor" ] }
    }
  }
}
```

Authorization runs *after* authentication (via `GitBackendOptions.Authorize`), so the
status codes are meaningful: bad/unknown credentials → **401** (git re-prompts),
authenticated-but-unlisted repo → **403** (forbidden, no re-prompt). The home page also
lists only the repos the caller may access. Repo names match with or without the `.git`
suffix.

Basic auth is what git clients (and CI runners) actually speak. A workflow authenticates
with a token in the URL — no browser flow needed:

```
git clone http://ci:$TOKEN@localhost:5050/projekt.git
```

The handler issues a proper `401 WWW-Authenticate: Basic` challenge, so interactive git
also prompts / uses its credential helper.

### Entra ID / Microsoft Identity

An interactive OIDC browser flow does **not** fit `git clone` in CI. Microsoft Identity
fits only as **JWT bearer validation**: the client sends `Authorization: Bearer <jwt>`
(e.g. via `git -c http.extraHeader=...`). To use it, replace the Basic block in
`Program.cs` with:

```csharp
builder.Services.AddAuthentication().AddJwtBearer(/* Entra config */);
```

and keep the `endpoint.RequireAuthorization()` line. No library change is required.

### Anonymous read + authenticated write

The sample gates the whole endpoint. To allow anonymous clone but require auth for push,
use `GitBackendOptions.Authorize` (it sees `PathInfo` — `git-receive-pack` is push) or a
custom authorization policy keyed on the path. Not wired in the sample yet.

## Running as a service account

When the host process does not own the repository folders — a Windows service (LocalSystem,
NETWORK SERVICE, gMSA), an IIS app pool, or a container running as a different UID — git
refuses to touch them:

```
fatal: detected dubious ownership in repository at 'D:\git-repos\projekt'
```

`git-http-backend` reports that as an **empty HTTP 500** (the reason only ever reaches stderr),
so clients just see `The requested URL returned error: 500`. It works when you run the app
interactively and breaks the moment it runs as a service.

Declare the repositories as trusted:

```csharp
var options = new GitBackendOptions
{
    ProjectRoot     = @"D:\git-repos",
    SafeDirectories = ["*"],   // or list the repository paths explicitly
};
```

In the sample this is `"Git:SafeDirectories": [ "*" ]` in `appsettings.json`. The entries become
`safe.directory` config for the backend process only — no machine-wide `git config --system`
change and no profile for the service account. Alternatively, make the service account the owner
of `ProjectRoot`.

## Backing up

The reason to host configuration in local bare repositories is history — being able to see
what changed and roll back. In the deployment this project is built for, those repositories
are deliberately *not* on GitHub, so that history exists in exactly one copy, on one disk. A
disk failure does not lose a working tree. It loses every version ever recorded.

There is a detail that makes this easy rather than awkward, and it is worth saying out loud:
when the secrets inside those repositories are already **encrypted at rest** — with a
certificate whose private key lives only on the machines that need to decrypt — the
repository is safe to copy anywhere. Offsite backup of a repository full of secrets is
normally the hard part. Here it is a file copy, because what leaves the machine is
ciphertext.

### The mirror clone

```
git clone --mirror D:\git-repos\projekt.git E:\zalohy\git\projekt.git
git -C E:\zalohy\git\projekt.git remote update --prune
```

`--mirror` copies **all** refs, not just branches; `remote update --prune` keeps the copy
current and drops refs deleted upstream. A mirror is itself a valid clone source, so there is
no restore procedure to remember and no archive format to stay compatible with next year.

### Where to put it

A second physical disk, a NAS, or cloud storage. State the precondition plainly: sending a
repository offsite is only safe when the secrets in it are encrypted **and the key is not in
the backup**. Otherwise the backup is a plaintext copy of every secret you have.

### Scheduling it

[`samples/Backup-GitRepositories.ps1`](samples/Backup-GitRepositories.ps1) walks every bare
repository under `ProjectRoot`, cloning on the first run and updating afterwards:

```
.\Backup-GitRepositories.ps1 -ProjectRoot D:\git-repos -BackupRoot E:\zalohy\git
```

It exits non-zero when any repository fails, so a scheduled task reports the failure instead
of quietly reporting success. Register it with Task Scheduler:

```powershell
$action  = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument '-NoProfile -ExecutionPolicy Bypass -File "D:\scripts\Backup-GitRepositories.ps1" -ProjectRoot D:\git-repos -BackupRoot E:\zalohy\git'
$trigger = New-ScheduledTaskTrigger -Daily -At 2am
Register-ScheduledTask -TaskName 'Zaloha git repozitaru' -Action $action -Trigger $trigger `
    -User 'SYSTEM' -RunLevel Highest
```

### Restoring

`git clone` from the mirror. That is the whole procedure:

```
git clone E:\zalohy\git\projekt.git D:\git-repos\projekt.git --mirror
```

### Verifying

A backup nobody has restored is a hypothesis. Pass `-Verify` to run `git fsck` on each
mirror, or check one by hand:

```
git -C E:\zalohy\git\projekt.git fsck
```

Slower, so it suits a weekly run rather than an hourly one. Cloning from the backup into a
temporary directory every so often turns the hypothesis into a fact.

### What a mirror does not cover

Server configuration itself: `appsettings.json` with its user list, and the reverse proxy
configuration. Either back those up too, or accept that they are reproducible from this
README — but decide which, rather than finding out during a restore.

## Notes / known limitations

- **Chunked uploads** (large pushes over `http.postBuffer`) arrive without a
  `Content-Length`; the body is streamed to the backend until EOF. Works for the localhost
  case; heavy-duty setups may want explicit buffering.
- **Auth** is left to the host (ASP.NET Core auth middleware + the `Authorize` hook).
  On plain localhost, none is required.
- `git-http-backend` is **not bundled** — it ships with Git and is located at runtime.
