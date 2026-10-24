using CIWorkbench.Components;
using CIWorkbench.Core.Demo;
using CIWorkbench.Core.Models;
using CIWorkbench.GitHub;
using CIWorkbench.Web;
using CIWorkbench.Web.Components;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Reusable UI state + the read-only GitHub provider. The token (GitHub:Token) is read server-side only.
builder.Services.AddCIWorkbenchComponents();
builder.Services.AddCIWorkbenchGitHub(builder.Configuration);
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
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
