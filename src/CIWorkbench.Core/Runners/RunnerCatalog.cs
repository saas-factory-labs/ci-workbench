using System.Text.RegularExpressions;

namespace CIWorkbench.Core.Runners;

/// <summary>A user-defined label rule so unknown providers and custom labels can be tracked without code changes.</summary>
public sealed class CustomRunnerRule
{
    public string Prefix { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public string? Name { get; set; }
    public UsageUnit Unit { get; set; } = UsageUnit.Minutes;
    /// <summary>Units per executed minute (e.g. 4 for a 4-core runner billed in core-minutes).</summary>
    public double Multiplier { get; set; } = 1;
}

/// <summary>
/// Identifies the provider behind a job's runner labels. Matching is by label <b>prefix</b>, in a fixed order, never by substring:
/// <c>avrea-ubuntu-latest</c> must not be mistaken for GitHub's <c>ubuntu-latest</c>. Anything unrecognised is reported as
/// <see cref="Custom"/>, never dropped, so the totals stay honest.
/// </summary>
public sealed partial class RunnerCatalog
{
    public const string GitHub = "github", GitHubLarger = "github-larger", Tenki = "tenki", Avrea = "avrea", BuildPulse = "buildpulse",
        Celesto = "celesto", HetznerArc = "hetzner-arc", Blacksmith = "blacksmith", SelfHosted = "self-hosted", Custom = "custom";

    private static readonly RunnerProviderDef[] BuiltIn =
    [
        new(GitHub, "GitHub Actions", RunnerProviderKind.GitHubHosted, UsageUnit.Minutes, RoundingRule.PerJobCeilMinute, RulesConfirmed: true,
            Note: "Minutes are weighted by operating system (Linux 1×, Windows 2×, macOS 10×). Standard runners in public repositories are free."),
        new(GitHubLarger, "GitHub larger runners", RunnerProviderKind.GitHubHosted, UsageUnit.Minutes, RoundingRule.PerJobCeilMinute, RulesConfirmed: true, Note: "Billed per minute and never from the included minutes."),
        new(Tenki, "Tenki", RunnerProviderKind.ThirdPartyHosted, UsageUnit.CoreMinutes, RoundingRule.PerJobCeilMinute, Note: "Billed in core-minutes (cores × minutes). Rounding and rate are assumptions until you confirm them in configuration."),
        new(Avrea, "Avrea", RunnerProviderKind.ThirdPartyHosted, UsageUnit.NormalizedMinutes, RoundingRule.PerJobCeilMinute, Note: "Normalized to 2-vCPU minutes; larger runners count proportionally. RAM-specific pricing is not modelled."),
        new(BuildPulse, "BuildPulse", RunnerProviderKind.ThirdPartyHosted, UsageUnit.NormalizedMinutes, RoundingRule.PerJobCeilMinute, Note: "Normalized to 2-vCPU minutes. This is runner time, not BuildPulse's test-result usage API."),
        new(Celesto, "Celesto", RunnerProviderKind.ThirdPartyHosted, UsageUnit.Minutes, RoundingRule.PerJobCeilMinute, Note: "Runtime is estimated. Celesto uses a separate prepaid credit balance, which cannot be read from GitHub."),
        new(HetznerArc, "Hetzner ARC", RunnerProviderKind.SelfHosted, UsageUnit.Minutes, RoundingRule.Exact, CostTracked: false, Note: "Self-managed runners: job time only. Hosting cost is not tracked (and is not zero)."),
        new(Blacksmith, "Blacksmith", RunnerProviderKind.ThirdPartyHosted, UsageUnit.NormalizedMinutes, RoundingRule.PerJobCeilMinute, Note: "Estimated from GitHub jobs; Blacksmith's own usage CLI is not integrated."),
        new(SelfHosted, "Self-hosted (other)", RunnerProviderKind.SelfHosted, UsageUnit.Minutes, RoundingRule.Exact, CostTracked: false, Note: "Runners you operate. Job time only."),
        new(Custom, "Unrecognized runners", RunnerProviderKind.Unknown, UsageUnit.Minutes, RoundingRule.Exact, CostTracked: false, Note: "Labels no rule matches. Add a rule under RunnerInsights:CustomRules to track them."),
    ];

    private readonly Dictionary<string, RunnerProviderDef> _defs;
    private readonly IReadOnlyList<CustomRunnerRule> _custom;

    public RunnerCatalog(IEnumerable<CustomRunnerRule>? custom = null)
    {
        _custom = (custom ?? []).Where(r => r.Prefix.Length > 0 && r.ProviderId.Length > 0).ToList();
        _defs = BuiltIn.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var r in _custom.Where(r => !_defs.ContainsKey(r.ProviderId)))
            _defs[r.ProviderId] = new RunnerProviderDef(r.ProviderId, r.Name ?? r.ProviderId, RunnerProviderKind.ThirdPartyHosted, r.Unit, RoundingRule.PerJobCeilMinute, Note: "Custom provider defined in configuration.");
    }

    public IReadOnlyCollection<RunnerProviderDef> Providers => _defs.Values;
    public RunnerProviderDef Get(string id) => _defs.TryGetValue(id, out var d) ? d : _defs[Custom];

    [GeneratedRegex(@"^(?<os>ubuntu|windows|macos)-(latest|slim|\d+(\.\d+)?)(-arm)?$", RegexOptions.IgnoreCase)] private static partial Regex StandardHosted();
    [GeneratedRegex(@"^(ubuntu|windows|macos)-.+(-\d+-cores?|-xlarge|-large|-xl|-\d+x)$", RegexOptions.IgnoreCase)] private static partial Regex LargerHosted();
    [GeneratedRegex(@"-(?<n>\d+)c(?:-|$)", RegexOptions.IgnoreCase)] private static partial Regex CoresC();
    [GeneratedRegex(@"(?:^|-)(?<n>\d+)-?vcpu(?:-|$)", RegexOptions.IgnoreCase)] private static partial Regex Vcpu();

    /// <summary>Resolves a job to exactly one provider. The first matching label of the first matching rule wins.</summary>
    public RunnerIdentity Identify(IReadOnlyList<string>? labels, string? runnerName = null)
    {
        var ls = (labels ?? []).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();

        foreach (var rule in _custom)
            if (Find(ls, rule.Prefix) is { } hit)
                return new RunnerIdentity(rule.ProviderId, hit.ToLowerInvariant(), rule.Multiplier <= 0 ? 1 : rule.Multiplier);

        if (Find(ls, "tenki-") is { } t) return new(Tenki, t.ToLowerInvariant(), Cores(t, 2, CoresC(), Vcpu()));
        if (Find(ls, "avrea-") is { } a) return new(Avrea, a.ToLowerInvariant(), Math.Max(1, Cores(a, 2, Vcpu(), CoresC()) / 2d));
        if (Find(ls, "bp-") is { } b) return new(BuildPulse, b.ToLowerInvariant(), Math.Max(1, Cores(b, 2, Vcpu(), CoresC()) / 2d));
        if (Find(ls, "celesto-") is { } c) return new(Celesto, c.ToLowerInvariant(), 1);
        if (Find(ls, "hetzner-arc") is { } h) return new(HetznerArc, h.ToLowerInvariant(), 1);
        if (Find(ls, "blacksmith-") is { } k) return new(Blacksmith, k.ToLowerInvariant(), Math.Max(1, Cores(k, 2, Vcpu(), CoresC()) / 2d));

        foreach (var l in ls)
        {
            if (LargerHosted().IsMatch(l)) return new(GitHubLarger, l.ToLowerInvariant(), 1);
            if (StandardHosted().Match(l) is { Success: true } m)
                return new(GitHub, l.ToLowerInvariant(), m.Groups["os"].Value.ToLowerInvariant() switch { "windows" => 2, "macos" => 10, _ => 1 });
        }
        if (ls.Any(l => l.Equals("self-hosted", StringComparison.OrdinalIgnoreCase)))
            return new(SelfHosted, string.Join(", ", ls.Where(l => !l.Equals("self-hosted", StringComparison.OrdinalIgnoreCase)).DefaultIfEmpty("self-hosted")).ToLowerInvariant(), 1);
        return new(Custom, ls.Count == 0 ? (runnerName ?? "unknown").ToLowerInvariant() : ls[0].ToLowerInvariant(), 1);
    }

    private static string? Find(List<string> labels, string prefix) => labels.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static double Cores(string label, int fallback, params Regex[] patterns)
    {
        foreach (var p in patterns)
            if (p.Match(label) is { Success: true } m && int.TryParse(m.Groups["n"].Value, out var n) && n > 0) return n;
        return fallback;
    }
}
