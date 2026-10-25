using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Runners;

/// <summary>Where a number comes from. Never present <see cref="Estimated"/> as <see cref="Verified"/>.</summary>
public enum DataQuality { Verified, Estimated, Unavailable }

public enum Coverage { Complete, Partial }

/// <summary>Units are never added across each other.</summary>
public enum UsageUnit { Minutes, NormalizedMinutes, CoreMinutes, UsdCredits }

public static class UsageUnitExtensions
{
    public static string Label(this UsageUnit u) => u switch
    {
        UsageUnit.Minutes => "minutes",
        UsageUnit.NormalizedMinutes => "normalized min (2 vCPU)",
        UsageUnit.CoreMinutes => "core-minutes",
        _ => "USD credits",
    };
}

public enum RunnerProviderKind { GitHubHosted, ThirdPartyHosted, SelfHosted, Unknown }

/// <summary>How a provider rounds one job. GitHub documents per-job rounding up to the next minute; for others it is an assumption (see <see cref="RunnerProviderDef.RulesConfirmed"/>).</summary>
public enum RoundingRule { PerJobCeilMinute, Exact }

public enum AllowanceKind
{
    None,
    /// <summary>Recurring included amount that resets every billing period: remaining can be derived from period usage.</summary>
    MonthlyIncluded,
    /// <summary>Granted once and not renewed: the balance depends on all past usage, so it cannot be derived from one period.</summary>
    OneTimeCredit,
    /// <summary>Prepaid balance topped up by the user: only the provider knows it (or the user, as of a date).</summary>
    Prepaid,
    /// <summary>No cap (or pay-as-you-go with no free part): usage is shown without a remaining amount.</summary>
    Unlimited,
}

/// <summary>Identity of a provider and the rules used to estimate its usage. Data, not code: custom providers use the same shape.</summary>
public sealed record RunnerProviderDef(
    string Id, string Name, RunnerProviderKind Kind, UsageUnit Unit, RoundingRule Rounding,
    bool CostTracked = true, bool RulesConfirmed = false, string? Note = null);

/// <summary>What a job's labels resolve to. <see cref="Multiplier"/> converts executed minutes to <see cref="RunnerProviderDef.Unit"/>.</summary>
public sealed record RunnerIdentity(string ProviderId, string RunnerType, double Multiplier);

/// <summary>A job as seen by the estimator: what ran, where, and when. Built from <see cref="JobInfo"/> plus its run and repository.</summary>
public sealed record JobSample(
    RepoRef Repo, bool RepoIsPrivate, long RunId, long WorkflowId, string WorkflowPath, string WorkflowName, long JobId, string JobName, int RunAttempt,
    IReadOnlyList<string> Labels, string? RunnerName, string? RunnerGroup, RunState State, DateTimeOffset? CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string HtmlUrl);

/// <summary>One counted job: executed minutes and the provider units they convert to.</summary>
public sealed record JobUsage(JobSample Job, RunnerIdentity Identity, double RawMinutes, double BilledUnits);

public enum ExclusionReason { NotCompleted, NotStarted, Skipped, InvalidDuration, Duplicate, OutsidePeriod }

/// <summary>A billing window <c>[Start, End)</c> in UTC.</summary>
public readonly record struct BillingPeriod(DateTimeOffset Start, DateTimeOffset End)
{
    public bool Contains(DateTimeOffset t) => t >= Start && t < End;
    public string Label => $"{Start:d MMM} – {End.AddDays(-1):d MMM yyyy}";

    /// <summary>
    /// The period containing <paramref name="now"/> for a cycle that starts on <paramref name="startDay"/> (1-28, UTC).
    /// GitHub uses calendar months (day 1). Days above 28 are clamped so every month has the anchor.
    /// </summary>
    public static BillingPeriod For(DateTimeOffset now, int startDay = 1)
    {
        var day = Math.Clamp(startDay, 1, 28);
        var utc = now.ToUniversalTime();
        var start = new DateTimeOffset(utc.Year, utc.Month, day, 0, 0, 0, TimeSpan.Zero);
        if (utc < start) start = start.AddMonths(-1);
        return new BillingPeriod(start, start.AddMonths(1));
    }
}
