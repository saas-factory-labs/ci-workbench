using System.Net;
using CIWorkbench.Core.Models;
using CIWorkbench.Core.Runners;
using System.Threading.Tasks;

namespace CIWorkbench.GitHub.Tests;

public class RunnerInsightsGitHubTests
{
    private const string JobsBody = """
        {"total_count":2,"jobs":[
         {"id":11,"name":"build","status":"completed","conclusion":"success","created_at":"2026-10-24T09:59:40Z","started_at":"2026-10-24T10:00:00Z","completed_at":"2026-10-24T10:03:10Z","html_url":"https://github.com/j/11",
          "labels":["avrea-ubuntu-latest-4-vcpu"],"runner_name":"avrea-7","runner_group_name":"Default","run_attempt":2,"steps":[]},
         {"id":12,"name":"test","status":"completed","conclusion":"failure","started_at":"2026-10-24T10:00:00Z","completed_at":"2026-10-24T10:01:00Z","labels":["ubuntu-latest"],"runner_name":"GitHub Actions 3","runner_group_name":"GitHub Actions","run_attempt":1}]}
        """;

    [Test]
    public async Task Jobs_carry_runner_labels_and_the_attempt_listing_asks_for_every_attempt()
    {
        var (p, api, _, _, _) = H.Build(_ => H.Json(JobsBody));
        var all = await p.ListJobAttemptsAsync(H.Repo, 5, default);
        await Assert.That(api.Calls.Single().RequestUri!.Query).Contains("filter=all");
        await Assert.That(all[0].Labels![0]).IsEqualTo("avrea-ubuntu-latest-4-vcpu");
        await Assert.That(all[0].RunAttempt).IsEqualTo(2);
        await Assert.That(all[0].RunnerGroup).IsEqualTo("Default");
        await Assert.That(all[0].CreatedAt).IsNotNull();                          // queue time is known, and is not part of the executed duration
        await Assert.That(all[0].Duration!.Value.TotalSeconds).IsEqualTo(190d);
        var latest = await p.ListJobsAsync(H.Repo, 5, default);
        await Assert.That(api.Calls[1].RequestUri!.Query).Contains("filter=latest");
        await Assert.That(latest.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Billing_uses_the_current_org_endpoint_not_the_deprecated_one_and_keeps_only_actions_lines()
    {
        var (p, api, _, _, _) = H.Build(_ => H.Json("""
            {"usageItems":[
             {"date":"2026-10-02","product":"Actions","sku":"Actions Linux","quantity":100,"unitType":"Minutes","pricePerUnit":0.006,"grossAmount":0.6,"discountAmount":0.6,"netAmount":0},
             {"date":"2026-10-03","product":"Actions","sku":"Actions Linux","quantity":50,"unitType":"Minutes","pricePerUnit":0.006,"grossAmount":0.3,"discountAmount":0.1,"netAmount":0.2},
             {"date":"2026-10-03","product":"Copilot","sku":"Copilot Seat","quantity":1,"unitType":"Seats","pricePerUnit":19,"grossAmount":19,"discountAmount":0,"netAmount":19}]}
            """));
        var b = await p.GetBillingUsageAsync("acme", "Organization", 2026, 10, default);
        var url = api.Calls.Single().RequestUri!;
        await Assert.That(url.AbsolutePath).IsEqualTo("/organizations/acme/settings/billing/usage");
        await Assert.That(url.Query).Contains("year=2026").And.Contains("month=10");
        await Assert.That(b.Access).IsEqualTo(BillingAccess.Available);
        var line = b.Lines.Single();                                               // the Copilot line is not Actions
        await Assert.That(line.Quantity).IsEqualTo(150d);
        await Assert.That(line.HasPaidUsage).IsTrue();                             // net > 0: the free allowance is used up
    }

    [Test]
    public async Task A_personal_account_uses_the_user_billing_endpoint()
    {
        var (p, api, _, _, _) = H.Build(_ => H.Json("{\"usageItems\":[]}"));
        await p.GetBillingUsageAsync("someone", "User", 2026, 10, default);
        await Assert.That(api.Calls.Single().RequestUri!.AbsolutePath).IsEqualTo("/users/someone/settings/billing/usage");
    }

    [Test]
    public async Task Missing_billing_permission_is_a_result_with_an_actionable_reason_not_an_exception()
    {
        var (p, _, _, _, _) = H.Build(_ => H.Json("{\"message\":\"Resource not accessible\"}", HttpStatusCode.Forbidden));
        var b = await p.GetBillingUsageAsync("acme", "Organization", 2026, 10, default);
        await Assert.That(b.Access).IsEqualTo(BillingAccess.PermissionMissing);
        await Assert.That(b.Reason).Contains("Administration: Read-only");
        var (p2, _, _, _, _) = H.Build(_ => H.Json("{\"message\":\"Resource not accessible\"}", HttpStatusCode.Unauthorized));
        await Assert.That((await p2.GetBillingUsageAsync("someone", "User", 2026, 10, default)).Reason).Contains("Plan: Read-only");
    }

    [Test]
    public async Task A_404_from_billing_means_not_available_and_a_server_error_means_failed()
    {
        var (p, _, _, _, _) = H.Build(_ => H.Json("{}", HttpStatusCode.NotFound));
        await Assert.That((await p.GetBillingUsageAsync("acme", "Organization", 2026, 10, default)).Access).IsEqualTo(BillingAccess.NotAvailable);
        var (p2, _, _, _, _) = H.Build(_ => H.Json("{}", HttpStatusCode.BadGateway), maxWaitSeconds: 0);
        await Assert.That((await p2.GetBillingUsageAsync("acme", "Organization", 2026, 10, default)).Access).IsEqualTo(BillingAccess.Failed);
    }

    [Test]
    public async Task Variables_merge_repository_over_organization_and_a_refusal_is_reported()
    {
        var (p, _, _, _, _) = H.Build(req => req.RequestUri!.AbsolutePath.EndsWith("organization-variables")
            ? H.Json("{\"variables\":[{\"name\":\"CI_RUNNER_DEFAULT\",\"value\":\"[\\\"org\\\"]\"},{\"name\":\"ONLY_ORG\",\"value\":\"x\"}]}")
            : H.Json("{\"variables\":[{\"name\":\"CI_RUNNER_DEFAULT\",\"value\":\"[\\\"repo\\\"]\"}]}"));
        var v = await p.GetActionsVariablesAsync(H.Repo, default);
        await Assert.That(v.IsAvailable).IsTrue();
        await Assert.That(v.Values["CI_RUNNER_DEFAULT"]).IsEqualTo("[\"repo\"]");
        await Assert.That(v.Values["ONLY_ORG"]).IsEqualTo("x");

        var (denied, _, _, _, _) = H.Build(_ => H.Json("{}", HttpStatusCode.Forbidden));
        var d = await denied.GetActionsVariablesAsync(H.Repo, default);
        await Assert.That(d.Access).IsEqualTo(VariablesAccess.PermissionMissing);
        await Assert.That(d.Reason).Contains("Variables: Read-only");
    }

    [Test]
    public async Task Optional_runner_checks_are_skipped_not_failed_so_actions_read_alone_stays_healthy()
    {
        var (p, _, _, _, _) = H.Build(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            if (path == "/user") return H.Json("{\"login\":\"someone\"}");
            if (path == "/user/repos") return H.Json("[{\"name\":\"demo\",\"owner\":{\"login\":\"example-org\",\"type\":\"Organization\"},\"private\":true}]");
            if (path.EndsWith("/actions/workflows")) return H.Json("{\"workflows\":[]}");
            if (path.Contains("/contents/")) return H.Json("{}", HttpStatusCode.NotFound);
            return H.Json("{}", HttpStatusCode.Forbidden);                        // variables and billing refused
        });
        var report = await p.CheckConnectionAsync(null, default);
        await Assert.That(report.IsHealthy).IsTrue();
        await Assert.That(report.Checks.Single(c => c.Name.StartsWith("Variables")).Status).IsEqualTo(CheckStatus.Skipped);
        var billing = report.Checks.Single(c => c.Name.StartsWith("Billing"));
        await Assert.That(billing.Status).IsEqualTo(CheckStatus.Skipped);
        await Assert.That(billing.Detail).Contains("Administration");
    }

    [Test]
    public async Task Repository_discovery_records_the_owner_type_for_billing()
    {
        var (p, _, _, _, _) = H.Build(_ => H.Json("[{\"name\":\"demo\",\"owner\":{\"login\":\"example-org\",\"type\":\"Organization\"},\"private\":true}]"));
        await foreach (var page in p.ListRepositoriesAsync(default)) { await Assert.That(page.Items.Single().OwnerType).IsEqualTo("Organization"); break; }
    }

    private static string RunsPage(int total, params long[] ids) => "{\"total_count\":" + total + ",\"workflow_runs\":[" + string.Join(",", ids.Select(i => H.RunJson(i))) + "]}";

    [Test]
    public async Task Period_listing_filters_by_creation_date_pages_through_everything_and_reports_complete()
    {
        var (p, api, _, _, _) = H.Build(req => req.RequestUri!.Query.Contains("page=2")
            ? H.Json(RunsPage(3, 3))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(RunsPage(3, 1, 2), System.Text.Encoding.UTF8, "application/json"), Headers = { { "Link", "<https://api.github.com/repos/example-org/demo/actions/runs?created=x&per_page=100&page=2>; rel=\"next\"" } } });
        var period = await p.ListRunsSinceAsync(H.Repo, new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), 1000, default);
        await Assert.That(period.Runs.Select(r => r.Id).ToArray()).IsEquivalentTo(new long[] { 1, 2, 3 });
        await Assert.That(period.Complete).IsTrue();
        await Assert.That(period.TotalCount).IsEqualTo(3);
        await Assert.That(api.Calls[0].RequestUri!.Query).Contains("created=%3E%3D2026-10-01T00%3A00%3A00Z").And.Contains("per_page=100");
        await Assert.That(api.Calls.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Period_listing_is_incomplete_when_the_caller_cap_or_githubs_1000_result_cap_cuts_it_short()
    {
        var (capped, _, _, _, _) = H.Build(_ => H.Json(RunsPage(500, 1, 2)));
        await Assert.That((await capped.ListRunsSinceAsync(H.Repo, H.Now.AddDays(-30), 1000, default)).Complete).IsFalse();      // 500 exist, only 2 listed
        var (githubCap, _, _, _, _) = H.Build(_ => H.Json(RunsPage(1000, 1, 2)));
        var many = await githubCap.ListRunsSinceAsync(H.Repo, H.Now.AddDays(-30), 1000, default);
        await Assert.That(many.Complete).IsFalse();                                                                            // a total at GitHub's cap cannot be trusted as the whole set
        var (small, _, _, _, _) = H.Build(_ => H.Json(RunsPage(2, 1, 2)));
        await Assert.That((await small.ListRunsSinceAsync(H.Repo, H.Now.AddDays(-30), 1000, default)).Complete).IsTrue();
    }

    [Test]
    public async Task Public_repository_billing_response_with_only_discounted_lines_is_not_treated_as_a_charge()
    {
        var (p, _, _, _, _) = H.Build(_ => H.Json("{\"usageItems\":[{\"product\":\"Actions\",\"sku\":\"Actions Linux\",\"quantity\":300,\"unitType\":\"Minutes\",\"pricePerUnit\":0.006,\"grossAmount\":1.8,\"discountAmount\":1.8,\"netAmount\":0}]}"));
        var b = await p.GetBillingUsageAsync("acme", "Organization", 2026, 10, default);
        await Assert.That(b.StandardMinutesCharged()).IsFalse();                  // a zero-dollar bill proves nothing about what remains
        await Assert.That(b.StandardWeightedMinutes()).IsEqualTo(300d);          // but the gross usage was real
    }
}
