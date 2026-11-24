using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shape.Core;

namespace Shape.Auth;

public sealed class JwtIssuer
{
    private readonly AuthConfig _config;

    public JwtIssuer(AuthConfig config)
    {
        _config = config;
    }

    public string Issue(Dictionary<string, object?> claims, int ttlSeconds)
    {
        var secret = _config.JwtSecret ?? "";
        if (string.IsNullOrEmpty(secret))
            throw new ApiException("AUTH_CONFIG_ERROR", "JWT secret is not configured.", 500);

        if (ttlSeconds < 60)
            ttlSeconds = 60;

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var header = new Dictionary<string, object?>
        {
            ["alg"] = "HS256",
            ["typ"] = "JWT"
        };

        var payload = new Dictionary<string, object?>(claims)
        {
            ["iat"] = now,
            ["nbf"] = now,
            ["exp"] = now + ttlSeconds
        };

        if (!string.IsNullOrEmpty(_config.JwtIssuer))
            payload["iss"] = _config.JwtIssuer;

        if (!string.IsNullOrEmpty(_config.JwtAudience))
            payload["aud"] = _config.JwtAudience;

        var h64 = Base64UrlEncodeJson(header);
        var p64 = Base64UrlEncodeJson(payload);

        var sig = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{h64}.{p64}"));
        var s64 = Base64UrlEncode(sig);

        return $"{h64}.{p64}.{s64}";
    }

    private static string Base64UrlEncodeJson(Dictionary<string, object?> payload)
    {
        var json = JsonSerializer.Serialize(payload,
            new JsonSerializerOptions { WriteIndented = false });
        return Base64UrlEncode(Encoding.UTF8.GetBytes(json));
    }

    private static string Base64UrlEncode(byte[] data)
    {
        return Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
