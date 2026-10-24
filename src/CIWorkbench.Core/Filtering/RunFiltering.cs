using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Filtering;

public enum RunSort { StartedDesc, StartedAsc, DurationDesc }

public sealed record RunFilter(string? Search = null, RunState? State = null, string? Branch = null, string? Event = null)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Search) && State is null && Branch is null && Event is null;
}

public static class RunFiltering
{
    public static IReadOnlyList<WorkflowRunInfo> Apply(IEnumerable<WorkflowRunInfo> runs, RunFilter filter, RunSort sort = RunSort.StartedDesc)
    {
        var q = runs;
        if (filter.State is { } s) q = q.Where(r => r.State == s);
        if (!string.IsNullOrWhiteSpace(filter.Branch)) q = q.Where(r => r.Branch.Equals(filter.Branch, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.Event)) q = q.Where(r => r.Event.Equals(filter.Event, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var t = filter.Search.Trim();
            q = q.Where(r => Contains(r.Name, t) || Contains(r.Branch, t) || Contains(r.Actor, t) || Contains(r.Event, t)
                || Contains(r.CommitMessage, t) || r.Id.ToString().Contains(t) || r.HeadSha.StartsWith(t, StringComparison.OrdinalIgnoreCase));
        }
        return sort switch
        {
            RunSort.StartedAsc => q.OrderBy(r => r.SortTime).ToList(),
            RunSort.DurationDesc => q.OrderByDescending(r => r.Duration ?? TimeSpan.Zero).ToList(),
            _ => q.OrderByDescending(r => r.SortTime).ToList(),
        };
    }

    public static bool WorkflowMatches(WorkflowInfo w, string? search) =>
        string.IsNullOrWhiteSpace(search) || Contains(w.Name, search.Trim()) || Contains(w.Path, search.Trim());

    private static bool Contains(string? hay, string needle) => hay?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>Success rate over completed runs that actually reached a verdict (cancelled/skipped excluded).</summary>
public sealed record RunSummary(int Total, int Succeeded, int Failed, int Active)
{
    public double? SuccessRate => Succeeded + Failed == 0 ? null : (double)Succeeded / (Succeeded + Failed);
    public static RunSummary From(IEnumerable<WorkflowRunInfo> runs)
    {
        int t = 0, s = 0, f = 0, a = 0;
        foreach (var r in runs) { t++; if (r.State == RunState.Success) s++; else if (r.State == RunState.Failure) f++; else if (r.State.IsActive()) a++; }
        return new RunSummary(t, s, f, a);
    }
}

