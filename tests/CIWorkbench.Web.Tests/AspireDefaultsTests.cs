using System.Net;
using CIWorkbench.Core.Models;
using CIWorkbench.GitHub;
using CIWorkbench.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;

namespace CIWorkbench.Web.Tests;

[ClassDataSource<WebApplicationFactory<Program>>(Shared = SharedType.PerClass)]
public class AspireDefaultsTests(WebApplicationFactory<Program> factory)
{
    private const string Token = "ghp_HEALTH_TEST_TOKEN_NOT_REAL_00000000";

    [Test]
    [Arguments("/alive")]
    [Arguments("/ready")]
    [Arguments("/health")]
    public async Task Health_endpoints_report_healthy_without_any_infrastructure_and_never_expose_the_token(string path)
    {
        using var client = factory.WithWebHostBuilder(b => b.UseSetting("GitHub:Token", Token)).CreateClient();
        var res = await client.GetAsync(path);
        await Assert.That(res.StatusCode).IsEqualTo(HttpStatusCode.OK);
        var body = await res.Content.ReadAsStringAsync();
        await Assert.That(body).IsEqualTo("Healthy");
        await Assert.That(body).DoesNotContain("HEALTH_TEST_TOKEN");
    }

    [Test]
    public async Task Standalone_host_starts_in_demo_mode_without_an_otlp_endpoint()
    {
        // No OTEL_EXPORTER_OTLP_ENDPOINT, no dashboard, no token: pages must still render.
        using var client = factory.WithWebHostBuilder(b => b.UseSetting("OTEL_EXPORTER_OTLP_ENDPOINT", "")).CreateClient();
        var res = await client.GetAsync("/repo/example-org/review-scraper-actor");
        res.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// The GitHub clients own their retry policy. If the generic Aspire resilience handler were also active, a persistent
    /// 503 would produce a multiple of 4 attempts (1 + 3 retries here, times the generic handler's own retries).
    /// </summary>
    [Test]
    public async Task Github_clients_use_only_their_own_retry_handler()
    {
        var calls = 0;
        using var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddHttpClient(GitHubCiProvider.ApiClientName).ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(() => Interlocked.Increment(ref calls)))));
        var provider = host.Services.GetRequiredService<GitHubCiProvider>();
        await Assert.ThrowsAsync<CiProviderException>(() => provider.ListWorkflowsAsync(new RepoRef("o", "r"), default));
        await Assert.That(calls).IsEqualTo(4);
    }

    private sealed class CountingHandler(Action onCall) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            onCall();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }
}

public class TelemetrySanitizerTests
{
    [Test]
    [Arguments("/repo/acme/secret-project", "/repo/{owner}/{name}")]
    [Arguments("/repo/acme/secret-project/", "/repo/{owner}/{name}")]
    [Arguments("/repo/acme/secret-project/failures", "/repo/{owner}/{name}/failures")]
    [Arguments("/REPO/Acme/Secret/Failures", "/repo/{owner}/{name}/failures")]
    [Arguments("/settings", "/settings")]
    [Arguments("/repositories", "/repositories")]
    [Arguments("/_blazor", "/_blazor")]
    public async Task Repository_identifiers_never_survive_in_exported_paths(string path, string expected) =>
        await Assert.That(TelemetrySanitizer.SanitizePath(path)).IsEqualTo(expected, StringComparison.OrdinalIgnoreCase);
}