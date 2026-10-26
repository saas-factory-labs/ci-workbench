using CIWorkbench.Components;
using CIWorkbench.Core.Models;
using CIWorkbench.GitHub;

// A minimal external Blazor host: nothing from the CI Workbench repository except the three NuGet packages.
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseDefaultServiceProvider(o => { o.ValidateScopes = true; o.ValidateOnBuild = true; });
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCIWorkbenchGitHub(builder.Configuration);
builder.Services.AddCIWorkbenchWorkspace(builder.Configuration);

var app = builder.Build();
app.UseStaticFiles();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<PackageConsumer.Components.App>().AddInteractiveServerRenderMode();

// Fails the run (not just the build) when the DI contract is incomplete.
using (var scope = app.Services.CreateScope())
{
    _ = scope.ServiceProvider.GetRequiredService<ICiProvider>();
    _ = scope.ServiceProvider.GetRequiredService<AppSession>();
    _ = scope.ServiceProvider.GetRequiredService<IGitHubCredentialSource>();
}
app.Run();
