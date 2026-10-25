using CIWorkbench.Core.Models;

namespace CIWorkbench.Core.Runners;

/// <summary>Where a suggestion comes from. A suggestion is never an entitlement: it only pre-fills a form the user must confirm.</summary>
public enum SuggestionBasis { GitHubDocs, VendorReported, ThirdParty, RouterAssumption }

public sealed record PlanSuggestion(string Label, AllowanceKind Kind, double Amount, UsageUnit Unit, SuggestionBasis Basis, string Source);

/// <summary>Public, documented free-tier information per provider. Deliberately small and always labelled as unverified for the user's account.</summary>
public static class PlanSuggestions
{
    public static IReadOnlyList<PlanSuggestion> For(string providerId) => providerId switch
    {
        // GitHub never reports the plan's included minutes through the API, and the plan itself is unknown here: offer, never assume.
        RunnerCatalog.GitHub =>
        [
            new("Free plan", AllowanceKind.MonthlyIncluded, 2000, UsageUnit.Minutes, SuggestionBasis.ThirdParty, "Commonly cited; check Billing → Plans in GitHub"),
            new("Pro / Team plan", AllowanceKind.MonthlyIncluded, 3000, UsageUnit.Minutes, SuggestionBasis.ThirdParty, "Commonly cited; check Billing → Plans in GitHub"),
            new("Enterprise Cloud", AllowanceKind.MonthlyIncluded, 50000, UsageUnit.Minutes, SuggestionBasis.GitHubDocs, "GitHub Docs: Enterprise Cloud includes 50,000 minutes"),
        ],
        RunnerCatalog.Blacksmith => [new("Pay-as-you-go free tier", AllowanceKind.MonthlyIncluded, 3000, UsageUnit.NormalizedMinutes, SuggestionBasis.VendorReported, "blacksmith.sh pricing page")],
        RunnerCatalog.Avrea => [new("As assumed by SaaS-Factory's router", AllowanceKind.MonthlyIncluded, 3000, UsageUnit.NormalizedMinutes, SuggestionBasis.RouterAssumption, "No public figure found; SaaS-Factory budgets 3,000 normalized minutes")],
        _ => [],
    };

    /// <summary>Short, provider-specific caution shown next to the editor.</summary>
    public static string? Caution(string providerId) => providerId switch
    {
        RunnerCatalog.Tenki => "Tenki's credit policy has changed over time (monthly top-up, one-time and no credit all exist). Read your balance from the Tenki dashboard; nothing is assumed.",
        RunnerCatalog.Celesto => "Celesto uses prepaid credits, not recurring free minutes. Enter your balance and when you read it.",
        RunnerCatalog.HetznerArc => "Self-managed runners have no free allowance. Usage is job time only; hosting cost is not tracked.",
        RunnerCatalog.BuildPulse => "Runner minutes are normalized to 2-vCPU minutes. BuildPulse's test-result usage is a different meter.",
        RunnerCatalog.GitHub => "Included minutes depend on your plan and apply per account (organization or user). Standard runners in public repositories are free.",
        _ => null,
    };
}

public static class PlanValidator
{
    /// <summary>User-presentable problems; empty when the plan can be saved.</summary>
    public static IReadOnlyList<string> Validate(RunnerProviderDef provider, ProviderPlanOptions p, DateTimeOffset now)
    {
        var errors = new List<string>();
        if (p.CycleStartDay is < 1 or > 28) errors.Add("The cycle start day must be between 1 and 28.");
        if (p.RatePerUnit is { } r && (r <= 0 || double.IsNaN(r) || double.IsInfinity(r))) errors.Add("The rate must be a positive number.");
        if (p.Allowance is AllowanceKind.None or AllowanceKind.Unlimited) return errors;

        var unit = p.AllowanceUnit ?? provider.Unit;
        if (p.Allowance == AllowanceKind.MonthlyIncluded && (p.AllowanceAmount is not { } a || a <= 0 || double.IsNaN(a) || double.IsInfinity(a))) errors.Add("Enter the monthly allowance as a positive number.");
        if (p.Allowance is AllowanceKind.OneTimeCredit or AllowanceKind.Prepaid)
        {
            if (p.BalanceAmount is { } b && (b < 0 || double.IsNaN(b) || double.IsInfinity(b))) errors.Add("The balance cannot be negative.");
            if (p.BalanceAmount is not null && p.BalanceAsOf is null) errors.Add("Say when you read the balance: remaining is calculated from usage since then.");
            if (p.BalanceAsOf is { } at && at > now.AddMinutes(5)) errors.Add("The balance date cannot be in the future.");
        }
        if (unit == UsageUnit.UsdCredits && provider.Unit != UsageUnit.UsdCredits && p.RatePerUnit is null)
            errors.Add($"Credits in USD need a rate (USD per {provider.Unit.Label()}) so usage can be converted.");
        if (unit != UsageUnit.UsdCredits && unit != provider.Unit) errors.Add($"{provider.Name} measures usage in {provider.Unit.Label()}; use that unit or USD credits with a rate.");
        return errors;
    }
}

public static class BillingUsageExtensions
{
    // Standard hosted SKUs draw from included minutes, weighted by GitHub's minute multipliers. Larger runners and other SKUs never do.
    private static readonly Dictionary<string, double> Standard = new(StringComparer.Ordinal) { ["actionslinux"] = 1, ["actionswindows"] = 2, ["actionsmacos"] = 10 };

    /// <summary>Billed runner minutes as reported (unweighted). Storage and other non-minute SKUs of the Actions product are ignored: GigabyteHours are not minutes.</summary>
    public static double BilledMinutes(this BillingUsage b) => b.IsAvailable ? b.Lines.Where(l => string.Equals(l.UnitType, "Minutes", StringComparison.OrdinalIgnoreCase)).Sum(l => l.Quantity) : 0;

    public static string NormalizeSku(string sku) => new string(sku.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>
    /// Gross standard-runner usage in multiplier-weighted minutes (what is counted against included minutes), from the official report.
    /// Gross, not net: discounted minutes were still used. Null when the report has no standard Actions lines to weigh.
    /// </summary>
    public static double? StandardWeightedMinutes(this BillingUsage b)
    {
        if (!b.IsAvailable) return null;
        var lines = b.Lines.Where(l => Standard.ContainsKey(NormalizeSku(l.Sku))).ToList();
        return lines.Count == 0 ? 0 : lines.Sum(l => l.Quantity * Standard[NormalizeSku(l.Sku)]);
    }

    /// <summary>True when GitHub charged for a standard SKU: the included minutes were used up. Zero-dollar bills prove nothing about what is left.</summary>
    public static bool StandardMinutesCharged(this BillingUsage b) => b.IsAvailable && b.Lines.Any(l => Standard.ContainsKey(NormalizeSku(l.Sku)) && l.HasPaidUsage);
}
