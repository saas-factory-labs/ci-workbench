using CIWorkbench.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Threading.Tasks;

namespace CIWorkbench.Web.Tests;

[ClassDataSource<WebApplicationFactory<Program>>(Shared = SharedType.PerClass)]
public class WebSmokeTests(WebApplicationFactory<Program> factory)
{
    [Test]
    [Arguments("/")]
    [Arguments("/overview")]
    [Arguments("/repositories")]
    [Arguments("/settings")]
    [Arguments("/repo/example-org/review-scraper-actor")]
    [Arguments("/repo/example-org/platform-monorepo/workflows/1011")]
    [Arguments("/repo/example-org/platform-monorepo/workflows/1011?tab=failures")]
    [Arguments("/repo/example-org/platform-monorepo/runs/1001100")]
    [Arguments("/repos/example-org/platform-monorepo/runs/1001100")]
    public async Task Pages_respond_ok_with_security_headers_and_no_token_in_markup(string path)
    {
        using var client = factory.WithWebHostBuilder(b => b.UseSetting("GitHub:Token", "ghp_unit_test_token_not_real_000000000")).CreateClient();
        var res = await client.GetAsync(path);
        res.EnsureSuccessStatusCode();
        await Assert.That(res.Headers.GetValues("X-Content-Type-Options").Single()).IsEqualTo("nosniff");
        var html = await res.Content.ReadAsStringAsync();
        await Assert.That(html).DoesNotContain("ghp_unit_test_token");
    }

    [Test]
    public async Task Static_component_assets_are_served()
    {
        using var client = factory.CreateClient();
        var css = await client.GetAsync("/_content/CIWorkbench.Components/ciworkbench.css");
        await Assert.That(css.IsSuccessStatusCode).IsTrue();
        await Assert.That(await css.Content.ReadAsStringAsync()).Contains("--ciw-bg");
    }

    [Test]
    public async Task Concept_four_brand_mark_is_served_as_svg()
    {
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/ciworkbench-logo.svg");
        response.EnsureSuccessStatusCode();
        await Assert.That(response.Content.Headers.ContentType?.MediaType).IsEqualTo("image/svg+xml");
        var markup = await response.Content.ReadAsStringAsync();
        await Assert.That(markup).Contains("ciw-ribbon-top");
        await Assert.That(markup).Contains("ciw-ribbon-bottom");
        await Assert.That(markup).Contains("ciw-center-dot");
    }
}

[ClassDataSource<WebApplicationFactory<Program>>(Shared = SharedType.PerClass)]
public class NetworkExposureTests(WebApplicationFactory<Program> factory)
{
    [Test]
    [Arguments(null, true)]
    [Arguments("http://localhost:5090", true)]
    [Arguments("http://127.0.0.1:5090;https://[::1]:7269", true)]
    [Arguments("http://0.0.0.0:5290", false)]
    [Arguments("http://*:5290", false)]
    [Arguments("http://+:5290", false)]
    [Arguments("http://192.168.1.20:5290", false)]
    [Arguments("http://localhost:5090;http://0.0.0.0:5091", false)]
    [Arguments("http://my-laptop.local:5090", false)]
    public async Task Only_loopback_addresses_count_as_local(string? urls, bool loopback) => await Assert.That(NetworkExposure.IsLoopbackOnly(urls)).IsEqualTo(loopback);

    [Test]
    [Arguments("http://localhost:5090", "8080", false, false)]     // explicit loopback URLs win over the ports shortcut (Aspire sets ASPNETCORE_URLS)
    [Arguments(null, "8080", false, true)]                         // HTTP_PORTS alone binds every interface
    [Arguments(null, null, false, false)]
    [Arguments("http://127.0.0.1:5090", null, true, true)]         // an explicit Kestrel endpoint beyond loopback
    public async Task Exposure_follows_kestrel_precedence(string? urls, string? ports, bool remoteEndpoint, bool expected) =>
        await Assert.That(NetworkExposure.IsRemoteReachable(urls, ports, remoteEndpoint ? ["http://0.0.0.0:6000"] : [null, "http://localhost:6000"])).IsEqualTo(expected);

    private bool LiveAvailable(string? urls)
    {
        using var f = factory.WithWebHostBuilder(b => { b.UseSetting("GitHub:Token", "ghp_unit_test_token_not_real_000000000"); if (urls is not null) b.UseSetting("urls", urls); });
        using var scope = f.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppSession>().LiveAvailable;
    }

    [Test]
    public async Task A_token_is_usable_on_loopback() => await Assert.That(LiveAvailable("http://localhost:5090")).IsTrue();

    [Test]
    public async Task A_token_is_dropped_when_the_host_listens_beyond_loopback() => await Assert.That(LiveAvailable("http://0.0.0.0:5290")).IsFalse();
}