using Microsoft.AspNetCore.Http;
using Shape.Auth;
using Shape.Core;
using Shape.Metadata;

namespace Shape.Http;

public sealed class AdminAuth
{
    private readonly AuthConfig _authConfig;

    public AdminAuth(AuthConfig authConfig)
    {
        _authConfig = authConfig;
    }

    public Dictionary<string, object?> AuthorizeAdmin(HttpContext context)
    {
        if (_authConfig.Driver != "jwt")
            throw new ApiException("AUTH_CONFIG_ERROR", "Admin endpoints require auth driver jwt.", 500);

        var guard = new JwtGuard(_authConfig);
        var headers = NormalizeHeaders(context);
        var claims = guard.Authorize(headers);

        var role = claims.GetValueOrDefault("role")?.ToString() ?? "";
        var allowed = role == "admin" || (_authConfig.Tenancy != null && TenancyResolver.IsUnscopedRole(_authConfig.Tenancy, role));
        if (!allowed)
            throw new ApiException("AUTH_FORBIDDEN", "Admin role is required.", 403);

        return claims;
    }

    internal static Dictionary<string, string> NormalizeHeaders(HttpContext context)
    {
        var result = new Dictionary<string, string>();
        foreach (var header in context.Request.Headers)
            result[header.Key.ToLowerInvariant()] = header.Value.ToString();
        return result;
    }
}
