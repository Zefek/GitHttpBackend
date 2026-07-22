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

## Notes / known limitations

- **Chunked uploads** (large pushes over `http.postBuffer`) arrive without a
  `Content-Length`; the body is streamed to the backend until EOF. Works for the localhost
  case; heavy-duty setups may want explicit buffering.
- **Auth** is left to the host (ASP.NET Core auth middleware + the `Authorize` hook).
  On plain localhost, none is required.
- `git-http-backend` is **not bundled** — it ships with Git and is located at runtime.
