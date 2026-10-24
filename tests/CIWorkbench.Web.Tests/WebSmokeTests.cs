using Microsoft.AspNetCore.Mvc.Testing;

namespace CIWorkbench.Web.Tests;

public class WebSmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/repositories")]
    [InlineData("/settings")]
    [InlineData("/repo/example-org/review-scraper-actor")]
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
}
