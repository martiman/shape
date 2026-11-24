using System.Text.Json;

namespace Shape.Http;

/// <summary>
/// Interactive API-reference page served at <c>GET {endpoint}?docs</c> when
/// <c>ShapeConfig.DocsUi</c> is on. One self-contained HTML page, zero NuGet
/// dependencies: it loads the Scalar viewer from a CDN (overridable via
/// <c>docs_ui_asset_url</c> for air-gapped deployments) and feeds it the
/// existing <c>?openapi</c> document. The page itself contains no schema data,
/// so it is served without auth — the spec fetch stays gated by
/// <c>manifest_roles</c>; when that fetch answers 401/403 the page prompts for
/// a Bearer token (stored in localStorage) and retries.
/// </summary>
public static class DocsUiPage
{
    /// <summary>Default viewer bundle — pinned to the Scalar standalone build.</summary>
    public const string DefaultAssetUrl = "https://cdn.jsdelivr.net/npm/@scalar/api-reference";

    public static string Render(string serviceName, string specUrl, string? assetUrl = null)
    {
        var asset = string.IsNullOrWhiteSpace(assetUrl) ? DefaultAssetUrl : assetUrl;
        // JSON-encode every interpolated value — they land inside JS string
        // literals / HTML, and the config values are operator-supplied.
        var titleHtml = System.Net.WebUtility.HtmlEncode(serviceName);
        var assetHtml = System.Net.WebUtility.HtmlEncode(asset);
        var specJs = JsonSerializer.Serialize(specUrl);
        var assetJs = JsonSerializer.Serialize(asset);

        return $$"""
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{titleHtml}} — API reference</title>
<style>body{margin:0;font-family:system-ui,sans-serif}#msg{padding:2rem;color:#555}</style>
</head>
<body>
<div id="app"><p id="msg">Loading API reference…</p></div>
<script src="{{assetHtml}}"></script>
<script>
(function () {
  var specUrl = {{specJs}};
  var assetUrl = {{assetJs}};
  var storageKey = 'shape_docs_token';
  function fail(text) { document.getElementById('msg').textContent = text; }
  function render(specText, token) {
    if (!window.Scalar || !window.Scalar.createApiReference) {
      fail('Could not load the Scalar viewer from ' + assetUrl +
        ' — check network access or set docs_ui_asset_url to a self-hosted bundle.');
      return;
    }
    document.getElementById('app').innerHTML = '';
    window.Scalar.createApiReference('#app', {
      content: specText,
      authentication: token ? { securitySchemes: { bearerAuth: { token: token } } } : undefined
    });
  }
  function load() {
    var token = (localStorage.getItem(storageKey) || '').trim();
    fetch(specUrl, token ? { headers: { Authorization: 'Bearer ' + token } } : {})
      .then(function (res) {
        if (res.status === 401 || res.status === 403) {
          localStorage.removeItem(storageKey);
          var entered = prompt('This API requires authentication to read its schema.\n' +
            'Paste a Bearer token (e.g. the access_token from auth.exchange or auth.token):');
          if (entered && entered.trim()) { localStorage.setItem(storageKey, entered.trim()); load(); }
          else { fail('Unauthorized — reload the page to enter a token.'); }
          return null;
        }
        if (!res.ok) { fail('Failed to load ' + specUrl + ' (HTTP ' + res.status + ').'); return null; }
        return res.text();
      })
      .then(function (specText) {
        if (specText) { render(specText, (localStorage.getItem(storageKey) || '').trim()); }
      })
      .catch(function (err) { fail('Failed to load ' + specUrl + ': ' + err); });
  }
  load();
})();
</script>
</body>
</html>
""";
    }
}
