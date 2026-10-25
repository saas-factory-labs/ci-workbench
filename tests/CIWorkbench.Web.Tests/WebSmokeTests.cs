using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CIWorkbench.Web.Tests;

public class WebSmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/repositories")]
    [InlineData("/settings")]
    [InlineData("/repo/example-org/review-scraper-actor")]
    [InlineData("/repo/example-org/platform-monorepo/workflows/1011")]
    [InlineData("/repo/example-org/platform-monorepo/workflows/1011?tab=failures")]
    [InlineData("/repo/example-org/platform-monorepo/runs/1001100")]
    [InlineData("/repos/example-org/platform-monorepo/runs/1001100")]
    public async Task Pages_respond_ok_with_security_headers_and_no_token_in_markup(string path)
    {
        using var client = factory.WithWebHostBuilder(b => b.UseSetting("GitHub:Token", "ghp_unit_test_token_not_real_000000000")).CreateClient();
        var res = await client.GetAsync(path);
        res.EnsureSuccessStatusCode();
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        var html = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("ghp_unit_test_token", html);
    }

    [Fact]
    public async Task Static_component_assets_are_served()
    {
        using var client = factory.CreateClient();
        var css = await client.GetAsync("/_content/CIWorkbench.Components/ciworkbench.css");
        Assert.True(css.IsSuccessStatusCode);
        Assert.Contains("--ciw-bg", await css.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Concept_four_brand_mark_is_served_as_svg()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/ciworkbench-logo.svg");
        response.EnsureSuccessStatusCode();
        Assert.Equal("image/svg+xml", response.Content.Headers.ContentType?.MediaType);
        var markup = await response.Content.ReadAsStringAsync();
        Assert.Contains("ciw-ribbon-top", markup);
        Assert.Contains("ciw-ribbon-bottom", markup);
        Assert.Contains("ciw-center-dot", markup);
    }
}

public class NetworkExposureTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("http://localhost:5090", true)]
    [InlineData("http://127.0.0.1:5090;https://[::1]:7269", true)]
    [InlineData("http://0.0.0.0:5290", false)]
    [InlineData("http://*:5290", false)]
    [InlineData("http://+:5290", false)]
    [InlineData("http://192.168.1.20:5290", false)]
    [InlineData("http://localhost:5090;http://0.0.0.0:5091", false)]
    [InlineData("http://my-laptop.local:5090", false)]
    public void Only_loopback_addresses_count_as_local(string? urls, bool loopback) => Assert.Equal(loopback, NetworkExposure.IsLoopbackOnly(urls));

    [Theory]
    [InlineData("http://localhost:5090", "8080", false, false)]     // explicit loopback URLs win over the ports shortcut (Aspire sets ASPNETCORE_URLS)
    [InlineData(null, "8080", false, true)]                         // HTTP_PORTS alone binds every interface
    [InlineData(null, null, false, false)]
    [InlineData("http://127.0.0.1:5090", null, true, true)]         // an explicit Kestrel endpoint beyond loopback
    public void Exposure_follows_kestrel_precedence(string? urls, string? ports, bool remoteEndpoint, bool expected) =>
        Assert.Equal(expected, NetworkExposure.IsRemoteReachable(urls, ports, remoteEndpoint ? ["http://0.0.0.0:6000"] : [null, "http://localhost:6000"]));

    private bool LiveAvailable(string? urls)
    {
        using var f = factory.WithWebHostBuilder(b => { b.UseSetting("GitHub:Token", "ghp_unit_test_token_not_real_000000000"); if (urls is not null) b.UseSetting("urls", urls); });
        using var scope = f.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppSession>().LiveAvailable;
    }

    [Fact]
    public void A_token_is_usable_on_loopback() => Assert.True(LiveAvailable("http://localhost:5090"));

    [Fact]
    public void A_token_is_dropped_when_the_host_listens_beyond_loopback() => Assert.False(LiveAvailable("http://0.0.0.0:5290"));
}
