using CIWorkbench.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace CIWorkbench.GitHub;

public static class GitHubServiceCollectionExtensions
{
    /// <summary>Registers the read-only GitHub provider. Credentials come from <see cref="IGitHubCredentialSource"/> (PAT by default).</summary>
    public static IServiceCollection AddCIWorkbenchGitHub(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<GitHubOptions>(configuration.GetSection(GitHubOptions.Section));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RateLimitTracker>();
        services.TryAddSingleton<GitHubMetrics>();
        services.TryAddSingleton<IGitHubCredentialSource, PatCredentialSource>();
        services.AddTransient<CredentialHandler>();
        services.AddTransient(sp =>
        {
            var o = sp.GetRequiredService<IOptions<GitHubOptions>>().Value;
            return new RetryHandler(sp.GetRequiredService<RateLimitTracker>(), sp.GetRequiredService<TimeProvider>(), TimeSpan.FromSeconds(o.MaxRateLimitWaitSeconds),
                metrics: sp.GetRequiredService<GitHubMetrics>(), logger: sp.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("CIWorkbench.GitHub.Retry"));
        });

        services.AddHttpClient(GitHubCiProvider.ApiClientName, (sp, c) =>
            {
                var o = sp.GetRequiredService<IOptions<GitHubOptions>>().Value;
                c.BaseAddress = new Uri(o.ApiBaseUrl.EndsWith('/') ? o.ApiBaseUrl : o.ApiBaseUrl + "/");
                c.Timeout = TimeSpan.FromSeconds(o.RequestTimeoutSeconds);
                c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .AddHttpMessageHandler<RetryHandler>()
            .AddHttpMessageHandler<CredentialHandler>();

        // Log blobs live on a different host and must never receive the token.
        services.AddHttpClient(GitHubCiProvider.BlobClientName, c => c.Timeout = TimeSpan.FromSeconds(60));

        services.AddSingleton<GitHubCiProvider>();

        // Readiness only (no GitHub calls). Harmless in hosts that do not map health endpoints.
        services.AddHealthChecks().AddCheck<GitHubHealthCheck>("github", tags: ["ready"]);
        return services;
    }
}

