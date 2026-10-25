using TUnit.Assertions.Enums;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text;
using CIWorkbench.Core.Models;
using CIWorkbench.GitHub;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Threading.Tasks;

namespace CIWorkbench.GitHub.Tests;

internal sealed class ListLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];
    public List<string> StateValues { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
        if (state is IEnumerable<KeyValuePair<string, object?>> kvs) foreach (var kv in kvs) StateValues.Add($"{kv.Key}={kv.Value}");
        if (exception is not null) Messages.Add(exception.ToString());
    }
}

/// <summary>Captures every metric measurement and span the GitHub integration emits, via the BCL listeners (no OpenTelemetry needed).</summary>
public sealed class TelemetryTests : IDisposable
{
    private const string Token = "ghp_TELEMETRY_TEST_TOKEN_NOT_REAL_000000";
    private static readonly RepoRef Secret = new("acme-secret-org", "private-project-xyz");

    private readonly List<(string Instrument, double Value, Dictionary<string, string> Tags)> _measurements = [];
    private readonly List<Activity> _activities = [];
    private readonly MeterListener _meters = new();
    private readonly ActivityListener _spans;
    private readonly ListLogger<GitHubCiProvider> _log = new();

    public TelemetryTests()
    {
        _meters.InstrumentPublished = (inst, l) => { if (inst.Meter.Name == CiWorkbenchTelemetry.Name) l.EnableMeasurementEvents(inst); };
        _meters.SetMeasurementEventCallback<double>((i, v, tags, _) => Capture(i, v, tags));
        _meters.SetMeasurementEventCallback<long>((i, v, tags, _) => Capture(i, v, tags));
        _meters.SetMeasurementEventCallback<int>((i, v, tags, _) => Capture(i, v, tags));
        _meters.Start();
        _spans = new ActivityListener { ShouldListenTo = s => s.Name == CiWorkbenchTelemetry.Name, Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData, ActivityStopped = a => { lock (_activities) _activities.Add(a); } };
        ActivitySource.AddActivityListener(_spans);
    }

    private void Capture(Instrument i, double v, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var d = tags.ToArray().ToDictionary(t => t.Key, t => Convert.ToString(t.Value) ?? "");
        lock (_measurements) _measurements.Add((i.Name, v, d));
    }

    private readonly List<IDisposable> _owned = [];

    public void Dispose() { _meters.Dispose(); _spans.Dispose(); foreach (var o in _owned) o.Dispose(); }

    private (GitHubCiProvider Provider, GitHubMetrics Metrics, List<bool> SuppressFlags) Make(Func<HttpRequestMessage, HttpResponseMessage> api, Func<HttpRequestMessage, HttpResponseMessage>? blob = null)
    {
        var options = Options.Create(new GitHubOptions { Token = Token });
        var tracker = new RateLimitTracker(); var metrics = new GitHubMetrics(tracker); _owned.Add(metrics); var flags = new List<bool>();
        var fakeApi = new Fake(r => { flags.Add(CiWorkbenchTelemetry.IsSuppressed(r)); return api(r); });
        var fakeBlob = new Fake(r => { flags.Add(CiWorkbenchTelemetry.IsSuppressed(r)); return (blob ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)))(r); });
        var cred = new PatCredentialSource(options);
        var chain = new RetryHandler(tracker, new FixedTime(H.Now), TimeSpan.FromSeconds(30), 3, (_, _) => Task.CompletedTask, metrics, _log)
        { InnerHandler = new CredentialHandler(cred, options) { InnerHandler = fakeApi } };
        var provider = new GitHubCiProvider(new Factory(new HttpClient(chain) { BaseAddress = new Uri("https://api.github.com/") }, new HttpClient(fakeBlob)), options, tracker, cred, metrics, _log);
        return (provider, metrics, flags);
    }

    private IEnumerable<string> AllTelemetryStrings() =>
        _measurements.SelectMany(m => m.Tags.Keys.Concat(m.Tags.Values).Append(m.Instrument))
            .Concat(_activities.SelectMany(a => new[] { a.DisplayName, a.OperationName }.Concat(a.TagObjects.SelectMany(t => new[] { t.Key, Convert.ToString(t.Value) ?? "" }))))
            .Concat(_log.Messages).Concat(_log.StateValues);

    [Test]
    public async Task Successful_requests_record_duration_with_bounded_labels_and_one_span_each()
    {
        var (p, _, _) = Make(_ => H.Json("{\"workflows\":[]}"));
        await p.ListWorkflowsAsync(Secret, default);
        var d = Enumerable.Single(_measurements, m => m.Instrument == "ciworkbench.github.request.duration");
        await Assert.That(d.Tags["operation"]).IsEqualTo("workflows.list"); await Assert.That(d.Tags["status_class"]).IsEqualTo("2xx");
        await Assert.That(d.Tags.Count).IsEqualTo(2);
        var span = Enumerable.Single(_activities, a => a.OperationName == "github.request");
        await Assert.That(span.GetTagItem("github.operation")).IsEqualTo("workflows.list");
        await Assert.That(span.GetTagItem("http.response.status_code")).IsEqualTo(200);
    }

    [Test]
    [Arguments(401, "unauthorized")] [Arguments(404, "not_found")] [Arguments(500, "server_error")] [Arguments(422, "client_error")]
    public async Task Failures_are_counted_by_operation_and_error_class(int status, string expected)
    {
        var (p, _, _) = Make(_ => new HttpResponseMessage((HttpStatusCode)status));
        await Assert.ThrowsAsync<CiProviderException>(() => p.ListWorkflowsAsync(Secret, default));
        var e = _measurements.First(m => m.Instrument == "ciworkbench.github.request.errors");
        await Assert.That(e.Tags["operation"]).IsEqualTo("workflows.list"); await Assert.That(e.Tags["error_type"]).IsEqualTo(expected);
        await Assert.That(_activities.First(a => a.OperationName == "github.request").Status).IsEqualTo(ActivityStatusCode.Error);
    }

    [Test]
    public async Task Permission_403_and_rate_limit_403_are_different_error_classes()
    {
        var (perm, _, _) = Make(_ => H.Json("{}", HttpStatusCode.Forbidden));
        await Assert.ThrowsAsync<CiProviderException>(() => perm.ListWorkflowsAsync(Secret, default));
        await Assert.That(_measurements).Contains(m => m.Instrument == "ciworkbench.github.request.errors" && m.Tags["error_type"] == "forbidden");

        _measurements.Clear();
        var (limited, _, _) = Make(_ => { var r = H.Json("{}", HttpStatusCode.Forbidden); r.Headers.Add("x-ratelimit-limit", "5000"); r.Headers.Add("x-ratelimit-remaining", "0"); r.Headers.Add("x-ratelimit-reset", H.Now.AddMinutes(40).ToUnixTimeSeconds().ToString()); return r; });
        await Assert.ThrowsAsync<CiProviderException>(() => limited.ListWorkflowsAsync(Secret, default));
        await Assert.That(_measurements).Contains(m => m.Instrument == "ciworkbench.github.request.errors" && m.Tags["error_type"] == "rate_limited");
    }

    [Test]
    public async Task Missing_optional_files_are_not_counted_as_errors()
    {
        var (p, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        await Assert.That(await p.GetFileAsync(Secret, ".ciworkbench.yml", default)).IsNull();
        await Assert.That(_measurements).DoesNotContain(m => m.Instrument == "ciworkbench.github.request.errors");
        await Assert.That(_measurements).Contains(m => m.Instrument == "ciworkbench.github.request.duration" && m.Tags["operation"] == "file.get" && m.Tags["status_class"] == "4xx");
    }

    [Test]
    public async Task Cache_hits_and_misses_are_counted_per_operation()
    {
        var n = 0;
        var (p, _, _) = Make(_ => { if (n++ == 0) { var r = H.Json("{\"workflows\":[]}"); r.Headers.ETag = new("\"v1\""); return r; } return new HttpResponseMessage(HttpStatusCode.NotModified); });
        await p.ListWorkflowsAsync(Secret, default); await p.ListWorkflowsAsync(Secret, default);
        var cache = _measurements.Where(m => m.Instrument == "ciworkbench.github.cache.requests").Select(m => m.Tags["result"]).ToList();
        await Assert.That(cache).IsEquivalentTo(["miss", "hit"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Discovery_and_workflow_load_durations_are_recorded_with_an_outcome()
    {
        var (p, _, _) = Make(req => req.RequestUri!.AbsolutePath == "/user/repos"
            ? H.Json("[{\"name\":\"a\",\"owner\":{\"login\":\"o\"},\"private\":false}]") : H.Json("{\"workflows\":[]}"));
        await foreach (var _ in p.ListRepositoriesAsync(default)) { }
        await p.ListWorkflowsAsync(Secret, default);
        await Assert.That(_measurements).Contains(m => m.Instrument == "ciworkbench.github.repository_discovery.duration" && m.Tags["outcome"] == "completed");
        await Assert.That(_measurements).Contains(m => m.Instrument == "ciworkbench.github.workflow_load.duration" && m.Tags["outcome"] == "completed");
        await Assert.That(_log.Messages).Contains(m => m.StartsWith("GitHub repository discovery completed: 1 page(s)"));
        await Assert.That(_log.Messages).Contains(m => m.StartsWith("GitHub workflow list loaded: 0 workflow(s)"));
    }

    [Test]
    public async Task Abandoned_discovery_is_distinguishable_from_failed_discovery()
    {
        var (p, _, _) = Make(_ => { var r = H.Json("[{\"name\":\"a\",\"owner\":{\"login\":\"o\"},\"private\":false}]"); r.Headers.Add("Link", "<https://api.github.com/user/repos?page=2>; rel=\"next\""); return r; });
        await foreach (var _ in p.ListRepositoriesAsync(default)) break;
        await Assert.That(_measurements).Contains(m => m.Instrument == "ciworkbench.github.repository_discovery.duration" && m.Tags["outcome"] == "abandoned");

        _measurements.Clear();
        var (bad, _, _) = Make(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<CiProviderException>(async () => { await foreach (var _ in bad.ListRepositoriesAsync(default)) { } });
        await Assert.That(_measurements).Contains(m => m.Instrument == "ciworkbench.github.repository_discovery.duration" && m.Tags["outcome"] == "failed");
    }

    [Test]
    public async Task Rate_limit_remaining_gauge_reports_the_last_value_seen_in_response_headers()
    {
        var (p, _, _) = Make(_ => { var r = H.Json("{\"workflows\":[]}"); r.Headers.Add("x-ratelimit-limit", "5000"); r.Headers.Add("x-ratelimit-remaining", "4321"); r.Headers.Add("x-ratelimit-reset", H.Now.AddMinutes(30).ToUnixTimeSeconds().ToString()); return r; });
        await p.ListWorkflowsAsync(Secret, default);
        _meters.RecordObservableInstruments();
        var g = Enumerable.Single(_measurements, m => m.Instrument == "ciworkbench.github.ratelimit.remaining");
        await Assert.That(g.Value).IsEqualTo(4321);
        await Assert.That(g.Tags).IsEmpty();
    }

    [Test]
    public async Task Retries_are_counted_logged_and_keep_the_instrumentation_suppression_flag()
    {
        var n = 0;
        var (p, _, flags) = Make(_ => n++ < 2 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : H.Json("{\"workflows\":[]}"));
        await p.ListWorkflowsAsync(Secret, default);
        await Assert.That(_measurements.Count(m => m.Instrument == "ciworkbench.github.request.retries" && m.Tags["reason"] == "server_error")).IsEqualTo(2);
        await Assert.That(flags.Count).IsEqualTo(3);
        foreach (var f in flags) await Assert.That(f).IsTrue();   // every attempt (not only the first) tells the host to skip generic HttpClient spans
    }

    [Test]
    public async Task Telemetry_and_logs_never_contain_tokens_urls_repository_names_signed_urls_or_log_text()
    {
        var (p, _, _) = Make(
            req =>
            {
                var path = req.RequestUri!.AbsolutePath;
                if (path.EndsWith("/logs")) { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new("https://blob.example.invalid/logs/abc?sig=SECRETSIG"); return r; }
                if (path.EndsWith("/contents/.ciworkbench.yml")) return H.Json("{}", HttpStatusCode.Forbidden);
                if (path == "/user/repos") return H.Json("[{\"name\":\"private-project-xyz\",\"owner\":{\"login\":\"acme-secret-org\"},\"private\":true,\"description\":\"CONFIDENTIAL-DESCRIPTION\"}]");
                return H.Json("{\"workflows\":[{\"id\":1,\"name\":\"CONFIDENTIAL-WORKFLOW\",\"path\":\".github/workflows/secret.yml\",\"state\":\"active\"}]}");
            },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("2026-01-01T00:00:00Z SECRET-LOG-LINE password=hunter2\n") });
        await foreach (var _ in p.ListRepositoriesAsync(default)) { }
        await p.ListWorkflowsAsync(Secret, default);
        await Assert.That((await p.GetJobLogTailAsync(Secret, 99, 1000, default))!).Contains("SECRET-LOG-LINE");   // the log IS fetched...
        await Assert.ThrowsAsync<CiProviderException>(() => p.GetFileAsync(Secret, ".ciworkbench.yml", default));

        var all = string.Join("\n", AllTelemetryStrings());
        await Assert.That(_measurements).IsNotEmpty(); await Assert.That(_activities).IsNotEmpty(); await Assert.That(_log.Messages).IsNotEmpty();
        foreach (var forbidden in new[] { Token, "TELEMETRY_TEST_TOKEN", "Bearer", "SECRETSIG", "SECRET-LOG-LINE", "hunter2", "acme-secret-org", "private-project-xyz",
                                          "CONFIDENTIAL", "secret.yml", "blob.example.invalid", "api.github.com", "https://", "/actions/", "/repos/" })
            await Assert.That(all).DoesNotContain(forbidden, StringComparison.OrdinalIgnoreCase);                   // ...but never reaches telemetry or logs
    }

    [Test]
    public async Task Metric_label_values_come_from_a_small_fixed_vocabulary()
    {
        var (p, _, _) = Make(_ => H.Json("{\"workflows\":[]}"));
        foreach (var i in Enumerable.Range(1, 20)) await p.ListWorkflowsAsync(new RepoRef($"owner{i}", $"repo{i}"), default);
        var distinct = _measurements.Where(m => m.Instrument == "ciworkbench.github.request.duration").Select(m => string.Join("|", m.Tags.OrderBy(t => t.Key).Select(t => t.Value))).Distinct().ToList();
        await Assert.That(distinct).HasSingleItem();   // 20 different repositories produce exactly one label combination
    }
}