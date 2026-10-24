using CIWorkbench.Core.Failures;
using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Tests;

public class SeriesTests
{
    private const string Wf = ".github/workflows/ci.yml";
    private static FailureSignature Sig(string s) => new(SignatureKind.Known, s, s);

    private static FailureObservation Obs(WorkflowRunInfo run, string job, string step, FailureSignature sig) =>
        new(run, job, step, sig, run.Id * 10);

    private static IReadOnlyList<FailureSeries> Build(IReadOnlyList<FailureObservation> o, IReadOnlyList<WorkflowRunInfo> runs, bool truncated = false) =>
        FailureSeriesBuilder.Build(T.Repo, o, runs, runs.Where(r => r.State == RunState.Failure).Select(r => r.Id).ToHashSet(), truncated);

    [Fact] public void Same_job_step_signature_merges_with_count_first_and_last_seen()
    {
        var runs = new[] { T.Run(3, Wf, RunState.Failure, 1), T.Run(2, Wf, RunState.Failure, 25), T.Run(1, Wf, RunState.Success, 50) };
        var s = Build(runs.Where(r => r.State == RunState.Failure).Select(r => Obs(r, "test", "Run tests", Sig("boom"))).ToList(), runs).Single();
        Assert.Equal(2, s.Count);
        Assert.Equal(T.Now.AddHours(-25), s.FirstSeen);
        Assert.Equal(T.Now.AddHours(-1), s.LastSeen);
        Assert.Equal(2, s.Streak);
        Assert.False(s.StreakIsLowerBound);
    }

    [Fact] public void Same_job_and_step_but_different_errors_stay_separate()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1); var r2 = T.Run(2, Wf, RunState.Failure, 2);
        var s = Build([Obs(r1, "e2e", "Run", Sig("TimeoutError: a")), Obs(r2, "e2e", "Run", Sig("Error: Browser missing"))], [r1, r2]);
        Assert.Equal(2, s.Count);
    }

    [Fact] public void Same_message_in_different_jobs_or_workflows_does_not_merge()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1); var r2 = T.Run(2, ".github/workflows/other.yml", RunState.Failure, 2);
        Assert.Equal(3, Build([Obs(r1, "a", "s", Sig("x")), Obs(r1, "b", "s", Sig("x")), Obs(r2, "a", "s", Sig("x"))], [r1, r2]).Count);
    }

    [Fact] public void Unknown_never_merges_with_known_and_different_steps_do_not_merge()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1); var r2 = T.Run(2, Wf, RunState.Failure, 2);
        var s = Build([Obs(r1, "a", "one", FailureSignature.Unknown), Obs(r2, "a", "one", Sig("x")), Obs(r2, "a", "two", FailureSignature.Unknown)], [r1, r2]);
        Assert.Equal(3, s.Count);
    }

    [Fact] public void Two_unknowns_in_same_step_merge_and_are_flagged_cause_unknown()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1); var r2 = T.Run(2, Wf, RunState.Failure, 2);
        var s = Build([Obs(r1, "a", "one", FailureSignature.Unknown), Obs(r2, "a", "one", FailureSignature.Unknown)], [r1, r2]).Single();
        Assert.True(s.CauseUnknown);
    }

    [Fact] public void Unavailable_log_is_its_own_state_not_unknown()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1); var r2 = T.Run(2, Wf, RunState.Failure, 2);
        var s = Build([Obs(r1, "a", "one", FailureSignature.Unavailable), Obs(r2, "a", "one", FailureSignature.Unknown)], [r1, r2]);
        Assert.Equal(2, s.Count);
        Assert.Contains(s, x => x.LogUnavailable);
    }

    [Fact] public void Matrix_variants_merge_and_are_recorded()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1); var r2 = T.Run(2, Wf, RunState.Failure, 2);
        var s = Build([Obs(r1, "build (ubuntu, 20)", "Test", Sig("x")), Obs(r2, "build (windows, 22)", "Test", Sig("x"))], [r1, r2]).Single();
        Assert.Equal("build", s.Key.JobKey);
        Assert.Equal(["build (ubuntu, 20)", "build (windows, 22)"], s.Variants);
    }

    [Fact] public void Missing_step_uses_job_setup_key()
    {
        var r1 = T.Run(1, Wf, RunState.Failure, 1);
        Assert.Equal(FailureSeriesBuilder.JobSetupStep, Build([Obs(r1, "a", "", Sig("x"))], [r1]).Single().Key.StepKey);
    }

    [Fact] public void Cancelled_between_two_failures_does_not_break_streak()
    {
        var runs = new[] { T.Run(3, Wf, RunState.Failure, 1), T.Run(2, Wf, RunState.Cancelled, 2), T.Run(1, Wf, RunState.Failure, 3), T.Run(0, Wf, RunState.Success, 4) };
        var s = Build([Obs(runs[0], "a", "s", Sig("x")), Obs(runs[2], "a", "s", Sig("x"))], runs).Single();
        Assert.Equal(2, s.Streak); Assert.False(s.StreakIsLowerBound);
    }

    [Fact] public void Success_in_between_breaks_streak_but_occurrences_keep_counting()
    {
        var runs = new[] { T.Run(3, Wf, RunState.Failure, 1), T.Run(2, Wf, RunState.Success, 2), T.Run(1, Wf, RunState.Failure, 3) };
        var s = Build([Obs(runs[0], "a", "s", Sig("x")), Obs(runs[2], "a", "s", Sig("x"))], runs).Single();
        Assert.Equal(1, s.Streak); Assert.Equal(2, s.Count);
    }

    [Fact] public void Different_failure_in_between_breaks_the_streak()
    {
        var runs = new[] { T.Run(3, Wf, RunState.Failure, 1), T.Run(2, Wf, RunState.Failure, 2), T.Run(1, Wf, RunState.Failure, 3) };
        var obs = new[] { Obs(runs[0], "a", "s", Sig("x")), Obs(runs[1], "a", "s", Sig("y")), Obs(runs[2], "a", "s", Sig("x")) };
        Assert.Equal(1, Build(obs, runs).Single(s => s.Key.Signature == "x").Streak);
    }

    [Fact] public void Streak_reaching_window_edge_is_a_lower_bound()
    {
        var runs = new[] { T.Run(2, Wf, RunState.Failure, 1), T.Run(1, Wf, RunState.Failure, 2) };
        var s = Build(runs.Select(r => Obs(r, "a", "s", Sig("x"))).ToList(), runs, truncated: true).Single();
        Assert.Equal(2, s.Streak); Assert.True(s.StreakIsLowerBound);
    }

    [Fact] public void Uninspected_failed_run_makes_streak_a_lower_bound()
    {
        var runs = new[] { T.Run(2, Wf, RunState.Failure, 1), T.Run(1, Wf, RunState.Failure, 2) };
        var s = FailureSeriesBuilder.Build(T.Repo, [Obs(runs[0], "a", "s", Sig("x"))], runs, new HashSet<long> { 2 }, false).Single();
        Assert.Equal(1, s.Streak); Assert.True(s.StreakIsLowerBound);
    }

    [Fact] public void Scheduled_and_push_runs_in_the_same_workflow_count_together()
    {
        var runs = new[] { T.Run(2, Wf, RunState.Failure, 1, ev: "schedule"), T.Run(1, Wf, RunState.Failure, 2, ev: "push") };
        Assert.Equal(2, Build(runs.Select(r => Obs(r, "a", "s", Sig("x"))).ToList(), runs).Single().Count);
    }
}

public class AgentContextTests
{
    [Theory]
    [InlineData("token ghp_" + "abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("github_pat_11ABCDEFG0abcdefghijklmnopqrstuvwxyz")]
    [InlineData("Authorization: Bearer abcdefghijklmnop12345")]
    [InlineData("password=hunter2hunter2")]
    public void Secrets_are_redacted(string text)
    {
        var o = AgentContext.Build(T.Repo, T.Run(1, "a.yml", RunState.Failure, 1), "job", "step", null, text);
        Assert.Contains("[REDACTED]", o);
        Assert.DoesNotContain("hunter2", o);
        Assert.DoesNotContain("ghp_abc", o);
    }

    [Fact] public void Excerpt_is_bounded_and_includes_identity()
    {
        var o = AgentContext.Build(T.Repo, T.Run(7, "a.yml", RunState.Failure, 1), "job", "step", "desc", new string('x', 50_000));
        Assert.True(o.Length < AgentContext.MaxExcerptChars + 1500);
        Assert.Contains("example-org/demo", o); Assert.Contains("a.yml", o); Assert.Contains("https://example.test/runs/7", o);
    }
}

