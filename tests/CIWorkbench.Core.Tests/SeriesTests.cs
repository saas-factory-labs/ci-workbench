using TUnit.Assertions.Enums;
using CIWorkbench.Core.Failures;
using CIWorkbench.Core.Models;
using System.Threading.Tasks;

namespace CIWorkbench.Core.Tests;

public class SeriesTests
{
    private const string Wf = ".github/workflows/ci.yml";
    private static FailureSignature Sig(string s) => new(SignatureKind.Known, s, s);

    private static FailureObservation Obs(WorkflowRunInfo run, string job, string step, FailureSignature sig) =>
        new(run, job, step, sig, run.Id * 10);

    private static IReadOnlyList<FailureSeries> Build(IReadOnlyList<FailureObservation> o, IReadOnlyList<WorkflowRunInfo> runs, bool truncated = false) =>
        FailureSeriesBuilder.Build(T.Repo, o, runs, runs.Where(r => r.State == RunState.Failure).Select(r => r.Id).ToHashSet(), truncated);

    [Test] public async Task Same_job_step_signature_merges_with_count_first_and_last_seen()
    {
        var runs = new[] { T.Run(3, Wf, RunState.Failure, 1), T.Run(2, Wf, RunState.Failure, 25), T.Run(1, Wf, RunState.Success, 50) };
        var s = Build(runs.Where(r => r.State == RunState.Failure).Select(r => Obs(r, "test", "Run tests", Sig("boom"))).ToList(), runs).Single();
        await Assert.That(s.Count).IsEqualTo(2);
        await Assert.That(s.FirstSeen).IsEqualTo(T.Now.AddHours(-25));
        await Assert.That(s.LastSeen).IsEqualTo(T.Now.AddHours(-1));
        await Assert.That(s.Streak).IsEqualTo(2);
        await Assert.That(s.StreakIsLowerBound).IsFalse();
    }

    [Test] public async Task Same_job_and_step_but_different_errors_stay_separate()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1); var r2 = T.Run(2, Wf, RunState.Failure, 2);
        var s = Build([Obs(r1, "e2e", "Run", Sig("TimeoutError: a")), Obs(r2, "e2e", "Run", Sig("Error: Browser missing"))], [r1, r2]);
        await Assert.That(s.Count).IsEqualTo(2);
    }

    [Test] public async Task Same_message_in_different_jobs_or_workflows_does_not_merge()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1); var r2 = T.Run(2, ".github/workflows/other.yml", RunState.Failure, 2);
        await Assert.That(Build([Obs(r1, "a", "s", Sig("x")), Obs(r1, "b", "s", Sig("x")), Obs(r2, "a", "s", Sig("x"))], [r1, r2]).Count).IsEqualTo(3);
    }

    [Test] public async Task Unknown_never_merges_with_known_and_different_steps_do_not_merge()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1); var r2 = T.Run(2, Wf, RunState.Failure, 2);
        var s = Build([Obs(r1, "a", "one", FailureSignature.Unknown), Obs(r2, "a", "one", Sig("x")), Obs(r2, "a", "two", FailureSignature.Unknown)], [r1, r2]);
        await Assert.That(s.Count).IsEqualTo(3);
    }

    [Test] public async Task Two_unknowns_in_same_step_merge_and_are_flagged_cause_unknown()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1); var r2 = T.Run(2, Wf, RunState.Failure, 2);
        var s = Build([Obs(r1, "a", "one", FailureSignature.Unknown), Obs(r2, "a", "one", FailureSignature.Unknown)], [r1, r2]).Single();
        await Assert.That(s.CauseUnknown).IsTrue();
    }

    [Test] public async Task Unavailable_log_is_its_own_state_not_unknown()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1); var r2 = T.Run(2, Wf, RunState.Failure, 2);
        var s = Build([Obs(r1, "a", "one", FailureSignature.Unavailable), Obs(r2, "a", "one", FailureSignature.Unknown)], [r1, r2]);
        await Assert.That(s.Count).IsEqualTo(2);
        await Assert.That(s).Contains(x => x.LogUnavailable);
    }

    [Test] public async Task Matrix_variants_merge_and_are_recorded()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1); var r2 = T.Run(2, Wf, RunState.Failure, 2);
        var s = Build([Obs(r1, "build (ubuntu, 20)", "Test", Sig("x")), Obs(r2, "build (windows, 22)", "Test", Sig("x"))], [r1, r2]).Single();
        await Assert.That(s.Key.JobKey).IsEqualTo("build");
        await Assert.That(s.Variants).IsEquivalentTo(["build (ubuntu, 20)", "build (windows, 22)"], CollectionOrdering.Matching);
    }

    [Test] public async Task Missing_step_uses_job_setup_key()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1);
        await Assert.That(Build([Obs(r1, "a", "", Sig("x"))], [r1]).Single().Key.StepKey).IsEqualTo(FailureSeriesBuilder.JobSetupStep);
    }

    [Test] public async Task Cancelled_between_two_failures_does_not_break_streak()
    {
        var runs = new[] { T.Run(3, Wf, RunState.Failure, 1), T.Run(2, Wf, RunState.Cancelled, 2), T.Run(1, Wf, RunState.Failure, 3), T.Run(0, Wf, RunState.Success, 4) };
        var s = Build([Obs(runs[0], "a", "s", Sig("x")), Obs(runs[2], "a", "s", Sig("x"))], runs).Single();
        await Assert.That(s.Streak).IsEqualTo(2); await Assert.That(s.StreakIsLowerBound).IsFalse();
    }

    [Test] public async Task Success_in_between_breaks_streak_but_occurrences_keep_counting()
    {
        var runs = new[] { T.Run(3, Wf, RunState.Failure, 1), T.Run(2, Wf, RunState.Success, 2), T.Run(1, Wf, RunState.Failure, 3) };
        var s = Build([Obs(runs[0], "a", "s", Sig("x")), Obs(runs[2], "a", "s", Sig("x"))], runs).Single();
        await Assert.That(s.Streak).IsEqualTo(1); await Assert.That(s.Count).IsEqualTo(2);
    }

    [Test] public async Task Different_failure_in_between_breaks_the_streak()
    {
        var runs = new[] { T.Run(3, Wf, RunState.Failure, 1), T.Run(2, Wf, RunState.Failure, 2), T.Run(1, Wf, RunState.Failure, 3) };
        var obs = new[] { Obs(runs[0], "a", "s", Sig("x")), Obs(runs[1], "a", "s", Sig("y")), Obs(runs[2], "a", "s", Sig("x")) };
        await Assert.That(Build(obs, runs).Single(s => s.Key.Signature == "x").Streak).IsEqualTo(1);
    }

    [Test] public async Task Streak_reaching_window_edge_is_a_lower_bound()
    {
        var runs = new[] { T.Run(2, Wf, RunState.Failure, 1), T.Run(1, Wf, RunState.Failure, 2) };
        var s = Build(runs.Select(r => Obs(r, "a", "s", Sig("x"))).ToList(), runs, truncated: true).Single();
        await Assert.That(s.Streak).IsEqualTo(2); await Assert.That(s.StreakIsLowerBound).IsTrue();
    }

    [Test] public async Task Uninspected_failed_run_makes_streak_a_lower_bound()
    {
        var runs = new[] { T.Run(2, Wf, RunState.Failure, 1), T.Run(1, Wf, RunState.Failure, 2) };
        var s = FailureSeriesBuilder.Build(T.Repo, [Obs(runs[0], "a", "s", Sig("x"))], runs, new HashSet<long> { 2 }, false).Single();
        await Assert.That(s.Streak).IsEqualTo(1); await Assert.That(s.StreakIsLowerBound).IsTrue();
    }

    [Test] public async Task Feature_branch_runs_do_not_break_or_extend_a_main_branch_streak()
    {
        var runs = new[]
        {
            T.Run(4, Wf, RunState.Failure, 1), T.Run(3, Wf, RunState.Success, 2, branch: "feat/x"),
            T.Run(2, Wf, RunState.Failure, 3), T.Run(1, Wf, RunState.Failure, 4, branch: "feat/x"),
        };
        var s = Build([Obs(runs[0], "a", "s", Sig("x")), Obs(runs[2], "a", "s", Sig("x"))], runs).Single();
        await Assert.That(s.Streak).IsEqualTo(2);
    }

    [Test] public async Task Single_failure_is_not_recurring_and_two_are()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1); var r2 = T.Run(2, Wf, RunState.Failure, 2);
        await Assert.That(Build([Obs(r1, "a", "s", Sig("x"))], [r1]).Single().IsRecurring).IsFalse();
        await Assert.That(Build([Obs(r1, "a", "s", Sig("x")), Obs(r2, "a", "s", Sig("x"))], [r1, r2]).Single().IsRecurring).IsTrue();
    }

    [Test] public async Task Scheduled_and_push_runs_in_the_same_workflow_count_together()
    {
        var runs = new[] { T.Run(2, Wf, RunState.Failure, 1, ev: "schedule"), T.Run(1, Wf, RunState.Failure, 2, ev: "push") };
        await Assert.That(Build(runs.Select(r => Obs(r, "a", "s", Sig("x"))).ToList(), runs).Single().Count).IsEqualTo(2);
    }
}

public class AgentContextTests
{
    [Test]
    [Arguments("token ghp_" + "abcdefghijklmnopqrstuvwxyz0123456789")]
    [Arguments("github_pat_11ABCDEFG0abcdefghijklmnopqrstuvwxyz")]
    [Arguments("Authorization: Bearer abcdefghijklmnop12345")]
    [Arguments("password=hunter2hunter2")]
    public async Task Secrets_are_redacted(string text)
    {
        var o = AgentContext.Build(T.Repo, T.Run(1, "a.yml", RunState.Failure, 1), "job", "step", null, text);
        await Assert.That(o).Contains("[REDACTED]");
        await Assert.That(o).DoesNotContain("hunter2");
        await Assert.That(o).DoesNotContain("ghp_abc");
    }

    [Test] public async Task Excerpt_is_bounded_and_includes_identity()
    {
        var o = AgentContext.Build(T.Repo, T.Run(7, "a.yml", RunState.Failure, 1), "job", "step", "desc", new string('x', 50_000));
        await Assert.That(o.Length < AgentContext.MaxExcerptChars + 1500).IsTrue();
        await Assert.That(o).Contains("example-org/demo"); await Assert.That(o).Contains("a.yml"); await Assert.That(o).Contains("https://example.test/runs/7");
    }
}


public class DemoFixtureTests
{
    private static readonly TimeProvider Clock = new FixedClock(T.Now);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    [Test] public async Task Monorepo_has_many_workflows_and_all_are_grouped_exactly_once()
    {
        var p = new CIWorkbench.Core.Demo.DemoCiProvider(Clock);
        var wfs = await p.ListWorkflowsAsync(CIWorkbench.Core.Demo.DemoCiProvider.Monorepo, default);
        await Assert.That(wfs.Count).IsBetween(35,45);
        var yaml = await p.GetFileAsync(CIWorkbench.Core.Demo.DemoCiProvider.Monorepo, ".ciworkbench.yml", default);
        var (cfg, warn) = CIWorkbench.Core.Grouping.GroupConfigParser.Parse(yaml);
        await Assert.That(warn).IsNull();
        var comments = new Dictionary<string, string?>();
        foreach (var w in wfs) comments[w.Path] = CIWorkbench.Core.Grouping.WorkflowGroupResolver.ExtractCommentGroup(await p.GetFileAsync(CIWorkbench.Core.Demo.DemoCiProvider.Monorepo, w.Path, default));
        var res = CIWorkbench.Core.Grouping.WorkflowGroupResolver.Resolve(wfs, cfg, comments);
        await Assert.That(res.AllWorkflows.Count()).IsEqualTo(wfs.Count);
        var names = res.Groups.Select(g => g.Name).ToList();
        foreach (var expected in new[] { "Build & Test", "Security", "Deployment", "Quality", "Packages", "Documentation", "Maintenance", "AI Agents" }) await Assert.That(names).Contains(expected);
        await Assert.That(res.Groups).DoesNotContain(g => g.Name == "Ungrouped");
    }

    [Test] public async Task Actor_repo_yields_two_distinct_series_and_keeps_same_job_different_error_apart()
    {
        var p = new CIWorkbench.Core.Demo.DemoCiProvider(Clock);
        var repo = CIWorkbench.Core.Demo.DemoCiProvider.ActorRepo;
        var runs = await p.ListRecentRunsAsync(repo, 100, default);
        var analysis = await new CIWorkbench.Core.Failures.FailureAnalyzer(p).AnalyzeAsync(repo, runs, false, 30, default);
        var paging = analysis.Series.Single(s => s.Key.JobKey == "Deep-paging test");
        var browser = analysis.Series.Single(s => s.Key.JobKey == "Browser scenario tests" && s.Raw().Contains("Executable"));
        var assertion = analysis.Series.Single(s => s.Key.JobKey == "Browser scenario tests" && s.Raw().Contains("AssertionError"));
        await Assert.That(paging.Count).IsEqualTo(15); await Assert.That(paging.Streak).IsEqualTo(15);
        await Assert.That(browser.Count).IsEqualTo(10); await Assert.That(browser.Streak).IsEqualTo(9);
        await Assert.That(assertion.Count).IsEqualTo(1);
        await Assert.That(analysis.Series.Count).IsEqualTo(3);
        await Assert.That(analysis.Recurring.Count).IsEqualTo(2);                 // the single AssertionError is a one-off, not a recurring series
        await Assert.That(assertion.IsRecurring).IsFalse();
    }
}

internal static class SeriesExt { public static string Raw(this CIWorkbench.Core.Failures.FailureSeries s) => s.RawSignature ?? ""; }