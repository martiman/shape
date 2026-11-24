using Shape.Http;
using Xunit;

namespace Shape.Tests;

public sealed class DocsUiPageTests
{
    [Fact]
    public void Render_produces_selfcontained_page_pointing_at_the_spec()
    {
        var html = DocsUiPage.Render("My.Service", "/api?openapi");

        Assert.StartsWith("<!doctype html>", html);
        Assert.Contains("My.Service — API reference", html);
        Assert.Contains("\"/api?openapi\"", html);              // JSON-encoded spec URL in the JS
        Assert.Contains(DocsUiPage.DefaultAssetUrl, html);      // Scalar CDN default
        Assert.Contains("shape_docs_token", html);              // token prompt/retry wiring
        Assert.Contains("createApiReference", html);
    }

    [Fact]
    public void Render_honors_custom_asset_url_and_escapes_values()
    {
        var html = DocsUiPage.Render("A<B&C", "/api?openapi", "https://intranet.local/scalar.js");

        Assert.Contains("https://intranet.local/scalar.js", html);
        Assert.DoesNotContain(DocsUiPage.DefaultAssetUrl, html);
        Assert.Contains("A&lt;B&amp;C", html);                  // HTML-encoded title
    }
}
