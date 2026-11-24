using Shape.Core;
using Shape.Metadata;

namespace Shape.Generator;

/// <summary>
/// Generates the standard set of CRUD masks (<c>list</c>, <c>get</c>, <c>getMany</c>,
/// <c>by{Fk}</c>, plus a writable mask) from <see cref="EntityMeta"/> metadata.
/// Honors soft-delete, audit columns, and inverse FK relations on writable masks.
/// </summary>
public sealed class MaskGenerator
{
    private readonly string _driver;
    private readonly int _defaultListLimit;
    private readonly TenancyConfig? _tenancy;
    private readonly Dictionary<string, ScopeConfig>? _scopes;

    /// <param name="driver">Identifier-quoting dialect: <c>"mysql"</c> (backticks), <c>"pgsql"</c>/<c>"sqlite"</c> (double-quotes), or <c>"mssql"</c>/<c>"sqlsrv"</c>/<c>"sqlserver"</c> (brackets).</param>
    /// <param name="defaultListLimit">
    /// Cap applied to <c>.list</c> and <c>.by{Fk}</c> masks when the caller doesn't
    /// supply <c>top</c>. BatchExecutor pushes the LIMIT into the SQL when no
    /// orderby/groupby/aggregate is requested. Without a default cap, an auto
    /// <c>.list</c> against a 75k-row table OOMs. Set 0 to disable.
    /// </param>
    /// <param name="tenancy">Optional tenancy config used to compile tenant-aware read variants and mask metadata.</param>
    /// <param name="scopes">Optional row-scope dimensions (ROW-SCOPES-PLAN) compiled alongside the tenant predicate.</param>
    public MaskGenerator(
        string driver = "mysql",
        int defaultListLimit = 100,
        TenancyConfig? tenancy = null,
        Dictionary<string, ScopeConfig>? scopes = null)
    {
        _driver = driver.ToLowerInvariant();
        _defaultListLimit = defaultListLimit > 0 ? defaultListLimit : 0;
        _tenancy = tenancy;
        _scopes = scopes;
    }

    /// <param name="entities">Entities to generate masks for.</param>
    /// <param name="accessMap">
    /// Optional per-table role allowlist (<see cref="ShapeConfig.Access"/>).
    /// When non-null:
    /// <list type="bullet">
    ///   <item>Entities whose table is not a key are skipped entirely — no masks emitted.</item>
    ///   <item>Entities whose table IS a key get the access-map roles stamped on every generated mask, replacing <see cref="EntityMeta.ReadRoles"/>/<see cref="EntityMeta.WriteRoles"/>.</item>
    /// </list>
    /// When null, today's behavior: all entities → masks, roles taken from entity meta.
    /// </param>
    public Dictionary<string, MaskConfig> Generate(
        IEnumerable<EntityMeta> entities,
        IReadOnlyDictionary<string, List<string>>? accessMap = null)
    {
        var asList = entities as IReadOnlyList<EntityMeta> ?? entities.ToList();
        var masks = new Dictionary<string, MaskConfig>();
        foreach (var entity in asList)
        {
            List<string>? overrideRoles = null;
            if (accessMap != null)
            {
                if (!accessMap.TryGetValue(entity.Table, out var roles))
                    continue;
                overrideRoles = roles;
            }
            foreach (var (name, mask) in GenerateForEntity(entity, asList, overrideRoles))
                masks[name] = mask;
        }
        return masks;
    }

    private Dictionary<string, MaskConfig> GenerateForEntity(
        EntityMeta meta,
        IReadOnlyList<EntityMeta> allEntities,
        List<string>? overrideRoles)
    {
        var readRoles = overrideRoles ?? meta.ReadRoles;
        var writeRoles = overrideRoles ?? meta.WriteRoles;
        var masks = new Dictionary<string, MaskConfig>();
        var table = meta.Table;
        var quotedTable = Quote(table);
        var columns = string.Join(", ", meta.Fields.Select(field => Quote(field.Name)));
        var pk = meta.PrimaryKey();
        var tenantCol = TenantColumnFor(meta);
        var tenantScoped = tenantCol != null;

        // Row scopes (ROW-SCOPES-PLAN §2.3): every dimension column present on
        // the table compiles its predicate into the scoped SQL alongside the
        // tenant predicate. The unscoped variant is emitted whenever tenancy OR
        // any applicable scope has unscoped roles — the runtime composes the
        // partial-unscoped case by wrapping it.
        var scopeCols = _scopes is { Count: > 0 }
            ? meta.ScopeColumns.Where(column => _scopes.ContainsKey(column)).ToList()
            : new List<string>();
        var rowScoped = scopeCols.Count > 0;
        var hasUnscopedVariant = (tenantScoped && _tenancy?.UnscopedRoles is { Count: > 0 })
            || scopeCols.Any(column => _scopes![column].UnscopedRoles is { Count: > 0 });

        // Soft-delete: when set, generated reads filter out tombstoned rows.
        var softCol = meta.SoftDeleteColumn;
        var tenantNote = tenantScoped ? ", limited to the caller's tenant" : "";
        var softNote = softCol != null ? ", excluding soft-deleted rows" : "";
        var whereClauses = new List<string>();
        if (tenantScoped)
            whereClauses.Add($"{Quote(tenantCol!)} = @__tenant");
        whereClauses.AddRange(scopeCols.Select(ScopeClause));
        if (softCol != null)
            whereClauses.Add($"{Quote(softCol)} IS NULL");
        var scopedWhere = BuildWhereClause(whereClauses);
        var unscopedWhere = softCol != null ? BuildWhereClause(new[] { $"{Quote(softCol)} IS NULL" }) : "";

        // Fresh carrier per mask — masks must not share one mutable dictionary.
        Dictionary<string, RowScopeMeta> RowScopesFor() => scopeCols.ToDictionary(
            column => column,
            column => new RowScopeMeta { Multi = _scopes![column].Multi },
            StringComparer.Ordinal);

        // Stamp the scoping contract on one generated mask. The unscoped SQL is
        // only attached when some role can actually cross a boundary.
        void ApplyScoping(MaskConfig mask, string unscopedSql)
        {
            if (tenantScoped)
                mask.TenantScope = tenantCol;
            if (rowScoped)
                mask.RowScopes = RowScopesFor();
            if (hasUnscopedVariant)
                mask.SqlUnscoped = unscopedSql;
        }

        // ── list ───────────────────────────────────────────────────
        var listMask = new MaskConfig
        {
            Sql = $"SELECT {columns} FROM {quotedTable}{scopedWhere}",
            Params = new Dictionary<string, ParamSchema>(),
            Roles = readRoles.ToList(),
            Description = $"List rows from {table}{softNote}{tenantNote}."
        };
        ApplyScoping(listMask, $"SELECT {columns} FROM {quotedTable}{unscopedWhere}");
        if (_defaultListLimit > 0)
            listMask.DefaultLimit = _defaultListLimit;
        masks[$"{table}.list"] = listMask;

        // ── get + getMany (PK-keyed) ───────────────────────────────
        if (pk != null)
        {
            var qpk = Quote(pk.Name);
            masks[$"{table}.get"] = new MaskConfig
            {
                Sql = $"SELECT {columns} FROM {quotedTable}{BuildWhereClause(new[]
                {
                    $"{qpk} = @{pk.Name}"
                }.Concat(whereClauses))}",
                Params = new Dictionary<string, ParamSchema>
                {
                    [pk.Name] = new() { Type = pk.ParamType(), Required = true }
                },
                Roles = readRoles.ToList(),
                Description = $"Get one row from {table} by primary key ({pk.Name}){softNote}{tenantNote}."
            };
            ApplyScoping(masks[$"{table}.get"], $"SELECT {columns} FROM {quotedTable}{BuildWhereClause(new[]
            {
                $"{qpk} = @{pk.Name}"
            }.Concat(SoftDeleteOnly(softCol)))}");

            var paramName = pk.Name + "s";
            var arrayType = pk.ParamType() == "int" ? "array_int" : "array_string";
            masks[$"{table}.getMany"] = new MaskConfig
            {
                Sql = $"SELECT {columns} FROM {quotedTable}{BuildWhereClause(new[]
                {
                    $"{qpk} IN @{paramName}"
                }.Concat(whereClauses))}",
                Params = new Dictionary<string, ParamSchema>
                {
                    [paramName] = new() { Type = arrayType, Required = true }
                },
                Roles = readRoles.ToList(),
                Description = $"Get multiple rows from {table} by a list of primary keys ({pk.Name}){softNote}{tenantNote}."
            };
            ApplyScoping(masks[$"{table}.getMany"], $"SELECT {columns} FROM {quotedTable}{BuildWhereClause(new[]
            {
                $"{qpk} IN @{paramName}"
            }.Concat(SoftDeleteOnly(softCol)))}");
        }

        // ── by{Fk} ─────────────────────────────────────────────────
        var filterableFields = new List<FieldMeta>();
        foreach (var field in meta.FkFields())
        {
            filterableFields.Add(field);
            var qf = Quote(field.Name);
            var byFkMask = new MaskConfig
            {
                Sql = $"SELECT {columns} FROM {quotedTable}{BuildWhereClause(new[]
                {
                    $"{qf} = @{field.Name}"
                }.Concat(whereClauses))}",
                Params = new Dictionary<string, ParamSchema>
                {
                    [field.Name] = new() { Type = field.ParamType(), Required = true }
                },
                Roles = readRoles.ToList(),
                Description = $"List {table} rows filtered by {field.Name}{softNote}{tenantNote}."
            };
            ApplyScoping(byFkMask, $"SELECT {columns} FROM {quotedTable}{BuildWhereClause(new[]
            {
                $"{qf} = @{field.Name}"
            }.Concat(SoftDeleteOnly(softCol)))}");
            if (_defaultListLimit > 0)
                byFkMask.DefaultLimit = _defaultListLimit;
            masks[$"{table}.by{PascalCase(field.Name)}"] = byFkMask;
        }

        // ── by{Enum} ───────────────────────────────────────────────
        // Closed value sets are common filter axes (status, role, kind, …).
        foreach (var field in meta.Fields.Where(f => f.EnumValues is { Count: > 0 } && !f.IsFk && !f.IsPrimary))
        {
            filterableFields.Add(field);
            var qf = Quote(field.Name);
            var byEnumMask = new MaskConfig
            {
                Sql = $"SELECT {columns} FROM {quotedTable}{BuildWhereClause(new[]
                {
                    $"{qf} = @{field.Name}"
                }.Concat(whereClauses))}",
                Params = new Dictionary<string, ParamSchema>
                {
                    [field.Name] = new() { Type = "string", Required = true }
                },
                Roles = readRoles.ToList(),
                Description = $"List {table} rows filtered by {field.Name} (one of: {string.Join(", ", field.EnumValues!)}){softNote}{tenantNote}."
            };
            ApplyScoping(byEnumMask, $"SELECT {columns} FROM {quotedTable}{BuildWhereClause(new[]
            {
                $"{qf} = @{field.Name}"
            }.Concat(SoftDeleteOnly(softCol)))}");
            if (_defaultListLimit > 0)
                byEnumMask.DefaultLimit = _defaultListLimit;
            masks[$"{table}.by{PascalCase(field.Name)}"] = byEnumMask;
        }

        // ── by{A}And{B} (combined filter pairs) ────────────────────
        // Walk every ordered pair of filterable fields. Cap at 2 columns —
        // 3+-column products explode the mask count without adding much
        // value (devs reach for `$filter` or a hand-written mask first).
        for (var i = 0; i < filterableFields.Count; i++)
        {
            for (var j = i + 1; j < filterableFields.Count; j++)
            {
                var a = filterableFields[i];
                var b = filterableFields[j];
                var qa = Quote(a.Name);
                var qb = Quote(b.Name);
                var maskName = $"{table}.by{PascalCase(a.Name)}And{PascalCase(b.Name)}";
                var combined = new MaskConfig
                {
                    Sql = $"SELECT {columns} FROM {quotedTable}{BuildWhereClause(new[]
                    {
                        $"{qa} = @{a.Name}",
                        $"{qb} = @{b.Name}"
                    }.Concat(whereClauses))}",
                    Params = new Dictionary<string, ParamSchema>
                    {
                        [a.Name] = new() { Type = a.IsFk ? a.ParamType() : "string", Required = true },
                        [b.Name] = new() { Type = b.IsFk ? b.ParamType() : "string", Required = true }
                    },
                    Roles = readRoles.ToList(),
                    Description = $"List {table} filtered by both {a.Name} and {b.Name}{softNote}{tenantNote}."
                };
                ApplyScoping(combined, $"SELECT {columns} FROM {quotedTable}{BuildWhereClause(new[]
                {
                    $"{qa} = @{a.Name}",
                    $"{qb} = @{b.Name}"
                }.Concat(SoftDeleteOnly(softCol)))}");
                if (_defaultListLimit > 0)
                    combined.DefaultLimit = _defaultListLimit;
                masks[maskName] = combined;
            }
        }

        // ── with{Relation} (auto-JOIN on FK) ───────────────────────
        // Walk each FK; if the parent entity is in scope, emit a JOIN mask.
        // Parent columns are aliased with the relation prefix to avoid name
        // collisions (e.g. invoices.id and customers.id both become id without).
        foreach (var fk in meta.FkFields())
        {
            var parent = allEntities.FirstOrDefault(e => e.ClrType == fk.FkEntityType);
            if (parent == null) continue;

            var relationName = StripIdSuffix(fk.Name);     // customer_id → customer
            var aliasI = "i_" + table[..Math.Min(2, table.Length)];
            var aliasP = "p_" + parent.Table[..Math.Min(2, parent.Table.Length)];

            var selfCols = string.Join(", ", meta.Fields.Select(f => $"{aliasI}.{Quote(f.Name)}"));
            var parentCols = string.Join(", ",
                parent.Fields.Where(f => f.Name != "id" || true) // include parent.id as <relation>_id_parent
                    .Select(f => $"{aliasP}.{Quote(f.Name)} AS {Quote($"{relationName}_{f.Name}")}"));
            var fkQuoted = Quote(fk.Name);
            var parentPk = parent.PrimaryKey()?.Name ?? "id";

            var withSql = $"SELECT {selfCols}, {parentCols} FROM {quotedTable} {aliasI} " +
                          $"INNER JOIN {Quote(parent.Table)} {aliasP} ON {aliasP}.{Quote(parentPk)} = {aliasI}.{fkQuoted}";
            var withWhere = new List<string>();
            if (tenantScoped)
                withWhere.Add($"{aliasI}.{Quote(tenantCol!)} = @__tenant");
            withWhere.AddRange(scopeCols.Select(column => AliasedScopeClause(aliasI, column)));
            if (softCol != null)
                withWhere.Add($"{aliasI}.{Quote(softCol)} IS NULL");
            withSql += BuildWhereClause(withWhere);

            var withMask = new MaskConfig
            {
                Sql = withSql,
                Params = new Dictionary<string, ParamSchema>(),
                Roles = readRoles.ToList(),
                Description = $"List {table} joined with {parent.Table} ({relationName}_*){softNote}{tenantNote}."
            };
            var relationUnscopedWhere = new List<string>();
            if (softCol != null)
                relationUnscopedWhere.Add($"{aliasI}.{Quote(softCol)} IS NULL");
            ApplyScoping(withMask, $"SELECT {selfCols}, {parentCols} FROM {quotedTable} {aliasI} " +
                                   $"INNER JOIN {Quote(parent.Table)} {aliasP} ON {aliasP}.{Quote(parentPk)} = {aliasI}.{fkQuoted}" +
                                   BuildWhereClause(relationUnscopedWhere));
            if (_defaultListLimit > 0)
                withMask.DefaultLimit = _defaultListLimit;
            masks[$"{table}.with{PascalCase(relationName)}"] = withMask;
        }

        // ── writable ───────────────────────────────────────────────
        var mode = meta.EffectiveWritableMode();
        if (mode != "none")
        {
            var firstColumn = meta.Fields.FirstOrDefault()?.Name ?? "id";
            var writeNote = softCol != null ? $" DELETE soft-deletes (sets {softCol})." : "";
            var auditNote = meta.AuditColumns.Count > 0
                ? $" Audit columns ({string.Join(", ", meta.AuditColumns.Keys)}) are populated automatically — do not include them in `data`."
                : "";

            var childRelations = CollectChildRelations(meta, allEntities);
            var relationsNote = childRelations.Count > 0
                ? " Bulk-create children inline via `relations: { <child_table>: [...] }` — framework walks the FK chain in one transaction."
                : "";

            var capabilities = mode switch
            {
                "append_only" => "Insert only — UPDATE and DELETE rejected (append-only contract).",
                "no_delete"   => "Insert / update only — DELETE rejected.",
                _             => "Insert / update / delete rows"
            };

            // Per-operation split (roles_by_action): the mask-level Roles gate
            // must admit every per-action role — the union — while the executor
            // narrows per mutation action. Only entity-declared ([Access]) —
            // a flat access-map override keeps flat semantics.
            var rolesByAction = overrideRoles == null && meta.WriteRolesByAction.Count > 0
                ? meta.WriteRolesByAction
                : null;
            var writableRoles = writeRoles.ToList();
            if (rolesByAction != null)
            {
                writableRoles = writableRoles
                    .Concat(rolesByAction.Values.SelectMany(roles => roles))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }

            var writableMask = new MaskConfig
            {
                // SQL Server has no LIMIT; it caps rows with SELECT TOP n.
                Sql = IsMssql
                    ? $"SELECT TOP 1 {Quote(firstColumn)} FROM {quotedTable}"
                    : $"SELECT {Quote(firstColumn)} FROM {quotedTable} LIMIT 1",
                Roles = writableRoles,
                Writable = true,
                WritableMode = mode == "full" ? null : mode,
                // Carry the PK name on every writable mask (manifest key `pk`) so the
                // executor can thread it into InsertReturningId; `pk_strategy` rides
                // along only when the resolver decided the framework allocates the id.
                Pk = pk?.Name,
                PkStrategy = meta.PkStrategy == "manual" ? "manual" : null,
                Description = $"{capabilities} in {table}.{writeNote}{auditNote}{relationsNote}"
            };
            if (rolesByAction != null)
                writableMask.RolesByAction = rolesByAction.ToDictionary(
                    kv => kv.Key, kv => new List<string>(kv.Value), StringComparer.Ordinal);
            // Field-level write gate (SECURITY-REVIEW H1): only these columns may
            // appear in a client `data` block. Excludes audit columns (auto-stamped
            // — listing them would let a client forge created_by/updated_at),
            // auto-increment PKs (DB-assigned), and sensitive/privilege columns
            // (password_hash, role, is_active, … — the escalation vector). The
            // tenant + row-scope columns stay writable: their values are validated
            // and stamped server-side, and unscoped-role writes must supply them.
            var manualPk = meta.PkStrategy == "manual";
            writableMask.WritableColumns = meta.Fields
                .Where(f =>
                {
                    if (meta.AuditColumns.ContainsKey(f.Name)) return false;
                    if (f.IsPrimary && (f.IsAutoIncrement || !manualPk)) return false;
                    if (Metadata.SensitiveColumns.IsWriteSensitive(f.Name)
                        && !f.IsTenantColumn && !f.IsScopeColumn) return false;
                    return true;
                })
                .Select(f => f.Name)
                .ToList();

            if (tenantScoped)
                writableMask.TenantScope = tenantCol;
            // The writable mask carries `row_scopes` (no unscoped variant — its
            // SQL is only an existence probe) so the write path can stamp and
            // guard the dimension columns.
            if (rowScoped)
                writableMask.RowScopes = RowScopesFor();
            if (softCol != null)
                writableMask.SoftDelete = softCol;
            if (meta.AuditColumns.Count > 0)
                writableMask.Audit = new Dictionary<string, string>(meta.AuditColumns);
            if (childRelations.Count > 0)
                writableMask.Relations = childRelations;

            // FK existence checks for every FK column where the parent entity
            // is in scope. BatchExecutor runs these before insert/update so
            // the caller gets a typed FK_VIOLATION error instead of a raw
            // constraint failure from the driver.
            var fkChecks = new Dictionary<string, FkCheck>();
            foreach (var fk in meta.FkFields())
            {
                var parent = allEntities.FirstOrDefault(e => e.ClrType == fk.FkEntityType);
                if (parent == null) continue;
                var parentPk = parent.PrimaryKey()?.Name ?? "id";
                fkChecks[fk.Name] = new FkCheck { Table = parent.Table, PrimaryKey = parentPk };
            }
            if (fkChecks.Count > 0)
                writableMask.FkChecks = fkChecks;

            masks[table] = writableMask;
        }

        return masks;
    }

    /// <summary>
    /// Inverse-FK map: which child tables point at <paramref name="parent"/>, and
    /// through which columns? Used by <see cref="BatchExecutor"/>'s nested-create
    /// walker to attach children. When a child has multiple FKs to the same parent
    /// (e.g. <c>notes.author_id</c> AND <c>notes.editor_id</c> both → users), all
    /// columns appear and the dispatcher requires explicit disambiguation at call
    /// time.
    /// </summary>
    private static Dictionary<string, RelationMeta> CollectChildRelations(
        EntityMeta parent, IReadOnlyList<EntityMeta> allEntities)
    {
        var relations = new Dictionary<string, RelationMeta>();
        var parentTable = parent.Table;
        var parentClass = parent.ClrType;

        foreach (var candidate in allEntities)
        {
            if (candidate.Table == parentTable && candidate.ClrType == parentClass)
                continue;

            foreach (var fk in candidate.FkFields())
            {
                var target = fk.FkEntityType;
                if (target == null) continue;

                if (target == parentClass)
                {
                    if (!relations.TryGetValue(candidate.Table, out var rel))
                    {
                        rel = new RelationMeta();
                        relations[candidate.Table] = rel;
                    }
                    rel.FkColumns.Add(fk.Name);
                }
            }
        }
        return relations;
    }

    private string? TenantColumnFor(EntityMeta meta)
    {
        if (_tenancy == null)
            return null;
        return meta.TenantColumn;
    }

    /// <summary>
    /// The compiled per-dimension predicate: <c>= @__scope_&lt;col&gt;</c>, or
    /// <c>IN @__scope_&lt;col&gt;</c> for multi-valued claims. The multi form is
    /// parenthesis-free on purpose — Dapper's native list expansion
    /// (<c>IN @ids</c> → <c>IN (@ids0,@ids1)</c>) requires the bare token;
    /// wrapping it as <c>IN (@ids)</c> yields a SQLite "row value misused"
    /// error. (PHP's Database expands array params itself and keeps the parens.)
    /// </summary>
    private string ScopeClause(string column) => _scopes![column].Multi
        ? $"{Quote(column)} IN @__scope_{column}"
        : $"{Quote(column)} = @__scope_{column}";

    /// <summary>Alias-qualified variant of <see cref="ScopeClause"/> for JOIN masks.</summary>
    private string AliasedScopeClause(string alias, string column) => _scopes![column].Multi
        ? $"{alias}.{Quote(column)} IN @__scope_{column}"
        : $"{alias}.{Quote(column)} = @__scope_{column}";

    private static string BuildWhereClause(IEnumerable<string> clauses)
    {
        var list = clauses.Where(clause => !string.IsNullOrWhiteSpace(clause)).ToList();
        return list.Count == 0 ? "" : " WHERE " + string.Join(" AND ", list);
    }

    /// <summary>
    /// The residual predicate for an unscoped read variant: the tenant clause is
    /// dropped (a superadmin sees across tenants), but soft-deleted rows stay
    /// hidden. Empty when the entity has no soft-delete column.
    /// </summary>
    private string[] SoftDeleteOnly(string? softCol) =>
        softCol != null ? new[] { $"{Quote(softCol)} IS NULL" } : Array.Empty<string>();

    private bool IsMssql => _driver is "mssql" or "sqlsrv" or "sqlserver";

    private string Quote(string name) => _driver switch
    {
        "pgsql" or "postgres" or "postgresql" or "sqlite" => $"\"{name.Replace("\"", "\"\"")}\"",
        _ when IsMssql => $"[{name.Replace("]", "]]")}]",
        _ => $"`{name.Replace("`", "``")}`"
    };

    private static string PascalCase(string value)
    {
        return string.Concat(value
            .Split('_', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    /// <summary>
    /// <c>customer_id</c> → <c>customer</c>. Used to derive the relation name
    /// for <c>with{Relation}</c> masks. If the column doesn't end in <c>_id</c>
    /// the original name is returned (the developer presumably named it
    /// intentionally — e.g. <c>author</c> referencing <c>authors.id</c>).
    /// </summary>
    private static string StripIdSuffix(string columnName)
    {
        return columnName.EndsWith("_id", StringComparison.OrdinalIgnoreCase)
            ? columnName[..^3]
            : columnName;
    }
}
