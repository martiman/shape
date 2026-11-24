using System.Text.RegularExpressions;
using Shape.Core;

namespace Shape.Metadata;

/// <summary>
/// Applies tenancy conventions to already-read entities and validates the
/// build-time contract described in MULTI-TENANCY-PLAN.md.
/// </summary>
public static class TenancyResolver
{
    public static List<string> Apply(
        ShapeConfig config,
        List<EntityMeta> entities,
        IReadOnlyDictionary<string, List<string>>? accessMap = null)
    {
        var tenancy = config.Tenancy;
        var warnings = new List<string>();

        ApplyScopes(config, entities, warnings);

        if (tenancy == null)
            return warnings;

        ValidateConfig(tenancy);

        var tenantColumn = tenancy.Column;
        var registryTable = tenancy.Table;
        var globalTables = new HashSet<string>(tenancy.GlobalTables ?? new(), StringComparer.Ordinal);
        var exposedTables = accessMap is { Count: > 0 }
            ? new HashSet<string>(accessMap.Keys, StringComparer.Ordinal)
            : entities.Select(e => e.Table).ToHashSet(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            var table = entity.Table;
            var fields = entity.Fields;
            var tenantField = fields.FirstOrDefault(f => string.Equals(f.Name, tenantColumn, StringComparison.Ordinal));
            var tenantScoped = tenantField != null;

            entity.TenantColumn = tenantScoped ? tenantColumn : null;
            foreach (var field in fields)
                field.IsTenantColumn = tenantScoped && string.Equals(field.Name, tenantColumn, StringComparison.Ordinal);

            if (tenantScoped && tenantField != null && tenantField.Nullable && string.Equals(tenancy.Enforce, "strict", StringComparison.OrdinalIgnoreCase))
            {
                throw ConfigInvalid(
                    $"Table '{table}' has nullable tenancy column '{tenantColumn}'.",
                    "Backfill tenant ids before enabling strict tenancy.");
            }

            if (string.Equals(table, registryTable, StringComparison.Ordinal))
            {
                if (tenantScoped)
                {
                    throw ConfigInvalid(
                        $"Registry table '{registryTable}' must be global and cannot carry '{tenantColumn}'.",
                        "Remove the tenancy column from the registry table.");
                }
                continue;
            }

            if (!exposedTables.Contains(table))
                continue;

            var isGlobal = globalTables.Contains(table);
            if (!tenantScoped && !isGlobal)
            {
                if (string.Equals(tenancy.Enforce, "strict", StringComparison.OrdinalIgnoreCase))
                {
                    throw ConfigInvalid(
                        $"Table '{table}' is exposed but missing tenancy column '{tenantColumn}'.",
                        $"Either add '{tenantColumn}' or list the table in tenancy.global_tables.");
                }

                warnings.Add($"Table '{table}' is exposed without tenancy column '{tenantColumn}' (relaxed mode).");
            }
        }

        return warnings;
    }

    /// <summary>
    /// Row-scope detection (ROW-SCOPES-PLAN §2.1): per entity, per configured
    /// dimension, the table is scoped iff the column is present — deliberately
    /// NO strict sweep (dimensions naturally apply to a subset of tables). A
    /// nullable dimension column is a boot warning by default (NULL rows are
    /// invisible to scoped callers) or <c>CONFIG_INVALID</c> when the scope
    /// sets <c>nullable: "error"</c>.
    /// </summary>
    private static void ApplyScopes(ShapeConfig config, List<EntityMeta> entities, List<string> warnings)
    {
        var scopes = config.Scopes;
        if (scopes is not { Count: > 0 })
            return;

        foreach (var (column, scope) in scopes)
            ValidateScope(column, scope);

        if (config.Tenancy != null && scopes.ContainsKey(config.Tenancy.Column))
        {
            throw ConfigInvalid(
                $"Config key `scopes.{config.Tenancy.Column}` collides with the tenancy column.",
                "Tenancy already scopes that column; remove it from `scopes` or rename one of them.");
        }

        foreach (var entity in entities)
        {
            var scopeColumns = new List<string>();
            foreach (var (column, scope) in scopes)
            {
                var field = entity.Fields.FirstOrDefault(f => string.Equals(f.Name, column, StringComparison.Ordinal));
                if (field == null)
                    continue;

                if (field.Nullable)
                {
                    if (string.Equals(scope.Nullable, "error", StringComparison.OrdinalIgnoreCase))
                    {
                        throw ConfigInvalid(
                            $"Table '{entity.Table}' has nullable scope column '{column}'.",
                            "Backfill the dimension values or relax `scopes.<column>.nullable` to 'warn'.");
                    }

                    warnings.Add(
                        $"Table '{entity.Table}' has nullable scope column '{column}' — rows with NULL are invisible to scoped callers.");
                }

                field.IsScopeColumn = true;
                scopeColumns.Add(column);
            }

            entity.ScopeColumns = scopeColumns;
        }
    }

    /// <summary>Validate one <c>scopes</c> entry — shared by <c>ConfigLoader</c> and code-supplied configs.</summary>
    public static void ValidateScope(string column, ScopeConfig scope)
    {
        if (string.IsNullOrWhiteSpace(column) || !IsSqlIdentifier(column))
            throw ConfigInvalid(
                $"Config key `scopes` has invalid column name '{column}'.",
                "Scope keys must be plain SQL identifiers (letters, digits, underscore).");

        if (scope.Claim != null && !IsSqlIdentifier(scope.Claim))
            throw ConfigInvalid(
                $"Config key `scopes.{column}.claim` is invalid.",
                "Use a plain identifier-style claim name.");

        if (!string.Equals(scope.Nullable, "warn", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(scope.Nullable, "error", StringComparison.OrdinalIgnoreCase))
        {
            throw ConfigInvalid(
                $"Config key `scopes.{column}.nullable` has invalid value '{scope.Nullable}'.",
                "Use either 'warn' or 'error'.");
        }

        if (scope.ClaimTable != null)
        {
            if (string.IsNullOrWhiteSpace(scope.ClaimTable.Table) || !IsSqlIdentifier(scope.ClaimTable.Table))
                throw ConfigInvalid(
                    $"Config key `scopes.{column}.claim_table.table` is invalid.",
                    "Use a non-empty table name (plain SQL identifier).");
            if (string.IsNullOrWhiteSpace(scope.ClaimTable.UserColumn) || !IsSqlIdentifier(scope.ClaimTable.UserColumn))
                throw ConfigInvalid(
                    $"Config key `scopes.{column}.claim_table.user_column` is invalid.",
                    "Use a non-empty column name (plain SQL identifier).");
            if (!scope.Multi)
                throw ConfigInvalid(
                    $"Config key `scopes.{column}.claim_table` requires `multi: true`.",
                    "A membership table implies a list-valued claim; set `multi` or drop `claim_table`.");
        }
    }

    private static bool IsSqlIdentifier(string value) =>
        Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$");

    public static bool IsUnscopedRole(TenancyConfig? tenancy, string? role)
    {
        if (tenancy == null || string.IsNullOrEmpty(role))
            return false;
        return tenancy.UnscopedRoles.Contains(role, StringComparer.Ordinal);
    }

    public static string? TenantColumnFor(Mask mask)
    {
        return mask.TenantScope is string column && !string.IsNullOrWhiteSpace(column)
            ? column
            : null;
    }

    public static string? TenantColumnFor(MaskConfig mask)
    {
        return mask.TenantScope is string column && !string.IsNullOrWhiteSpace(column)
            ? column
            : null;
    }

    public static string? TenantValueAsString(object? value)
    {
        if (value == null) return null;
        var text = value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public static void ValidateMaskContracts(
        ShapeConfig config,
        IEnumerable<EntityMeta> entities,
        List<string>? warnings = null,
        IReadOnlySet<string>? generatedMaskNames = null)
    {
        var entityList = entities as IReadOnlyList<EntityMeta> ?? entities.ToList();
        ValidateTenantMaskContracts(config, entityList);
        ValidateRowScopeMaskContracts(config, entityList, warnings, generatedMaskNames);
    }

    private static void ValidateTenantMaskContracts(ShapeConfig config, IReadOnlyList<EntityMeta> entities)
    {
        var tenancy = config.Tenancy;
        if (tenancy == null)
            return;

        var unscopedRoles = new HashSet<string>(tenancy.UnscopedRoles ?? new(), StringComparer.Ordinal);
        var scopedTables = entities
            .Where(entity => entity.TenantScoped)
            .Select(entity => entity.Table)
            .ToHashSet(StringComparer.Ordinal);
        var globalTables = new HashSet<string>(tenancy.GlobalTables ?? new(), StringComparer.Ordinal);

        foreach (var (maskName, mask) in config.Masks)
        {
            var declaredColumn = TenantColumnFor(mask);
            var explicitOptOut = IsExplicitTenantOptOut(mask.TenantScope);
            var sqlScopedRefs = ReferencesScopedTable(mask.Sql, scopedTables, globalTables)
                || ReferencesScopedTable(mask.SqlUnscoped, scopedTables, globalTables);

            if (!string.IsNullOrWhiteSpace(mask.CacheControl)
                && mask.CacheControl.Contains("public", StringComparison.OrdinalIgnoreCase)
                && (declaredColumn != null || sqlScopedRefs))
            {
                throw ConfigInvalid(
                    $"Mask '{maskName}' uses public cache-control while tenant-scoped.",
                    "Remove `public` from Cache-Control or drop tenant scoping.");
            }

            if (declaredColumn != null)
            {
                // Writable masks carry `tenant_scope` so the write path
                // (ApplyTenantColumn) can stamp/enforce the tenant column; their
                // `Sql` is only an existence probe, never a data-returning read,
                // so it legitimately has no `__tenant` placeholder.
                if (!mask.Writable && !ContainsTenantPlaceholder(mask.Sql))
                {
                    throw ConfigInvalid(
                        $"Mask '{maskName}' declares tenant scope but SQL does not reference `__tenant`.",
                        "Add `:__tenant` (or `@__tenant`) to the scoped SQL.");
                }
                continue;
            }

            if (!explicitOptOut && sqlScopedRefs && string.Equals(tenancy.Enforce, "strict", StringComparison.OrdinalIgnoreCase))
            {
                throw ConfigInvalid(
                    $"Mask '{maskName}' references tenant-scoped tables but does not declare tenant scope.",
                    "Declare `tenant_scope` or move the table(s) to `global_tables` if they are intentionally shared.");
            }

            if (explicitOptOut)
            {
                var roles = mask.Roles ?? new List<string>();
                if (roles.Count == 0 || roles.Any(role => !unscopedRoles.Contains(role)))
                {
                    throw ConfigInvalid(
                        $"Mask '{maskName}' opts out of tenant scoping but is reachable by a scoped role.",
                        "Restrict the mask to roles listed in `tenancy.unscoped_roles`.");
                }
            }
        }
    }

    /// <summary>
    /// Normalize manual masks' declared <c>row_scopes</c> column lists into the
    /// full <c>{column: {multi}}</c> carrier (multi flags come from config), and
    /// reject declarations referencing scopes that don't exist.
    /// </summary>
    public static void NormalizeRowScopeDeclarations(ShapeConfig config)
    {
        var scopes = config.Scopes;
        foreach (var (maskName, mask) in config.Masks)
        {
            switch (mask.RowScopes)
            {
                case List<string> declared:
                {
                    var map = new Dictionary<string, RowScopeMeta>(StringComparer.Ordinal);
                    foreach (var column in declared.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal))
                    {
                        if (scopes == null || !scopes.TryGetValue(column, out var scope))
                            throw UnknownRowScope(maskName, column);
                        map[column] = new RowScopeMeta { Multi = scope.Multi };
                    }
                    mask.RowScopes = map.Count > 0 ? map : null;
                    break;
                }
                case Dictionary<string, RowScopeMeta> carrier:
                {
                    foreach (var column in carrier.Keys)
                    {
                        if (scopes == null || !scopes.ContainsKey(column))
                            throw UnknownRowScope(maskName, column);
                    }
                    break;
                }
            }
        }
    }

    private static ApiException UnknownRowScope(string maskName, string column) =>
        ConfigInvalid(
            $"Mask '{maskName}' declares unknown row scope '{column}'.",
            "Declare the column under the `scopes` config key or remove it from the mask's `row_scopes`.");

    /// <summary>
    /// Row-scope declaration contract (ROW-SCOPES-PLAN §2.5): a declared scope
    /// column must appear as a <c>__scope_&lt;column&gt;</c> placeholder in the
    /// SQL (writable masks exempt — probe SQL); <c>row_scopes: false</c> opt-out
    /// requires every role to be unscoped for the dimensions; an undeclared
    /// manual mask touching a dimension-scoped table is a WARNING, not a boot
    /// error (dimensions default relaxed — tenancy's strict rule is unchanged).
    /// </summary>
    private static void ValidateRowScopeMaskContracts(
        ShapeConfig config,
        IReadOnlyList<EntityMeta> entities,
        List<string>? warnings,
        IReadOnlySet<string>? generatedMaskNames)
    {
        var scopes = config.Scopes;
        if (scopes is not { Count: > 0 })
            return;

        var scopedTables = entities
            .Where(entity => entity.ScopeColumns.Count > 0)
            .Select(entity => entity.Table)
            .ToHashSet(StringComparer.Ordinal);
        var noGlobals = new HashSet<string>(StringComparer.Ordinal);
        var unscopedUnion = scopes.Values
            .SelectMany(scope => scope.UnscopedRoles)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (maskName, mask) in config.Masks)
        {
            var declared = mask.RowScopes as Dictionary<string, RowScopeMeta>;
            var explicitOptOut = mask.RowScopes is bool optOut && optOut == false;
            var sqlScopedRefs = ReferencesScopedTable(mask.Sql, scopedTables, noGlobals)
                || ReferencesScopedTable(mask.SqlUnscoped, scopedTables, noGlobals);

            if (!string.IsNullOrWhiteSpace(mask.CacheControl)
                && mask.CacheControl.Contains("public", StringComparison.OrdinalIgnoreCase)
                && (declared is { Count: > 0 } || sqlScopedRefs))
            {
                throw ConfigInvalid(
                    $"Mask '{maskName}' uses public cache-control while row-scoped.",
                    "Remove `public` from Cache-Control or drop the row scoping.");
            }

            if (declared is { Count: > 0 })
            {
                // Writable masks carry `row_scopes` for the write path only;
                // their SQL is an existence probe with no placeholders.
                if (!mask.Writable)
                {
                    foreach (var column in declared.Keys)
                    {
                        if (!ContainsScopePlaceholder(mask.Sql, column))
                        {
                            throw ConfigInvalid(
                                $"Mask '{maskName}' declares row scope '{column}' but SQL does not reference `__scope_{column}`.",
                                $"Add `:__scope_{column}` (or `@__scope_{column}`) to the scoped SQL.");
                        }
                    }
                }
                continue;
            }

            if (explicitOptOut)
            {
                var roles = mask.Roles ?? new List<string>();
                if (roles.Count == 0 || roles.Any(role => !unscopedUnion.Contains(role)))
                {
                    throw ConfigInvalid(
                        $"Mask '{maskName}' opts out of row scoping but is reachable by a scoped role.",
                        "Restrict the mask to roles listed in the scopes' `unscoped_roles`.");
                }
                continue;
            }

            // Undeclared mask touching a dimension-scoped table — warn (manual
            // masks only; generated masks always declare their scopes).
            if (sqlScopedRefs && (generatedMaskNames == null || !generatedMaskNames.Contains(maskName)))
            {
                warnings?.Add(
                    $"Mask '{maskName}' references row-scoped tables but does not declare `row_scopes` — its rows are NOT dimension-filtered.");
            }
        }
    }

    private static bool ContainsScopePlaceholder(string? sql, string column)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        return Regex.IsMatch(sql, $@"(?i)([:@])__scope_{Regex.Escape(column)}\b");
    }

    public static void EnsureTenantActive(Database db, TenancyConfig? tenancy, object? tenantId, string? @ref = null)
    {
        if (tenancy == null || tenantId == null)
            return;

        var tenantValue = TenantValueAsString(tenantId);
        if (tenantValue == null)
            return;

        // SELECT * and inspect the returned columns rather than running a
        // separate information_schema probe first — this check runs per-request
        // when tenancy.check_active_per_request is on, and the registry is a
        // small internal table, so the extra introspection round-trip on the hot
        // auth path is pure overhead. Matches the PHP runtime's assertTenantActive.
        var sql = $"SELECT {db.TopClause(1)}* FROM {db.QuoteIdentifier(tenancy.Table)} " +
                  $"WHERE {db.QuoteIdentifier("id")} = @tenant_id{db.RowLimitSuffix(1)}";
        var result = db.Run(sql, new Dictionary<string, object?> { ["tenant_id"] = tenantId });

        if (result.Rows.Count == 0)
            throw TenantSuspended($"Tenant '{tenantValue}' is not active.");

        var columnIndex = result.Columns
            .Select((name, index) => new { name, index })
            .ToDictionary(item => item.name, item => item.index, StringComparer.Ordinal);

        if (columnIndex.TryGetValue("is_active", out var activeIndex))
        {
            var activeValue = activeIndex < result.Rows[0].Length ? result.Rows[0][activeIndex] : null;
            if (activeValue is bool b && !b)
                throw TenantSuspended($"Tenant '{tenantValue}' is suspended.");
            if (activeValue is int i && i == 0)
                throw TenantSuspended($"Tenant '{tenantValue}' is suspended.");
            if (activeValue is long l && l == 0)
                throw TenantSuspended($"Tenant '{tenantValue}' is suspended.");
            if (activeValue is string s && (s == "0" || s.Equals("false", StringComparison.OrdinalIgnoreCase)))
                throw TenantSuspended($"Tenant '{tenantValue}' is suspended.");
        }

        if (columnIndex.TryGetValue("deleted_at", out var deletedIndex))
        {
            var deletedValue = deletedIndex < result.Rows[0].Length ? result.Rows[0][deletedIndex] : null;
            if (deletedValue != null && !string.IsNullOrWhiteSpace(deletedValue.ToString()))
                throw TenantSuspended($"Tenant '{tenantValue}' is suspended.");
        }
    }

    private static void ValidateConfig(TenancyConfig tenancy)
    {
        if (string.IsNullOrWhiteSpace(tenancy.Table))
            throw ConfigInvalid("Config key `tenancy.table` is required.", "Use a non-empty registry table name.");
        if (string.IsNullOrWhiteSpace(tenancy.Column))
            throw ConfigInvalid("Config key `tenancy.column` is required.", "Use a non-empty tenant discriminator column name.");
        if (!string.Equals(tenancy.Enforce, "strict", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(tenancy.Enforce, "relaxed", StringComparison.OrdinalIgnoreCase))
        {
            throw ConfigInvalid(
                $"Config key `tenancy.enforce` has invalid value '{tenancy.Enforce}'.",
                "Use either 'strict' or 'relaxed'.");
        }
    }

    private static ApiException TenantSuspended(string message) =>
        new("TENANT_SUSPENDED", message, 403, null, new Dictionary<string, object?>
        {
            ["hint"] = "Reactivate the tenant or select a different tenant before retrying."
        });

    private static bool IsExplicitTenantOptOut(object? tenantScope) =>
        tenantScope is bool b && b == false;

    private static bool ContainsTenantPlaceholder(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return false;

        return Regex.IsMatch(sql, @"(?i)([:@])__tenant\b");
    }

    private static bool ReferencesScopedTable(string? sql, HashSet<string> scopedTables, HashSet<string> globalTables)
    {
        if (string.IsNullOrWhiteSpace(sql) || scopedTables.Count == 0)
            return false;

        foreach (var table in scopedTables)
        {
            if (globalTables.Contains(table))
                continue;

            if (Regex.IsMatch(sql, $@"(?i)\b{Regex.Escape(table)}\b"))
                return true;
        }
        return false;
    }

    private static ApiException ConfigInvalid(string message, string hint) =>
        new("CONFIG_INVALID", message, 500, null, new Dictionary<string, object?>
        {
            ["hint"] = hint
        });
}
