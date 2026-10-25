using CIWorkbench.GitHub;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using System.Threading.Tasks;

namespace CIWorkbench.GitHub.Tests;

public class HealthCheckTests
{
    private static GitHubHealthCheck Make(string? token, RateLimitTracker tracker) =>
        new(Options.Create(new GitHubOptions { Token = token }), tracker, new FixedTime(H.Now));

    [Test]
    public async Task Missing_token_is_healthy_because_demo_mode_needs_no_credentials()
    {
        var r = await Make(null, new RateLimitTracker()).CheckHealthAsync(new HealthCheckContext());
        await Assert.That(r.Status).IsEqualTo(HealthStatus.Healthy);
        await Assert.That((bool)r.Data["token_configured"]).IsFalse();
    }

    [Test]
    public async Task Configured_token_is_reported_as_a_boolean_never_as_a_value()
    {
        const string token = "ghp_HC_TOKEN_NOT_REAL_000000000000000000";
        var r = await Make(token, new RateLimitTracker()).CheckHealthAsync(new HealthCheckContext());
        await Assert.That((bool)r.Data["token_configured"]).IsTrue();
        await Assert.That(r.Description + string.Join(",", r.Data.Select(d => $"{d.Key}={d.Value}"))).DoesNotContain("HC_TOKEN");
    }

    [Test]
    public async Task Active_rate_limiting_degrades_readiness_without_failing_it()
    {
        var tracker = new RateLimitTracker();
        tracker.SetThrottled(H.Now.AddMinutes(5));
        var r = await Make("x", tracker).CheckHealthAsync(new HealthCheckContext());
        await Assert.That(r.Status).IsEqualTo(HealthStatus.Degraded);
    }
}