namespace Shape.Auth;

/// <summary>
/// Shape's OIDC configuration block — wired into <see cref="AuthConfig.Oidc"/>.
/// When set, <see cref="JwtGuard"/> dispatches RS256/RS384/RS512 tokens to
/// <see cref="OidcVerifier"/> instead of the local HS256 path.
///
/// Most fields are optional. The minimum viable configuration is either:
/// <list type="bullet">
///   <item><c>JwksUri</c> + <c>Issuer</c> + <c>Audience</c> directly, or</item>
///   <item>A <c>Preset</c> name (entra/auth0/okta/google/cognito) plus the
///   single preset-specific input (TenantId / Domain / Region+UserPoolId).</item>
/// </list>
/// </summary>
public sealed class OidcConfig
{
    /// <summary>Preset name: <c>entra</c>/<c>azure</c>/<c>azuread</c> | <c>auth0</c> | <c>okta</c> | <c>google</c> | <c>cognito</c>. Empty = no preset.</summary>
    public string? Preset { get; set; }

    /// <summary>Entra ID / Azure AD tenant id. Used by the entra preset.</summary>
    public string? TenantId { get; set; }

    /// <summary>Auth0 / Okta tenant domain (e.g. <c>tenant.auth0.com</c>).</summary>
    public string? Domain { get; set; }

    /// <summary>AWS region (e.g. <c>eu-west-1</c>) for the cognito preset.</summary>
    public string? Region { get; set; }

    /// <summary>Cognito user pool id.</summary>
    public string? UserPoolId { get; set; }

    /// <summary>JWKS endpoint URL. Filled by preset when not set explicitly.</summary>
    public string? JwksUri { get; set; }

    /// <summary>Expected <c>iss</c> claim. Filled by preset when not set explicitly.</summary>
    public string? Issuer { get; set; }

    /// <summary>Expected <c>aud</c> claim. Always operator-supplied.</summary>
    public string? Audience { get; set; }

    /// <summary>Whitelist of accepted alg values. Default <c>{"RS256"}</c>. Tokens with anything else are rejected before signature work.</summary>
    public List<string>? Algorithms { get; set; }

    /// <summary>Claim name carrying the IdP-side user identifier. Default <c>"sub"</c> (Entra preset uses <c>"oid"</c>).</summary>
    public string IdClaim { get; set; } = "sub";

    /// <summary>Claim name carrying the user's email. Default <c>"email"</c> (Entra preset uses <c>"preferred_username"</c>).</summary>
    public string EmailClaim { get; set; } = "email";

    /// <summary>Claim name carrying role information. Default <c>"role"</c>; common variants: <c>"roles"</c> (Entra), <c>"groups"</c> (Okta), <c>"cognito:groups"</c>.</summary>
    public string RoleClaim { get; set; } = "role";

    /// <summary>Map IdP role names to Shape framework roles. e.g. <c>{ "BackendAdmin": "admin" }</c>.</summary>
    public Dictionary<string, string>? RoleMapping { get; set; }

    /// <summary>JWKS cache TTL in seconds. Default 3600.</summary>
    public int JwksCacheTtl { get; set; } = 3600;

    /// <summary>JWKS cache directory. Default <c>{Path.GetTempPath()}/shape_jwks</c>.</summary>
    public string? JwksCacheDir { get; set; }

    /// <summary>
    /// Maximum age (seconds) a stale JWKS cache may be served after a failed
    /// refresh (IdP outage). Beyond this the verifier fails closed rather than
    /// trusting a possibly-revoked key indefinitely (SECURITY-REVIEW H2).
    /// Default 0 → falls back to 8× <see cref="JwksCacheTtl"/>.
    /// </summary>
    public int JwksMaxStaleTtl { get; set; }

    // ── Local user resolution ─────────────────────────────────────────

    /// <summary>Local users table name. Default <c>"users"</c>.</summary>
    public string UsersTable { get; set; } = "users";

    /// <summary>
    /// Column on the users table holding the IdP's foreign id.
    /// <c>null</c> (default) = auto-detect at cold boot in
    /// <see cref="Shape.ShapeApp.FromDatabase(Shape.Core.ShapeConfig)"/>:
    /// the introspector probes <c>oid</c> / <c>sub</c> / <c>azure_oid</c> /
    /// <c>auth0_id</c> / <c>external_id</c> on the users table and uses the
    /// single match. Multi-match emits a warning and falls back to
    /// <c>"oid"</c>; no match also falls back to <c>"oid"</c>. The
    /// <see cref="OidcVerifier"/> coalesces null → <c>"oid"</c> at consume
    /// time so non-DB-backed paths still have a working default.
    /// </summary>
    public string? UsersLookupColumn { get; set; }

    /// <summary>If true, create a row in the users table for OIDC-authenticated callers we've never seen before.</summary>
    public bool AutoProvision { get; set; }

    /// <summary>Fallback role used during auto-provision when the token has no mapped role. Default <c>"user"</c>.</summary>
    public string AutoProvisionRole { get; set; } = "user";
}
