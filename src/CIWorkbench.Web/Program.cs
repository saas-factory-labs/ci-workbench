using CIWorkbench.Components;
using CIWorkbench.Core.Demo;
using CIWorkbench.Core.Models;
using CIWorkbench.GitHub;
using CIWorkbench.Web;
using CIWorkbench.Web.Components;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Aspire service defaults: OpenTelemetry, health checks, service discovery. All inert unless an OTLP endpoint is configured
// (the Aspire AppHost sets one); a plain `dotnet run` of this project behaves exactly as before.
// Standard HTTP resilience is switched off on purpose: this app's only outbound HTTP is GitHub, whose clients carry a
// domain-aware retry handler (honors Retry-After / x-ratelimit-reset, never retries 401/403-permission/404, waits out
// rate limits up to a cap). Stacking the generic handler on top would multiply retries and cut rate-limit waits short.
builder.AddServiceDefaults(o => o.StandardHttpResilience = false);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Reusable UI state + the read-only GitHub provider. The token (GitHub:Token) is read server-side only.
builder.Services.AddCIWorkbenchComponents();
builder.Services.AddCIWorkbenchGitHub(builder.Configuration);

// ---- Telemetry hardening ------------------------------------------------------------------------------------------------
// Export the GitHub integration's own meter and spans (bounded labels only; see CiWorkbenchTelemetry).
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m.AddMeter(CiWorkbenchTelemetry.Name))
    .WithTracing(t => t.AddSource(CiWorkbenchTelemetry.Name).SetSampler(new BlazorHubNoiseSampler()));
// Generic HttpClient spans carry the full URL (repository paths, signed log-download URLs): skip GitHub requests; the
// provider emits its own span per request instead.
builder.Services.Configure<HttpClientTraceInstrumentationOptions>(o => o.FilterHttpRequestMessage = r => !CiWorkbenchTelemetry.IsSuppressed(r));
// Server spans: replace "/repo/{owner}/{name}" with the route template (private names, unbounded cardinality).
builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(o => o.EnrichWithHttpRequest = (activity, request) => activity.SetTag("url.path", TelemetrySanitizer.SanitizePath(request.Path.Value ?? "")));
// Logging scopes include RequestPath ("/repo/owner/name"); the log record already carries trace and span ids.
builder.Services.Configure<OpenTelemetryLoggerOptions>(o => o.IncludeScopes = false);
// HttpClient logging prints request URLs at Information level.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
// Live mode needs a PAT held by this unauthenticated single-user host: never keep it when the host is reachable beyond loopback.
var remoteReachable = NetworkExposure.IsRemoteReachable(
    builder.Configuration["urls"] ?? builder.Configuration["ASPNETCORE_URLS"],
    builder.Configuration["HTTP_PORTS"] ?? builder.Configuration["HTTPS_PORTS"] ?? builder.Configuration["ASPNETCORE_HTTP_PORTS"] ?? builder.Configuration["ASPNETCORE_HTTPS_PORTS"],
    builder.Configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(e => e["Url"]));
if (remoteReachable)
{
    builder.Services.PostConfigure<GitHubOptions>(o => o.Token = null);
    Console.WriteLine("CIWorkbench: listening beyond loopback, so Live GitHub mode is disabled and only demo data is served.");
}
builder.Services.AddScoped<DemoCiProvider>(sp => new DemoCiProvider(sp.GetRequiredService<TimeProvider>()));
builder.Services.AddScoped<AppSession>();
builder.Services.AddScoped<ICiProvider, SwitchableCiProvider>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

// The standalone host is single-user and intended for localhost (see docs/development/security.md).
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    await next();
});

app.UseAntiforgery();
app.MapStaticAssets();
app.MapDefaultEndpoints();   // /health, /alive, /ready (Development only, as in the Aspire template)
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
