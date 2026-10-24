using CIWorkbench.Core.Health;
using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Tests;

public class HealthTests
{
    private const string P = ".github/workflows/ci.yml";
    private static WorkflowRunInfo R(long id, RunState s, int hoursAgo, string branch = "main") => T.Run(id, P, s, hoursAgo, branch);
    private static WorkflowHealth Assess(string? def, bool? recurring, params WorkflowRunInfo[] runs) => WorkflowHealthAnalyzer.Assess(runs, def, recurring, T.Now);

    [Fact]
    public void Latest_failure_is_failing_now_and_needs_attention()
    {
        var h = Assess("main", null, R(1, RunState.Failure, 2), R(2, RunState.Success, 30));
        Assert.Equal(HealthKind.FailingNow, h.Kind);
        Assert.True(h.NeedsAttention);
        Assert.False(h.Recurring);       // no analysis => not claimed
    }

    [Fact]
    public void Newer_success_supersedes_older_failure_and_is_recovered_not_failing()
    {
        var h = Assess("main", null, R(1, RunState.Success, 2), R(2, RunState.Failure, 30), R(3, RunState.Success, 60));
        Assert.Equal(HealthKind.Recovered, h.Kind);
        Assert.False(h.NeedsAttention);
    }

    [Fact]
    public void Success_with_no_earlier_failure_is_healthy()
    {
        Assert.Equal(HealthKind.Healthy, Assess("main", null, R(1, RunState.Success, 2), R(2, RunState.Success, 30)).Kind);
    }

    [Fact]
    public void Failure_beyond_the_recovery_window_is_history_not_recovery()
    {
        var runs = new List<WorkflowRunInfo> { R(1, RunState.Success, 1), R(99, RunState.Failure, 500) };
        for (var i = 0; i < WorkflowHealthAnalyzer.RecoveryWindowVerdicts; i++) runs.Add(R(10 + i, RunState.Success, 10 + i * 5));
        runs = [.. runs.OrderBy(r => r.Id == 99 ? 1 : 0)];
        Assert.Equal(HealthKind.Healthy, WorkflowHealthAnalyzer.Assess(runs, "main", null, T.Now).Kind);
    }

    [Fact]
    public void Cancelled_skipped_and_active_runs_never_decide_health()
    {
        var h = Assess("main", null, R(1, RunState.InProgress, 0), R(2, RunState.Cancelled, 1), R(3, RunState.Skipped, 2), R(4, RunState.Failure, 3));
        Assert.Equal(HealthKind.FailingNow, h.Kind);
        Assert.True(h.HasActiveRun);
        Assert.Equal(4, h.Verdict!.Id);
    }

    [Fact]
    public void Only_cancelled_or_active_runs_means_unknown()
    {
        Assert.Equal(HealthKind.Unknown, Assess("main", null, R(1, RunState.Cancelled, 1), R(2, RunState.Queued, 0)).Kind);
        Assert.Equal(HealthKind.Unknown, Assess("main", null).Kind);
    }

    [Fact]
    public void Old_failure_is_stale_and_does_not_need_attention()
    {
        var h = Assess("main", null, R(1, RunState.Failure, 24 * 40));
        Assert.Equal(HealthKind.FailingNow, h.Kind);
        Assert.True(h.Stale);
        Assert.False(h.NeedsAttention);
    }

    [Fact]
    public void Feature_branch_failure_does_not_make_a_default_branch_workflow_fail()
    {
        var h = Assess("main", null, R(1, RunState.Failure, 1, "feat/x"), R(2, RunState.Success, 5, "main"));
        Assert.Equal(HealthKind.Healthy, h.Kind);
        Assert.Equal("main", h.ScopedToBranch);
    }

    [Fact]
    public void Without_default_branch_data_or_default_runs_every_branch_counts()
    {
        Assert.Equal(HealthKind.FailingNow, Assess(null, null, R(1, RunState.Failure, 1, "feat/x")).Kind);
        Assert.Equal(HealthKind.FailingNow, Assess("main", null, R(1, RunState.Failure, 1, "feat/x")).Kind);
    }

    [Fact]
    public void Recurring_requires_evidence_and_ranks_first()
    {
        var recurring = Assess("main", true, R(1, RunState.Failure, 2));
        var plain = Assess("main", false, R(1, RunState.Failure, 2));
        var healthy = Assess("main", null, R(1, RunState.Success, 2));
        Assert.True(recurring.Recurring); Assert.False(plain.Recurring);
        Assert.True(recurring.Priority < plain.Priority);
        Assert.True(plain.Priority < healthy.Priority);
    }

    [Fact]
    public void Recovery_is_not_flagged_recurring()
    {
        Assert.False(Assess("main", true, R(1, RunState.Success, 2), R(2, RunState.Failure, 9)).Recurring);
    }

    [Fact]
    public void Attention_kpi_separates_failing_stale_and_recovered()
    {
        var hs = new[]
        {
            Assess("main", null, R(1, RunState.Failure, 2)),
            Assess("main", null, R(1, RunState.Failure, 24 * 60)),
            Assess("main", null, R(1, RunState.Success, 2), R(2, RunState.Failure, 6)),
            Assess("main", null, R(1, RunState.Success, 2)),
        };
        var k = Kpis.Attention(hs);
        Assert.Equal((1, 1, 1, 4), (k.FailingNow, k.Stale, k.Recovered, k.Total));
    }

    private static List<WorkflowRunInfo> Window(int count, Func<int, RunState> state, Func<int, TimeSpan>? dur = null) =>
        Enumerable.Range(0, count).Select(i =>
        {
            var started = T.Now.AddHours(-i - 1);
            return new WorkflowRunInfo(i + 1, 1, P, "CI", i, 1, state(i), "push", "main", "abc", null, "a", started, started, started + (dur?.Invoke(i) ?? TimeSpan.FromMinutes(5)), "https://x.test/");
        }).ToList();

    [Fact]
    public void Success_rate_excludes_cancelled_and_skipped()
    {
        var k = Kpis.SuccessRate(Window(10, i => i switch { < 6 => RunState.Success, < 8 => RunState.Failure, 8 => RunState.Cancelled, _ => RunState.Skipped }));
        Assert.Equal(0.75, k.Rate);
        Assert.Equal((6, 2, 10), (k.Succeeded, k.Failed, k.RunsInScope));
        Assert.Null(k.Trend);            // too little data for a comparison
    }

    [Fact]
    public void Success_rate_trend_compares_newer_half_with_older_half_only_with_enough_data()
    {
        // newest 15 all succeed, oldest 15 half fail => +50 points
        var runs = Window(30, i => i < 15 ? RunState.Success : i % 2 == 0 ? RunState.Success : RunState.Failure);
        var trend = Kpis.SuccessRate(runs).Trend;
        Assert.NotNull(trend);
        Assert.Equal(15, trend!.RecentCount);
        Assert.Equal(100 - 7 / 15.0 * 100, trend.Delta, 3);
        Assert.Null(Kpis.SuccessRate(Window(19, _ => RunState.Success)).Trend);
    }

    [Fact]
    public void Success_rate_is_null_without_verdicts()
    {
        Assert.Null(Kpis.SuccessRate(Window(3, _ => RunState.InProgress)).Rate);
        Assert.Null(Kpis.SuccessRate([]).Rate);
    }

    [Fact]
    public void Average_duration_ignores_cancelled_and_active_runs()
    {
        var runs = Window(4, i => i switch { 0 => RunState.Success, 1 => RunState.Failure, 2 => RunState.Cancelled, _ => RunState.InProgress },
            i => i == 2 ? TimeSpan.FromHours(5) : TimeSpan.FromMinutes(i == 0 ? 2 : 4));
        var k = Kpis.AverageDuration(runs);
        Assert.Equal(TimeSpan.FromMinutes(3), k.Average);
        Assert.Equal(2, k.Samples);
        Assert.Null(k.Trend);
    }

    [Fact]
    public void Average_duration_trend_needs_two_full_periods()
    {
        var runs = Window(30, _ => RunState.Success, i => TimeSpan.FromMinutes(i < 15 ? 10 : 5));
        Assert.Equal(300, Kpis.AverageDuration(runs).Trend!.Delta, 3);   // newer runs are 5 minutes slower
        Assert.Null(Kpis.AverageDuration(Window(12, _ => RunState.Success)).Trend);
    }
}
