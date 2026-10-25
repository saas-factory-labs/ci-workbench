using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CIWorkbench.Core.Grouping;
using CIWorkbench.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CIWorkbench.GitHub;

/// <summary>Read-only GitHub REST implementation of <see cref="ICiProvider"/>.</summary>
public sealed class GitHubCiProvider(
    IHttpClientFactory httpFactory, IOptions<GitHubOptions> options, RateLimitTracker tracker, IGitHubCredentialSource credentials,
    GitHubMetrics? metrics = null, ILogger<GitHubCiProvider>? logger = null) : ICiProvider
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
        var sw = Stopwatch.StartNew();
        var outcome = "abandoned";           // the consumer stopped enumerating early (e.g. only the first page was needed)
        try
        {
            while (url is not null)
            {
                (string Body, string? Next, int? LastPage) result;
                try { result = await GetPageAsync(url, ct, GitHubOperations.RepositoriesList); }
                catch (OperationCanceledException) { outcome = "cancelled"; throw; }
                catch (CiProviderException) { outcome = "failed"; throw; }
                estimated ??= result.LastPage;
                var items = Parse<List<RepoDto>>(result.Body)!.Select(r => new RepositoryInfo(new RepoRef(r.Owner.Login, r.Name), r.Description, r.DefaultBranch ?? "main", r.Private, r.HtmlUrl ?? "", r.PushedAt, r.Owner.Type))
                    .Where(r => IsAllowed(r.Ref)).ToList();
                yield return new RepositoryPage(items, page++, estimated, result.Next is null);
                url = result.Next;
            }
            outcome = "completed";
        }
        finally
        {
            sw.Stop();
            metrics?.RecordDiscovery(sw.Elapsed, outcome);
            if (logger is not null) Log.DiscoveryFinished(logger, outcome, page - 1, sw.ElapsedMilliseconds);
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
        var sw = Stopwatch.StartNew(); var outcome = "failed";
        try
        {
            while (url is not null)
            {
                var (body, next, _) = await GetPageAsync(url, ct, GitHubOperations.WorkflowsList);
                all.AddRange(Parse<WorkflowsEnvelope>(body)!.Workflows.Select(w => new WorkflowInfo(w.Id, w.Name, w.Path, w.State == "active", w.HtmlUrl)));
                url = next;
            }
            outcome = "completed";
            return all;
        }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        finally
        {
            sw.Stop();
            metrics?.RecordWorkflowLoad(sw.Elapsed, outcome);
            if (logger is not null && outcome == "completed") Log.WorkflowsLoaded(logger, all.Count, sw.ElapsedMilliseconds);
        }
    }

    public Task<IReadOnlyList<WorkflowRunInfo>> ListRecentRunsAsync(RepoRef repo, int max, CancellationToken ct) =>
        ListRunsAsync(repo, $"repos/{repo.Owner}/{repo.Name}/actions/runs", max, GitHubOperations.RunsList, ct);

    public Task<IReadOnlyList<WorkflowRunInfo>> ListWorkflowRunsAsync(RepoRef repo, long workflowId, int max, CancellationToken ct) =>
        ListRunsAsync(repo, $"repos/{repo.Owner}/{repo.Name}/actions/workflows/{workflowId}/runs", max, GitHubOperations.WorkflowRunsList, ct);

    private async Task<IReadOnlyList<WorkflowRunInfo>> ListRunsAsync(RepoRef repo, string path, int max, string operation, CancellationToken ct)
    {
        EnsureAllowed(repo);
        var all = new List<WorkflowRunInfo>();
        string? url = $"{path}?per_page={Math.Min(PageSize, Math.Max(1, max))}";
        while (url is not null && all.Count < max)
        {
            var (body, next, _) = await GetPageAsync(url, ct, operation);
            all.AddRange(Parse<RunsEnvelope>(body)!.WorkflowRuns.Select(MapRun));
            url = next;
        }
        return all.Take(max).ToList();
    }

    /// <summary>
    /// Runs created since <paramref name="since"/>, paged at 100. GitHub returns at most 1,000 results for a filtered listing, so a total at
    /// or above that is reported as incomplete rather than presented as the whole period.
    /// </summary>
    public async Task<RunPeriod> ListRunsSinceAsync(RepoRef repo, DateTimeOffset since, int maxRuns, CancellationToken ct)
    {
        EnsureAllowed(repo);
        var created = Uri.EscapeDataString(">=" + since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
        string? url = $"repos/{repo.Owner}/{repo.Name}/actions/runs?created={created}&per_page={PageSize}";
        var all = new List<WorkflowRunInfo>(); int? total = null;
        while (url is not null && all.Count < maxRuns)
        {
            var (body, next, _) = await GetPageAsync(url, ct, GitHubOperations.RunsList);
            var env = Parse<RunsEnvelope>(body)!;
            total ??= env.TotalCount;
            all.AddRange(env.WorkflowRuns.Select(MapRun));
            url = next;
        }
        var runs = all.Take(maxRuns).ToList();
        return new RunPeriod(runs, total, url is null && (total is null || (runs.Count >= total && total < FilteredListingCap)));
    }

    private const int FilteredListingCap = 1000;

    public async Task<WorkflowRunInfo?> GetRunAsync(RepoRef repo, long runId, CancellationToken ct)
    {
        EnsureAllowed(repo);
        try
        {
            var (body, _, _) = await GetPageAsync($"repos/{repo.Owner}/{repo.Name}/actions/runs/{runId}", ct, GitHubOperations.RunGet);
            return MapRun(Parse<RunDto>(body)!);
        }
        catch (CiProviderException ex) when (ex.StatusCode == 404) { return null; }
    }

    public Task<IReadOnlyList<JobInfo>> ListJobsAsync(RepoRef repo, long runId, CancellationToken ct) => ListJobsCoreAsync(repo, runId, "latest", GitHubOperations.JobsList, ct);

    /// <summary>Every attempt of the run: reruns consumed runner time too, and <c>filter=latest</c> would hide them.</summary>
    public Task<IReadOnlyList<JobInfo>> ListJobAttemptsAsync(RepoRef repo, long runId, CancellationToken ct) => ListJobsCoreAsync(repo, runId, "all", GitHubOperations.JobAttemptsList, ct);

    private async Task<IReadOnlyList<JobInfo>> ListJobsCoreAsync(RepoRef repo, long runId, string filter, string operation, CancellationToken ct)
    {
        EnsureAllowed(repo);
        var all = new List<JobInfo>();
        string? url = $"repos/{repo.Owner}/{repo.Name}/actions/runs/{runId}/jobs?filter={filter}&per_page={PageSize}";
        while (url is not null)
        {
            var (body, next, _) = await GetPageAsync(url, ct, operation);
            all.AddRange(Parse<JobsEnvelope>(body)!.Jobs.Select(j => new JobInfo(j.Id, j.Name, MapState(j.Status, j.Conclusion), j.StartedAt, j.CompletedAt,
                (j.Steps ?? []).Select(s => new StepInfo(s.Number, s.Name, MapState(s.Status, s.Conclusion))).ToList(), j.HtmlUrl ?? "",
                j.Labels ?? [], j.RunnerName, j.RunnerGroupName, j.RunAttempt <= 0 ? 1 : j.RunAttempt, j.CreatedAt)));
            url = next;
        }
        return all;
    }

    // ------------------------------------------------------------------ runner insights (optional permissions)

    /// <summary>
    /// Repository variables merged over the organisation variables shared with the repository. Needs "Variables: Read"; without it the
    /// result says so and the caller treats <c>vars.X</c> as unknown (not as unset).
    /// </summary>
    public async Task<ActionsVariables> GetActionsVariablesAsync(RepoRef repo, CancellationToken ct)
    {
        EnsureAllowed(repo);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            // Organisation variables first so a repository variable of the same name wins, as it does at run time.
            foreach (var path in new[] { "actions/organization-variables", "actions/variables" })
            {
                string? url = $"repos/{repo.Owner}/{repo.Name}/{path}?per_page=30";
                for (var page = 0; url is not null && page < 5; page++)
                {
                    string body; string? next;
                    try { (body, next, _) = await GetPageAsync(url, ct, GitHubOperations.VariablesList); }
                    catch (CiProviderException ex) when (path.StartsWith("actions/organization") && ex.StatusCode is 403 or 404) { break; }   // org-level variables are best effort
                    foreach (var v in Parse<VariablesEnvelope>(body)!.Variables ?? []) values[v.Name] = v.Value ?? "";
                    url = next;
                }
            }
            return new ActionsVariables(VariablesAccess.Available, values, null);
        }
        catch (CiProviderException ex) when (ex.StatusCode is 401 or 403 && !IsRateLimit(ex))
            { return new ActionsVariables(VariablesAccess.PermissionMissing, values, "Reading Actions variables needs the fine-grained permission Variables: Read-only (repository and, for shared variables, organization)."); }
        catch (CiProviderException ex) when (ex.StatusCode == 404)
            { return new ActionsVariables(VariablesAccess.NotAvailable, values, "Actions variables are not visible to this token."); }
        catch (CiProviderException ex)
            { return new ActionsVariables(VariablesAccess.Failed, values, ex.Message); }
    }

    private static bool IsRateLimit(CiProviderException ex) => ex.StatusCode == 429 || ex.Message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// GitHub's enhanced-billing usage report for one account and month, reduced to the Actions product. The deprecated
    /// <c>/settings/billing/actions</c> endpoint is deliberately not used. Never throws for a missing permission: that is a normal result.
    /// </summary>
    public async Task<BillingUsage> GetBillingUsageAsync(string owner, string? ownerType, int year, int month, CancellationToken ct)
    {
        var o = Uri.EscapeDataString(owner);
        var query = $"?year={year}&month={month}";
        var isUser = string.Equals(ownerType, "User", StringComparison.OrdinalIgnoreCase);
        var org = $"organizations/{o}/settings/billing/usage{query}";
        var user = $"users/{o}/settings/billing/usage{query}";
        try
        {
            string body;
            if (isUser) body = (await GetPageAsync(user, ct, GitHubOperations.BillingUsageGet)).Body;
            else
            {
                try { body = (await GetPageAsync(org, ct, GitHubOperations.BillingUsageGet)).Body; }
                catch (CiProviderException ex) when (ownerType is null && ex.StatusCode == 404) { body = (await GetPageAsync(user, ct, GitHubOperations.BillingUsageGet)).Body; }
            }
            var items = Parse<BillingEnvelope>(body)!.UsageItems ?? [];
            var lines = items.Where(i => string.Equals(i.Product, "actions", StringComparison.OrdinalIgnoreCase))
                .GroupBy(i => (Sku: i.Sku ?? "unknown", Unit: i.UnitType ?? ""))
                .Select(g => new BillingLine(g.Key.Sku, g.Key.Unit, g.Sum(i => i.Quantity), g.Max(i => i.PricePerUnit), g.Sum(i => i.GrossAmount), g.Sum(i => i.DiscountAmount), g.Sum(i => i.NetAmount)))
                .OrderBy(l => l.Sku, StringComparer.Ordinal).ToList();
            return new BillingUsage(BillingAccess.Available, lines, null, year, month, DateTimeOffset.UtcNow);
        }
        catch (CiProviderException ex) when (ex.StatusCode is 401 or 403 && !IsRateLimit(ex))
        {
            var need = isUser ? "the fine-grained user permission Plan: Read-only" : "the fine-grained organization permission Administration: Read-only (and an organization owner or billing manager)";
            return new BillingUsage(BillingAccess.PermissionMissing, [], $"Billing usage needs {need}. Usage is estimated from job history instead.", year, month, null);
        }
        catch (CiProviderException ex) when (ex.StatusCode is 404 or 410)
            {
            // For personal accounts GitHub answers 404 (not 403) when the token lacks the Plan permission or the account is someone else's.
            var why = isUser ? "GitHub answers 404 for a personal account's billing unless the token has the user permission Plan: Read-only and the account is your own"
                : "the account may not be on the enhanced billing platform, or is not visible to this token";
            return new BillingUsage(BillingAccess.NotAvailable, [], $"No billing usage report for {owner}: {why}. Usage is estimated instead.", year, month, null);
        }
        catch (CiProviderException ex)
            { return new BillingUsage(BillingAccess.Failed, [], ex.Message, year, month, null); }
    }

    // ------------------------------------------------------------------ logs & files

    public async Task<string?> GetJobLogTailAsync(RepoRef repo, long jobId, int maxBytes, CancellationToken ct)
    {
        EnsureAllowed(repo);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{repo.Owner}/{repo.Name}/actions/jobs/{jobId}/logs");
        using var res = await SendApiAsync(Api, req, GitHubOperations.JobLogGet, HttpCompletionOption.ResponseHeadersRead, ct, notFoundIsExpected: true);
        // The API answers 302 to a short-lived blob URL. Follow it WITHOUT credentials (separate client, no auth handler).
        if (res.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Found && res.Headers.Location is { } loc)
        {
            using var blobReq = new HttpRequestMessage(HttpMethod.Get, loc);
            using var blob = await SendApiAsync(httpFactory.CreateClient(BlobClientName), blobReq, GitHubOperations.JobLogBlob, HttpCompletionOption.ResponseHeadersRead, ct, notFoundIsExpected: true);
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
        using var res = await SendApiAsync(Api, req, GitHubOperations.FileGet, HttpCompletionOption.ResponseContentRead, ct, notFoundIsExpected: true);
        if (res.StatusCode == HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode) throw Describe(res, path);
        return await res.Content.ReadAsStringAsync(ct);
    }

    // ------------------------------------------------------------------ rate limit & connection

    public async Task<RateLimitInfo?> GetRateLimitAsync(CancellationToken ct)
    {
        try
        {
            var (body, _, _) = await GetPageAsync("rate_limit", ct, GitHubOperations.RateLimitGet, useEtag: false);
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

        async Task<ConnectionCheck> Optional(string name, Func<Task<(bool Ok, string Detail)>> op)
        {
            try { var (ok, detail) = await op(); return new ConnectionCheck(name, ok ? CheckStatus.Ok : CheckStatus.Skipped, detail); }
            catch (CiProviderException ex) { return new ConnectionCheck(name, CheckStatus.Skipped, ex.Message); }
        }

        checks.Add(await Run("Authentication", async () =>
        {
            var (body, _, _) = await GetPageAsync("user", ct, GitHubOperations.UserGet, useEtag: false);
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
                foreach (var path in GroupConfigContract.SearchPaths)
                    if (await GetFileAsync(t, path, ct) is not null) return $"Read {path} from {t}.";
                return $"Contents readable ({t} has no {GroupConfigContract.PreferredPath}, which is fine).";
            }, "Grant Contents: Read-only to read .github/ciworkbench.yml and workflow comments (grouping by YAML comment will be unavailable)."));

            // Runner Insights works from Actions: Read alone. These two are optional extras and must never turn the connection unhealthy.
            checks.Add(await Optional("Variables read (optional)", async () =>
            {
                var v = await GetActionsVariablesAsync(t, ct);
                return v.IsAvailable ? (true, $"Read {v.Values.Count} Actions variables; configured runner labels can be resolved.") : (false, v.Reason ?? "Not available.");
            }));
            checks.Add(await Optional("Billing usage (optional)", async () =>
            {
                var now = DateTimeOffset.UtcNow;
                var b = await GetBillingUsageAsync(t.Owner, null, now.Year, now.Month, ct);
                return b.IsAvailable ? (true, $"GitHub billing usage is readable for {t.Owner}: verified usage will be shown.") : (false, b.Reason ?? "Not available.");
            }));
        }
        return new ConnectionReport(account, checks);
    }

    // ------------------------------------------------------------------ plumbing

    /// <summary>GET with ETag revalidation. Returns body, next-page URL and the estimated last page.</summary>
    private async Task<(string Body, string? Next, int? LastPage)> GetPageAsync(string url, CancellationToken ct, string operation, bool useEtag = true)
    {
        ct.ThrowIfCancellationRequested();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var cacheKey = credentials.Identity + "|" + url;
        (string ETag, string Body, string? Next, int? Last)? cached = useEtag && _etags.TryGetValue(cacheKey, out var c) ? c : null;
        if (cached is { } hit) req.Headers.TryAddWithoutValidation("If-None-Match", hit.ETag);
        HttpResponseMessage res;
        try { res = await SendApiAsync(Api, req, operation, HttpCompletionOption.ResponseContentRead, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new CiProviderException("GitHub did not respond in time.", null); }
        catch (HttpRequestException ex) { throw new CiProviderException($"Could not reach GitHub: {ex.Message}", null, ex); }
        using (res)
        {
            var (next, last) = ParseLink(res);
            if (res.StatusCode == HttpStatusCode.NotModified && cached is { } notModified)
            {
                metrics?.RecordCache(operation, hit: true);
                return (notModified.Body, notModified.Next, notModified.Last);
            }
            if (useEtag) metrics?.RecordCache(operation, hit: false);
            if (!res.IsSuccessStatusCode) throw Describe(res, url);
            var body = await res.Content.ReadAsStringAsync(ct);
            if (useEtag && res.Headers.ETag is { } e) _etags[cacheKey] = (e.ToString(), body, next, last);
            return (body, next, last);
        }
    }

    /// <summary>
    /// Single instrumented send path: one low-cardinality span and duration/error metrics per logical request.
    /// URLs, repository names, headers and bodies are deliberately never attached to telemetry or logs.
    /// </summary>
    private async Task<HttpResponseMessage> SendApiAsync(HttpClient client, HttpRequestMessage req, string operation, HttpCompletionOption completion, CancellationToken ct, bool notFoundIsExpected = false)
    {
        CiWorkbenchTelemetry.Suppress(req);
        using var activity = CiWorkbenchTelemetry.Source.StartActivity("github.request", ActivityKind.Client);
        activity?.SetTag("github.operation", operation);
        var sw = Stopwatch.StartNew();
        try
        {
            var res = await client.SendAsync(req, completion, ct);
            sw.Stop();
            var code = (int)res.StatusCode;
            metrics?.RecordRequest(operation, $"{code / 100}xx", sw.Elapsed);
            activity?.SetTag("http.response.status_code", code);
            var expectedMissing = notFoundIsExpected && res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone;
            if (code >= 400 && !expectedMissing)
            {
                var errorType = GitHubMetrics.ClassifyError(res, tracker);
                metrics?.RecordError(operation, errorType);
                activity?.SetTag("github.error_type", errorType);
                activity?.SetStatus(ActivityStatusCode.Error);
                if (logger is not null) Log.RequestFailed(logger, operation, errorType, code);
            }
            return res;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is TaskCanceledException or HttpRequestException)
        {
            sw.Stop();
            var errorType = ex is TaskCanceledException ? "timeout" : "network";
            metrics?.RecordRequest(operation, "error", sw.Elapsed);
            metrics?.RecordError(operation, errorType);
            activity?.SetTag("github.error_type", errorType);
            activity?.SetStatus(ActivityStatusCode.Error);
            if (logger is not null) Log.RequestAborted(logger, operation, errorType);   // the exception message (which may name a host/URL) is intentionally not logged
            throw;
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
    internal sealed record OwnerDto(string Login, string? Type = null);
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
    internal sealed record RunsEnvelope([property: JsonPropertyName("workflow_runs")] List<RunDto> WorkflowRuns, [property: JsonPropertyName("total_count")] int? TotalCount = null);
    internal sealed record StepDto(int Number, string Name, string? Status, string? Conclusion);
    internal sealed record JobDto(long Id, string Name, string? Status, string? Conclusion, [property: JsonPropertyName("started_at")] DateTimeOffset? StartedAt,
        [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt, List<StepDto>? Steps, [property: JsonPropertyName("html_url")] string? HtmlUrl,
        List<string>? Labels = null, [property: JsonPropertyName("runner_name")] string? RunnerName = null, [property: JsonPropertyName("runner_group_name")] string? RunnerGroupName = null,
        [property: JsonPropertyName("run_attempt")] int RunAttempt = 1, [property: JsonPropertyName("created_at")] DateTimeOffset? CreatedAt = null);
    internal sealed record VariableDto(string Name, string? Value);
    internal sealed record VariablesEnvelope(List<VariableDto>? Variables);
    internal sealed record BillingItemDto(string? Product, string? Sku, double Quantity, string? UnitType, double PricePerUnit, double GrossAmount, double DiscountAmount, double NetAmount);
    internal sealed record BillingEnvelope(List<BillingItemDto>? UsageItems);
    internal sealed record JobsEnvelope(List<JobDto> Jobs);
    internal sealed record RateCore(int Limit, int Remaining, long Reset);
    internal sealed record RateResources(RateCore Core);
    internal sealed record RateEnvelope(RateResources Resources);
}

