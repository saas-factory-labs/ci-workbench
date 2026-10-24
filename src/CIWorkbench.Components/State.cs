using CIWorkbench.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CIWorkbench.Components;

public enum Density { Comfortable, Compact }

public enum WorkspaceView { Groups, Failures }

/// <summary>What the user picked; hosts decide where the inspector lives.</summary>
public sealed record RunSelection(RepoRef Repo, WorkflowRunInfo Run, string? FocusJob = null);

/// <summary>Non-sensitive, per-session UI preferences. Hosts persist/restore them however they like (the standalone host uses localStorage).</summary>
public sealed class UiPreferences
{
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private Density _density = Density.Comfortable;
    private bool _inspectorOpen;

    public event Action? Changed;

    public Density Density { get => _density; set { if (_density != value) { _density = value; Changed?.Invoke(); } } }
    public bool InspectorOpen { get => _inspectorOpen; set { if (_inspectorOpen != value) { _inspectorOpen = value; Changed?.Invoke(); } } }
    public IReadOnlyCollection<string> ExpandedKeys => _expanded;

    public bool IsExpanded(string key) => _expanded.Contains(key);
    public void SetExpanded(string key, bool expanded)
    {
        if (expanded ? _expanded.Add(key) : _expanded.Remove(key)) Changed?.Invoke();
    }
    public void Toggle(string key) => SetExpanded(key, !IsExpanded(key));
    public void RestoreExpanded(IEnumerable<string> keys) { foreach (var k in keys) _expanded.Add(k); }
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
    public event Action? RefreshRequested;
    public event Action? Changed;
    public DateTimeOffset? LastUpdated { get; private set; }
    public bool IsLoading { get; private set; }
    public string? SearchText { get; private set; }
    public void RequestRefresh() => RefreshRequested?.Invoke();
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
        services.TryAddScoped<InspectorState>();
        services.TryAddScoped<WorkspaceSignals>();
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
