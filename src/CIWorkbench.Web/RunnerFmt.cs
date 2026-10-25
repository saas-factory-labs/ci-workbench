using System.Globalization;
using CIWorkbench.Core.Runners;

namespace CIWorkbench.Web;

/// <summary>Display rules for Runner Insights. Every amount carries its unit; amounts of different units are never combined.</summary>
public static class RunnerFmt
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static string Amount(double v, UsageUnit unit) => unit switch
    {
        UsageUnit.UsdCredits => "$" + v.ToString("N2", C),
        UsageUnit.CoreMinutes => v.ToString("N0", C) + " core-min",
        UsageUnit.NormalizedMinutes => v.ToString("N0", C) + " norm. min",
        _ => v.ToString("N0", C) + " min",
    };

    public static string Quality(DataQuality q) => q switch { DataQuality.Verified => "Verified", DataQuality.Estimated => "Estimated", _ => "Unavailable" };
    public static string QualityClass(DataQuality q) => q switch { DataQuality.Verified => "ver", DataQuality.Estimated => "est", _ => "na" };
    public static string QualityTitle(DataQuality q) => q switch
    {
        DataQuality.Verified => "Read from the provider's official billing API.",
        DataQuality.Estimated => "Calculated from GitHub job history and known billing rules. Not provider billing data.",
        _ => "Cannot be determined reliably.",
    };

    /// <summary>One line describing an allowance and what is left. Remaining is only worded as such when it was derived.</summary>
    public static string AllowanceLine(AllowanceStatus a) => a.Remaining is { } rem && a.Used is { } used && a.Amount is { } amount && a.Unit is { } unit
        ? $"{Amount(used, unit)} of {Amount(amount, unit)} used · {Amount(rem, unit)} left"
        : a.Used is { } u && a.Unit is { } un ? $"{Amount(u, un)} used this period" : a.Explanation;

    public static string Kind(AllowanceKind k) => k switch
    {
        AllowanceKind.MonthlyIncluded => "Monthly included", AllowanceKind.OneTimeCredit => "One-time credit", AllowanceKind.Prepaid => "Prepaid balance", _ => "None configured",
    };

    public static string RoleLabel(RunnerRole r) => r switch { RunnerRole.Configured => "configured", RunnerRole.Fallback => "potential fallback", _ => "unknown (dynamic)" };

    public static string Excluded(IReadOnlyDictionary<ExclusionReason, int> ex)
    {
        var parts = new List<string>();
        if (ex.GetValueOrDefault(ExclusionReason.NotCompleted) is > 0 and var n) parts.Add($"{n} not finished");
        if (ex.GetValueOrDefault(ExclusionReason.Skipped) is > 0 and var s) parts.Add($"{s} skipped");
        if (ex.GetValueOrDefault(ExclusionReason.NotStarted) is > 0 and var ns) parts.Add($"{ns} never started");
        if (ex.GetValueOrDefault(ExclusionReason.OutsidePeriod) is > 0 and var o) parts.Add($"{o} outside the period");
        return string.Join(", ", parts);
    }
}
