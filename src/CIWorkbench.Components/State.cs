using CIWorkbench.Core.Demo;
using CIWorkbench.Core.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CIWorkbench.Components;

public enum Density { Comfortable, Compact }

public enum WorkspaceView { Groups, Failures }

/// <summary>Panel = quick inspector beside a list; Page = the same diagnostics inside a full run page.</summary>
public enum InspectorMode { Panel, Page }

/// <summary>Tab and filters of the workflow details page; hosts map it to and from their URL.</summary>
public sealed record WorkflowDetailsQuery(string? Tab = null, string? Q = null, string? Status = null, string? Branch = null, string? Event = null)
{
    public static readonly WorkflowDetailsQuery Empty = new();
}

/// <summary>What the user picked; hosts decide where the inspector lives.</summary>
public sealed record RunSelection(RepoRef Repo, WorkflowRunInfo Run, string? FocusJob = null);

/// <summary>Non-sensitive, per-session UI preferences. Hosts persist/restore them however they like (the standalone host uses localStorage).</summary>
public sealed class UiPreferences
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private Density _density = Density.Comfortable;
    private bool _inspectorOpen;
    private string _statusFilter = "All";

    public event Action? Changed;

    /// <summary>Status filter chip of the workspace and the repository it was chosen for; survives a trip to a detail page and back.</summary>
    public string StatusFilter { get => _statusFilter; set { if (_statusFilter != value) { _statusFilter = value; Changed?.Invoke(); } } }
    public RepoRef? FilterRepo { get; set; }

    public Density Density { get => _density; set { if (_density != value) { _density = value; Changed?.Invoke(); } } }
    public bool InspectorOpen { get => _inspectorOpen; set { if (_inspectorOpen != value) { _inspectorOpen = value; Changed?.Invoke(); } } }
    public IReadOnlyCollection<string> ExpandedKeys => _expanded;

    public bool IsExpanded(string key) => _expanded.Contains(key);
    public void SetExpanded(string key, bool expanded)
    {
        if (expanded ? _expanded.Add(key) : _expanded.Remove(key)) Changed?.Invoke();
    }
    public void Toggle(string key) => SetExpanded(key, !IsExpanded(key));
    public void RestoreExpanded(IEnumerable<string> keys)
    {
        var added = false;
        foreach (var k in keys) added |= _expanded.Add(k);
        if (added) Changed?.Invoke();   // components that rendered before the preferences arrived (slower browsers) must pick them up
    }
}

/// <summary>User choice for the colour theme. <see cref="System"/> follows the operating system and is the default.</summary>
public enum ThemePreference { System, Light, Dark }

/// <summary>
/// The one source of truth for the theme in a circuit. The host persists <see cref="Preference"/> (localStorage "ciw:theme", via ciw-theme.js)
/// and reports what System resolved to; components only read and set the preference. Holds no credentials, only a UI choice.
/// </summary>
public sealed class ThemeState
{
    private ThemePreference _preference = ThemePreference.System;
    private bool _resolvedDark = true;

    public event Action? Changed;

    public ThemePreference Preference { get => _preference; set { if (_preference != value) { _preference = value; Changed?.Invoke(); } } }
    /// <summary>What is actually rendered: the preference, or the OS setting when the preference is System. Reported by the host after the browser resolves it.</summary>
    public bool ResolvedDark => _preference switch { ThemePreference.Dark => true, ThemePreference.Light => false, _ => _resolvedDark };
    public void SetResolvedFromHost(bool dark) { if (_resolvedDark != dark) { _resolvedDark = dark; Changed?.Invoke(); } }

    public static string ToKey(ThemePreference p) => p switch { ThemePreference.Light => "light", ThemePreference.Dark => "dark", _ => "system" };
    public static ThemePreference Parse(string? key) => key switch { "light" => ThemePreference.Light, "dark" => ThemePreference.Dark, _ => ThemePreference.System };
}

/// <summary>Shared selection state between a workspace and whichever host panel renders the inspector.</summary>
public sealed class InspectorState
{
    public RunSelection? Selection { get; private set; }
    public event Action? Changed;
    public void Select(RunSelection s) { Selection = s; Changed?.Invoke(); }
    public void Clear() { Selection = null; Changed?.Invoke(); }
}

/// <summary>Lets a host's topbar trigger a refresh and read back "last updated" without knowing the workspace.</summary>
public sealed class WorkspaceSignals
{
    private Action? _refresh;

    /// <summary>
    /// Pages that can refresh themselves subscribe here. Subscribing or unsubscribing raises <see cref="Changed"/>, so the host's Refresh button
    /// enables and disables itself as pages come and go instead of depending on unrelated state such as the selected repository.
    /// </summary>
    public event Action? RefreshRequested
    {
        add { _refresh += value; Changed?.Invoke(); }
        remove { _refresh -= value; Changed?.Invoke(); }
    }
    public event Action? Changed;
    public DateTimeOffset? LastUpdated { get; private set; }
    public bool IsLoading { get; private set; }
    public string? SearchText { get; private set; }
    /// <summary>True while at least one mounted page handles refresh requests.</summary>
    public bool CanRefresh => _refresh is not null;
    public void RequestRefresh() => _refresh?.Invoke();
    /// <summary>Forgets what belonged to the previous data source (last update time, loading flag, search text).</summary>
    public void Reset() { LastUpdated = null; IsLoading = false; SearchText = null; Changed?.Invoke(); }
    public void ReportLoading(bool loading) { IsLoading = loading; Changed?.Invoke(); }
    public void ReportUpdated(DateTimeOffset at) { LastUpdated = at; IsLoading = false; Changed?.Invoke(); }
    public void SetSearch(string? text) { SearchText = text; Changed?.Invoke(); }
}

public static class CIWorkbenchComponentsExtensions
{
    /// <summary>Registers the per-circuit state used by the reusable components. Hosts must also register an <see cref="ICiProvider"/>.</summary>
    public static IServiceCollection AddCIWorkbenchComponents(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<UiPreferences>();
        services.TryAddScoped<ThemeState>();
        services.TryAddScoped<InspectorState>();
        services.TryAddScoped<WorkspaceSignals>();
        services.TryAddScoped(sp => new CachedCi(sp.GetRequiredService<ICiProvider>(), sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<CiwRoutes>();
        return services;
    }

    /// <summary>
    /// Everything an embedding host needs for the workspace components (Overview, repository, workflow and run views, Runner Usage), on top of
    /// <see cref="AddCIWorkbenchComponents"/>. Requires <c>AddCIWorkbenchGitHub</c>. Registers a scoped <see cref="ICiProvider"/> that
    /// follows the session's data source: live GitHub when a credential is configured (set <c>CIWorkbench:Mode</c> to <c>live</c>), otherwise demo data.
    /// The standalone host registers the same services.
    /// </summary>
    public static IServiceCollection AddCIWorkbenchWorkspace(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddCIWorkbenchComponents();
        services.TryAddScoped<DemoCiProvider>(sp => new DemoCiProvider(sp.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<AppSession>();
        services.TryAddScoped<OverviewData>();
        services.AddRunnerInsights(configuration);
        services.TryAddScoped<ICiProvider, SwitchableCiProvider>();
        return services;
    }
}

public static class Fmt
{
    public static string Ago(DateTimeOffset? at, DateTimeOffset now)
    {
        if (at is null) return "—";
        var d = now - at.Value;
        if (d < TimeSpan.Zero) d = TimeSpan.Zero;
        if (d.TotalSeconds < 45) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)Math.Max(1, Math.Round(d.TotalMinutes))} min ago";
        if (d.TotalHours < 24) return d.TotalHours < 1.5 ? "1 hour ago" : $"{(int)Math.Round(d.TotalHours)} hours ago";
        if (d.TotalDays < 2) return "yesterday";
        return $"{(int)d.TotalDays} days ago";
    }

    public static string Duration(TimeSpan? d)
    {
        if (d is null) return "—";
        var t = d.Value;
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds}s";
        if (t.TotalHours < 1) return $"{(int)t.TotalMinutes}m {t.Seconds:00}s";
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }

    public static string When(DateTimeOffset at) => at.UtcDateTime.ToString("MMM dd, yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC";

    /// <summary>Table-friendly timestamp; the column header states the time zone.</summary>
    public static string WhenShort(DateTimeOffset at) => at.UtcDateTime.ToString("MMM dd, yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    public static string Percent(double? rate) => rate is null ? "—" : $"{Math.Round(rate.Value * 100)}%";

    public static string StateLabel(RunState s) => s switch
    {
        RunState.Success => "Success", RunState.Failure => "Failed", RunState.Cancelled => "Cancelled", RunState.Skipped => "Skipped",
        RunState.InProgress => "Running", RunState.Queued => "Queued", _ => "Neutral",
    };

    public static string StateClass(RunState s) => s switch
    {
        RunState.Success => "ok", RunState.Failure => "fail", RunState.Cancelled or RunState.Skipped or RunState.Neutral => "muted",
        RunState.Queued => "wait",
        _ => "active",
    };
}
