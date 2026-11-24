namespace Shape.Core;

/// <summary>Which convention a <see cref="ScopeSpec"/> came from.</summary>
internal enum ScopeKind
{
    /// <summary>The tenancy convention — keeps its registry/suspension/provision extras outside the shared loop.</summary>
    Tenant,

    /// <summary>A row-scope dimension (department / cost center / branch …) from <c>scopes</c> config.</summary>
    Dimension
}

/// <summary>
/// The internal shape (ROW-SCOPES-PLAN §2.6 / R1) that drives detection,
/// predicate compilation, variant/wrap selection, stamping, and WHERE guards.
/// <see cref="TenancyConfig"/> constructs the <see cref="ScopeKind.Tenant"/>
/// spec; each <see cref="ScopeConfig"/> a <see cref="ScopeKind.Dimension"/> one.
/// </summary>
internal sealed record ScopeSpec(
    string Column,
    string ClaimKey,
    IReadOnlySet<string> UnscopedRoles,
    bool Multi,
    ScopeKind Kind)
{
    /// <summary>Reserved bind-parameter name: <c>__tenant</c> for the tenant spec, <c>__scope_&lt;column&gt;</c> per dimension.</summary>
    internal string ParamName => Kind == ScopeKind.Tenant ? "__tenant" : "__scope_" + Column;

    internal bool IsUnscopedRole(string? role) =>
        !string.IsNullOrEmpty(role) && UnscopedRoles.Contains(role);

    /// <summary>The canonical claim key for a dimension scope: config <c>claim</c>, defaulting to the column name.</summary>
    internal static string ClaimKeyFor(string column, ScopeConfig scope) =>
        string.IsNullOrWhiteSpace(scope.Claim) ? column : scope.Claim!;

    /// <summary>
    /// Build the spec list: tenant first (when configured), then one dimension
    /// spec per <c>scopes</c> entry in declaration order.
    /// </summary>
    internal static List<ScopeSpec> Build(TenancyConfig? tenancy, Dictionary<string, ScopeConfig>? scopes)
    {
        var specs = new List<ScopeSpec>();
        if (tenancy != null)
        {
            specs.Add(new ScopeSpec(
                tenancy.Column,
                "tenant_id",
                new HashSet<string>(tenancy.UnscopedRoles ?? new List<string>(), StringComparer.Ordinal),
                Multi: false,
                ScopeKind.Tenant));
        }

        if (scopes != null)
        {
            foreach (var (column, scope) in scopes)
            {
                if (string.IsNullOrWhiteSpace(column))
                    continue;
                specs.Add(new ScopeSpec(
                    column,
                    ClaimKeyFor(column, scope),
                    new HashSet<string>(scope.UnscopedRoles ?? new List<string>(), StringComparer.Ordinal),
                    scope.Multi,
                    ScopeKind.Dimension));
            }
        }

        return specs;
    }
}
