using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shape.Core;
using Shape.Metadata;

namespace Shape.Auth;

public sealed class JwtGuard : IAuthGuard
{
    /// <summary>Clock-skew leeway (seconds) applied to exp/nbf (SECURITY-REVIEW L10).</summary>
    private const long ClockSkewSeconds = 60;

    private readonly AuthConfig _config;
    private readonly Database? _database;
    private readonly OidcVerifier? _oidc;

    public JwtGuard(AuthConfig config, Database? database = null)
    {
        _config = config;
        _database = database;
        _oidc = config.Oidc != null
            ? new OidcVerifier(config.Oidc, database, config.PkAssignment, config.Tenancy, config.Scopes)
            : null;
    }

    public Dictionary<string, object?> Authorize(Dictionary<string, string> headers)
    {
        var auth = headers.GetValueOrDefault("authorization") ?? "";
        if (!auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new ApiException("AUTH_REQUIRED", "Missing Bearer token.", 401);

        var token = auth[7..].Trim();
        if (string.IsNullOrEmpty(token))
            throw new ApiException("AUTH_REQUIRED", "Missing Bearer token.", 401);

        var parts = token.Split('.');
        if (parts.Length != 3)
            throw new ApiException("AUTH_INVALID", "Invalid JWT format.", 401);

        var (h64, p64, s64) = (parts[0], parts[1], parts[2]);
        var header = DecodeJsonBase64Url(h64);
        var payload = DecodeJsonBase64Url(p64);

        var alg = header.GetValueOrDefault("alg")?.ToString() ?? "";

        // OIDC dispatch: when alg is RS256/RS384/RS512 and an OidcVerifier is
        // wired, hand off to it. The local HS256 path stays the default.
        if (_oidc != null && _oidc.HandlesAlgorithm(alg))
        {
            var signingInput = Encoding.UTF8.GetBytes($"{h64}.{p64}");
            var sigBytes = Base64UrlDecode(s64);
            return NormalizeScoping(_oidc.Verify(signingInput, sigBytes, header, payload));
        }

        if (alg != "HS256")
            throw new ApiException("AUTH_INVALID", "Unsupported JWT algorithm.", 401);

        var secret = _config.JwtSecret ?? "";
        if (string.IsNullOrEmpty(secret))
            throw new ApiException("AUTH_INVALID", "JWT secret is not configured.", 500);

        var sig = Base64UrlDecode(s64);
        var signed = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{h64}.{p64}"));

        if (!CryptographicOperations.FixedTimeEquals(signed, sig))
            throw new ApiException("AUTH_INVALID", "JWT signature mismatch.", 401);

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // SECURITY-REVIEW M1: require `exp`. A token with no expiry claim would
        // otherwise be accepted forever. Shape's own JwtIssuer always sets it,
        // so a missing exp means a hand-forged or non-conformant token.
        if (!payload.TryGetValue("exp", out var expVal) || expVal == null ||
            !long.TryParse(expVal.ToString(), out var exp))
            throw new ApiException("AUTH_INVALID", "JWT is missing the required exp claim.", 401);
        // SECURITY-REVIEW L10: allow a small clock-skew leeway so a valid token
        // isn't rejected at the boundary across hosts with drifting clocks.
        if (now > exp + ClockSkewSeconds)
            throw new ApiException("AUTH_INVALID", "JWT expired.", 401);

        if (payload.TryGetValue("nbf", out var nbfVal) && nbfVal != null &&
            long.TryParse(nbfVal.ToString(), out var nbf) && now + ClockSkewSeconds < nbf)
            throw new ApiException("AUTH_INVALID", "JWT not active yet.", 401);

        if (!string.IsNullOrEmpty(_config.JwtIssuer))
        {
            var iss = payload.GetValueOrDefault("iss")?.ToString() ?? "";
            if (iss != _config.JwtIssuer)
                throw new ApiException("AUTH_INVALID", "JWT issuer mismatch.", 401);
        }

        if (!string.IsNullOrEmpty(_config.JwtAudience))
        {
            var pAud = payload.GetValueOrDefault("aud");
            if (pAud is string audStr)
            {
                if (audStr != _config.JwtAudience)
                    throw new ApiException("AUTH_INVALID", "JWT audience mismatch.", 401);
            }
            else if (pAud is List<object?> audList)
            {
                if (!audList.Any(a => a?.ToString() == _config.JwtAudience))
                    throw new ApiException("AUTH_INVALID", "JWT audience mismatch.", 401);
            }
            else
            {
                throw new ApiException("AUTH_INVALID", "JWT audience mismatch.", 401);
            }
        }

        return NormalizeScoping(payload);
    }

    /// <summary>
    /// Per-spec claim normalization (ROW-SCOPES-PLAN §2.2): a role in a spec's
    /// unscoped list gets its claim forced to <c>null</c>; a scoped caller
    /// missing the claim is rejected with <c>AUTH_INVALID</c> so pre-feature
    /// tokens never fall through to unscoped. The tenant convention keeps its
    /// per-request registry check outside the shared loop.
    /// </summary>
    private Dictionary<string, object?> NormalizeScoping(Dictionary<string, object?> context)
    {
        var tenancy = _config.Tenancy;
        var scopes = _config.Scopes;
        if (tenancy == null && scopes is not { Count: > 0 })
            return context;

        var normalized = new Dictionary<string, object?>(context);
        var role = normalized.GetValueOrDefault("role")?.ToString() ?? "";

        if (tenancy != null)
        {
            if (TenancyResolver.IsUnscopedRole(tenancy, role))
            {
                normalized["tenant_id"] = null;
            }
            else
            {
                var tenantId = normalized.GetValueOrDefault("tenant_id");
                if (TenancyResolver.TenantValueAsString(tenantId) == null)
                    throw new ApiException(
                        "AUTH_INVALID",
                        "Missing tenant_id claim. Re-login required after tenancy was enabled.",
                        401,
                        null,
                        new Dictionary<string, object?>
                        {
                            ["hint"] = "Re-login required after tenancy was enabled."
                        });

                if (_database != null && tenancy.CheckActivePerRequest)
                    TenancyResolver.EnsureTenantActive(_database, tenancy, tenantId);
            }
        }

        if (scopes is { Count: > 0 })
        {
            foreach (var (column, scope) in scopes)
            {
                var claimKey = string.IsNullOrWhiteSpace(scope.Claim) ? column : scope.Claim!;
                if (!string.IsNullOrEmpty(role)
                    && scope.UnscopedRoles.Contains(role, StringComparer.Ordinal))
                {
                    normalized[claimKey] = null;
                    continue;
                }

                if (IsMissingScopeClaim(normalized.GetValueOrDefault(claimKey), scope.Multi))
                    throw new ApiException(
                        "AUTH_INVALID",
                        $"Missing {claimKey} claim. Re-login required after row scopes were enabled.",
                        401,
                        null,
                        new Dictionary<string, object?>
                        {
                            ["hint"] = "Re-login required after row scopes were enabled."
                        });
            }
        }

        return normalized;
    }

    /// <summary>Missing = null, blank string, or (for multi) an empty / all-null list.</summary>
    private static bool IsMissingScopeClaim(object? value, bool multi)
    {
        if (value is JsonElement je)
            value = Core.ParamResolver.UnwrapJsonElement(je);
        if (value == null)
            return true;
        if (value is string s)
            return string.IsNullOrWhiteSpace(s);
        if (multi && value is IList<object?> list)
            return !list.Any(v => v != null && !(v is string vs && string.IsNullOrWhiteSpace(vs)));
        return false;
    }

    private static Dictionary<string, object?> DecodeJsonBase64Url(string raw)
    {
        var decoded = Encoding.UTF8.GetString(Base64UrlDecode(raw));
        var doc = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(decoded);
        if (doc == null)
            throw new ApiException("AUTH_INVALID", "Invalid JWT payload.", 401);

        var result = new Dictionary<string, object?>();
        foreach (var (k, v) in doc)
        {
            result[k] = v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.TryGetInt64(out var l) ? (object)l : v.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                // Unwrap each element (string / number / bool …) — multi-valued
                // scope claims ride as arrays of ints or strings and must bind
                // as IN lists downstream. Non-scalar elements collapse via
                // UnwrapJsonElement's recursive handling.
                JsonValueKind.Array => v.EnumerateArray()
                    .Select(e => Core.ParamResolver.UnwrapJsonElement(e))
                    .ToList(),
                _ => v.ToString()
            };
        }
        return result;
    }

    private static byte[] Base64UrlDecode(string raw)
    {
        var padded = raw.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}

public sealed class AuthConfig
{
    /// <summary>
    /// Shorthand factory: equivalent to <c>new AuthConfig { Driver = "jwt" }</c>
    /// (with <see cref="JwtSecretMaterializer"/> filling the secret on first
    /// boot). Mirrors the JSON shorthand <c>"auth": "jwt"</c> /
    /// <c>"auth": true</c> handled by <c>Shape.Config.ConfigLoader</c>.
    /// </summary>
    public static AuthConfig Jwt() => new() { Driver = "jwt" };

    public string Driver { get; set; } = "none";
    public string? JwtSecret { get; set; }
    public string? JwtIssuer { get; set; }
    public string? JwtAudience { get; set; }
    public string UsersTable { get; set; } = "users";
    public string RefreshTokensTable { get; set; } = "refresh_tokens";
    public int AccessTokenTtl { get; set; } = 3600;
    public int RefreshTokenTtl { get; set; } = 2592000;
    public int PasswordMinLength { get; set; } = 8;

    /// <summary>
    /// What admin create-user / change-password accept as the login name:
    /// <c>"email"</c> (default, must be a valid email) or <c>"any"</c> (e.g. "jan.novak").
    /// </summary>
    public string LoginFormat { get; set; } = "email";
    public List<string> AssignableRoles { get; set; } = new() { "user", "admin" };

    /// <summary>
    /// When set, <see cref="JwtGuard"/> dispatches RS256/RS384/RS512 tokens to
    /// <see cref="OidcVerifier"/> for IdP-issued tokens. The local HS256 path
    /// is unchanged; both can coexist on the same endpoint.
    /// </summary>
    public OidcConfig? Oidc { get; set; }

    /// <summary>
    /// When true, <see cref="LoginService.Login"/> and <see cref="LoginService.ChangePassword"/>
    /// short-circuit with <c>AUTH_NOT_CONFIGURED</c>. Useful for OIDC-only deployments
    /// where the local users table has no <c>password_hash</c> column.
    /// </summary>
    public bool LocalLoginDisabled { get; set; }

    /// <summary>
    /// When set, the <c>auth.exchange</c> op swaps an app-validated external
    /// token (opaque bearer, session id, …) for a Shape-issued JWT — the
    /// brownfield bridge (TISCLOUD-SHAPE-EXTRACTION-PLAN §3). Constructed
    /// programmatically; the <see cref="ITokenExchangeSource"/> is code.
    /// </summary>
    public TokenExchangeConfig? Exchange { get; set; }

    /// <summary>
    /// Copied from <c>ShapeConfig.PkAssignment</c> by <c>ShapeApp</c> so the auth
    /// insert paths (<see cref="LoginService"/> users / refresh_tokens,
    /// <see cref="OidcVerifier"/> auto-provision) honor manual PK allocation when
    /// the auth tables live in a legacy non-identity database. Only an explicit
    /// <c>"manual"</c> (table key or <c>"*"</c>) routes these inserts through
    /// <c>Database.InsertWithManualPk</c> — the <c>auto</c> detection needs schema
    /// introspection, which the per-request auth path deliberately skips.
    /// </summary>
    public Dictionary<string, string>? PkAssignment { get; set; }

    /// <summary>Copied from <c>ShapeConfig.Tenancy</c> so auth can issue / validate tenant claims.</summary>
    public TenancyConfig? Tenancy { get; set; }

    /// <summary>Copied from <c>ShapeConfig.Scopes</c> so auth can issue / validate per-dimension claims.</summary>
    public Dictionary<string, ScopeConfig>? Scopes { get; set; }
}
