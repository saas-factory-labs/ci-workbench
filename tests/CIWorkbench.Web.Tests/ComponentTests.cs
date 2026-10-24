using Bunit;
using Microsoft.AspNetCore.Components;
using CIWorkbench.Components;
using CIWorkbench.Core.Demo;
using CIWorkbench.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CIWorkbench.Web.Tests;

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

public class ComponentTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    public ComponentTests()
    {
        var clock = new FixedClock(Now);
        Services.AddSingleton<TimeProvider>(clock);
        Services.AddCIWorkbenchComponents();
        Services.AddSingleton<ICiProvider>(new DemoCiProvider(clock));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private async Task<IRenderedComponent<RepositoryWorkspace>> RenderWorkspace(RepoRef repo, Action<ComponentParameterCollectionBuilder<RepositoryWorkspace>>? more = null)
    {
        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, repo); p.Add(c => c.AutoRefresh, false); more?.Invoke(p); });
        await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-group]")), TimeSpan.FromSeconds(10));
        return cut;
    }

    [Fact]
    public async Task Monorepo_renders_groups_collapsed_and_workflows_only_when_expanded()
    {
        var cut = await RenderWorkspace(DemoCiProvider.Monorepo);
        var all = cut.FindAll("[data-group]");
        Assert.True(all.Count >= 5);
        var before = cut.FindAll("[data-workflow]").Count;
        cut.Find("[data-group] .ciw-group-head").Click();
        Assert.True(cut.FindAll("[data-workflow]").Count > before);
    }

    [Fact]
    public async Task Expand_state_is_preserved_across_rerender()
    {
        var cut = await RenderWorkspace(DemoCiProvider.Monorepo);
        var prefs = Services.GetRequiredService<UiPreferences>();
        cut.Find("[data-group] .ciw-group-head").Click();
        var open = prefs.ExpandedKeys.Count(k => k.StartsWith("group|"));
        cut.Render();
        Assert.Equal(open, prefs.ExpandedKeys.Count(k => k.StartsWith("group|")));
        Assert.Equal("true", cut.Find("[data-group] .ciw-group-head").GetAttribute("aria-expanded"));
        cut.Find("[data-group] .ciw-group-head").Click();
        Assert.Equal("false", cut.Find("[data-group] .ciw-group-head").GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task Search_filters_workflows_and_status_chip_filters_by_latest_state()
    {
        var cut = await RenderWorkspace(DemoCiProvider.Monorepo);
        cut.Find("button[data-filter=Failed]").Click();
        cut.Find(".ciw-toolbar .ciw-btn.sm").Click(); // Expand all
        var failed = cut.FindAll("[data-workflow]").Select(e => e.GetAttribute("data-workflow")).ToList();
        Assert.NotEmpty(failed);
        Assert.Contains(".github/workflows/container-scan.yml", failed);
        Services.GetRequiredService<WorkspaceSignals>().SetSearch("container");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-workflow]")));
    }

    [Fact]
    public async Task Actor_repo_shows_banner_two_plus_series_and_selecting_a_run_raises_callback()
    {
        RunSelection? picked = null;
        var cut = await RenderWorkspace(DemoCiProvider.ActorRepo, p => p.Add(c => c.OnRunSelected, EventCallback.Factory.Create<RunSelection>(this, s => picked = s)));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=failure-banner]")));
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll("[data-series]").Count));
        Assert.Single(cut.FindAll("[data-oneoff]"));
        cut.Find("[data-series]").Click();
        Assert.NotEmpty(cut.FindAll(".ciw-series-body"));
        cut.Find("tr.ciw-row").Click();
        Assert.NotNull(picked);
        Assert.Equal(DemoCiProvider.ActorRepo, picked!.Repo);
    }

    [Fact]
    public async Task Inspector_shows_failed_jobs_first_and_loads_logs_only_on_demand()
    {
        var provider = Services.GetRequiredService<ICiProvider>();
        var run = (await provider.ListRecentRunsAsync(DemoCiProvider.ActorRepo, 10, default)).First(r => r.State == RunState.Failure);
        var cut = Render<RunInspector>(p => p.Add(c => c.Selection, new RunSelection(DemoCiProvider.ActorRepo, run, "scenario-tests")));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ciw-jobitem")));
        var first = cut.FindAll(".ciw-jobitem")[0];
        Assert.Contains("fail", first.ClassList);
        Assert.Empty(cut.FindAll(".ciw-log"));                       // no log fetched yet
        cut.FindAll("button").First(b => b.TextContent.Contains("Load log excerpt")).Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ciw-log")));
        Assert.Contains("Copy context for coding agent", cut.Markup);
    }

    [Fact]
    public void Inspector_without_selection_shows_placeholder() =>
        Assert.Contains("Select a run", Render<RunInspector>().Markup);

    [Fact]
    public void Density_toggle_switches_preferences_without_reload()
    {
        Assert.Equal("true", Render<DensityToggle>().FindAll("button")[0].GetAttribute("aria-pressed"));
        var prefs = Services.GetRequiredService<UiPreferences>();
        var cut = Render<DensityToggle>();
        cut.FindAll("button").First(b => b.TextContent == "Compact").Click();
        Assert.Equal(Density.Compact, prefs.Density);
        Assert.Contains("on", cut.FindAll("button").First(b => b.TextContent == "Compact").ClassList);
    }

    [Fact]
    public async Task Failures_view_lists_series_per_workflow()
    {
        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.ActorRepo); p.Add(c => c.View, WorkspaceView.Failures); p.Add(c => c.AutoRefresh, false); });
        await cut.WaitForAssertionAsync(() => Assert.Equal(2, cut.FindAll("[data-series]").Count), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Components_do_not_depend_on_host_layout_or_routes()
    {
        var asm = typeof(RepositoryWorkspace).Assembly;
        Assert.DoesNotContain(asm.GetTypes(), t => t.GetCustomAttributes(typeof(Microsoft.AspNetCore.Components.RouteAttribute), true).Length > 0);
        Assert.DoesNotContain(asm.GetReferencedAssemblies(), a => a.Name!.Contains("SaaS", StringComparison.OrdinalIgnoreCase) || a.Name!.Contains("AppBlueprint", StringComparison.OrdinalIgnoreCase));
    }
}
