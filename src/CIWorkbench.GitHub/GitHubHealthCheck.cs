using CIWorkbench.Core.Models;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CIWorkbench.GitHub;

/// <summary>
/// Readiness check for the GitHub integration. Never calls GitHub (that would burn rate limit on every probe) and never
/// returns Unhealthy: demo mode works without any token, so a missing token is information, not a failure.
/// </summary>
public sealed class GitHubHealthCheck(IOptions<GitHubOptions> options, RateLimitTracker tracker, TimeProvider time) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>
        {
            ["token_configured"] = options.Value.HasToken,   // a boolean only; the value is never exposed
        };
        if (tracker.Last is { } l) data["rate_limit_remaining"] = l.Remaining;

        if (tracker.IsThrottled(time.GetUtcNow()))
            return Task.FromResult(HealthCheckResult.Degraded("GitHub requests are currently being rate-limited.", data: data));

        return Task.FromResult(HealthCheckResult.Healthy(options.Value.HasToken ? "GitHub token configured." : "No GitHub token configured; demo mode only.", data));
    }
}