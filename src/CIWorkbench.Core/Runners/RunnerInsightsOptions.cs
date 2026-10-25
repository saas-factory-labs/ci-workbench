namespace CIWorkbench.Core.Runners;

/// <summary>
/// What the user states about one provider's plan. Nothing here has a built-in default: an unset allowance means "unknown",
/// and Runner Insights then shows usage without inventing remaining minutes. Bound from <c>RunnerInsights:Providers:{id}</c>
/// (server configuration, which wins) or saved from Settings.
/// </summary>
public sealed class ProviderPlanOptions
{
    public AllowanceKind Allowance { get; set; } = AllowanceKind.None;
    /// <summary>Size of the allowance in <see cref="AllowanceUnit"/> (per period for <see cref="AllowanceKind.MonthlyIncluded"/>).</summary>
    public double? AllowanceAmount { get; set; }
    /// <summary>Unit of the amounts: the provider's own unit, or <see cref="UsageUnit.UsdCredits"/> together with <see cref="RatePerUnit"/>.</summary>
    public UsageUnit? AllowanceUnit { get; set; }
    /// <summary>USD per provider unit (e.g. per core-minute); converts usage to credits. Unset = no credit estimate.</summary>
    public double? RatePerUnit { get; set; }
    /// <summary>Day of month (1-28, UTC) the provider's billing cycle restarts. GitHub is always day 1.</summary>
    public int CycleStartDay { get; set; } = 1;
    /// <summary>True when the user has checked these numbers against the provider's current plan; false marks them as assumptions.</summary>
    public bool Confirmed { get; set; }
    /// <summary>One-time credits and prepaid balances: the balance read from the provider's dashboard ...</summary>
    public double? BalanceAmount { get; set; }
    /// <summary>... and the moment it was read. Remaining = balance − usage since then, and only while collection is complete.</summary>
    public DateTimeOffset? BalanceAsOf { get; set; }
    public string? Note { get; set; }
}

/// <summary>Bound from the <c>RunnerInsights</c> configuration section. Read-only feature: nothing here grants write access to anything.</summary>
public sealed class RunnerInsightsOptions
{
    public const string Section = "RunnerInsights";

    /// <summary>Most runs per repository listed for the billing period (GitHub caps a filtered listing at 1,000).</summary>
    public int MaxRunsPerRepository { get; set; } = 1000;
    /// <summary>Job requests one automatic pass may issue (cached jobs are free). Collection is resumable: the rest follows on the next pass.</summary>
    public int MaxJobRequestsPerRefresh { get; set; } = 100;
    /// <summary>Job requests one explicit "Collect remaining" may issue.</summary>
    public int MaxJobRequestsPerCollect { get; set; } = 500;
    /// <summary>Collection stops while fewer API requests than this remain in GitHub's rate limit window.</summary>
    public int RateLimitReserve { get; set; } = 300;
    public int Concurrency { get; set; } = 3;
    /// <summary>How long a finished measurement is reused before the newest runs are looked at again.</summary>
    public int RefreshMinutes { get; set; } = 10;
    /// <summary>Set false to never call the GitHub billing API (estimates only).</summary>
    public bool BillingEnabled { get; set; } = true;
    /// <summary>Where allowances saved from Settings are kept (server side). Default: the user's local application data folder.</summary>
    public string? DataDirectory { get; set; }

    public Dictionary<string, ProviderPlanOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<CustomRunnerRule> CustomRules { get; set; } = [];

    /// <summary>
    /// The plan for a provider; GitHub plans are per account, so <paramref name="pool"/> (the owner) is tried first as <c>github.{owner}</c>.
    /// </summary>
    public ProviderPlanOptions? PlanOf(string providerId, string? pool = null) =>
        pool is not null && Providers.TryGetValue($"{providerId}.{pool}", out var specific) ? specific
        : Providers.TryGetValue(providerId, out var p) ? p : null;

    /// <summary>GitHub's cycle is the calendar month regardless of configuration.</summary>
    public int CycleDayOf(string providerId) => providerId is RunnerCatalog.GitHub or RunnerCatalog.GitHubLarger ? 1 : PlanOf(providerId)?.CycleStartDay ?? 1;
}
