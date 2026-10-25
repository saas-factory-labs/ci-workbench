using TUnit.Assertions.Enums;
using CIWorkbench.Core.Filtering;
using CIWorkbench.Core.Models;
using System.Threading.Tasks;

namespace CIWorkbench.Core.Tests;

public class FilteringTests
{
    private static readonly WorkflowRunInfo[] Runs =
    [
        T.Run(1, "a.yml", RunState.Success, 5), T.Run(2, "a.yml", RunState.Failure, 1, branch: "feat/x"),
        T.Run(3, "a.yml", RunState.Failure, 3, ev: "schedule"), T.Run(4, "a.yml", RunState.InProgress, 0),
    ];

    [Test] public async Task Sorts_newest_first_by_default() =>
        await Assert.That(RunFiltering.Apply(Runs, new RunFilter()).Select(r => r.Id)).IsEquivalentTo([4L, 2L, 3L, 1L], CollectionOrdering.Matching);

    [Test] public async Task Filters_by_state() =>
        await Assert.That(RunFiltering.Apply(Runs, new RunFilter(State: RunState.Failure)).Select(r => r.Id)).IsEquivalentTo([2L, 3L], CollectionOrdering.Matching);

    [Test] public async Task Filters_by_branch_and_event_and_search()
    {
        await Assert.That(RunFiltering.Apply(Runs, new RunFilter(Branch: "feat/x")).Select(r => r.Id)).IsEquivalentTo([2L], CollectionOrdering.Matching);
        await Assert.That(RunFiltering.Apply(Runs, new RunFilter(Event: "schedule")).Select(r => r.Id)).IsEquivalentTo([3L], CollectionOrdering.Matching);
        await Assert.That(RunFiltering.Apply(Runs, new RunFilter(Search: "3")).Select(r => r.Id)).IsEquivalentTo([3L], CollectionOrdering.Matching);
    }

    [Test] public async Task Summary_excludes_active_and_cancelled_from_rate()
    {
        var s = RunSummary.From([.. Runs, T.Run(5, "a.yml", RunState.Cancelled, 9)]);
        await Assert.That(s.SuccessRate!.Value).IsEqualTo(1.0 / 3);
        await Assert.That(s.Active).IsEqualTo(1);
    }

    [Test] public async Task Summary_rate_is_null_without_verdicts() =>
        await Assert.That(RunSummary.From([T.Run(1, "a", RunState.InProgress, 0)]).SuccessRate).IsNull();
}
