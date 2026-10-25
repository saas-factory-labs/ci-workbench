using CIWorkbench.Core.Health;
using CIWorkbench.Core.Models;
using CIWorkbench.Core.Overview;
using System.Threading.Tasks;

namespace CIWorkbench.Core.Tests;

public class PortfolioTriageTests
{
    private const string P = ".github/workflows/ci.yml";
    private static readonly RepoRef Ref = new("example-org", "demo");
    private static readonly RepositoryInfo Repo = new(Ref, "d", "main", false, "https://example.test", T.Now);
    private static WorkflowRunInfo R(long id, RunState s, int hoursAgo, string branch = "main", string ev = "push") => T.Run(id, P, s, hoursAgo, branch, ev);

    private static RepoSnapshot Snap(params WorkflowRunInfo[] runs) => new(Repo, [T.Wf(P, "CI", 1)], runs, T.Now);
    private static RepoTriage Analyze(RepoSnapshot s, TriageScope? scope = null) => PortfolioTriage.Analyze(s, T.Now, scope);

    [Test]
    public async Task A_repository_that_was_never_scanned_is_not_part_of_the_analysis_and_a_scan_without_verdicts_is_not_healthy()
    {
        var empty = Analyze(Snap());                                 // scanned, but no runs at all
        await Assert.That(empty.Status).IsEqualTo(RepoStatus.NoRuns);
        await Assert.That(empty.Workflows.Single().IsUnknown).IsTrue();
        var summary = PortfolioTriage.Summarize([empty], totalRepositories: 5, notChecked: 4);
        await Assert.That(summary.NotChecked).IsEqualTo(4);
        await Assert.That(summary.UnverifiedRepositories).IsEqualTo(5);          // 4 unscanned + 1 scanned without a verdict, none counted as healthy
    }

    [Test]
    public async Task Failing_streak_marks_recurring_but_never_claims_a_log_based_series()
    {
        var t = Analyze(Snap(R(1, RunState.Failure, 1), R(2, RunState.Cancelled, 2), R(3, RunState.Failure, 3), R(4, RunState.Success, 9)));
        var w = t.Workflows.Single();
        await Assert.That(w.FailureStreak).IsEqualTo(2);                         // cancelled run neither extends nor breaks it
        await Assert.That(w.IsRecurring).IsTrue();
        await Assert.That(w.Health.Recurring).IsFalse();                         // "same error signature" needs log analysis, which the dashboard never runs
        await Assert.That(t.Status).IsEqualTo(RepoStatus.Failing);
    }

    [Test]
    public async Task One_failure_needs_attention_but_is_not_recurring_and_a_later_success_recovers_it()
    {
        var failing = Analyze(Snap(R(1, RunState.Failure, 1), R(2, RunState.Success, 5))).Workflows.Single();
        await Assert.That(failing.NeedsAttention).IsTrue();
        await Assert.That(failing.IsRecurring).IsFalse();
        var recovered = Analyze(Snap(R(1, RunState.Success, 1), R(2, RunState.Failure, 5))).Workflows.Single();
        await Assert.That(recovered.IsRecovered).IsTrue();
        await Assert.That(recovered.NeedsAttention).IsFalse();
    }

    [Test]
    public async Task Stale_failures_stay_in_needs_attention_but_not_in_failing_now()
    {
        var t = Analyze(Snap(R(1, RunState.Failure, 24 * 30)));
        await Assert.That(PortfolioTriage.Rank([t], TriageFilter.NeedsAttention)).HasSingleItem();
        await Assert.That(PortfolioTriage.Rank([t], TriageFilter.FailingNow)).IsEmpty();
        await Assert.That(t.Workflows.Single().IsStaleFailure).IsTrue();
    }

    [Test]
    public async Task Counts_match_the_workspace_health_for_the_same_runs()
    {
        // Same inputs, same analyzer: the dashboard and the repository page must never disagree about "failing".
        var runs = new[] { R(1, RunState.Failure, 1), R(2, RunState.Failure, 4), R(3, RunState.Success, 8) };
        var viaDashboard = Analyze(Snap(runs)).Failing;
        var viaWorkspace = WorkflowHealthAnalyzer.Assess(runs, "main", null, T.Now).NeedsAttention ? 1 : 0;
        await Assert.That(viaDashboard).IsEqualTo(viaWorkspace);
    }

    [Test]
    public async Task Default_branch_decides_the_verdict_and_a_branch_scope_overrides_it()
    {
        var runs = new[] { R(1, RunState.Failure, 1, "feat/x"), R(2, RunState.Success, 3, "main") };
        await Assert.That(Analyze(Snap(runs)).Failing).IsEqualTo(0);                                       // main is green
        await Assert.That(Analyze(Snap(runs), new TriageScope(Branch: "feat/x")).Failing).IsEqualTo(1);     // scoped to the failing branch
        await Assert.That(Analyze(Snap(runs), new TriageScope(Branch: "release")).Workflows).IsEmpty();    // no runs on it: omitted, not "healthy"
    }

    [Test]
    public async Task Event_scope_keeps_only_matching_runs()
    {
        var runs = new[] { R(1, RunState.Failure, 1, ev: "schedule"), R(2, RunState.Success, 2, ev: "push") };
        await Assert.That(Analyze(Snap(runs), new TriageScope(Event: "push")).Failing).IsEqualTo(0);
        await Assert.That(Analyze(Snap(runs), new TriageScope(Event: "schedule")).Failing).IsEqualTo(1);
    }

    [Test]
    public async Task Ranking_puts_repeated_failures_before_single_ones_and_fresh_before_stale()
    {
        var a = new RepositoryInfo(new RepoRef("o", "a"), null, "main", false, "u", T.Now);
        var b = new RepositoryInfo(new RepoRef("o", "b"), null, "main", false, "u", T.Now);
        var c = new RepositoryInfo(new RepoRef("o", "c"), null, "main", false, "u", T.Now);
        RepoTriage Of(RepositoryInfo r, params WorkflowRunInfo[] runs) => PortfolioTriage.Analyze(new RepoSnapshot(r, [T.Wf(P, "CI", 1)], runs, T.Now), T.Now);
        var order = PortfolioTriage.Rank([Of(a, R(1, RunState.Failure, 1)), Of(b, R(2, RunState.Failure, 24 * 40)), Of(c, R(3, RunState.Failure, 2), R(4, RunState.Failure, 3))], TriageFilter.NeedsAttention)
            .Select(w => w.Repo.Name).ToArray();
        await Assert.That(string.Join(",", order)).IsEqualTo("c,a,b");
    }

    [Test]
    public async Task Summary_counts_repositories_and_workflows_separately()
    {
        var t = Analyze(Snap(R(1, RunState.Failure, 1), R(2, RunState.Failure, 2), R(3, RunState.InProgress, 0)));
        var s = PortfolioTriage.Summarize([t], 1, 0);
        await Assert.That(s.RepositoriesNeedingAttention).IsEqualTo(1);
        await Assert.That(s.FailingWorkflows).IsEqualTo(1);
        await Assert.That(s.RecurringWorkflows).IsEqualTo(1);
        await Assert.That(s.RunningWorkflows).IsEqualTo(1);
    }

    [Test]
    public async Task Failure_hint_is_bounded_and_reads_no_logs()
    {
        var steps = new[] { new StepInfo(1, "Checkout", RunState.Success), new StepInfo(2, new string('x', 300), RunState.Failure) };
        var jobs = new[] { new JobInfo(1, "build", RunState.Failure, T.Now, T.Now, steps, "u") };
        var hint = PortfolioTriage.FailureHint(jobs)!;
        await Assert.That(hint.StartsWith("build › ")).IsTrue();
        await Assert.That(hint.Length).IsLessThanOrEqualTo(125);
        await Assert.That(PortfolioTriage.FailureHint([new JobInfo(2, "ok", RunState.Success, null, null, [], "u")])).IsNull();
        await Assert.That(PortfolioTriage.Headline("first line\nsecond line")).IsEqualTo("first line");
    }
}
