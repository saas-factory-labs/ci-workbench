using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CIWorkbench.Core.Models;
using Microsoft.Extensions.Options;

namespace CIWorkbench.GitHub;

/// <summary>Read-only GitHub REST implementation of <see cref="ICiProvider"/>.</summary>
public sealed class GitHubCiProvider(
    IHttpClientFactory httpFactory, IOptions<GitHubOptions> options, RateLimitTracker tracker, IGitHubCredentialSource credentials) : ICiProvider
{
    public const string ApiClientName = "github-api";
    public const string BlobClientName = "github-blob";
    private const int PageSize = 100;

    private readonly ConcurrentDictionary<string, (string ETag, string Body, string? Next, int? Last)> _etags = new();

    public string Name => "GitHub";
    public bool IsDemo => false;

    private HttpClient Api => httpFactory.CreateClient(ApiClientName);

    // ------------------------------------------------------------------ repositories

    public async IAsyncEnumerable<RepositoryPage> ListRepositoriesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        var url = $"user/repos?per_page={PageSize}&sort=pushed&affiliation=owner,collaborator,organization_member";
        var page = 1; int? estimated = null;
        while (url is not null)
        {
            var (body, next, last) = await GetPageAsync(url, ct);
            estimated ??= last;
            var items = Parse<List<RepoDto>>(body)!.Select(r => new RepositoryInfo(new RepoRef(r.Owner.Login, r.Name), r.Description, r.DefaultBranch ?? "main", r.Private, r.HtmlUrl ?? "", r.PushedAt))
                .Where(r => IsAllowed(r.Ref)).ToList();
            yield return new RepositoryPage(items, page++, estimated, next is null);
            url = next;
        }
    }

    /// <summary>The allowlist is enforced on every repository-scoped call, not just discovery.</summary>
    private void EnsureAllowed(RepoRef r)
    {
        if (!IsAllowed(r)) throw new CiProviderException($"{r} is not in the configured allowlist (GitHub:AllowedRepositories).", 403);
    }

    private bool IsAllowed(RepoRef r)
    {
        var allow = options.Value.AllowedRepositories;
        return allow.Count == 0 || allow.Any(a => a.Equals(r.FullName, StringComparison.OrdinalIgnoreCase) || (a.EndsWith("/*") && a[..^2].Equals(r.Owner, StringComparison.OrdinalIgnoreCase)));
    }

    // ------------------------------------------------------------------ workflows & runs

    public async Task<IReadOnlyList<WorkflowInfo>> ListWorkflowsAsync(RepoRef repo, CancellationToken ct)
    {
        EnsureAllowed(repo);
        var all = new List<WorkflowInfo>();
        string? url = $"repos/{repo.Owner}/{repo.Name}/actions/workflows?per_page={PageSize}";
        while (url is not null)
        {
            var (body, next, _) = await GetPageAsync(url, ct);
            all.AddRange(Parse<WorkflowsEnvelope>(body)!.Workflows.Select(w => new WorkflowInfo(w.Id, w.Name, w.Path, w.State == "active", w.HtmlUrl)));
            url = next;
        }
        return all;
    }

    public Task<IReadOnlyList<WorkflowRunInfo>> ListRecentRunsAsync(RepoRef repo, int max, CancellationToken ct) =>
        ListRunsAsync(repo, $"repos/{repo.Owner}/{repo.Name}/actions/runs", max, ct);

    public Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(RepoRef repo, long workflowId, int max, CancellationToken ct) =>
        ListRunsAsync(repo, $"repos/{repo.Owner}/{repo.Name}/actions/workflows/{workflowId}/runs", max, ct);

    private async Task<IReadOnlyList<WorkflowRunInfo>> ListRunsAsync(RepoRef repo, string path, int max, CancellationToken ct)
    {
        EnsureAllowed(repo);
        var all = new List<WorkflowRunInfo>();
        string? url = $"{path}?per_page={Math.Min(PageSize, Math.Max(1, max))}";
        while (url is not null && all.Count < max)
        {
            var (body, next, _) = await GetPageAsync(url, ct);
            all.AddRange(Parse<RunsEnvelope>(body)!.WorkflowRuns.Select(MapRun));
            url = next;
        }
        return all.Take(max).ToList();
    }

    public async Task<IReadOnlyList<JobInfo>> ListJobsAsync(RepoRef repo, long runId, CancellationToken ct)
    {
        EnsureAllowed(repo);
        var all = new List<JobInfo>();
        string? url = $"repos/{repo.Owner}/{repo.Name}/actions/runs/{runId}/jobs?filter=latest&per_page={PageSize}";
        while (url is not null)
        {
            var (body, next, _) = await GetPageAsync(url, ct);
            all.AddRange(Parse<JobsEnvelope>(body)!.Jobs.Select(j => new JobInfo(j.Id, j.Name, MapState(j.Status, j.Conclusion), j.StartedAt, j.CompletedAt,
                (j.Steps ?? []).Select(s => new StepInfo(s.Number, s.Name, MapState(s.Status, s.Conclusion))).ToList(), j.HtmlUrl ?? "")));
            url = next;
        }
        return all;
    }

    // ------------------------------------------------------------------ logs & files

    public async Task<string?> GetJobLogTailAsync(RepoRef repo, long jobId, int maxBytes, CancellationToken ct)
    {
        EnsureAllowed(repo);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/actions/jobs/{jobId}/logs");
        using var res = await Api.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        // The API answers 302 to a short-lived blob URL. Follow it WITHOUT credentials (separate client, no auth handler).
        if (res.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found && res.Headers.Location is { } loc)
        {
            using var blob = await httpFactory.CreateClient(BlobClientName).GetAsync(loc, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!blob.IsSuccessStatusCode) return null;
            return await ReadTailAsync(await blob.Content.ReadAsStreamAsync(ct), maxBytes, ct);
        }
        if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;   // expired or deleted logs
        if (!res.IsSuccessStatusCode) throw Describe(res, $"job {jobId} log");
        return await ReadTailAsync(await res.Content.ReadAsStreamAsync(ct), maxBytes, ct);
    }

    /// <summary>Streams the body keeping only the last <paramref name="maxBytes"/>, so a huge log never sits in memory.</summary>
    internal static async Task<string> ReadTailAsync(Stream s, int maxBytes, CancellationToken ct)
    {
        var ring = new byte[maxBytes]; long total = 0; var buf = new byte[16 * 1024]; int n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
            for (var i = 0; i < n; i++) ring[total++ % maxBytes] = buf[i];
        byte[] ordered;
        if (total <= maxBytes) ordered = ring[..(int)total];
        else { var start = (int)(total % maxBytes); ordered = [.. ring[start..], .. ring[..start]]; }
        var text = Encoding.UTF8.GetString(ordered);
        if (total > maxBytes) { var nl = text.IndexOf('\n'); if (nl >= 0) text = text[(nl + 1)..]; } // drop the partial first line
        return text;
    }

    public async Task<string?> GetFileAsync(RepoRef repo, string path, CancellationToken ct)
    {
        EnsureAllowed(repo);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/contents/{string.Join('/', path.Split('/').Select(Uri.EscapeDataString))}");
        req.Headers.Accept.ParseAdd("application/vnd.github.raw+json");
        using var res = await Api.SendAsync(req, ct);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw Describe(res, path);
        return await res.Content.ReadAsStringAsync(ct);
    }

    // ------------------------------------------------------------------ rate limit & connection

    public async Task<RateLimitInfo?> GetRateLimitAsync(CancellationToken ct)
    {
        try
        {
            var (body, _, _) = await GetPageAsync("rate_limit", ct, useEtag: false);
            var core = Parse<RateEnvelope>(body)!.Resources.Core;
            return new RateLimitInfo(core.Limit, core.Remaining, DateTimeOffset.FromUnixTimeSeconds(core.Reset));
        }
        catch (CiProviderException) { return tracker.Last; }
    }

    public async Task<ConnectionReport> CheckConnectionAsync(RepoRef? sampleRepo, CancellationToken ct)
    {
        var checks = new List<ConnectionCheck>();
        var account = "(unknown)";
        if ((await credentials.GetAsync(new CiScope(), ct)) is null)
            return new ConnectionReport(account, [new ConnectionCheck("Authentication", CheckStatus.Failed, "No token configured. Set GitHub:Token (user-secrets or GitHub__Token env var) or use demo mode.")]);

        async Task<ConnectionCheck> Run(string name, Func<Task<string>> op, string failHint)
        {
            try { return new ConnectionCheck(name, CheckStatus.Ok, await op()); }
            catch (CiProviderException ex) { return new ConnectionCheck(name, CheckStatus.Failed, $"{ex.Message} {failHint}".Trim()); }
        }

        checks.Add(await Run("Authentication", async () =>
        {
            var (body, _, _) = await GetPageAsync("user", ct, useEtag: false);
            account = Parse<UserDto>(body)!.Login; return $"Authenticated as {account}.";
        }, "Check that the token is valid and not expired."));

        RepositoryInfo? first = null;
        checks.Add(await Run("Repository discovery", async () =>
        {
            await foreach (var p in ListRepositoriesAsync(ct)) { first = p.Items.FirstOrDefault(); break; }
            return first is null ? "Token works but no repositories are accessible (check the token's repository access)." : $"Repositories are accessible (first: {first.Ref}).";
        }, "Grant the token access to the repositories you want to view."));

        var target = sampleRepo ?? first?.Ref;
        if (target is null)
        {
            checks.Add(new ConnectionCheck("Actions read", CheckStatus.Skipped, "No repository to test against."));
            checks.Add(new ConnectionCheck("Contents read", CheckStatus.Skipped, "No repository to test against."));
        }
        else
        {
            var t = target.Value;
            checks.Add(await Run("Actions read", async () => { var w = await ListWorkflowsAsync(t, ct); return $"Listed {w.Count} workflows in {t}."; },
                "Grant the fine-grained permission Actions: Read-only."));
            checks.Add(await Run("Contents read", async () =>
            {
                var f = await GetFileAsync(t, ".ciworkbench.yml", ct);
                return f is null ? $"Contents readable ({t} has no .ciworkbench.yml, which is fine)." : $"Read .ciworkbench.yml from {t}.";
            }, "Grant Contents: Read-only to read .ciworkbench.yml and workflow comments (grouping by YAML comment will be unavailable)."));
        }
        return new ConnectionReport(account, checks);
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>GET with ETag revalidation. Returns body, next-page URL and the estimated last page.</summary>
    private async Task<(string Body, string? Next, int? LastPage)> GetPageAsync(string url, CancellationToken ct, bool useEtag = true)
    {
        ct.ThrowIfCancellationRequested();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var cacheKey = credentials.Identity + "|" + url;
        (string ETag, string Body, string? Next, int? Last)? cached = useEtag && _etags.TryGetValue(cacheKey, out var c) ? c : null;
        if (cached is { } hit) req.Headers.TryAddWithoutValidation("If-None-Match", hit.ETag);
        HttpResponseMessage res;
        try { res = await Api.SendAsync(req, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new CiProviderException("GitHub did not respond in time.", null); }
        catch (HttpRequestException ex) { throw new CiProviderException($"Could not reach GitHub: {ex.Message}", null, ex); }
        using (res)
        {
            var (next, last) = ParseLink(res);
            if (res.StatusCode == HttpStatusCode.NotModified && cached is { } notModified) return (notModified.Body, notModified.Next, notModified.Last);
            if (!res.IsSuccessStatusCode) throw Describe(res, url);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (useEtag && res.Headers.ETag is { } e) _etags[cacheKey] = (e.ToString(), body, next, last);
            return (body, next, last);
        }
    }

    internal static (string? Next, int? Last) ParseLink(HttpResponseMessage res)
    {
        if (!res.Headers.TryGetValues("Link", out var values)) return (null, null);
        string? next = null; int? last = null;
        foreach (var part in string.Join(",", values).Split(','))
        {
            var seg = part.Split(';');
            if (seg.Length < 2) continue;
            var u = seg[0].Trim().Trim('<', '>');
            if (seg[1].Contains("rel=\"next\"")) next = u;
            else if (seg[1].Contains("rel=\"last\"")) { var q = System.Web.HttpUtility.ParseQueryString(new Uri(u).Query)["page"]; if (int.TryParse(q, out var l)) last = l; }
        }
        return (next, last);
    }

    internal CiProviderException Describe(HttpResponseMessage res, string what)
    {
        var code = (int)res.StatusCode;
        var rateReset = tracker.Last is { Remaining: 0 } l ? $" Resets at {l.ResetsAt:HH:mm:ss} UTC." : "";
        return res.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new CiProviderException("GitHub rejected the token (401): it is missing, expired or revoked.", code),
            HttpStatusCode.Forbidden when tracker.IsThrottled(DateTimeOffset.UtcNow) || res.Headers.RetryAfter is not null || (RateLimitTracker.TryHeader(res, "x-ratelimit-remaining", out var rem) && rem == 0)
                => new CiProviderException("GitHub rate limit reached (403)." + rateReset, code),
            HttpStatusCode.TooManyRequests => new CiProviderException("GitHub rate limit reached (429)." + rateReset, code),
            HttpStatusCode.Forbidden => new CiProviderException($"The token lacks permission for {what} (403 \"Resource not accessible\").", code),
            HttpStatusCode.NotFound => new CiProviderException($"Not found (404): {what} does not exist or is not visible to this token.", code),
            _ => new CiProviderException($"GitHub returned {code} for {what}.", code),
        };
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static T? Parse<T>(string body) { try { return JsonSerializer.Deserialize<T>(body, Json); } catch (JsonException ex) { throw new CiProviderException("GitHub returned an unexpected response.", null, ex); } }

    // ------------------------------------------------------------------ mapping

    internal static RunState MapState(string? status, string? conclusion) => status switch
    {
        "queued" or "waiting" or "pending" or "requested" => RunState.Queued,
        "in_progress" => RunState.InProgress,
        _ => conclusion switch
        {
            "success" => RunState.Success,
            "failure" or "timed_out" or "startup_failure" => RunState.Failure,
            "cancelled" => RunState.Cancelled,
            "skipped" => RunState.Skipped,
            _ => RunState.Neutral,
        },
    };

    internal static WorkflowRunInfo MapRun(RunDto r) => new(
        r.Id, r.WorkflowId, StripRef(r.Path), r.Name ?? r.DisplayTitle ?? "(unnamed)", r.RunNumber, r.RunAttempt <= 0 ? 1 : r.RunAttempt, MapState(r.Status, r.Conclusion),
        r.Event ?? "", r.HeadBranch ?? "", r.HeadSha ?? "", r.HeadCommit?.Message?.Split('\n')[0], r.Actor?.Login ?? "",
        r.CreatedAt, r.RunStartedAt, r.UpdatedAt, r.HtmlUrl ?? "");

    /// <summary>Runs report path as ".github/workflows/ci.yml@refs/heads/main"; workflow identity is the path only.</summary>
    internal static string StripRef(string? path) { if (string.IsNullOrEmpty(path)) return ""; var i = path.IndexOf('@'); return i < 0 ? path : path[..i]; }

    // DTOs (internal to this assembly; never exposed to components)
    internal sealed record OwnerDto(string Login);
    internal sealed record UserDto(string Login);
    internal sealed record RepoDto(string Name, OwnerDto Owner, string? Description, [property: JsonPropertyName("default_branch")] string? DefaultBranch, bool Private,
        [property: JsonPropertyName("html_url")] string? HtmlUrl, [property: JsonPropertyName("pushed_at")] DateTimeOffset? PushedAt);
    internal sealed record WorkflowDto(long Id, string Name, string Path, string State, [property: JsonPropertyName("html_url")] string? HtmlUrl);
    internal sealed record WorkflowsEnvelope([property: JsonPropertyName("workflows")] List<WorkflowDto> Workflows);
    internal sealed record CommitDto(string? Message);
    internal sealed record RunDto(long Id, [property: JsonPropertyName("workflow_id")] long WorkflowId, string? Name, [property: JsonPropertyName("display_title")] string? DisplayTitle,
        string? Path, [property: JsonPropertyName("run_number")] int RunNumber, [property: JsonPropertyName("run_attempt")] int RunAttempt, string? Status, string? Conclusion, string? Event,
        [property: JsonPropertyName("head_branch")] string? HeadBranch, [property: JsonPropertyName("head_sha")] string? HeadSha, [property: JsonPropertyName("head_commit")] CommitDto? HeadCommit,
        OwnerDto? Actor, [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt, [property: JsonPropertyName("run_started_at")] DateTimeOffset? RunStartedAt,
        [property: JsonPropertyName("updated_at")] DateTimeOffset? UpdatedAt, [property: JsonPropertyName("html_url")] string? HtmlUrl);
    internal sealed record RunsEnvelope([property: JsonPropertyName("workflow_runs")] List<RunDto> WorkflowRuns);
    internal sealed record StepDto(int Number, string Name, string? Status, string? Conclusion);
    internal sealed record JobDto(long Id, string Name, string? Status, string? Conclusion, [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
        [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt, List<StepDto>? Steps, [property: JsonPropertyName("html_url")] string? HtmlUrl);
    internal sealed record JobsEnvelope(List<JobDto> Jobs);
    internal sealed record RateCore(int Limit, int Remaining, long Reset);
    internal sealed record RateResources(RateCore Core);
    internal sealed record RateEnvelope(RateResources Resources);
}

