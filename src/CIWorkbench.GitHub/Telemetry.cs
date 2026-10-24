using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Logging;

namespace CIWorkbench.GitHub;

/// <summary>
/// Telemetry surface of the GitHub integration, built only on BCL types (<see cref="Meter"/>, <see cref="ActivitySource"/>,
/// <see cref="ILogger"/>) so this assembly needs no OpenTelemetry package. A host opts in with
/// <c>AddMeter(CiWorkbenchTelemetry.Name)</c> / <c>AddSource(CiWorkbenchTelemetry.Name)</c>.
/// Privacy rule: attributes are limited to a fixed set of operation names, status classes and error classes.
/// Tokens, URLs, repository/owner names, response bodies and CI log text are never recorded.
/// </summary>
public static class CiWorkbenchTelemetry
{
    public const string Name = "CIWorkbench.GitHub";
    public static readonly ActivitySource Source = new(Name);

    /// <summary>
    /// Set on every GitHub request so the host's generic HttpClient auto-instrumentation can skip it: those spans would carry
    /// the full URL (repository paths, signed log-download URLs). The provider emits its own low-cardinality spans instead.
    /// </summary>
    public static readonly HttpRequestOptionsKey<bool> SuppressHttpInstrumentation = new("ciworkbench.suppress-http-instrumentation");

    public static void Suppress(HttpRequestMessage request) => request.Options.Set(SuppressHttpInstrumentation, true);
    public static bool IsSuppressed(HttpRequestMessage request) => request.Options.TryGetValue(SuppressHttpInstrumentation, out var v) && v;
}

/// <summary>The complete, fixed set of operation labels (keeps metric cardinality bounded).</summary>
public static class GitHubOperations
{
    public const string UserGet = "user.get";
    public const string RepositoriesList = "repositories.list";
    public const string WorkflowsList = "workflows.list";
    public const string RunsList = "runs.list";
    public const string WorkflowRunsList = "workflow_runs.list";
    public const string JobsList = "jobs.list";
    public const string JobLogGet = "job_log.get";
    public const string JobLogBlob = "job_log.blob";
    public const string FileGet = "file.get";
    public const string RateLimitGet = "rate_limit.get";
}

/// <summary>Instruments of the GitHub integration (one instance per process; registered as a singleton).</summary>
public sealed class GitHubMetrics : IDisposable
{
    private readonly Meter _meter = new(CiWorkbenchTelemetry.Name);
    private readonly Histogram<double> _requestDuration;
    private readonly Counter<long> _requestErrors;
    private readonly Counter<long> _retries;
    private readonly Counter<long> _cache;
    private readonly Histogram<double> _discoveryDuration;
    private readonly Histogram<double> _workflowLoadDuration;

    public GitHubMetrics(RateLimitTracker tracker)
    {
        _requestDuration = _meter.CreateHistogram<double>("ciworkbench.github.request.duration", "s", "Duration of a logical GitHub API request, including retries and rate-limit waits.");
        _requestErrors = _meter.CreateCounter<long>("ciworkbench.github.request.errors", "{error}", "Failed GitHub API requests by operation and error class.");
        _retries = _meter.CreateCounter<long>("ciworkbench.github.request.retries", "{retry}", "Automatic retries by reason (rate_limit, server_error, network).");
        _cache = _meter.CreateCounter<long>("ciworkbench.github.cache.requests", "{request}", "Conditional (ETag) request outcomes: hit = 304 Not Modified, miss = fresh response.");
        _discoveryDuration = _meter.CreateHistogram<double>("ciworkbench.github.repository_discovery.duration", "s", "Duration of a full repository discovery run.");
        _workflowLoadDuration = _meter.CreateHistogram<double>("ciworkbench.github.workflow_load.duration", "s", "Duration of loading a repository's workflow list.");
        _meter.CreateObservableGauge<int>("ciworkbench.github.ratelimit.remaining", () => (IEnumerable<Measurement<int>>)(tracker.Last is { } l ? new[] { new Measurement<int>(l.Remaining) } : []),
            "{request}", "Remaining GitHub core API requests as last reported by response headers.");
    }

    internal void RecordRequest(string operation, string statusClass, TimeSpan elapsed) =>
        _requestDuration.Record(elapsed.TotalSeconds, new("operation", operation), new("status_class", statusClass));

    internal void RecordError(string operation, string errorType) =>
        _requestErrors.Add(1, new("operation", operation), new("error_type", errorType));

    internal void RecordRetry(string reason) => _retries.Add(1, new KeyValuePair<string, object?>("reason", reason));

    internal void RecordCache(string operation, bool hit) =>
        _cache.Add(1, new("operation", operation), new("result", hit ? "hit" : "miss"));

    internal void RecordDiscovery(TimeSpan elapsed, string outcome) =>
        _discoveryDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));

    internal void RecordWorkflowLoad(TimeSpan elapsed, string outcome) =>
        _workflowLoadDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("outcome", outcome));

    public void Dispose() => _meter.Dispose();

    /// <summary>Maps a failed response to a bounded error class.</summary>
    internal static string ClassifyError(HttpResponseMessage res, RateLimitTracker tracker) => res.StatusCode switch
    {
        HttpStatusCode.Unauthorized => "unauthorized",
        HttpStatusCode.TooManyRequests => "rate_limited",
        HttpStatusCode.Forbidden when res.Headers.RetryAfter is not null
            || (RateLimitTracker.TryHeader(res, "x-ratelimit-remaining", out var rem) && rem == 0)
            || tracker.IsThrottled(DateTimeOffset.UtcNow) => "rate_limited",
        HttpStatusCode.Forbidden => "forbidden",
        HttpStatusCode.NotFound => "not_found",
        >= HttpStatusCode.InternalServerError => "server_error",
        _ => "client_error",
    };
}

/// <summary>Source-generated structured log messages. Parameters are counts, durations and bounded labels only.</summary>
internal static partial class Log
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "GitHub repository discovery {Outcome}: {Pages} page(s) in {ElapsedMs} ms")]
    public static partial void DiscoveryFinished(ILogger logger, string outcome, int pages, long elapsedMs);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "GitHub workflow list loaded: {Count} workflow(s) in {ElapsedMs} ms")]
    public static partial void WorkflowsLoaded(ILogger logger, int count, long elapsedMs);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "GitHub request failed: {Operation} ({ErrorType}, status {StatusCode})")]
    public static partial void RequestFailed(ILogger logger, string operation, string errorType, int statusCode);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "GitHub rate limit reached; waiting {WaitSeconds} s before retry {Attempt}")]
    public static partial void RateLimitWait(ILogger logger, int waitSeconds, int attempt);

    [LoggerMessage(EventId = 5, Level = LogLevel.Information, Message = "Retrying GitHub request ({Reason}, attempt {Attempt})")]
    public static partial void Retrying(ILogger logger, string reason, int attempt);

    [LoggerMessage(EventId = 6, Level = LogLevel.Warning, Message = "GitHub request could not complete: {Operation} ({ErrorType})")]
    public static partial void RequestAborted(ILogger logger, string operation, string errorType);
}