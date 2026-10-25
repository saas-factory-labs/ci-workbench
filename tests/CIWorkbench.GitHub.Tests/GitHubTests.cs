using System.Net;
using System.Text;
using CIWorkbench.Core.Models;
using CIWorkbench.GitHub;
using Microsoft.Extensions.Options;

namespace CIWorkbench.GitHub.Tests;

internal sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<HttpRequestMessage> Calls { get; } = [];
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var snapshot = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var h in request.Headers) snapshot.Headers.TryAddWithoutValidation(h.Key, h.Value);
        Calls.Add(snapshot);
        return Task.FromResult(respond(request));
    }
}

internal sealed class Factory(HttpClient api, HttpClient blob) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => name == GitHubCiProvider.BlobClientName ? blob : api;
}

internal sealed class FixedTime(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

internal static class H
{
    public static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
    public static readonly RepoRef Repo = new("example-org", "demo");

    public static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static (GitHubCiProvider Provider, Fake Api, Fake Blob, List<TimeSpan> Delays, RateLimitTracker Tracker) Build(
        Func<HttpRequestMessage, HttpResponseMessage> api, Func<HttpRequestMessage, HttpResponseMessage>? blob = null,
        string? token = "ghp_testtoken_not_real_0000000000000000", int maxWaitSeconds = 30)
    {
        var options = Options.Create(new GitHubOptions { Token = token, MaxRateLimitWaitSeconds = maxWaitSeconds });
        var tracker = new RateLimitTracker(); var time = new FixedTime(Now);
        var delays = new List<TimeSpan>();
        var fakeApi = new Fake(api); var fakeBlob = new Fake(blob ?? (_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        var cred = new PatCredentialSource(options);
        var chain = new RetryHandler(tracker, time, TimeSpan.FromSeconds(maxWaitSeconds), 3, (t, _) => { delays.Add(t); return Task.CompletedTask; })
        { InnerHandler = new CredentialHandler(cred, options) { InnerHandler = fakeApi } };
        var apiClient = new HttpClient(chain) { BaseAddress = new Uri("https://api.github.com/") };
        var blobClient = new HttpClient(fakeBlob);
        return (new GitHubCiProvider(new Factory(apiClient, blobClient), options, tracker, cred), fakeApi, fakeBlob, delays, tracker);
    }

    public static string RunJson(long id, string status = "completed", string? conclusion = "success", string path = ".github/workflows/ci.yml@refs/heads/main") =>
        $"{{\"id\":{id},\"workflow_id\":7,\"name\":\"CI\",\"path\":\"{path}\",\"run_number\":3,\"run_attempt\":1,\"status\":\"{status}\",\"conclusion\":{(conclusion is null ? "null" : $"\"{conclusion}\"")}," +
        "\"event\":\"push\",\"head_branch\":\"main\",\"head_sha\":\"abc\",\"head_commit\":{\"message\":\"first line\\nsecond\"},\"actor\":{\"login\":\"octo\"}," +
        "\"created_at\":\"2026-10-24T10:00:00Z\",\"run_started_at\":\"2026-10-24T10:00:05Z\",\"updated_at\":\"2026-10-24T10:05:05Z\",\"html_url\":\"https://github.com/x\"}";
}

public class PaginationAndMappingTests
{
    [Fact] public async Task Repositories_stream_page_by_page_following_link_headers()
    {
        var (p, api, _, _, _) = H.Build(req =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("page=2"))
                return H.Json("[{\"name\":\"b\",\"owner\":{\"login\":\"o\"},\"private\":true}]");
            var r = H.Json("[{\"name\":\"a\",\"owner\":{\"login\":\"o\"},\"private\":false,\"default_branch\":\"main\"}]");
            r.Headers.Add("Link", "<https://api.github.com/user/repos?per_page=100&page=2>; rel=\"next\", <https://api.github.com/user/repos?per_page=100&page=2>; rel=\"last\"");
            return r;
        });
        var pages = new List<RepositoryPage>();
        await foreach (var page in p.ListRepositoriesAsync(default)) pages.Add(page);
        Assert.Equal(2, pages.Count);
        Assert.False(pages[0].IsLast); Assert.True(pages[1].IsLast);
        Assert.Equal(2, pages[0].EstimatedPages);
        Assert.Equal(["o/a", "o/b"], pages.SelectMany(x => x.Items).Select(r => r.Ref.FullName));
        Assert.Equal(2, api.Calls.Count);
    }

    [Fact] public async Task Allowlist_filters_repositories()
    {
        var options = new GitHubOptions { AllowedRepositories = ["o/a"] };
        var (_, _, _, _, _) = H.Build(_ => H.Json("[]"));
        var tracker = new RateLimitTracker(); var opt = Options.Create(options);
        var fake = new Fake(_ => H.Json("[{\"name\":\"a\",\"owner\":{\"login\":\"o\"},\"private\":false},{\"name\":\"z\",\"owner\":{\"login\":\"o\"},\"private\":false}]"));
        var provider = new GitHubCiProvider(new Factory(new HttpClient(fake) { BaseAddress = new Uri("https://api.github.com/") }, new HttpClient()), opt, tracker, new PatCredentialSource(opt));
        var items = new List<RepositoryInfo>();
        await foreach (var page in provider.ListRepositoriesAsync(default)) items.AddRange(page.Items);
        Assert.Equal(["o/a"], items.Select(r => r.Ref.FullName));
    }

    [Fact] public async Task Runs_are_mapped_with_ref_suffix_stripped_and_first_commit_line()
    {
        var (p, _, _, _, _) = H.Build(_ => H.Json("{\"workflow_runs\":[" + H.RunJson(1) + "," + H.RunJson(2, "in_progress", null) + "," + H.RunJson(3, conclusion: "timed_out") + "]}"));
        var runs = await p.ListRecentRunsAsync(H.Repo, 50, default);
        Assert.All(runs, r => Assert.Equal(".github/workflows/ci.yml", r.WorkflowPath));
        Assert.Equal([RunState.Success, RunState.InProgress, RunState.Failure], runs.Select(r => r.State));
        Assert.Equal("first line", runs[0].CommitMessage);
        Assert.Equal(TimeSpan.FromMinutes(5), runs[0].Duration);
    }

    [Fact] public async Task Recent_runs_stop_paging_once_max_is_reached()
    {
        var (p, api, _, _, _) = H.Build(_ =>
        {
            var r = H.Json("{\"workflow_runs\":[" + H.RunJson(1) + "," + H.RunJson(2) + "]}");
            r.Headers.Add("Link", "<https://api.github.com/x?page=2>; rel=\"next\"");
            return r;
        });
        var runs = await p.ListRecentRunsAsync(H.Repo, 2, default);
        Assert.Equal(2, runs.Count); Assert.Single(api.Calls);
    }

    [Theory]
    [InlineData("completed", "cancelled", RunState.Cancelled)] [InlineData("completed", "skipped", RunState.Skipped)]
    [InlineData("queued", null, RunState.Queued)] [InlineData("waiting", null, RunState.Queued)]
    [InlineData("completed", "startup_failure", RunState.Failure)] [InlineData("completed", "action_required", RunState.Neutral)]
    public void State_mapping(string status, string? conclusion, RunState expected) => Assert.Equal(expected, GitHubCiProvider.MapState(status, conclusion));

    [Fact] public async Task Jobs_include_steps_and_failed_step_state()
    {
        var (p, _, _, _, _) = H.Build(_ => H.Json("{\"jobs\":[{\"id\":9,\"name\":\"test (ubuntu)\",\"status\":\"completed\",\"conclusion\":\"failure\",\"started_at\":\"2026-10-24T10:00:00Z\",\"completed_at\":\"2026-10-24T10:01:00Z\",\"steps\":[{\"number\":1,\"name\":\"Checkout\",\"status\":\"completed\",\"conclusion\":\"success\"},{\"number\":2,\"name\":\"Run tests\",\"status\":\"completed\",\"conclusion\":\"failure\"}]}]}"));
        var job = (await p.ListJobsAsync(H.Repo, 1, default)).Single();
        Assert.Equal(RunState.Failure, job.State);
        Assert.Equal("Run tests", job.Steps.Single(s => s.State == RunState.Failure).Name);
    }
}

public class ErrorTests
{
    [Theory]
    [InlineData(401, "rejected the token")] [InlineData(404, "Not found")] [InlineData(500, "returned 500")]
    public async Task Http_errors_have_actionable_messages(int code, string expected)
    {
        var (p, _, _, _, _) = H.Build(_ => new HttpResponseMessage((HttpStatusCode)code));
        var ex = await Assert.ThrowsAsync<CiProviderException>(() => p.ListWorkflowsAsync(H.Repo, default));
        Assert.Contains(expected, ex.Message); Assert.Equal(code, ex.StatusCode);
    }

    [Fact] public async Task Permission_403_is_not_retried_and_says_permission()
    {
        var (p, api, _, delays, _) = H.Build(_ => H.Json("{\"message\":\"Resource not accessible by personal access token\"}", HttpStatusCode.Forbidden));
        var ex = await Assert.ThrowsAsync<CiProviderException>(() => p.ListWorkflowsAsync(H.Repo, default));
        Assert.Contains("lacks permission", ex.Message);
        Assert.Single(api.Calls); Assert.Empty(delays);
    }

    [Fact] public async Task Primary_rate_limit_waits_until_reset_then_succeeds()
    {
        var n = 0;
        var (p, api, _, delays, tracker) = H.Build(_ =>
        {
            if (n++ == 0)
            {
                var r = H.Json("{}", HttpStatusCode.Forbidden);
                r.Headers.Add("x-ratelimit-limit", "5000"); r.Headers.Add("x-ratelimit-remaining", "0");
                r.Headers.Add("x-ratelimit-reset", H.Now.AddSeconds(10).ToUnixTimeSeconds().ToString());
                return r;
            }
            return H.Json("{\"workflows\":[]}");
        });
        await p.ListWorkflowsAsync(H.Repo, default);
        Assert.Equal(2, api.Calls.Count);
        Assert.Equal(TimeSpan.FromSeconds(11), Assert.Single(delays));
        Assert.Equal(1, tracker.RetriesPerformed);
    }

    [Fact] public async Task Secondary_limit_honors_retry_after()
    {
        var n = 0;
        var (p, _, _, delays, _) = H.Build(_ =>
        {
            if (n++ == 0) { var r = H.Json("{}", HttpStatusCode.Forbidden); r.Headers.RetryAfter = new(TimeSpan.FromSeconds(7)); return r; }
            return H.Json("{\"workflows\":[]}");
        });
        await p.ListWorkflowsAsync(H.Repo, default);
        Assert.Equal(TimeSpan.FromSeconds(8), Assert.Single(delays));
    }

    [Fact] public async Task Rate_limit_longer_than_max_wait_fails_fast_with_reset_time_and_does_not_sleep()
    {
        var (p, api, _, delays, tracker) = H.Build(_ =>
        {
            var r = H.Json("{}", HttpStatusCode.Forbidden);
            r.Headers.Add("x-ratelimit-limit", "5000"); r.Headers.Add("x-ratelimit-remaining", "0");
            r.Headers.Add("x-ratelimit-reset", H.Now.AddMinutes(40).ToUnixTimeSeconds().ToString());
            return r;
        });
        var ex = await Assert.ThrowsAsync<CiProviderException>(() => p.ListWorkflowsAsync(H.Repo, default));
        Assert.Contains("rate limit", ex.Message); Assert.Contains("Resets at", ex.Message);
        Assert.Single(api.Calls); Assert.Empty(delays);
        Assert.True(tracker.IsThrottled(H.Now));
    }

    [Fact] public async Task Transient_503_is_retried_with_exponential_backoff()
    {
        var n = 0;
        var (p, api, _, delays, _) = H.Build(_ => n++ < 2 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : H.Json("{\"workflows\":[]}"));
        await p.ListWorkflowsAsync(H.Repo, default);
        Assert.Equal(3, api.Calls.Count);
        Assert.Equal(2, delays.Count); Assert.True(delays[1] > delays[0]);
    }

    [Fact] public async Task Cancellation_propagates_as_operation_canceled()
    {
        var (p, _, _, _, _) = H.Build(_ => H.Json("{\"workflows\":[]}"));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => p.ListWorkflowsAsync(H.Repo, cts.Token));
    }
}

public class CachingAndLogTests
{
    [Fact] public async Task Etag_revalidation_returns_cached_body_on_304()
    {
        var n = 0;
        var (p, api, _, _, _) = H.Build(req =>
        {
            if (n++ == 0) { var r = H.Json("{\"workflows\":[{\"id\":1,\"name\":\"CI\",\"path\":\"a.yml\",\"state\":\"active\"}]}"); r.Headers.ETag = new("\"v1\""); return r; }
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var a = await p.ListWorkflowsAsync(H.Repo, default); var b = await p.ListWorkflowsAsync(H.Repo, default);
        Assert.Equal(a, b);
        Assert.Equal("\"v1\"", api.Calls[1].Headers.IfNoneMatch.ToString());
    }

    [Fact] public async Task Log_redirect_is_followed_without_credentials_and_only_the_tail_is_kept()
    {
        var big = string.Concat(Enumerable.Range(0, 5000).Select(i => $"line {i}\n"));
        var (p, api, blob, _, _) = H.Build(
            _ => { var r = new HttpResponseMessage(HttpStatusCode.Found); r.Headers.Location = new("https://blob.example.invalid/logs/abc?sig=x"); return r; },
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(big) });
        var tail = await p.GetJobLogTailAsync(H.Repo, 9, 2000, default);
        Assert.NotNull(tail);
        Assert.True(tail!.Length <= 2000);
        Assert.EndsWith("line 4999\n", tail);
        Assert.StartsWith("line ", tail);       // partial first line dropped
        Assert.Null(blob.Calls.Single().Headers.Authorization);
        Assert.NotNull(api.Calls.Single().Headers.Authorization);
    }

    [Fact] public async Task Expired_log_is_null_not_an_error()
    {
        var (p, _, _, _, _) = H.Build(_ => new HttpResponseMessage(HttpStatusCode.Gone));
        Assert.Null(await p.GetJobLogTailAsync(H.Repo, 9, 1000, default));
    }

    [Fact] public async Task Missing_file_is_null()
    {
        var (p, _, _, _, _) = H.Build(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await p.GetFileAsync(H.Repo, ".ciworkbench.yml", default));
    }

    [Fact] public async Task Token_is_only_sent_to_the_configured_api_host()
    {
        var options = Options.Create(new GitHubOptions { Token = "ghp_x_not_real_0000000000000000000" });
        var fake = new Fake(_ => H.Json("{}"));
        var handler = new CredentialHandler(new PatCredentialSource(options), options) { InnerHandler = fake };
        using var client = new HttpClient(handler);
        await client.GetAsync("https://api.github.com/user"); await client.GetAsync("https://evil.example.invalid/user");
        Assert.NotNull(fake.Calls[0].Headers.Authorization);
        Assert.Null(fake.Calls[1].Headers.Authorization);
    }

    [Fact] public void Credential_to_string_never_contains_the_token() =>
        Assert.DoesNotContain("ghp_", new GitHubCredential("ghp_secret", CredentialKind.PersonalAccessToken).ToString());
}

public class AllowlistAndPagingTests
{
    [Fact] public async Task Allowlist_is_enforced_on_every_repo_scoped_call_not_just_discovery()
    {
        var options = Options.Create(new GitHubOptions { Token = "ghp_x_not_real_0000000000000000000", AllowedRepositories = ["example-org/allowed"] });
        var fake = new Fake(_ => H.Json("{\"workflows\":[]}"));
        var p = new GitHubCiProvider(new Factory(new HttpClient(fake) { BaseAddress = new Uri("https://api.github.com/") }, new HttpClient()), options, new RateLimitTracker(), new PatCredentialSource(options));
        var other = new RepoRef("example-org", "secret");
        await Assert.ThrowsAsync<CiProviderException>(() => p.ListWorkflowsAsync(other, default));
        await Assert.ThrowsAsync<CiProviderException>(() => p.ListRecentRunsAsync(other, 10, default));
        await Assert.ThrowsAsync<CiProviderException>(() => p.ListWorkflowRunsAsync(other, 1, 10, default));
        await Assert.ThrowsAsync<CiProviderException>(() => p.ListJobsAsync(other, 1, default));
        await Assert.ThrowsAsync<CiProviderException>(() => p.GetJobLogTailAsync(other, 1, 100, default));
        await Assert.ThrowsAsync<CiProviderException>(() => p.GetFileAsync(other, "x", default));
        Assert.Empty(fake.Calls);                                   // blocked before any network call
        await p.ListWorkflowsAsync(new RepoRef("example-org", "allowed"), default);
        Assert.Single(fake.Calls);
    }

    [Fact] public async Task Not_modified_response_keeps_pagination_from_the_cached_response()
    {
        var calls = 0;
        var (p, _, _, _, _) = H.Build(req =>
        {
            var page2 = req.RequestUri!.Query.Contains("page=2");
            calls++;
            if (calls > 2) return new HttpResponseMessage(HttpStatusCode.NotModified);          // revalidation of page 1 and page 2: no Link header
            var r = H.Json(page2 ? "{\"workflows\":[{\"id\":2,\"name\":\"B\",\"path\":\"b.yml\",\"state\":\"active\"}]}" : "{\"workflows\":[{\"id\":1,\"name\":\"A\",\"path\":\"a.yml\",\"state\":\"active\"}]}");
            r.Headers.ETag = new(page2 ? "\"p2\"" : "\"p1\"");
            if (!page2) r.Headers.Add("Link", "<https://api.github.com/repos/example-org/demo/actions/workflows?per_page=100&page=2>; rel=\"next\"");
            return r;
        });
        Assert.Equal(2, (await p.ListWorkflowsAsync(H.Repo, default)).Count);
        Assert.Equal(2, (await p.ListWorkflowsAsync(H.Repo, default)).Count);                // second pass fully served by 304s
    }
}

public class ConnectionTests
{
    [Fact] public async Task No_token_reports_failed_authentication_without_calling_github()
    {
        var (p, api, _, _, _) = H.Build(_ => H.Json("{}"), token: null);
        var report = await p.CheckConnectionAsync(null, default);
        Assert.False(report.IsHealthy); Assert.Empty(api.Calls);
    }

    [Fact] public async Task Each_capability_is_verified_by_its_own_api_call_and_failures_are_isolated()
    {
        var (p, _, _, _, _) = H.Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/user") return H.Json("{\"login\":\"someone\"}");
            if (path == "/user/repos") return H.Json("[{\"name\":\"demo\",\"owner\":{\"login\":\"example-org\"},\"private\":false}]");
            if (path.EndsWith("/actions/workflows")) return H.Json("{}", HttpStatusCode.Forbidden);
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var report = await p.CheckConnectionAsync(null, default);
        Assert.Equal("someone", report.Account);
        Assert.Equal(CheckStatus.Ok, report.Checks.Single(c => c.Name == "Authentication").Status);
        Assert.Equal(CheckStatus.Ok, report.Checks.Single(c => c.Name == "Repository discovery").Status);
        var actions = report.Checks.Single(c => c.Name == "Actions read");
        Assert.Equal(CheckStatus.Failed, actions.Status); Assert.Contains("Actions: Read-only", actions.Detail);
        Assert.Equal(CheckStatus.Ok, report.Checks.Single(c => c.Name == "Contents read").Status); // 404 on optional config file is fine
    }
}


public class SingleRunTests
{
    [Fact]
    public async Task Get_run_maps_one_run_with_a_read_only_get()
    {
        HttpRequestMessage? seen = null;
        var (p, _, _, _, _) = H.Build(r => { seen = r; return H.Json(H.RunJson(42, "completed", "failure")); });
        var run = await p.GetRunAsync(H.Repo, 42, default);
        Assert.Equal(HttpMethod.Get, seen!.Method);
        Assert.EndsWith("repos/example-org/demo/actions/runs/42", seen.RequestUri!.AbsolutePath.TrimStart('/'));
        Assert.Equal(RunState.Failure, run!.State);
        Assert.Equal(42, run.Id);
        Assert.Equal(".github/workflows/ci.yml", run.WorkflowPath);
    }

    [Fact]
    public async Task Get_run_returns_null_for_a_run_that_does_not_exist()
    {
        var (p, _, _, _, _) = H.Build(_ => H.Json("{\"message\":\"Not Found\"}", System.Net.HttpStatusCode.NotFound));
        Assert.Null(await p.GetRunAsync(H.Repo, 1, default));
    }
}
