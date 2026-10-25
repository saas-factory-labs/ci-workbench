using System.Globalization;
using System.Text.RegularExpressions;
using Bunit;
using CIWorkbench.Components;
using CIWorkbench.Core.Demo;
using CIWorkbench.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CIWorkbench.Web.Tests;

/// <summary>Reads the shipped stylesheets so the design-token contract is enforced by the build, not by eye.</summary>
internal static class Css
{
    private static readonly string Root = FindRoot();
    private static string FindRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "CIWorkbench.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    public static string Components => File.ReadAllText(Path.Combine(Root, "src", "CIWorkbench.Components", "wwwroot", "ciworkbench.css"));
    public static string Host => File.ReadAllText(Path.Combine(Root, "src", "CIWorkbench.Web", "wwwroot", "app.css"));
    public static string Reconnect => File.ReadAllText(Path.Combine(Root, "src", "CIWorkbench.Web", "Components", "Layout", "ReconnectModal.razor.css"));
    public static string ThemeScript => File.ReadAllText(Path.Combine(Root, "src", "CIWorkbench.Components", "wwwroot", "ciw-theme.js"));
    public static string AppShell => File.ReadAllText(Path.Combine(Root, "src", "CIWorkbench.Web", "Components", "App.razor"));

    public const string DarkSelector = ":root, :root[data-theme=\"dark\"]";
    public const string LightSelector = ":root[data-theme=\"light\"]";

    /// <summary>Body of the first rule whose selector is exactly <paramref name="selector"/>.</summary>
    public static string Block(string css, string selector)
    {
        var i = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(i >= 0, $"rule '{selector}' not found");
        var start = css.IndexOf('{', i) + 1;
        return css[start..css.IndexOf('}', start)];
    }

    public static Dictionary<string, string> Tokens(string block) =>
        Regex.Matches(block, @"(--ciw-[\w-]+)\s*:\s*([^;]+);").ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.Trim());

    public static string WithoutPalettes(string css)
    {
        foreach (var sel in new[] { DarkSelector, LightSelector }) css = css.Replace($"{sel} {{{Block(css, sel)}}}", "");
        return Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
    }
}

/// <summary>WCAG 2.x contrast maths over the token values (rgba composited on the surface it actually sits on).</summary>
internal static class Contrast
{
    public static (double R, double G, double B, double A) Parse(string v)
    {
        v = v.Trim();
        if (v.StartsWith('#')) { var h = v[1..]; return (Convert.ToInt32(h[..2], 16), Convert.ToInt32(h[2..4], 16), Convert.ToInt32(h[4..6], 16), 1); }
        var m = Regex.Match(v, @"rgba?\(\s*([\d.]+)\s*,\s*([\d.]+)\s*,\s*([\d.]+)\s*(?:,\s*([\d.]+))?\s*\)");
        Assert.True(m.Success, $"not a colour literal: {v}");
        double P(int g) => double.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        return (P(1), P(2), P(3), m.Groups[4].Success ? P(4) : 1);
    }

    public static (double R, double G, double B) Over((double R, double G, double B, double A) top, (double R, double G, double B, double A) under) =>
        (top.R * top.A + under.R * (1 - top.A), top.G * top.A + under.G * (1 - top.A), top.B * top.A + under.B * (1 - top.A));

    private static double Lin(double c) { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
    public static double Lum((double R, double G, double B) c) => 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    public static double Ratio((double, double, double) a, (double, double, double) b)
    {
        var (l1, l2) = (Lum(a), Lum(b));
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }
}

public class DesignTokenTests
{
    [Fact]
    public void Component_stylesheet_has_no_colour_literals_outside_the_two_palettes()
    {
        var literal = new Regex(@"#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(|\b(?:white|black)\b(?!-)");
        foreach (var (name, css) in new[] { ("ciworkbench.css", Css.WithoutPalettes(Css.Components)), ("app.css", Css.Host), ("ReconnectModal.razor.css", Css.Reconnect) })
        {
            var hits = literal.Matches(Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline)).Select(m => m.Value).Distinct().ToList();
            Assert.True(hits.Count == 0, $"{name} hardcodes colours instead of using --ciw-* tokens: {string.Join(", ", hits)}");
        }
    }

    [Fact]
    public void Light_and_dark_palettes_define_exactly_the_same_tokens()
    {
        var dark = Css.Tokens(Css.Block(Css.Components, Css.DarkSelector));
        var light = Css.Tokens(Css.Block(Css.Components, Css.LightSelector));
        var onlyDark = dark.Keys.Except(light.Keys).ToList(); var onlyLight = light.Keys.Except(dark.Keys).ToList();
        Assert.True(onlyDark.Count == 0, "missing from light: " + string.Join(", ", onlyDark));
        Assert.True(onlyLight.Count == 0, "missing from dark: " + string.Join(", ", onlyLight));
        Assert.True(dark.Count >= 50, "palette unexpectedly small");
    }

    [Fact]
    public void Every_semantic_token_named_in_the_design_brief_exists()
    {
        var all = Css.Tokens(Css.Block(Css.Components, Css.DarkSelector));
        foreach (var t in new[] { "bg", "surface", "surface-raised", "surface-hover", "surface-selected", "text-primary", "text-secondary", "text-muted", "border", "border-strong", "brand", "brand-hover", "brand-subtle", "focus-ring", "success", "danger", "warning", "info", "shadow-sm", "shadow-md" })
            Assert.True(all.ContainsKey("--ciw-" + t), $"--ciw-{t} missing");
    }

    [Fact]
    public void Every_token_that_is_used_is_defined_somewhere()
    {
        var css = Css.Components + Css.Host + Css.Reconnect;
        var defined = Regex.Matches(css, @"(--ciw-[\w-]+)\s*:").Select(m => m.Groups[1].Value).ToHashSet();
        var used = Regex.Matches(css, @"var\((--ciw-[\w-]+)\s*\)").Select(m => m.Groups[1].Value).Distinct();   // no fallback given
        var undefinedTokens = used.Where(u => !defined.Contains(u)).ToList();
        Assert.True(undefinedTokens.Count == 0, "undefined tokens: " + string.Join(", ", undefinedTokens));
    }

    [Fact]
    public void Brand_palette_keeps_the_specified_anchors()
    {
        var dark = Css.Tokens(Css.Block(Css.Components, Css.DarkSelector));
        Assert.Equal("#0e1218", dark["--ciw-bg"]); Assert.Equal("#171e28", dark["--ciw-surface"]); Assert.Equal("#202b39", dark["--ciw-surface-raised"]);
        Assert.Equal("#4f8cff", dark["--ciw-brand"]); Assert.Equal("#78a9ff", dark["--ciw-brand-hover"]); Assert.Equal("#f1f5fb", dark["--ciw-text-primary"]);
        var light = Css.Tokens(Css.Block(Css.Components, Css.LightSelector));
        Assert.Equal("#f7f9fc", light["--ciw-bg"]); Assert.Equal("#ffffff", light["--ciw-surface"]); Assert.Equal("#eef3f9", light["--ciw-surface-raised"]);
        Assert.Equal("#205fcf", light["--ciw-brand"]); Assert.Equal("#162233", light["--ciw-text-primary"]); Assert.Equal("#526477", light["--ciw-text-secondary"]);
    }

    public static TheoryData<string> Themes => new() { Css.DarkSelector, Css.LightSelector };

    [Theory]
    [MemberData(nameof(Themes))]
    public void Text_and_status_colours_meet_WCAG_AA_on_the_surfaces_they_are_used_on(string theme)
    {
        var t = Css.Tokens(Css.Block(Css.Components, theme));
        (double, double, double, double) C(string k) => Contrast.Parse(t[k]);
        (double, double, double) Solid(string k) => Contrast.Over(C(k), (0, 0, 0, 1));
        var failures = new List<string>();
        void Check(string what, (double, double, double) fg, (double, double, double) bg, double min)
        {
            var r = Contrast.Ratio(fg, bg);
            if (r < min) failures.Add($"{what}: {r:0.00} < {min}");
        }

        var grounds = new[] { "--ciw-bg", "--ciw-surface", "--ciw-surface-raised", "--ciw-sidebar", "--ciw-topbar" };
        foreach (var g in grounds)
        {
            var bg = Solid(g);
            Check($"text-primary on {g}", Solid("--ciw-text-primary"), bg, 7);
            Check($"text-secondary on {g}", Solid("--ciw-text-secondary"), bg, 4.5);
            Check($"link on {g}", Solid("--ciw-link"), bg, 4.5);
            if (g is "--ciw-bg" or "--ciw-surface" or "--ciw-sidebar" or "--ciw-topbar") Check($"text-muted on {g}", Solid("--ciw-text-muted"), bg, 4.5);
            Check($"focus ring on {g}", Solid("--ciw-focus-ring"), bg, 3);
            Check($"brand (graphics) on {g}", Solid("--ciw-brand"), bg, 3);
        }
        var surface = Solid("--ciw-surface");
        Check("control border on surface", Solid("--ciw-border-control"), surface, 3);
        Check("control border on page", Solid("--ciw-border-control"), Solid("--ciw-bg"), 3);
        Check("on-brand text on brand", Solid("--ciw-on-brand"), Solid("--ciw-brand"), 4.5);
        Check("on-brand text on brand-hover", Solid("--ciw-on-brand"), Solid("--ciw-brand-hover"), 4.5);

        // Status text on its own tinted badge background, composited over the card surface and over the page.
        foreach (var under in new[] { "--ciw-surface", "--ciw-bg", "--ciw-surface-raised" })
        {
            var u = C(under);
            foreach (var (fg, soft) in new[] { ("--ciw-success", "--ciw-success-soft"), ("--ciw-danger", "--ciw-danger-soft"), ("--ciw-danger-text", "--ciw-danger-soft"), ("--ciw-warning", "--ciw-warning-soft"), ("--ciw-warning-text", "--ciw-warning-soft"), ("--ciw-neutral", "--ciw-neutral-soft"), ("--ciw-brand-text", "--ciw-brand-subtle"), ("--ciw-info", "--ciw-info-soft"), ("--ciw-brand-text", "--ciw-info-soft") })
            {
                var bg = Contrast.Over(C(soft), u);
                var fgc = Contrast.Over(C(fg), (bg.Item1, bg.Item2, bg.Item3, 1));
                Check($"{fg} on {soft} over {under}", fgc, bg, 4.5);
            }
        }
        // Graphical status indicators (dots, bars, ticks) need 3:1 against the surface.
        foreach (var fill in new[] { "--ciw-success-fill", "--ciw-danger-fill", "--ciw-warning-fill", "--ciw-neutral", "--ciw-brand" })
            Check($"{fill} indicator on surface", Solid(fill), surface, 3);
        // Logs.
        var code = Solid("--ciw-code-bg");
        Check("code text", Solid("--ciw-code-fg"), code, 7);
        Check("code line numbers", Solid("--ciw-code-line"), code, 4.5);
        var errBg = Contrast.Over(C("--ciw-code-err-bg"), (code.Item1, code.Item2, code.Item3, 1));
        Check("code error text on error highlight", Solid("--ciw-code-err-fg"), errBg, 4.5);
        Check("code text on error highlight", Solid("--ciw-code-fg"), errBg, 4.5);
        Check("danger-text on code surface", Solid("--ciw-danger-text"), code, 4.5);
        Assert.True(failures.Count == 0, $"{theme}\n" + string.Join("\n", failures));
    }

    [Fact]
    public void Type_scale_never_goes_below_12px_and_headings_follow_the_brief()
    {
        var css = Css.Components + Css.Host;
        foreach (Match m in Regex.Matches(css, @"font-size:\s*([\d.]+)px"))
            Assert.True(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) >= 12, $"font-size {m.Value}");
        var root = Css.Tokens(Css.Block(Css.Components, ".ciw-root"));
        Assert.Equal("24px", root["--ciw-fs-title"]); Assert.Equal("16px", root["--ciw-fs-group"]); Assert.Equal("14px", root["--ciw-fs-name"]);
        Assert.Equal("12px", root["--ciw-fs-label"]);
    }

    [Fact]
    public void Reduced_motion_is_honoured_and_transitions_stay_between_120_and_180_ms()
    {
        Assert.Contains("prefers-reduced-motion: reduce", Css.Components);
        Assert.Contains("prefers-reduced-motion: reduce", Css.Host);
        Assert.Equal("140ms", Css.Tokens(Css.Block(Css.Components, ".ciw-root"))["--ciw-t"]);
    }
}

public class ThemeBootstrapTests
{
    [Fact]
    public void Theme_script_is_an_external_synchronous_script_placed_before_the_stylesheets()
    {
        var shell = Css.AppShell;
        var script = shell.IndexOf("ciw-theme.js", StringComparison.Ordinal);
        var sheet = shell.IndexOf("ciworkbench.css", StringComparison.Ordinal);
        Assert.True(script > 0 && script < sheet, "ciw-theme.js must load in <head> before the stylesheets");
        var tag = shell[shell.LastIndexOf("<script", script, StringComparison.Ordinal)..];
        Assert.DoesNotContain("defer", tag[..tag.IndexOf('>')]); Assert.DoesNotContain("async", tag[..tag.IndexOf('>')]);
        Assert.DoesNotMatch(@"<script(?![^>]*\bsrc=)[^>]*>", shell);   // no inline script: a strict CSP needs no nonce or hash
        Assert.Contains("content=\"dark light\"", shell);
    }

    [Fact]
    public void Theme_script_stores_only_a_non_sensitive_choice_and_defaults_to_system()
    {
        var js = Css.ThemeScript;
        Assert.Contains("'ciw:theme'", js);
        Assert.Contains("return 'system'", js);
        Assert.Contains("prefers-color-scheme: dark", js);
        Assert.DoesNotContain("token", js, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("eval(", js);
    }

    [Fact]
    public void ThemeState_defaults_to_System_and_round_trips_its_stored_keys()
    {
        var s = new ThemeState();
        Assert.Equal(ThemePreference.System, s.Preference);
        foreach (var p in Enum.GetValues<ThemePreference>()) Assert.Equal(p, ThemeState.Parse(ThemeState.ToKey(p)));
        Assert.Equal(ThemePreference.System, ThemeState.Parse(null));
        Assert.Equal(ThemePreference.System, ThemeState.Parse("<script>"));
    }

    [Fact]
    public void ThemeState_resolves_explicit_choices_regardless_of_the_OS_and_System_to_the_OS()
    {
        var s = new ThemeState();
        s.SetResolvedFromHost(false);
        Assert.False(s.ResolvedDark);
        s.SetResolvedFromHost(true);
        Assert.True(s.ResolvedDark);
        s.Preference = ThemePreference.Light; Assert.False(s.ResolvedDark);
        s.SetResolvedFromHost(false);
        s.Preference = ThemePreference.Dark; Assert.True(s.ResolvedDark);
        var raised = 0; s.Changed += () => raised++;
        s.Preference = ThemePreference.Dark; Assert.Equal(0, raised);   // no-op writes do not notify
        s.Preference = ThemePreference.System; Assert.Equal(1, raised);
    }

    [Fact]
    public async Task Served_shell_references_the_theme_script_before_any_stylesheet()
    {
        using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/");
        var script = html.IndexOf("ciw-theme", StringComparison.Ordinal);
        Assert.True(script > 0 && script < html.IndexOf("rel=\"stylesheet\"", StringComparison.Ordinal));
        Assert.Contains("<html lang=\"en\"", html);
        var ok = await client.GetAsync(Regex.Match(html, "src=\"(_content/CIWorkbench.Components/ciw-theme[^\"]*\\.js)\"").Groups[1].Value);
        Assert.True(ok.IsSuccessStatusCode);
    }
}

public class ThemeComponentTests : BunitContext
{
    public ThemeComponentTests()
    {
        Services.AddCIWorkbenchComponents();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Toggle_offers_Light_Dark_System_with_System_selected_first()
    {
        var cut = Render<ThemeToggle>();
        var buttons = cut.FindAll("button");
        Assert.Equal(["Light", "Dark", "System"], buttons.Select(b => b.TextContent.Trim()));
        Assert.Equal("true", cut.Find("[data-theme-option=system]").GetAttribute("aria-pressed"));
        Assert.Equal("false", cut.Find("[data-theme-option=dark]").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Toggle_and_menu_share_one_source_of_truth()
    {
        var state = Services.GetRequiredService<ThemeState>();
        var toggle = Render<ThemeToggle>();
        var menu = Render<ThemeMenu>();
        toggle.Find("[data-theme-option=dark]").Click();
        Assert.Equal(ThemePreference.Dark, state.Preference);
        Assert.Contains("Theme: Dark", menu.Find("[data-testid=theme-menu-button]").GetAttribute("aria-label"));
        menu.Find("[data-testid=theme-menu-button]").Click();
        Assert.Equal("true", menu.Find("[data-theme-option=dark]").GetAttribute("aria-checked"));
        menu.Find("[data-theme-option=light]").Click();
        Assert.Equal(ThemePreference.Light, state.Preference);
        Assert.Equal("true", toggle.Find("[data-theme-option=light]").GetAttribute("aria-pressed"));
    }

    [Fact]
    public void Menu_is_a_labelled_menu_of_radio_items_and_closes_on_choice_and_escape()
    {
        var cut = Render<ThemeMenu>();
        Assert.Empty(cut.FindAll("[role=menu]"));
        var button = cut.Find("[data-testid=theme-menu-button]");
        Assert.Equal("menu", button.GetAttribute("aria-haspopup"));
        Assert.Equal("false", button.GetAttribute("aria-expanded"));
        button.Click();
        Assert.Equal("true", cut.Find("[data-testid=theme-menu-button]").GetAttribute("aria-expanded"));
        var items = cut.FindAll("[role=menuitemradio]");
        Assert.Equal(3, items.Count);
        Assert.Single(items, i => i.GetAttribute("aria-checked") == "true");
        cut.Find("[role=menu]").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(cut.FindAll("[role=menu]"));
        cut.Find("[data-testid=theme-menu-button]").Click();
        cut.Find("[data-theme-option=dark]").Click();
        Assert.Empty(cut.FindAll("[role=menu]"));
    }

    [Fact]
    public void Changing_theme_does_not_touch_other_UI_state()
    {
        var prefs = Services.GetRequiredService<UiPreferences>();
        prefs.Density = Density.Compact; prefs.SetExpanded("group|x|y", true); prefs.StatusFilter = "Failed";
        Services.GetRequiredService<ThemeState>().Preference = ThemePreference.Dark;
        Assert.Equal(Density.Compact, prefs.Density); Assert.True(prefs.IsExpanded("group|x|y")); Assert.Equal("Failed", prefs.StatusFilter);
    }
}

public class WorkflowOutcomesTests : BunitContext
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    // newest first, like the provider returns them
    private static WorkflowRunInfo Run(int n, RunState s) => new(1000 + n, 7, ".github/workflows/ci.yml", "CI", n, 1, s, "push", "main", "abc1234", "msg", "dev",
        Now.AddHours(-(100 - n)), Now.AddHours(-(100 - n)), Now.AddHours(-(100 - n)).AddMinutes(3), $"https://github.com/o/r/actions/runs/{1000 + n}");

    private IRenderedComponent<WorkflowOutcomes> Draw(IReadOnlyList<WorkflowRunInfo> runs, bool truncated = false) =>
        Render<WorkflowOutcomes>(p => p.Add(c => c.Runs, runs).Add(c => c.Truncated, truncated).Add(c => c.Now, Now).Add(c => c.RunHref, r => $"/runs/{r.Id}"));

    [Fact]
    public void Ninety_four_failures_in_a_hundred_runs_say_so_in_words_with_the_window_named()
    {
        var runs = Enumerable.Range(1, 100).Reverse().Select(n => Run(n, n <= 6 ? RunState.Success : RunState.Failure)).ToList();   // #100..#7 failed, #6..#1 passed
        var cut = Draw(runs);
        Assert.Contains("newest 100 loaded runs, not lifetime", cut.Find("[data-testid=outcomes-window]").TextContent);
        Assert.Equal("Of the 100 loaded runs: 6 succeeded, 94 failed.", cut.Find(".ciw-outcomes-bar").GetAttribute("aria-label"));
        Assert.Contains("94 failures in a row", cut.Find("[data-testid=outcomes-streak]").TextContent);
        Assert.Contains("#6", cut.Find("[data-testid=outcomes-lastok]").TextContent);
        Assert.Equal(60, cut.FindAll(".ciw-tick").Count);   // the timeline is bounded; the table below holds every run
        Assert.All(cut.FindAll(".ciw-tick"), t => Assert.False(string.IsNullOrWhiteSpace(t.GetAttribute("data-tip"))));
        Assert.Equal("img", cut.Find(".ciw-timeline").GetAttribute("role"));
    }

    [Fact]
    public void Cancelled_skipped_running_and_queued_runs_are_counted_separately_and_never_as_failures()
    {
        var runs = new[] { Run(9, RunState.Queued), Run(8, RunState.InProgress), Run(7, RunState.Cancelled), Run(6, RunState.Skipped), Run(5, RunState.Neutral), Run(4, RunState.Success), Run(3, RunState.Failure) };
        var cut = Draw(runs);
        var label = cut.Find(".ciw-outcomes-bar").GetAttribute("aria-label")!;
        Assert.Contains("1 succeeded", label); Assert.Contains("1 failed", label); Assert.Contains("1 cancelled", label);
        Assert.Contains("2 skipped or neutral", label); Assert.Contains("1 running", label); Assert.Contains("1 queued", label);
        Assert.Contains("Newest finished run", cut.Find(".ciw-outcomes-facts").TextContent);   // a single success is not a "streak"
        Assert.NotEmpty(cut.FindAll(".ciw-seg.cancel")); Assert.NotEmpty(cut.FindAll(".ciw-seg.skip")); Assert.NotEmpty(cut.FindAll(".ciw-seg.wait"));
    }

    [Fact]
    public void A_truncated_history_is_marked_as_a_lower_bound()
    {
        var runs = Enumerable.Range(1, 10).Reverse().Select(n => Run(n, RunState.Failure)).ToList();
        var cut = Draw(runs, truncated: true);
        Assert.Contains("newest 10+ loaded runs", cut.Find("[data-testid=outcomes-window]").TextContent);
        Assert.Contains("At least 10 failures in a row", cut.Find("[data-testid=outcomes-streak]").TextContent);
        Assert.Contains("none in the loaded runs", cut.Find("[data-testid=outcomes-lastok]").TextContent);
    }

    [Fact]
    public void No_runs_renders_nothing_rather_than_an_empty_chart() => Assert.Empty(Draw([]).FindAll("section"));
}

public class RunIndicatorTests : BunitContext
{
    [Fact]
    public async Task Preview_run_indicators_are_named_links_to_the_run_with_a_tooltip()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));
        Services.AddSingleton<TimeProvider>(clock); Services.AddCIWorkbenchComponents(); Services.AddSingleton<ICiProvider>(new DemoCiProvider(clock));
        JSInterop.Mode = JSRuntimeMode.Loose;
        var cut = Render<RepositoryWorkspace>(p => { p.Add(c => c.Repo, DemoCiProvider.Monorepo); p.Add(c => c.AutoRefresh, false); });
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".ciw-wf-toggle")), TimeSpan.FromSeconds(10));
        cut.FindAll(".ciw-wf-toggle")[0].Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid=quick-preview] a.ciw-sdot")), TimeSpan.FromSeconds(10));
        await Task.CompletedTask;
        var dots = cut.FindAll("[data-testid=quick-preview] a.ciw-sdot");
        Assert.All(dots, d =>
        {
            Assert.Matches(@"/runs/\d+", d.GetAttribute("href"));
            Assert.Matches(@"^Run #\d+, (Success|Failed|Cancelled|Skipped|Running|Queued|Neutral), ", d.GetAttribute("aria-label"));
            Assert.Contains("#", d.GetAttribute("data-tip"));
        });
        Assert.Equal("group", cut.Find("[data-testid=quick-preview] .ciw-strip").GetAttribute("role"));
    }
}
