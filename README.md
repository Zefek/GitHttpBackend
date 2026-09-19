# GitHttpBackend

A single executable that serves your bare Git repositories over **Smart HTTP** — clone,
fetch and push — on a machine you already own. No database, no user accounts, no web UI, no
SSH server. Install it as a Windows service, point it at a folder, put a reverse proxy in
front of it, and you have Git hosting.

Under the hood it wraps Git's own `git-http-backend` CGI: Kestrel handles the HTTP,
`git-http-backend` handles the Git wire protocol (pkt-line, ref advertisement, packfile
negotiation). Nothing about the Git protocol is reimplemented here.

There are also two NuGet libraries for embedding the same thing in an existing .NET
application — see [Using the libraries](#using-the-libraries) further down.

## Why this and not a forge

The gap this fills is narrow and real. You want a handful of bare repositories served over
HTTP on a box you control, and every option is out of proportion:

- **Gitea or Forgejo** bring a database, a user model, a web UI and an SSH server so you can
  serve three repositories.
- **Apache with `mod_cgi`**, or **IIS with the CGI feature** and a hand-written `web.config`,
  is a surprising amount of setup and a surprising amount of surface for what should be one
  `git clone`.
- **A file share** loses the HTTP transport, and with it tokens, TLS and anything that looks
  like an audit trail.

**Use a forge instead** when you have more than a handful of repositories, want issues and
pull requests, or have several contributors who each need an account and permissions to
match. Those are real needs and this project answers none of them.

**Use this** when the repositories are infrastructure rather than collaboration: machine
configuration, deployment manifests, a few things CI clones and nobody browses.

## Quick start

1. Download `GitHttpBackend.Server-<version>-win-x64.zip` from the
   [latest release](https://github.com/Zefek/GitHttpBackend/releases/latest) and unpack it.
   It is self-contained, so no .NET runtime is needed on the machine. Git must be installed —
   `git-http-backend` ships with it and is located at runtime.
2. Point it at a folder and start it:

   ```
   GitHttpBackend.Server.exe --Git:ProjectRoot D:\git-repos
   ```

   Or set `"Git": { "ProjectRoot": "D:\\git-repos" }` in `appsettings.json` next to the
   executable.
3. Create a repository and clone it:

   ```
   git init --bare D:\git-repos\projekt.git
   git clone http://localhost:5050/projekt.git
   ```

`http://localhost:5050` opens a page listing the repositories with ready-to-copy clone URLs.

## The intended deployment

**Bind to loopback and terminate TLS at a reverse proxy.** `appsettings.json` ships with
`"Urls": "http://localhost:5050"` and that is deliberate, not a leftover from development.

Basic auth sends the token in a header that anyone on the path can read, so a plaintext
listener is only sound when nothing is on the path. Bound to `127.0.0.1`, the only thing that
can reach Kestrel is the proxy on the same machine; the proxy speaks HTTPS to the world.
Binding to `0.0.0.0` instead puts credentials on the wire — do not.

```
client ──HTTPS──▶ reverse proxy (IIS / nginx / Caddy) ──HTTP──▶ 127.0.0.1:5050
```

**Tell the app it is behind a proxy.** Otherwise every request appears to come from
`127.0.0.1`, so the audit trail records the proxy rather than the caller, and the home page
offers `http://` clone URLs to someone who arrived over `https://`:

```json
"Git": {
  "ForwardedHeaders": {
    "Enabled": true,
    "KnownProxies": [ "127.0.0.1", "::1" ]
  }
}
```

Off by default on purpose: trusting `X-Forwarded-For` when nothing is actually in front lets
any client name its own source address, which does not blank the audit trail — it falsifies
it. `KnownProxies` lists the addresses allowed to speak for someone else. Keep it narrow.

## Installing as a Windows service

The executable detects that it was started by the service control manager, so the same binary
runs interactively and as a service. Register it:

```powershell
New-Service -Name GitHttpBackend `
    -BinaryPathName 'D:\GitHttpBackend\GitHttpBackend.Server.exe' `
    -DisplayName 'Git HTTP Backend' `
    -StartupType Automatic
Start-Service GitHttpBackend
```

or with `sc.exe`, minding the space after `binPath=`:

```
sc create GitHttpBackend binPath= "D:\GitHttpBackend\GitHttpBackend.Server.exe" start= auto
```

Start, stop and startup failures go to the Windows Event Log, which is where to look when the
service will not come up.

**The account matters.** A service runs as LocalSystem unless told otherwise, and that
account almost certainly does not own `D:\git-repos`. Git refuses to touch repositories owned
by someone else, which surfaces as an empty HTTP 500 — see
[Running as a service account](#running-as-a-service-account) for the `SafeDirectories`
setting that fixes it. Choose the account first, then apply that setting, or make the service
account the owner of `ProjectRoot`.

## A worked example

The deployment this project was written for: a few bare repositories holding the
configuration of individual machines, cloned by CI runners during deployment.

**On the server.** Repositories under `D:\git-repos`, the service bound to loopback, a
reverse proxy publishing `https://git.internal`, Basic auth with one account per runner:

```json
{
  "Urls": "http://localhost:5050",
  "Git": {
    "ProjectRoot": "D:\\git-repos",
    "SafeDirectories": [ "*" ],
    "AllowCreateOnPush": true,
    "ForwardedHeaders": { "Enabled": true, "KnownProxies": [ "127.0.0.1", "::1" ] },
    "Auth": {
      "Mode": "basic",
      "Users": {
        "runner-web":  { "Password": "…", "Repos": [ "web-config" ] },
        "runner-iot":  { "Password": "…", "Repos": [ "iot-config" ] },
        "pavel":       { "Password": "…", "Repos": [ "*" ] }
      }
    }
  }
}
```

**In the pipeline.** A token in the URL, no browser flow:

```
git clone https://runner-web:$TOKEN@git.internal/web-config.git
```

**The security model.** Each runner's account can reach exactly its own repository, so a
compromised runner does not get the others. The secrets inside the repositories are
encrypted at rest with a certificate whose private key lives only on the machines that need
to decrypt, so the server never holds anything usable in the clear — and the repositories
stay safe to copy offsite. `REMOTE_USER` reaches git, so pushes carry an identity in the
reflog, and with forwarded headers on, the logs carry the origin too.

**Adding a machine** means pushing to a name that does not exist yet, from wherever the
configuration is authored. No remote session — see [Create on push](#create-on-push).

## Creating repositories

```
git init --bare D:\git-repos\projekt.git
```

`git-http-backend` then refuses push unless the repository opts in:

```
git -C D:\git-repos\projekt.git config http.receivepack true
```

Note that **everything under `ProjectRoot` is published** — `ExportAll` defaults to `true`,
which tells `git-http-backend` to serve every repository it finds without requiring a
`git-daemon-export-ok` marker. Treat `ProjectRoot` as the set of repositories you intend to
serve, never as a scratch directory.

### Create on push

Creating a repository is otherwise the one step that needs a shell on the server.
`AllowCreateOnPush` closes that gap: a push to a name that does not exist creates the bare
repository, sets `http.receivepack = true` on it, and lets the push complete.

```json
"Git": { "AllowCreateOnPush": true }
```

or, from code, `AllowCreateOnPush = true` on `GitBackendOptions`.

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
git clone https://ci:$TOKEN@git.internal/projekt.git
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

Declare the repositories as trusted — `"Git:SafeDirectories": [ "*" ]` in `appsettings.json`,
or from code:

```csharp
var options = new GitBackendOptions
{
    ProjectRoot     = @"D:\git-repos",
    SafeDirectories = ["*"],   // or list the repository paths explicitly
};
```

The entries become `safe.directory` config for the backend process only — no machine-wide
`git config --system` change and no profile for the service account. Alternatively, make the
service account the owner of `ProjectRoot`.

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

## Using the libraries

The standalone server above is one host. If you already have an ASP.NET Core application —
with its own user store, its own authentication, its own deployment — you can serve Git from
inside it instead:

| Package | What it is |
|---|---|
| [`GitHttpBackend`](https://www.nuget.org/packages/GitHttpBackend) | Host-agnostic core: runs `git-http-backend`, maps request/response. No ASP.NET dependency. |
| [`GitHttpBackend.AspNetCore`](https://www.nuget.org/packages/GitHttpBackend.AspNetCore) | ASP.NET Core adapter: `MapGitHttpBackend()`. |

```csharp
app.MapGitHttpBackend("/git", new GitBackendOptions
{
    ProjectRoot = @"D:\git-repos",   // contains projekt.git\
    ExportAll   = true,
    // BackendPath = null            // auto-detected via git --exec-path
    // Authorize = req => ...        // gate push, check your own permissions, etc.
});
```

`Authorize` receives a host-agnostic `CgiRequest` — method, `PathInfo`, `RemoteUser`,
`RemoteAddr` — so authorising against your own tables is a lambda, not an integration.
`MapGitHttpBackend` returns an `IEndpointConventionBuilder`, so `RequireAuthorization()` and
your existing policies apply as usual.

The repository layout:

| Project | What it is |
|---|---|
| `src/GitHttpBackend` | The core library. |
| `src/GitHttpBackend.AspNetCore` | The ASP.NET Core adapter. |
| `samples/GitHttpBackend.Server` | The standalone server this README leads with. |
| `tests/` | Unit tests, plus end-to-end tests that drive the real git client. |

## Building from source

- .NET 11 SDK
- Git installed (provides `git-http-backend`; auto-detected via `git --exec-path`).

```
dotnet build GitHttpBackend.slnx
dotnet test GitHttpBackend.slnx
dotnet publish samples/GitHttpBackend.Server -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

The end-to-end tests drive the real `git` client against a hosted server; they skip rather
than fail on a machine without Git.

## Behind a reverse proxy

The intended topology is Kestrel bound to loopback with a reverse proxy terminating HTTPS in
front of it — which is what makes Basic auth over a plaintext listener sound. In that
topology every connection arrives from `127.0.0.1`, so unless the forwarded headers are read:

- `REMOTE_ADDR` handed to git, and every log line the handler writes, says `127.0.0.1` for
  every caller. "Which machine fetched this configuration" becomes unanswerable.
- The home page builds clone URLs from the incoming request, so a user who arrived over
  `https://` is offered `git clone http://…`.

The sample reads `X-Forwarded-For` and `X-Forwarded-Proto` when you turn it on:

```json
"Git": {
  "ForwardedHeaders": {
    "Enabled": true,
    "KnownProxies": [ "127.0.0.1", "::1" ]
  }
}
```

It is **off by default on purpose**. Trusting these headers when nothing is actually in front
lets any client name its own source address, which makes the audit trail worse than blank —
it makes it wrong. `KnownProxies` lists the addresses allowed to speak for someone else; it
defaults to loopback and a forged header from anywhere else is ignored. Keep it narrow.

## Notes / known limitations

- **Chunked uploads** (large pushes over `http.postBuffer`) arrive without a
  `Content-Length`; the body is streamed to the backend until EOF. Works for the localhost
  case; heavy-duty setups may want explicit buffering.
- **Auth** is left to the host (ASP.NET Core auth middleware + the `Authorize` hook).
  On plain localhost, none is required.
- `git-http-backend` is **not bundled** — it ships with Git and is located at runtime.
- Releases carry a **win-x64** self-contained build. Other runtimes are a
  `dotnet publish -r <rid>` away, but are not published here.
