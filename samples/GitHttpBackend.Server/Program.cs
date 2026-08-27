using GitHttpBackend;
using GitHttpBackend.AspNetCore;
using GitHttpBackend.AspNetCore.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// --- Windows service hosting ---------------------------------------------------------
// No-op unless the process was actually started by the SCM, so `dotnet run` is unaffected.
// It also pins the content root to AppContext.BaseDirectory — a service starts with
// C:\Windows\System32 as its working directory, which is where appsettings.json would
// otherwise be looked for.
if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options => options.ServiceName = "GitHttpBackend");
    builder.Logging.AddEventLog();   // start/stop and startup failures reach the Event Log
}

// Where the bare repositories live. Configure via appsettings.json ("Git:ProjectRoot"),
// environment variable Git__ProjectRoot, or --Git:ProjectRoot on the command line.
var configuredRoot = builder.Configuration["Git:ProjectRoot"];
var projectRoot = string.IsNullOrWhiteSpace(configuredRoot)
    ? Path.Combine(AppContext.BaseDirectory, "repos")
    : configuredRoot;
Directory.CreateDirectory(projectRoot);

// --- Authentication ------------------------------------------------------------------
// "Git:Auth:Mode" = "none" (default, automation-friendly) | "basic".
// Basic auth is what git clients (and CI runners) actually speak, via user:token.
// For Entra ID / OIDC instead, swap this block for builder.Services.AddAuthentication()
//   .AddJwtBearer(...) / .AddMicrosoftIdentityWebApi(...) — the endpoint wiring below
//   stays identical.
var authMode = builder.Configuration["Git:Auth:Mode"] ?? "none";
var useBasic = string.Equals(authMode, "basic", StringComparison.OrdinalIgnoreCase);

// user -> { password/token, allowed repos }, from configuration.
var users = builder.Configuration.GetSection("Git:Auth:Users")
    .Get<Dictionary<string, UserAccess>>() ?? new();

if (useBasic)
{
    builder.Services
        .AddAuthentication(GitBasicAuthenticationHandler.SchemeName)
        .AddGitBasicAuthentication(options =>
        {
            options.Realm = "Git";
            options.ValidateCredentialsAsync = creds =>
            {
                var ok = users.TryGetValue(creds.Username, out var entry)
                         && FixedTimeEquals(entry.Password, creds.Password);
                ClaimsPrincipal? principal = ok
                    ? new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.Name, creds.Username) },
                        GitBasicAuthenticationHandler.SchemeName))
                    : null;
                return Task.FromResult(principal);
            };
        });
    builder.Services.AddAuthorization();
}

var app = builder.Build();

if (useBasic)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

// "Git:SafeDirectories": [ "*" ] — required when the host runs as a service account that does
// not own the repository folders, otherwise git aborts with "dubious ownership" (empty HTTP 500).
var safeDirectories = builder.Configuration.GetSection("Git:SafeDirectories").Get<string[]>();

var options = new GitBackendOptions
{
    ProjectRoot = projectRoot,
    ExportAll = true,
    SafeDirectories = safeDirectories,
    // BackendPath = null -> auto-detected from the installed Git.

    // Per-user repo authorization. Only enforced when Basic auth is on; runs after
    // authentication, so an authenticated-but-unlisted repo yields 403 (not a re-prompt).
    Authorize = useBasic
        ? req =>
        {
            var repo = RepoFromPath(req.PathInfo);
            var allowed = repo is not null
                && req.RemoteUser is not null
                && users.TryGetValue(req.RemoteUser, out var entry)
                && IsRepoAllowed(entry, repo);
            return ValueTask.FromResult(allowed);
        }
        : null,
};

// Home page: lists the repositories found under ProjectRoot with ready-to-copy clone URLs.
// Registered before the git catch-all; a literal "/" route out-specifies "/{**gitPath}",
// so this wins for the root while everything else still flows to git-http-backend.
var home = app.MapGet("/", (HttpContext ctx) =>
{
    // Only list repos the caller may actually access (or all when auth is off).
    Func<string, bool> canAccess = _ => true;
    if (useBasic)
    {
        var user = ctx.User.Identity?.Name;
        canAccess = repo => user is not null
            && users.TryGetValue(user, out var entry)
            && IsRepoAllowed(entry, NormalizeRepo(repo));
    }
    return Results.Content(RenderHomePage(projectRoot, ctx.Request, canAccess), "text/html; charset=utf-8");
});

var endpoint = app.MapGitHttpBackend("/", options);
if (useBasic)
{
    endpoint.RequireAuthorization();
    home.RequireAuthorization();
}

app.Logger.LogInformation("Serving git repositories from {ProjectRoot}", projectRoot);
app.Logger.LogInformation("Auth mode: {AuthMode}", useBasic ? "basic" : "none");
app.Logger.LogInformation("git-http-backend: {BackendPath}", new GitHttpBackendInvoker(options).BackendPath);

app.Run();

// Scans ProjectRoot for bare repositories and renders a self-contained HTML index.
// A "repo" is a top-level directory named *.git, or one holding a HEAD file + objects/
// (the shape of a bare repo). Clone URLs are built from the incoming request so the page
// works whether reached via localhost, a LAN IP, or behind a reverse proxy.
static string RenderHomePage(string projectRoot, HttpRequest request, Func<string, bool> canAccess)
{
    var baseUrl = $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');

    var repos = new List<(string Name, string Description, DateTime LastActivity)>();
    if (Directory.Exists(projectRoot))
    {
        foreach (var dir in Directory.EnumerateDirectories(projectRoot))
        {
            var isBare = dir.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                || (File.Exists(Path.Combine(dir, "HEAD")) && Directory.Exists(Path.Combine(dir, "objects")));
            if (!isBare)
                continue;

            var name = Path.GetFileName(dir);
            if (!canAccess(name))
                continue;
            repos.Add((name, ReadDescription(dir), LastActivity(dir)));
        }
    }
    repos.Sort((a, b) => b.LastActivity.CompareTo(a.LastActivity));

    var rows = new StringBuilder();
    if (repos.Count == 0)
    {
        rows.Append($"""
            <tr><td colspan="2" class="empty">No repositories found in <code>{Enc(projectRoot)}</code>.
            Create one with <code>git init --bare myproject.git</code> inside that folder.</td></tr>
            """);
    }
    else
    {
        foreach (var (name, description, lastActivity) in repos)
        {
            var cloneUrl = $"{baseUrl}/{name}";
            rows.Append($"""
                <tr>
                  <td>
                    <div class="name">{Enc(name)}</div>
                    {(description.Length > 0 ? $"<div class=\"desc\">{Enc(description)}</div>" : "")}
                    <div class="meta">updated {Enc(lastActivity.ToString("yyyy-MM-dd HH:mm"))}</div>
                  </td>
                  <td class="clone">
                    <code>git clone {Enc(cloneUrl)}</code>
                  </td>
                </tr>
                """);
        }
    }

    return $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width, initial-scale=1">
          <title>Git repositories</title>
          <style>
            :root { color-scheme: light dark; }
            body { font: 15px/1.5 system-ui, sans-serif; margin: 0; padding: 2rem 1rem;
                   background: #fafafa; color: #1a1a1a; }
            @media (prefers-color-scheme: dark) {
              body { background: #16181d; color: #e6e6e6; }
              table { background: #1e2127; }
              tr + tr td { border-top-color: #2c3038; }
              code { background: #2c3038; }
              a { color: #6ea8fe; }
            }
            main { max-width: 900px; margin: 0 auto; }
            h1 { font-size: 1.4rem; margin: 0 0 .25rem; }
            .sub { color: #888; margin: 0 0 1.5rem; font-size: .9rem; }
            table { width: 100%; border-collapse: collapse; background: #fff;
                    border-radius: 10px; overflow: hidden;
                    box-shadow: 0 1px 3px rgba(0,0,0,.08); }
            td { padding: .85rem 1rem; vertical-align: top; }
            tr + tr td { border-top: 1px solid #eee; }
            .name { font-weight: 600; }
            .desc { color: #888; font-size: .9rem; margin-top: .15rem; }
            .meta { color: #aaa; font-size: .8rem; margin-top: .35rem; }
            .clone { text-align: right; white-space: nowrap; }
            .empty { text-align: center; color: #888; padding: 2rem 1rem; }
            code { background: #f0f0f0; padding: .15rem .4rem; border-radius: 5px;
                   font: 13px/1.4 ui-monospace, monospace; }
            .clone code { user-select: all; }
          </style>
        </head>
        <body>
          <main>
            <h1>Git repositories</h1>
            <p class="sub">{{repos.Count}} repositor{{(repos.Count == 1 ? "y" : "ies")}} &middot; served over Smart HTTP</p>
            <table>{{rows}}</table>
          </main>
        </body>
        </html>
        """;
}

// The repo's one-line description, unless it's Git's default placeholder.
static string ReadDescription(string repoDir)
{
    try
    {
        var path = Path.Combine(repoDir, "description");
        if (!File.Exists(path))
            return "";
        var text = File.ReadAllText(path).Trim();
        return text.StartsWith("Unnamed repository", StringComparison.Ordinal) ? "" : text;
    }
    catch { return ""; }
}

// Cheap "last activity" proxy: newest mtime among HEAD, refs, and the packed-refs file —
// these move on every push, without shelling out to git.
static DateTime LastActivity(string repoDir)
{
    var candidates = new[]
    {
        Path.Combine(repoDir, "HEAD"),
        Path.Combine(repoDir, "refs"),
        Path.Combine(repoDir, "packed-refs"),
    };
    var latest = Directory.GetLastWriteTime(repoDir);
    foreach (var c in candidates)
    {
        try
        {
            var t = File.Exists(c) ? File.GetLastWriteTime(c)
                  : Directory.Exists(c) ? Directory.GetLastWriteTime(c)
                  : DateTime.MinValue;
            if (t > latest) latest = t;
        }
        catch { /* ignore */ }
    }
    return latest;
}

static string Enc(string s) => System.Net.WebUtility.HtmlEncode(s);

// Extracts the repository name from a git PATH_INFO like "/projekt.git/info/refs".
static string? RepoFromPath(string pathInfo)
{
    var seg = pathInfo.Trim('/');
    if (seg.Length == 0)
        return null;
    int slash = seg.IndexOf('/');
    if (slash >= 0)
        seg = seg[..slash];
    return NormalizeRepo(seg);
}

// Strips a trailing ".git" so config can list repos with or without the suffix.
static string NormalizeRepo(string name)
    => name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

// True if the user may access the repo: "*" grants everything, otherwise name match.
static bool IsRepoAllowed(UserAccess entry, string repo)
    => entry.Repos.Any(r => r == "*"
        || string.Equals(NormalizeRepo(r), repo, StringComparison.OrdinalIgnoreCase));

// Constant-time comparison so credential checks don't leak length/content via timing.
static bool FixedTimeEquals(string a, string b)
{
    var ba = Encoding.UTF8.GetBytes(a);
    var bb = Encoding.UTF8.GetBytes(b);
    // Hash to equal-length buffers first; FixedTimeEquals requires equal lengths.
    Span<byte> ha = stackalloc byte[32];
    Span<byte> hb = stackalloc byte[32];
    SHA256.HashData(ba, ha);
    SHA256.HashData(bb, hb);
    return CryptographicOperations.FixedTimeEquals(ha, hb);
}

// A configured user: password/token plus the repos they may access ("*" = all).
sealed class UserAccess
{
    public string Password { get; set; } = "";
    public string[] Repos { get; set; } = [];
}
