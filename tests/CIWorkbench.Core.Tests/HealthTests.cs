using CIWorkbench.Core.Health;
using CIWorkbench.Core.Models;
using System.Threading.Tasks;

namespace CIWorkbench.Core.Tests;

public class HealthTests
{
    private const string P = ".github/workflows/ci.yml";
    private static WorkflowRunInfo R(long id, RunState s, int hoursAgo, string branch = "main") => T.Run(id, P, s, hoursAgo, branch);
    private static WorkflowHealth Assess(string? def, bool? recurring, params WorkflowRunInfo[] runs) => WorkflowHealthAnalyzer.Assess(runs, def, recurring, T.Now);

    [Test]
    public async Task Latest_failure_is_failing_now_and_needs_attention()
    {
        var h = Assess("main", null, R(1, RunState.Failure, 2), R(2, RunState.Success, 30));
        await Assert.That(h.Kind).IsEqualTo(HealthKind.FailingNow);
        await Assert.That(h.NeedsAttention).IsTrue();
        await Assert.That(h.Recurring).IsFalse();       // no analysis => not claimed
    }

    [Test]
    public async Task Newer_success_supersedes_older_failure_and_is_recovered_not_failing()
    {
        var h = Assess("main", null, R(1, RunState.Success, 2), R(2, RunState.Failure, 30), R(3, RunState.Success, 60));
        await Assert.That(h.Kind).IsEqualTo(HealthKind.Recovered);
        await Assert.That(h.NeedsAttention).IsFalse();
    }

    [Test]
    public async Task Success_with_no_earlier_failure_is_healthy()
    {
        await Assert.That(Assess("main", null, R(1, RunState.Success, 2), R(2, RunState.Success, 30)).Kind).IsEqualTo(HealthKind.Healthy);
    }

    [Test]
    public async Task Failure_beyond_the_recovery_window_is_history_not_recovery()
    {
        var runs = new List<WorkflowRunInfo> { R(1, RunState.Success, 1), R(99, RunState.Failure, 500) };
        for (var i = 0; i < WorkflowHealthAnalyzer.RecoveryWindowVerdicts; i++) runs.Add(R(10 + i, RunState.Success, 10 + i * 5));
        runs = [.. runs.OrderBy(r => r.Id == 99 ? 1 : 0)];
        await Assert.That(WorkflowHealthAnalyzer.Assess(runs, "main", null, T.Now).Kind).IsEqualTo(HealthKind.Healthy);
    }

    [Test]
    public async Task Cancelled_skipped_and_active_runs_never_decide_health()
    {
        var h = Assess("main", null, R(1, RunState.InProgress, 0), R(2, RunState.Cancelled, 1), R(3, RunState.Skipped, 2), R(4, RunState.Failure, 3));
        await Assert.That(h.Kind).IsEqualTo(HealthKind.FailingNow);
        await Assert.That(h.HasActiveRun).IsTrue();
        await Assert.That(h.Verdict!.Id).IsEqualTo(4);
    }

    [Test]
    public async Task Only_cancelled_or_active_runs_means_unknown()
    {
        await Assert.That(Assess("main", null, R(1, RunState.Cancelled, 1), R(2, RunState.Queued, 0)).Kind).IsEqualTo(HealthKind.Unknown);
        await Assert.That(Assess("main", null).Kind).IsEqualTo(HealthKind.Unknown);
    }

    [Test]
    public async Task Old_failure_is_stale_and_does_not_need_attention()
    {
        var h = Assess("main", null, R(1, RunState.Failure, 24 * 40));
        await Assert.That(h.Kind).IsEqualTo(HealthKind.FailingNow);
        await Assert.That(h.Stale).IsTrue();
        await Assert.That(h.NeedsAttention).IsFalse();
    }

    [Test]
    public async Task Feature_branch_failure_does_not_make_a_default_branch_workflow_fail()
    {
        var h = Assess("main", null, R(1, RunState.Failure, 1, "feat/x"), R(2, RunState.Success, 5, "main"));
        await Assert.That(h.Kind).IsEqualTo(HealthKind.Healthy);
        await Assert.That(h.ScopedToBranch).IsEqualTo("main");
    }

    [Test]
    public async Task Without_default_branch_data_or_default_runs_every_branch_counts()
    {
        await Assert.That(Assess(null, null, R(1, RunState.Failure, 1, "feat/x")).Kind).IsEqualTo(HealthKind.FailingNow);
        await Assert.That(Assess("main", null, R(1, RunState.Failure, 1, "feat/x")).Kind).IsEqualTo(HealthKind.FailingNow);
    }

    [Test]
    public async Task Recurring_requires_evidence_and_ranks_first()
    {
        var recurring = Assess("main", true, R(1, RunState.Failure, 2));
        var plain = Assess("main", false, R(1, RunState.Failure, 2));
        var healthy = Assess("main", null, R(1, RunState.Success, 2));
        await Assert.That(recurring.Recurring).IsTrue(); await Assert.That(plain.Recurring).IsFalse();
        await Assert.That(recurring.Priority < plain.Priority).IsTrue();
        await Assert.That(plain.Priority < healthy.Priority).IsTrue();
    }

    [Test]
    public async Task Recovery_is_not_flagged_recurring()
    {
        await Assert.That(Assess("main", true, R(1, RunState.Success, 2), R(2, RunState.Failure, 9)).Recurring).IsFalse();
    }

    [Test]
    public async Task Attention_kpi_separates_failing_stale_and_recovered()
    {
        var hs = new[]
        {
            Assess("main", null, R(1, RunState.Failure, 2)),
            Assess("main", null, R(1, RunState.Failure, 24 * 60)),
            Assess("main", null, R(1, RunState.Success, 2), R(2, RunState.Failure, 6)),
            Assess("main", null, R(1, RunState.Success, 2)),
        };
        var k = Kpis.Attention(hs);
        await Assert.That((k.FailingNow, k.Stale, k.Recovered, k.Total)).IsEqualTo((1, 1, 1, 4));
    }

    private static List<WorkflowRunInfo> Window(int count, Func<int, RunState> state, Func<int, TimeSpan>? dur = null) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var started = T.Now.AddHours(-i - 1);
            return new WorkflowRunInfo(i + 1, 1, P, "CI", i, 1, state(i), "push", "main", "abc", null, "a", started, started, started + (dur?.Invoke(i) ?? TimeSpan.FromMinutes(5)), "https://x.test/");
        }).ToList();

    [Test]
    public async Task Success_rate_excludes_cancelled_and_skipped()
    {
        var k = Kpis.SuccessRate(Window(10, i => i switch { < 6 => RunState.Success, < 8 => RunState.Failure, 8 => RunState.Cancelled, _ => RunState.Skipped }));
        await Assert.That(k.Rate).IsEqualTo(0.75);
        await Assert.That((k.Succeeded, k.Failed, k.RunsInScope)).IsEqualTo((6, 2, 10));
        await Assert.That(k.Trend).IsNull();            // too little data for a comparison
    }

    [Test]
    public async Task Success_rate_trend_compares_newer_half_with_older_half_only_with_enough_data()
    {
        // newest 15 all succeed, oldest 15 half fail => +50 points
        var runs = Window(30, i => i < 15 ? RunState.Success : i % 2 == 0 ? RunState.Success : RunState.Failure);
        var trend = Kpis.SuccessRate(runs).Trend;
        await Assert.That(trend).IsNotNull();
        await Assert.That(trend!.RecentCount).IsEqualTo(15);
        await Assert.That(trend.Delta).IsEqualTo(100 - 7 / 15.0 * 100);
        await Assert.That(Kpis.SuccessRate(Window(19, _ => RunState.Success)).Trend).IsNull();
    }

    [Test]
    public async Task Success_rate_is_null_without_verdicts()
    {
        await Assert.That(Kpis.SuccessRate(Window(3, _ => RunState.InProgress)).Rate).IsNull();
        await Assert.That(Kpis.SuccessRate([]).Rate).IsNull();
    }

    [Test]
    public async Task Average_duration_ignores_cancelled_and_active_runs()
    {
        var runs = Window(4, i => i switch { 0 => RunState.Success, 1 => RunState.Failure, 2 => RunState.Cancelled, _ => RunState.InProgress },
            i => i == 2 ? TimeSpan.FromHours(5) : TimeSpan.FromMinutes(i == 0 ? 2 : 4));
        var k = Kpis.AverageDuration(runs);
        await Assert.That(k.Average).IsEqualTo(TimeSpan.FromMinutes(3));
        await Assert.That(k.Samples).IsEqualTo(2);
        await Assert.That(k.Trend).IsNull();
    }

    [Test]
    public async Task Average_duration_trend_needs_two_full_periods()
    {
        var runs = Window(30, _ => RunState.Success, i => TimeSpan.FromMinutes(i < 15 ? 10 : 5));
        await Assert.That(Kpis.AverageDuration(runs).Trend!.Delta).IsEqualTo(300);   // newer runs are 5 minutes slower
        await Assert.That(Kpis.AverageDuration(Window(12, _ => RunState.Success)).Trend).IsNull();
    }
}