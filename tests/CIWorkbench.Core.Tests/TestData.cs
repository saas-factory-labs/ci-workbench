using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Tests;

internal static class T
{
    public static readonly RepoRef Repo = new("example-org", "demo");
    public static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public static WorkflowInfo Wf(string path, string? name = null, long id = 0) =>
        new(id == 0 ? Math.Abs(path.GetHashCode()) : id, name ?? System.IO.Path.GetFileNameWithoutExtension(path), path, true, null);

    public static WorkflowRunInfo Run(long id, string wfPath, RunState state, int hoursAgo, string branch = "main", string ev = "push", int attempt = 1) =>
        new(id, 1, wfPath, "CI", (int)id, attempt, state, ev, branch, "abc1234", "msg", "octo", Now.AddHours(-hoursAgo), Now.AddHours(-hoursAgo),
            Now.AddHours(-hoursAgo).AddMinutes(5), $"https://example.test/runs/{id}");
}

