using Shape.Core;

namespace Shape.Auth;

/// <summary>
/// App-supplied validator for the <c>auth.exchange</c> op
/// (brownfield adapter guide §3): brownfield apps with an existing,
/// non-JWT token source (opaque DB-backed bearer tokens, session ids, …)
/// implement this to bridge their identity into a Shape session. Shape calls
/// it exactly once per exchange; from then on the client holds a standard
/// Shape-issued JWT and every downstream mechanism (tenancy claims, role
/// gates, suspension checks) applies unchanged.
/// </summary>
public interface ITokenExchangeSource
{
    /// <summary>
    /// Validate the external token and return the verified identity, or null
    /// when the token is invalid / expired / revoked (Shape answers
    /// <c>AUTH_EXCHANGE_INVALID</c>, 401).
    ///
    /// Expected keys (pre-mapping — <see cref="TokenExchangeConfig.ClaimMapping"/>
    /// renames app-specific names to these canonical ones):
    /// <c>id</c> (required), <c>email</c>, <c>role</c>,
    /// <c>tenant_id</c> (required for tenancy-scoped roles), <c>display_name</c>.
    /// Throwing <see cref="ApiException"/> passes through verbatim; any other
    /// exception surfaces as a 500.
    /// </summary>
    /// <param name="externalToken">The raw token from the exchange payload.</param>
    /// <param name="db">The request's database — the source's lookups join the
    /// same connection Shape uses.</param>
    Dictionary<string, object?>? Validate(string externalToken, Database db);
}

/// <summary>
/// Configuration for the <c>auth.exchange</c> op. The <see cref="Source"/> is
/// code, so this block is constructed programmatically (not from JSON config):
/// <c>config.Auth.Exchange = new TokenExchangeConfig { Source = new MySource(), … }</c>.
/// </summary>
public sealed class TokenExchangeConfig
{
    /// <summary>The app's validator for its external tokens. Required.</summary>
    public ITokenExchangeSource? Source { get; set; }

    /// <summary>
    /// Renames source-identity keys to Shape's canonical claim names —
    /// e.g. <c>{ "OrganizationId": "tenant_id" }</c>. Same idea as the OIDC
    /// path's <c>id_claim</c>/<c>email_claim</c> mapping. Unmapped keys pass
    /// through unchanged.
    /// </summary>
    public Dictionary<string, string>? ClaimMapping { get; set; }

    /// <summary>
    /// Maps external role strings to Shape role names —
    /// e.g. <c>{ "Referent": "referent" }</c>. Unmapped roles pass through
    /// verbatim (the mask role gates are the backstop).
    /// </summary>
    public Dictionary<string, string>? RoleMapping { get; set; }

    /// <summary>
    /// Default <c>false</c>: the exchange is <b>stateless</b> — it returns an
    /// access token only, and the client simply re-exchanges its (still valid)
    /// external token when the Shape token expires. No <c>refresh_tokens</c>
    /// table is required, which is the right fit for apps whose external
    /// source already manages sessions. Set <c>true</c> to issue a full
    /// access+refresh pair through the standard rotation machinery (requires
    /// the refresh_tokens table and compatible user ids).
    /// </summary>
    public bool IssueRefreshToken { get; set; }
}
