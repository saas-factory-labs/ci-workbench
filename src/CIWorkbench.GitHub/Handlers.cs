using System.Net;
using System.Net.Http.Headers;
using CIWorkbench.Core.Models;

namespace CIWorkbench.GitHub;

/// <summary>Adds auth/user-agent headers per request, resolving the credential fresh each time.</summary>
public sealed class CredentialHandler(IGitHubCredentialSource source, Microsoft.Extensions.Options.IOptions<GitHubOptions> options) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // Never send the token to any host other than the configured API host.
        var sameHost = string.Equals(request.RequestUri?.Host, new Uri(options.Value.ApiBaseUrl).Host, StringComparison.OrdinalIgnoreCase);
        var cred = sameHost ? await source.GetAsync(new CiScope(), ct) : null;
        if (cred is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred.Token);
        if (!request.Headers.UserAgent.Any()) request.Headers.UserAgent.ParseAdd("CIWorkbench/0.1 (+https://github.com/saas-factory-labs/ci-workbench)");
        if (!request.Headers.Contains("X-GitHub-Api-Version")) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return await base.SendAsync(request, ct);
    }
}

/// <summary>
/// Retries GETs on primary/secondary rate limits (waiting at most <c>MaxWait</c>), 429 and transient 5xx with exponential backoff.
/// 401, permission-403 and 404 are never retried.
/// </summary>
public sealed class RetryHandler(RateLimitTracker tracker, TimeProvider time, TimeSpan maxWait, int maxRetries = 3, Func<TimeSpan, CancellationToken, Task>? delay = null,
    GitHubMetrics? metrics = null, Microsoft.Extensions.Logging.ILogger? logger = null) : DelegatingHandler
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((t, ct) => Task.Delay(t, time, ct));
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromMilliseconds(400);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage original, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = Clone(original);
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, ct);
            }
            catch (HttpRequestException) when (attempt < maxRetries)
            {
                metrics?.RecordRetry("network");
                if (logger is not null) Log.Retrying(logger, "network", attempt + 1);
                await _delay(Backoff(attempt), ct);
                continue;
            }
            tracker.Observe(response);

            var wait = RateLimitWait(response);
            if (wait is { } w && w <= maxWait && attempt < maxRetries)
            {
                tracker.SetThrottled(time.GetUtcNow() + w);
                metrics?.RecordRetry("rate_limit");
                if (logger is not null) Log.RateLimitWait(logger, (int)Math.Ceiling(w.TotalSeconds), attempt + 1);
                response.Dispose();
                await _delay(w, ct);
                tracker.ClearThrottle();
                continue;
            }
            if (wait is { } over) tracker.SetThrottled(time.GetUtcNow() + over); // surfaced to caller as a friendly error
            if (response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout && attempt < maxRetries)
            {
                metrics?.RecordRetry("server_error");
                if (logger is not null) Log.Retrying(logger, "server_error", attempt + 1);
                response.Dispose();
                await _delay(Backoff(attempt), ct);
                continue;
            }
            return response;
        }
    }

    private TimeSpan Backoff(int attempt) => TimeSpan.FromMilliseconds(BaseBackoff.TotalMilliseconds * Math.Pow(2, attempt));

    /// <summary>Returns the required wait when the response is a rate-limit rejection, else null (permission 403s are not rate limits).</summary>
    internal TimeSpan? RateLimitWait(HttpResponseMessage r)
    {
        var isLimit = r.StatusCode == HttpStatusCode.TooManyRequests
            || (r.StatusCode == HttpStatusCode.Forbidden && (r.Headers.RetryAfter is not null || (RateLimitTracker.TryHeader(r, "x-ratelimit-remaining", out var rem) && rem == 0)));
        if (!isLimit) return null;
        if (r.Headers.RetryAfter?.Delta is { } d) return d + TimeSpan.FromSeconds(1);
        if (RateLimitTracker.TryHeader(r, "x-ratelimit-reset", out var reset))
        {
            var until = DateTimeOffset.FromUnixTimeSeconds(reset) - time.GetUtcNow() + TimeSpan.FromSeconds(1);
            return until > TimeSpan.Zero ? until : TimeSpan.FromSeconds(1);
        }
        return TimeSpan.FromSeconds(60); // secondary limit without hints: GitHub asks for at least a minute
    }

    private static HttpRequestMessage Clone(HttpRequestMessage src)
    {
        var c = new HttpRequestMessage(src.Method, src.RequestUri) { Version = src.Version };
        foreach (var h in src.Headers) c.Headers.TryAddWithoutValidation(h.Key, h.Value);
        foreach (var o in (IDictionary<string, object?>)src.Options) ((IDictionary<string, object?>)c.Options)[o.Key] = o.Value;   // keeps the telemetry-suppression flag on every attempt
        return c;
    }
}


