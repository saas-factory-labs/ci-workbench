using CIWorkbench.Core.Filtering;
using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Tests;

public class FilteringTests
{
    private static readonly WorkflowRunInfo[] Runs =
    [
        T.Run(1, "a.yml", RunState.Success, 5), T.Run(2, "a.yml", RunState.Failure, 1, branch: "feat/x"),
        T.Run(3, "a.yml", RunState.Failure, 3, ev: "schedule"), T.Run(4, "a.yml", RunState.InProgress, 0),
    ];

    [Fact] public void Sorts_newest_first_by_default() =>
        Assert.Equal([4L, 2L, 3L, 1L], RunFiltering.Apply(Runs, new RunFilter()).Select(r => r.Id));

    [Fact] public void Filters_by_state() =>
        Assert.Equal([2L, 3L], RunFiltering.Apply(Runs, new RunFilter(State: RunState.Failure)).Select(r => r.Id));

    [Fact] public void Filters_by_branch_and_event_and_search()
    {
        Assert.Equal([2L], RunFiltering.Apply(Runs, new RunFilter(Branch: "feat/x")).Select(r => r.Id));
        Assert.Equal([3L], RunFiltering.Apply(Runs, new RunFilter(Event: "schedule")).Select(r => r.Id));
        Assert.Equal([3L], RunFiltering.Apply(Runs, new RunFilter(Search: "3")).Select(r => r.Id));
    }

    [Fact] public void Summary_excludes_active_and_cancelled_from_rate()
    {
        var s = RunSummary.From([.. Runs, T.Run(5, "a.yml", RunState.Cancelled, 9)]);
        Assert.Equal(1.0 / 3, s.SuccessRate!.Value, 3);
        Assert.Equal(1, s.Active);
    }

    [Fact] public void Summary_rate_is_null_without_verdicts() =>
        Assert.Null(RunSummary.From([T.Run(1, "a", RunState.InProgress, 0)]).SuccessRate);
}

