using Shape.Core;

namespace Shape.Generator;

/// <summary>
/// Compact schema input for <see cref="AdminMaskGenerator"/>. Phase 2 callers
/// build this from <c>EntityMeta</c>; Phase 3's <c>DatabaseIntrospector</c> will
/// build it from live schema reads.
/// </summary>
public sealed record TableSchema(string Table, IReadOnlyList<string> Columns);

/// <summary>
/// Built-in admin masks for "secret" tables (users, refresh_tokens, audit_log, …).
/// Secret tables are never exposed via auto-CRUD. When the dev opts in via
/// <c>AdminMasks = true</c>, this generator builds read-only <c>&lt;table&gt;.adminList</c>
/// and <c>&lt;table&gt;.adminGet</c> masks gated to the <c>admin</c> role, with
/// sensitive columns (password*, *_hash, *_secret, salt*, mfa_*, totp_*, exact
/// <c>token</c>/<c>secret</c>) stripped from the SELECT.
/// </summary>
public static class AdminMaskGenerator
{
    private static readonly HashSet<string> ExactBlocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "token", "secret", "password", "salt"
    };

    private static readonly string[] BlockedPrefixes =
    {
        "password_", "secret_", "salt_", "mfa_", "totp_"
    };

    private static readonly string[] BlockedSuffixes =
    {
        "_hash", "_secret", "_token"
    };

    /// <param name="schemas">All known tables with their column lists.</param>
    /// <param name="secretTables">Tables to expose as admin-only (silently skipped if absent from <paramref name="schemas"/>).</param>
    /// <param name="driver">Identifier-quoting dialect (<c>"mysql"</c>, <c>"pgsql"</c>, <c>"sqlite"</c>, <c>"mssql"</c>).</param>
    /// <param name="tenancy">Optional tenancy config used to scope admin masks when a secret table carries the tenant discriminator.</param>
    public static Dictionary<string, MaskConfig> Generate(
        IEnumerable<TableSchema> schemas,
        IEnumerable<string> secretTables,
        string driver = "mysql",
        TenancyConfig? tenancy = null)
    {
        var byTable = schemas.ToDictionary(s => s.Table, StringComparer.Ordinal);
        var masks = new Dictionary<string, MaskConfig>();
        var adminRoles = new List<string> { "admin" };
        if (tenancy?.UnscopedRoles is { Count: > 0 })
            adminRoles.AddRange(tenancy.UnscopedRoles);
        adminRoles = adminRoles.Distinct(StringComparer.Ordinal).ToList();

        foreach (var table in secretTables)
        {
            if (!byTable.TryGetValue(table, out var schema))
                continue;

            var safeNames = schema.Columns.Where(IsSafeColumn).ToList();
            if (safeNames.Count == 0)
                continue;

            var select = string.Join(", ", safeNames.Select(c => Quote(c, driver)));
            var qt = Quote(table, driver);
            var tenantColumn = tenancy?.Column;
            var tenantScoped = !string.IsNullOrWhiteSpace(tenantColumn)
                && schema.Columns.Contains(tenantColumn, StringComparer.Ordinal);
            var hasUnscopedVariant = tenantScoped && tenancy?.UnscopedRoles is { Count: > 0 };
            var scopedWhere = tenantScoped ? $" WHERE {Quote(tenantColumn!, driver)} = @__tenant" : "";

            masks[$"{table}.adminList"] = new MaskConfig
            {
                Sql = $"SELECT {select} FROM {qt}{scopedWhere}",
                Params = new Dictionary<string, ParamSchema>(),
                Roles = adminRoles,
                Description = $"(admin only) List rows from {table} with sensitive columns stripped."
            };
            if (tenantScoped)
            {
                masks[$"{table}.adminList"].TenantScope = tenantColumn;
                if (hasUnscopedVariant)
                    masks[$"{table}.adminList"].SqlUnscoped = $"SELECT {select} FROM {qt}";
            }

            if (safeNames.Contains("id", StringComparer.Ordinal))
            {
                masks[$"{table}.adminGet"] = new MaskConfig
                {
                    Sql = $"SELECT {select} FROM {qt}{(tenantScoped ? $" WHERE {Quote("id", driver)} = @id AND {Quote(tenantColumn!, driver)} = @__tenant" : $" WHERE {Quote("id", driver)} = @id")}",
                    Params = new Dictionary<string, ParamSchema>
                    {
                        ["id"] = new() { Type = "int", Required = true }
                    },
                    Roles = adminRoles,
                    Description = $"(admin only) Get one row from {table} by id with sensitive columns stripped."
                };
                if (tenantScoped)
                {
                    masks[$"{table}.adminGet"].TenantScope = tenantColumn;
                    if (hasUnscopedVariant)
                        masks[$"{table}.adminGet"].SqlUnscoped = $"SELECT {select} FROM {qt} WHERE {Quote("id", driver)} = @id";
                }
            }
        }

        return masks;
    }

    /// <summary>
    /// True when the column should appear in admin masks. Excludes the exact
    /// secret-name list, common prefixes (<c>password_</c>, <c>mfa_</c>, …),
    /// and common suffixes (<c>_hash</c>, <c>_token</c>, …).
    /// </summary>
    public static bool IsSafeColumn(string name)
    {
        var lower = name.ToLowerInvariant();
        if (ExactBlocked.Contains(lower)) return false;
        foreach (var prefix in BlockedPrefixes)
            if (lower.StartsWith(prefix, StringComparison.Ordinal)) return false;
        foreach (var suffix in BlockedSuffixes)
            if (lower.EndsWith(suffix, StringComparison.Ordinal)) return false;
        return true;
    }

    private static string Quote(string name, string driver) => driver.ToLowerInvariant() switch
    {
        "pgsql" or "postgres" or "postgresql" or "sqlite" => "\"" + name.Replace("\"", "\"\"") + "\"",
        "mssql" or "sqlsrv" or "sqlserver" => "[" + name.Replace("]", "]]") + "]",
        _ => "`" + name.Replace("`", "``") + "`"
    };
}
