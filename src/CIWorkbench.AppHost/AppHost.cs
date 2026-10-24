// CIWorkbench local orchestration (optional). The standalone host runs fine without this project:
//   dotnet run --project src/CIWorkbench.Web --urls http://localhost:5290
// Running through the AppHost adds the Aspire Dashboard (logs, traces, metrics) and health checks. It starts no
// containers and needs no database, cache or other infrastructure.
var builder = DistributedApplication.CreateBuilder(args);

var web = builder.AddProject<Projects.CIWorkbench_Web>("web")
    .WithHttpHealthCheck("/ready");

// Optional GitHub token. Set it once, outside source control (never in appsettings*.json):
//   dotnet user-secrets set "Parameters:github-token" "<fine-grained read-only PAT>" --project src/CIWorkbench.AppHost
// When it is absent nothing is configured and the web app starts in credential-free demo mode.
// Declared as a secret parameter so the dashboard masks it; the value is passed to the web app as GitHub__Token.
if (!string.IsNullOrWhiteSpace(builder.Configuration["Parameters:github-token"]))
{
    var token = builder.AddParameter("github-token", secret: true);
    web.WithEnvironment("GitHub__Token", token)
       .WithEnvironment("CIWORKBENCH_MODE", "live");
}

// Optional: GitHub Enterprise Server or a local test double (read-only API base URL, not a secret).
if (builder.Configuration["GitHub:ApiBaseUrl"] is { Length: > 0 } apiBase)
{
    web.WithEnvironment("GitHub__ApiBaseUrl", apiBase);
}

builder.Build().Run();
