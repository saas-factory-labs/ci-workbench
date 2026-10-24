using System.Net;
using CIWorkbench.Core.Models;
using CIWorkbench.GitHub;
using CIWorkbench.Web;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace CIWorkbench.Web.Tests;

public class AspireDefaultsTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Token = "ghp_HEALTH_TEST_TOKEN_NOT_REAL_00000000";

    [Theory]
    [InlineData("/alive")]
    [InlineData("/ready")]
    [InlineData("/health")]
    public async Task Health_endpoints_report_healthy_without_any_infrastructure_and_never_expose_the_token(string path)
    {
        using var client = factory.WithWebHostBuilder(b => b.UseSetting("GitHub:Token", Token)).CreateClient();
        var res = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal("Healthy", body);
        Assert.DoesNotContain("HEALTH_TEST_TOKEN", body);
    }

    [Fact]
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
    [Fact]
    public async Task Github_clients_use_only_their_own_retry_handler()
    {
        var calls = 0;
        using var host = factory.WithWebHostBuilder(b => b.ConfigureServices(s =>
            s.AddHttpClient(GitHubCiProvider.ApiClientName).ConfigurePrimaryHttpMessageHandler(() => new CountingHandler(() => Interlocked.Increment(ref calls)))));
        var provider = host.Services.GetRequiredService<GitHubCiProvider>();
        await Assert.ThrowsAsync<CiProviderException>(() => provider.ListWorkflowsAsync(new RepoRef("o", "r"), default));
        Assert.Equal(4, calls);
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
    [Theory]
    [InlineData("/repo/acme/secret-project", "/repo/{owner}/{name}")]
    [InlineData("/repo/acme/secret-project/", "/repo/{owner}/{name}")]
    [InlineData("/repo/acme/secret-project/failures", "/repo/{owner}/{name}/failures")]
    [InlineData("/REPO/Acme/Secret/Failures", "/repo/{owner}/{name}/failures")]
    [InlineData("/settings", "/settings")]
    [InlineData("/repositories", "/repositories")]
    [InlineData("/_blazor", "/_blazor")]
    public void Repository_identifiers_never_survive_in_exported_paths(string path, string expected) =>
        Assert.Equal(expected, TelemetrySanitizer.SanitizePath(path), ignoreCase: true);
}