namespace CIWorkbench.Core.Models;

/// <summary>Why billing numbers are or are not available. Billing permissions are optional and independent of repository access.</summary>
public enum BillingAccess
{
    Available,
    /// <summary>401/403: the token lacks the billing permission (organization "Administration: read" or user "Plan: read").</summary>
    PermissionMissing,
    /// <summary>404 or a response without usage data: the account is not on the enhanced billing platform, or is not visible to this token.</summary>
    NotAvailable,
    /// <summary>This data source has no billing API.</summary>
    Unsupported,
    /// <summary>Transient failure (network, rate limit).</summary>
    Failed,
}

/// <summary>One Actions line of GitHub's billing usage report, aggregated per SKU. Amounts are USD as returned by GitHub.</summary>
public sealed record BillingLine(string Sku, string UnitType, double Quantity, double PricePerUnit, double GrossAmount, double DiscountAmount, double NetAmount)
{
    /// <summary>True when GitHub charged for this line, i.e. the included free allowance no longer covered it.</summary>
    public bool HasPaidUsage => NetAmount > 0.0000001;
}

/// <summary>Result of <c>GET /organizations/{org}/settings/billing/usage</c> (or the user variant) filtered to the Actions product.</summary>
public sealed record BillingUsage(BillingAccess Access, IReadOnlyList<BillingLine> Lines, string? Reason, int Year, int Month, DateTimeOffset? FetchedAt)
{
    public static readonly BillingUsage Unsupported = new(BillingAccess.Unsupported, [], "This data source has no billing API.", 0, 0, null);
    public bool IsAvailable => Access == BillingAccess.Available;
}

/// <param name="TotalCount">The provider's own count of matching runs, when it reports one.</param>
/// <param name="Complete">Every run created in the window is in <see cref="Runs"/>.</param>
public sealed record RunPeriod(IReadOnlyList<WorkflowRunInfo> Runs, int? TotalCount, bool Complete);

public enum VariablesAccess { Available, PermissionMissing, NotAvailable, Unsupported, Failed }

/// <summary>Actions variables (name → value) of a repository. Used only to resolve <c>vars.X</c> in <c>runs-on</c>.</summary>
public sealed record ActionsVariables(VariablesAccess Access, IReadOnlyDictionary<string, string> Values, string? Reason)
{
    public static readonly ActionsVariables Unsupported = new(VariablesAccess.Unsupported, new Dictionary<string, string>(), "This data source has no variables API.");
    public bool IsAvailable => Access == VariablesAccess.Available;
}
