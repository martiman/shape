namespace Shape.Core;

/// <summary>
/// Multi-tenant convention block. Shared by config, generators, and runtime
/// enforcement so the database, auth token, and compiled masks agree on the
/// same tenant contract.
/// </summary>
public sealed class TenancyConfig
{
    /// <summary>Registry table that owns tenant rows. Default <c>tenants</c>.</summary>
    public string Table { get; set; } = "tenants";

    /// <summary>Discriminator column on tenant-owned tables. Default <c>tenant_id</c>.</summary>
    public string Column { get; set; } = "tenant_id";

    /// <summary>Roles that may see across tenants. Default empty.</summary>
    public List<string> UnscopedRoles { get; set; } = new();

    /// <summary><c>strict</c> (default) or <c>relaxed</c>.</summary>
    public string Enforce { get; set; } = "strict";

    /// <summary>Shared lookup tables exempt from strict tenant-scoping checks.</summary>
    public List<string> GlobalTables { get; set; } = new();

    /// <summary>
    /// When true, login / refresh / OIDC resolution re-checks tenant activity
    /// on each request instead of letting JWT TTL absorb suspension lag.
    /// </summary>
    public bool CheckActivePerRequest { get; set; }

    /// <summary>Opt-in FK validation for tenant-scoped target tables.</summary>
    public bool ValidateFks { get; set; }

    /// <summary>
    /// Optional IdP claim that carries the tenant source for OIDC auto-provision.
    /// The framework only uses this when app code explicitly opts into the feature.
    /// </summary>
    public string? OidcTenantClaim { get; set; }

    /// <summary>
    /// Optional fixed tenant value to stamp during OIDC auto-provision.
    /// Stored as raw JSON so callers can use a numeric id or a string slug.
    /// </summary>
    public System.Text.Json.JsonElement? OidcAutoProvisionTenant { get; set; }
}

/// <summary>
/// Row-level security by dimension (ROW-SCOPES-PLAN §2): one entry per
/// discriminator column (the map key in <see cref="ShapeConfig.Scopes"/>).
/// The tenancy machinery generalized — a table carrying the column is scoped
/// (no strict sweep), predicates AND together with the tenant predicate.
/// </summary>
public sealed class ScopeConfig
{
    /// <summary>JWT claim name carrying the caller's dimension value. Default: the column name.</summary>
    public string? Claim { get; set; }

    /// <summary>Roles that see across this dimension. Default: none.</summary>
    public List<string> UnscopedRoles { get; set; } = new();

    /// <summary>When true the claim is a LIST and the compiled predicate becomes <c>IN</c>.</summary>
    public bool Multi { get; set; }

    /// <summary>
    /// Multi-membership source (e.g. a <c>user_departments</c> join table).
    /// Default null = single value read from <c>users.&lt;column&gt;</c>.
    /// </summary>
    public ClaimTableConfig? ClaimTable { get; set; }

    /// <summary>
    /// Behavior when the dimension column is nullable: <c>"warn"</c> (default,
    /// boot warning — NULL rows are invisible to scoped callers) or
    /// <c>"error"</c> (boot <c>CONFIG_INVALID</c>).
    /// </summary>
    public string Nullable { get; set; } = "warn";

    /// <summary>
    /// Optional fixed value stamped into <c>users.&lt;column&gt;</c> during OIDC
    /// auto-provision (mirror of <see cref="TenancyConfig.OidcAutoProvisionTenant"/>).
    /// Without it, auto-provision on a scoped deployment rejects with
    /// <c>AUTH_CONFIG_ERROR</c>.
    /// </summary>
    public System.Text.Json.JsonElement? OidcAutoProvisionValue { get; set; }
}

/// <summary>Membership-table pointer for <see cref="ScopeConfig.ClaimTable"/>.</summary>
public sealed class ClaimTableConfig
{
    /// <summary>Join table holding one row per (user, dimension value).</summary>
    public string Table { get; set; } = "";

    /// <summary>Column on <see cref="Table"/> referencing <c>users.id</c>. Default <c>user_id</c>.</summary>
    public string UserColumn { get; set; } = "user_id";
}
