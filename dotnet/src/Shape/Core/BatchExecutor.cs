using System.Globalization;
using System.Text.Json;
using Shape.Metadata;

namespace Shape.Core;

public sealed class BatchExecutor
{
    private const int MaxTop = 200;

    /// <summary>
    /// Cap on the number of queries/mutations in a single batch (SECURITY-REVIEW
    /// M5). <c>top</c> bounds rows per query, but without this a client could
    /// send tens of thousands of ops in one request as a DoS.
    /// </summary>
    private const int MaxBatchItems = 100;

    private readonly Database _database;
    private readonly MaskRegistry _registry;
    private readonly ValidatorRegistry _validators;
    private readonly TenancyConfig? _tenancy;
    private readonly List<ScopeSpec> _scopeSpecs;
    private readonly ParamResolver _resolver = new();

    /// <summary>
    /// (table, action, rowId, authContext, changedColumns) — invoked after each
    /// successful mutation. Inside a transaction the audit write is part of the
    /// same transaction; outside, best-effort.
    /// </summary>
    public delegate void AuditLogger(
        string table,
        string action,
        string? rowId,
        Dictionary<string, object?> authContext,
        List<string> changedColumns);

    private AuditLogger? _auditLogger;

    /// <summary>Audit Tier 2 sink: like <see cref="AuditLogger"/> plus before/after values.</summary>
    public delegate void AuditValuesLogger(
        string table,
        string action,
        string? rowId,
        Dictionary<string, object?> authContext,
        List<string> changedColumns,
        Dictionary<string, object?>? oldValues,
        Dictionary<string, object?>? newValues);

    private AuditValuesLogger? _auditValues;
    private HashSet<string>? _diffTables;      // null = every table
    private List<string> _diffExclude = new();
    private Action? _diffProbe;
    private bool _diffProbed;
    private const int AuditDiffMaxRows = 200;

    public BatchExecutor(
        Database database,
        MaskRegistry registry,
        ValidatorRegistry? validators = null,
        TenancyConfig? tenancy = null,
        Dictionary<string, ScopeConfig>? scopes = null)
    {
        _database = database;
        _registry = registry;
        _validators = validators ?? new ValidatorRegistry();
        _tenancy = tenancy;
        _scopeSpecs = ScopeSpec.Build(tenancy, scopes);
    }

    public void SetAuditLogger(AuditLogger cb) => _auditLogger = cb;

    /// <summary>
    /// Audit Tier 2: capture before/after VALUES for the given tables (<c>null</c> = all). Update/delete
    /// snapshot the matched rows (same WHERE incl. scope/tenant guards) before the mutation and log one
    /// entry per affected row; sensitive columns are recorded as <c>[redacted]</c>. Requires
    /// <see cref="SetAuditLogger"/>.
    /// </summary>
    /// <param name="probe">Called once before the first mutation to fail loudly when the audit table lacks the value columns.</param>
    public void SetAuditDiff(IEnumerable<string>? tables, IEnumerable<string> exclude, AuditValuesLogger logger, Action? probe = null)
    {
        _diffTables = tables == null ? null : new HashSet<string>(tables, StringComparer.Ordinal);
        _diffExclude = exclude.ToList();
        _auditValues = logger;
        _diffProbe = probe;
        _diffProbed = false;
    }

    private bool DiffActiveFor(string table) =>
        _auditValues != null && _auditLogger != null && table != "audit_log"
        && (_diffTables == null || _diffTables.Contains(table));

    // ---------------------------------------------------------------------
    // Mutations
    // ---------------------------------------------------------------------

    public Dictionary<string, object?> ExecuteMutations(
        List<Dictionary<string, object?>> mutations,
        Dictionary<string, object?>? authContext = null,
        bool useTransaction = false)
    {
        authContext ??= new();

        if (mutations.Count > MaxBatchItems)
            throw new ApiException(
                "MUTATION_VALIDATION_ERROR",
                $"Batch exceeds the maximum of {MaxBatchItems} mutations.",
                400,
                null,
                new Dictionary<string, object?>
                {
                    ["expected"] = $"at most {MaxBatchItems} mutations per request",
                    ["hint"] = "Split the work across multiple requests."
                });

        if (mutations.Count == 0)
            throw new ApiException(
                "MUTATION_VALIDATION_ERROR",
                "Request must include at least one mutation.",
                400,
                null,
                new Dictionary<string, object?>
                {
                    ["expected"] = "non-empty array of mutation objects",
                    ["example"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["ref"] = "m1",
                            ["table"] = "books",
                            ["action"] = "insert",
                            ["data"] = new Dictionary<string, object?> { ["title"] = "example" }
                        }
                    }
                });

        // Force a transaction whenever any mutation declares `relations` —
        // a partial nested-create that left orphan children would be worse
        // than a clean rollback.
        if (!useTransaction)
        {
            foreach (var m in mutations)
            {
                if (m.TryGetValue("relations", out var rel) && rel is Dictionary<string, object?> relDict && relDict.Count > 0)
                {
                    useTransaction = true;
                    break;
                }
            }
        }

        return useTransaction
            ? ExecuteMutationsInTransaction(mutations, authContext)
            : ExecuteMutationsWithoutTransaction(mutations, authContext);
    }

    private Dictionary<string, object?> ExecuteMutationsInTransaction(
        List<Dictionary<string, object?>> mutations, Dictionary<string, object?> authContext)
    {
        _database.BeginTransaction();
        try
        {
            var results = new List<Dictionary<string, object?>>();
            var seenRefs = new HashSet<string>();

            foreach (var mutation in mutations)
                results.Add(ExecuteSingleMutation(mutation, authContext, seenRefs));

            _database.Commit();
            return new Dictionary<string, object?> { ["results"] = results };
        }
        catch
        {
            if (_database.IsInTransaction)
                _database.Rollback();
            throw;
        }
    }

    private Dictionary<string, object?> ExecuteMutationsWithoutTransaction(
        List<Dictionary<string, object?>> mutations, Dictionary<string, object?> authContext)
    {
        var results = new List<Dictionary<string, object?>>();
        var seenRefs = new HashSet<string>();

        foreach (var mutation in mutations)
            results.Add(ExecuteSingleMutation(mutation, authContext, seenRefs));

        return new Dictionary<string, object?> { ["results"] = results };
    }

    private Dictionary<string, object?> ExecuteSingleMutation(
        Dictionary<string, object?> mutation, Dictionary<string, object?> authContext, HashSet<string> seenRefs)
    {
        var table = GetString(mutation, "table")?.Trim() ?? "";
        var action = GetString(mutation, "action")?.Trim() ?? "";
        var @ref = GetString(mutation, "ref");
        var filter = GetDict(mutation, "filter");
        var data = GetDict(mutation, "data");

        if (string.IsNullOrEmpty(table))
        {
            throw new ApiException(
                "MUTATION_VALIDATION_ERROR",
                "Field table is required.",
                400,
                @ref,
                new Dictionary<string, object?>
                {
                    ["expected"] = new Dictionary<string, object?>
                    {
                        ["table"] = "string (required)",
                        ["action"] = "string (required)",
                        ["data"] = "object",
                        ["filter"] = "object"
                    },
                    ["example"] = new Dictionary<string, object?>
                    {
                        ["ref"] = "m1",
                        ["table"] = "books",
                        ["action"] = "insert",
                        ["data"] = new Dictionary<string, object?> { ["title"] = "example" }
                    }
                });
        }

        if (action is not ("insert" or "update" or "delete"))
        {
            throw new ApiException(
                "MUTATION_VALIDATION_ERROR",
                "Field action must be insert, update, or delete.",
                400,
                @ref,
                new Dictionary<string, object?>
                {
                    ["expected"] = new Dictionary<string, object?>
                    {
                        ["action"] = "one of: 'insert', 'update', 'delete'"
                    },
                    ["example"] = new Dictionary<string, object?>
                    {
                        ["ref"] = "m1",
                        ["table"] = table,
                        ["action"] = "insert",
                        ["data"] = new Dictionary<string, object?> { ["column"] = "value" }
                    }
                });
        }

        if (@ref != null && !seenRefs.Add(@ref))
            throw new ApiException("DUPLICATE_REF", $"Duplicate ref '{@ref}'.", 400, @ref);

        var mask = _registry.Resolve(table, authContext, @ref ?? table);
        if (!mask.Writable)
            throw new ApiException(
                "AUTHORIZATION_ERROR",
                $"Table '{table}' is not writable for your role.",
                403,
                @ref,
                new Dictionary<string, object?>
                {
                    ["hint"] = $"The mask for '{table}' has Writable = false. Mark the mask as Writable in your config or request a different table."
                });

        // Append-only / no-delete contracts — reject inappropriate actions
        // before any SQL runs. The mask still exists (so insert paths work)
        // but the executor honours the mode tag set by MaskGenerator/EntityMeta.
        if (mask.WritableMode == "append_only" && action is "update" or "delete")
            throw new ApiException(
                "WRITABLE_MODE_FORBIDDEN",
                $"Table '{table}' is append-only — '{action}' is not permitted.",
                403,
                @ref,
                new Dictionary<string, object?>
                {
                    ["expected"] = new Dictionary<string, object?> { ["action"] = "'insert' (append-only contract)" },
                    ["hint"] = "Append-only tables (audit / event / log) accept INSERT only. Use a sibling table or mark the entity Writable=full if mutation is intended."
                });

        if (mask.WritableMode == "no_delete" && action == "delete")
            throw new ApiException(
                "WRITABLE_MODE_FORBIDDEN",
                $"Table '{table}' does not allow DELETE — entity is marked no_delete.",
                403,
                @ref,
                new Dictionary<string, object?>
                {
                    ["expected"] = new Dictionary<string, object?> { ["action"] = "one of: 'insert', 'update'" },
                    ["hint"] = "no_delete tables accept INSERT and UPDATE only. Consider a soft-delete column instead, or mark the entity Writable=full."
                });

        // Per-operation write roles: the mask-level Roles gate (Resolve above)
        // admits the union of all action roles; narrow to the caller's action
        // here. Config keys are create/update/delete — the mutation action
        // 'insert' maps to 'create'.
        if (mask.RolesByAction is { Count: > 0 } rolesByAction)
        {
            var actionKey = action == "insert" ? "create" : action;
            var allowed = rolesByAction.GetValueOrDefault(actionKey) ?? new List<string>();
            var callerRole = authContext.GetValueOrDefault("role")?.ToString() ?? "";
            if (callerRole.Length == 0 || !allowed.Contains(callerRole, StringComparer.Ordinal))
                throw new ApiException(
                    "AUTH_FORBIDDEN",
                    $"Your role cannot {action} on '{table}'.",
                    403,
                    @ref,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = new Dictionary<string, object?>
                        {
                            ["role"] = allowed.Count > 0 ? "one of: " + string.Join(", ", allowed) : "no role is permitted for this action"
                        },
                        ["hint"] = $"The '{actionKey}' operation on '{table}' is restricted separately from the other write operations."
                    });
        }

        if (action != "insert" && filter == null)
            throw new ApiException(
                "MUTATION_VALIDATION_ERROR",
                $"Field filter is required for {action} actions.",
                400,
                @ref,
                new Dictionary<string, object?>
                {
                    ["expected"] = new Dictionary<string, object?> { ["filter"] = $"object (required for {action})" },
                    ["example"] = new Dictionary<string, object?>
                    {
                        ["ref"] = @ref ?? "m1",
                        ["table"] = table,
                        ["action"] = action,
                        ["filter"] = new Dictionary<string, object?> { ["id"] = 1 }
                    }
                });

        if ((action == "insert" || action == "update") && data == null)
            throw new ApiException(
                "MUTATION_VALIDATION_ERROR",
                $"Field data is required for {action} actions.",
                400,
                @ref,
                new Dictionary<string, object?>
                {
                    ["expected"] = new Dictionary<string, object?> { ["data"] = $"object (required for {action})" },
                    ["example"] = new Dictionary<string, object?>
                    {
                        ["ref"] = @ref ?? "m1",
                        ["table"] = table,
                        ["action"] = action,
                        ["data"] = new Dictionary<string, object?> { ["column"] = "value" }
                    }
                });

        RunBeforeHook(table, action, data ?? new(), filter ?? new());

        if (data != null)
            ValidateFields(table, action, data);

        // Field-level write gate (SECURITY-REVIEW H1). Enforce the mask's
        // writable-column allowlist against the CLIENT-supplied data, before any
        // server-side augmentation (tenant stamping, audit columns) adds its own
        // keys. Blocks mass-assignment / privilege-escalation (e.g. a client
        // setting `role` or `password_hash` on a writable users entity) and
        // audit-column forgery. Null allowlist = legacy unrestricted mask.
        if (data != null && (action == "insert" || action == "update"))
            EnforceWritableColumns(data, mask, table, action, @ref);

        object? effectiveTenant = null;
        if (data != null)
        {
            data = ApplyScopeColumns(data, action, mask, authContext, @ref, out effectiveTenant);
        }

        // FK existence: run before SQL touches the row. Maps the raw driver
        // foreign-key constraint failure into a typed `FK_VIOLATION` error
        // the caller can match on (and an LLM can correct from).
        if (data != null && action != "delete")
            CheckForeignKeys(data, mask, authContext, @ref);

        // Audit columns: stamp created_at/by + updated_at/by from convention.
        // Runs AFTER validators so auto-injected values don't fail per-field
        // rules the dev didn't write for them. Dev's explicit value wins.
        if (data != null)
            data = ApplyAuditColumns(data, action, mask, authContext);

        var diff = DiffActiveFor(table);
        List<Dictionary<string, object?>>? snapshot = null;
        if (diff)
        {
            if (!_diffProbed && _diffProbe != null)
            {
                _diffProbe();   // fail before the data changes, not after
                _diffProbed = true;
            }
            if (action != "insert")
                snapshot = SnapshotRows(table, filter ?? new(), mask, authContext, action);
        }

        int affected;
        long? newId = null;
        string? rowId;

        switch (action)
        {
            case "insert":
            {
                var (a, id) = PerformInsert(table, data ?? new(), mask);
                affected = a;
                newId = id;
                rowId = id?.ToString(CultureInfo.InvariantCulture);
                break;
            }
            case "update":
                affected = PerformUpdate(table, filter ?? new(), data ?? new(), mask, authContext);
                rowId = ExtractRowId(filter ?? new());
                break;
            case "delete":
                affected = PerformDelete(table, filter ?? new(), mask, authContext);
                rowId = ExtractRowId(filter ?? new());
                break;
            default:
                affected = 0;
                rowId = null;
                break;
        }

        // Audit trail: append a row to the audit_log table when the framework
        // detected one at boot. audit_log itself is excluded so it never
        // recurses through this path.
        if (_auditLogger != null && table != "audit_log")
        {
            var changed = action == "delete" ? new List<string>() : (data?.Keys.ToList() ?? new List<string>());
            if (diff && affected > 0)
                LogWithDiff(table, action, rowId, authContext, changed, data ?? new(), snapshot, mask);
            else
                _auditLogger(table, action, rowId, authContext, changed);
        }

        RunAfterHook(table, action, affected);

        var result = new Dictionary<string, object?>
        {
            ["ref"] = @ref,
            ["action"] = action,
            ["table"] = table,
            ["affected"] = affected
        };
        if (newId.HasValue)
            result["id"] = newId.Value;

        // Bulk nested create: walk the relations tree. Child mutations recurse
        // back through ExecuteSingleMutation, so audit log + audit columns +
        // role checks + further nested relations all behave uniformly.
        if (action == "insert"
            && newId.HasValue
            && mutation.TryGetValue("relations", out var relRaw)
            && relRaw is Dictionary<string, object?> relations
            && relations.Count > 0)
        {
            var childTenant = effectiveTenant ?? ResolveTenantId(authContext);
            result["relations"] = WalkRelations(
                relations,
                mask,
                newId.Value,
                authContext,
                seenRefs,
                table,
                childTenant);
        }

        return result;
    }

    /// <summary>
    /// Walk a <c>relations</c> tree under a parent insert. For each entry:
    ///   1. Resolve the FK column from the parent mask's relations map.
    ///   2. Inject the parent's new PK into each child row's FK column.
    ///   3. Recurse via ExecuteSingleMutation (audit log, validators, soft-delete,
    ///      deeper relations all behave uniformly).
    ///   4. Strip <c>relations</c> from each child's data block before insert and
    ///      pass it as a sibling key on the recursive call — keeps arbitrary
    ///      nesting depth working without ambiguous schema.
    /// </summary>
    internal Dictionary<string, object?> WalkRelations(
        Dictionary<string, object?> relations,
        Mask parentMask,
        long parentId,
        Dictionary<string, object?> authContext,
        HashSet<string> seenRefs,
        string parentTable,
        object? parentTenantId = null)
    {
        var declared = parentMask.Relations ?? new Dictionary<string, RelationMeta>();
        var output = new Dictionary<string, object?>();

        foreach (var (key, rowsRaw) in relations)
        {
            // Allow `child_table` (auto-FK) or `child_table.fk_column` (explicit
            // disambiguation when the child has multiple FKs to this parent).
            string? explicitFk = null;
            var childTable = key;
            var dotIdx = key.IndexOf('.');
            if (dotIdx > 0)
            {
                childTable = key[..dotIdx];
                explicitFk = key[(dotIdx + 1)..];
            }

            if (!declared.TryGetValue(childTable, out var relMeta))
            {
                throw new ApiException(
                    "MUTATION_VALIDATION_ERROR",
                    $"Unknown relation '{childTable}' from '{parentTable}'.",
                    400,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = declared.Count == 0
                            ? $"(no auto-detected FK relations from {parentTable})"
                            : "one of: " + string.Join(", ", declared.Keys),
                        ["hint"] = "Auto-detected relations come from FK columns in other tables that point at this one."
                    });
            }

            var fkColumns = relMeta.FkColumns;
            if (fkColumns.Count == 0)
                throw new ApiException(
                    "MUTATION_VALIDATION_ERROR",
                    $"Relation '{childTable}' has no FK columns.",
                    500);

            string fkColumn;
            if (explicitFk != null)
            {
                if (!fkColumns.Contains(explicitFk))
                {
                    throw new ApiException(
                        "MUTATION_VALIDATION_ERROR",
                        $"FK column '{explicitFk}' is not a valid relation from '{childTable}' to '{parentTable}'.",
                        400,
                        null,
                        new Dictionary<string, object?>
                        {
                            ["expected"] = fkColumns.Select(c => $"{childTable}.{c}").ToList()
                        });
                }
                fkColumn = explicitFk;
            }
            else if (fkColumns.Count == 1)
            {
                fkColumn = fkColumns[0];
            }
            else
            {
                throw new ApiException(
                    "MUTATION_VALIDATION_ERROR",
                    $"'{childTable}' has multiple FKs to '{parentTable}'; disambiguate the relation key.",
                    400,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = fkColumns.Select(c => $"{childTable}.{c}").ToList(),
                        ["hint"] = $"Use 'relations: {{ \"{childTable}.<fk_column>\": [...] }}' to pick which FK the children attach through."
                    });
            }

            if (rowsRaw is not IList<object?> rows)
            {
                throw new ApiException(
                    "MUTATION_VALIDATION_ERROR",
                    $"Relation '{key}' must be an array of child rows.",
                    400,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = $"list<object> for relation '{childTable}'",
                        ["example"] = new Dictionary<string, object?>
                        {
                            [key] = new List<object?>
                            {
                                new Dictionary<string, object?> { ["<col>"] = "<value>" }
                            }
                        }
                    });
            }

            var childResults = new List<Dictionary<string, object?>>();
            foreach (var rowRaw in rows)
            {
                if (rowRaw is not Dictionary<string, object?> childRow)
                    throw new ApiException(
                        "MUTATION_VALIDATION_ERROR",
                        $"Each entry in relation '{key}' must be an object.",
                        400);

                // Pull out any further nesting before inserting the child.
                Dictionary<string, object?>? deeperRelations = null;
                if (childRow.TryGetValue("relations", out var deeperRaw)
                    && deeperRaw is Dictionary<string, object?> deeperDict
                    && deeperDict.Count > 0)
                {
                    deeperRelations = deeperDict;
                }
                childRow = childRow
                    .Where(kv => kv.Key != "relations")
                    .ToDictionary(kv => kv.Key, kv => kv.Value);

                // Inject the parent's PK into the child's FK column. Dev's
                // explicit value wins (rare — usually nonsensical here, but
                // we don't second-guess).
                if (!childRow.ContainsKey(fkColumn))
                    childRow[fkColumn] = parentId;

                var childMutation = new Dictionary<string, object?>
                {
                    ["table"] = childTable,
                    ["action"] = "insert",
                    ["data"] = childRow
                };
                if (deeperRelations != null)
                    childMutation["relations"] = deeperRelations;

                var childAuthContext = authContext;
                if (parentTenantId != null)
                {
                    childAuthContext = new Dictionary<string, object?>(authContext)
                    {
                        ["tenant_id"] = parentTenantId
                    };
                }

                childResults.Add(ExecuteSingleMutation(childMutation, childAuthContext, seenRefs));
            }

            output[childTable] = childResults;
        }

        return output;
    }

    // ---------------------------------------------------------------------
    // Hooks + per-field validators
    // ---------------------------------------------------------------------

    private void RunBeforeHook(string table, string action,
        Dictionary<string, object?> data, Dictionary<string, object?> filter)
    {
        var hook = _validators.GetBeforeHook(table, action);
        if (hook == null) return;

        try
        {
            hook(new Dictionary<string, object?>
            {
                ["table"] = table, ["action"] = action, ["data"] = data, ["filter"] = filter
            });
        }
        catch (Exception e)
        {
            throw new ApiException("HOOK_ERROR", "Before hook failed: " + e.Message, 400);
        }
    }

    private void RunAfterHook(string table, string action, int affected)
    {
        var hook = _validators.GetAfterHook(table, action);
        if (hook == null) return;

        try
        {
            hook(new Dictionary<string, object?>
            {
                ["table"] = table, ["action"] = action, ["affected"] = affected
            });
        }
        catch (Exception e)
        {
            throw new ApiException("HOOK_ERROR", "After hook failed: " + e.Message, 400);
        }
    }

    private void ValidateFields(string table, string action, Dictionary<string, object?> data)
    {
        foreach (var (field, value) in data)
        {
            var error = _validators.Validate(table, action, field, value);
            if (error != null)
                throw new ApiException("VALIDATION_ERROR", error, 400);
        }
    }

    /// <summary>
    /// Field-level write authorization (SECURITY-REVIEW H1). Rejects any
    /// client-supplied column that isn't on the mask's writable-column
    /// allowlist. Null allowlist = legacy unrestricted mask (all columns
    /// writable). Runs on raw client data before server-side augmentation.
    /// </summary>
    private static void EnforceWritableColumns(
        Dictionary<string, object?> data, Mask mask, string table, string action, string? @ref)
    {
        if (mask.WritableColumns is not { } allow)
            return;

        var allowed = new HashSet<string>(allow, StringComparer.Ordinal);
        foreach (var col in data.Keys)
        {
            var name = col.Trim();
            if (allowed.Contains(name))
                continue;
            throw new ApiException(
                "WRITE_COLUMN_FORBIDDEN",
                $"Column '{name}' is not writable on '{table}'.",
                403,
                @ref,
                new Dictionary<string, object?>
                {
                    ["expected"] = allow.Count > 0
                        ? "one of: " + string.Join(", ", allow)
                        : "(no columns are client-writable on this entity)",
                    ["hint"] = "Audit, primary-key, tenant-managed, and sensitive columns "
                        + "(password_hash, role, is_active, …) are not settable through the generic "
                        + "mutation op. Use a dedicated command or admin endpoint for those."
                });
        }
    }

    /// <summary>
    /// Verify each FK column referenced in <paramref name="data"/> points at an
    /// existing parent row. Runs as a single <c>EXISTS</c>-style query per FK;
    /// the cost is amortized on a real-world payload (most mutations touch
    /// 0–2 FKs). Null and absent values skip the check (respect FK nullability).
    /// </summary>
    private void CheckForeignKeys(
        Dictionary<string, object?> data,
        Mask mask,
        Dictionary<string, object?> authContext,
        string? @ref)
    {
        if (mask.FkChecks is not { Count: > 0 } fkChecks)
            return;

        // The tenant column is configurable (tenancy.column / mask.TenantScope),
        // so read the row's stamped tenant under that name — ApplyTenantColumn has
        // already run — rather than the literal "tenant_id". Fall back to the
        // caller's JWT claim (always keyed "tenant_id") when the row carries none.
        var currentTenant = mask.TenantScope is string tenantColumn
                            && !string.IsNullOrWhiteSpace(tenantColumn)
                            && data.TryGetValue(tenantColumn, out var stampedTenant)
                            && stampedTenant != null
            ? stampedTenant
            : ResolveTenantId(authContext);

        foreach (var (column, fk) in fkChecks)
        {
            if (!data.TryGetValue(column, out var value) || value is null)
                continue;

            var scopedMask = _registry.FindByTable(fk.Table);
            var scopedTenantColumn = scopedMask?.TenantScope as string;
            var parameters = new Dictionary<string, object?> { ["v"] = value };
            var baseWhere = $"WHERE {_database.QuoteIdentifier(fk.PrimaryKey)} = @v";
            var sql = $"SELECT {_database.TopClause(1)}1 FROM {_database.QuoteIdentifier(fk.Table)} " +
                      $"{baseWhere}{_database.RowLimitSuffix(1)}";
            var result = _database.Run(sql, parameters);
            if (result.Rows.Count == 0)
            {
                throw new ApiException(
                    "FK_VIOLATION",
                    $"Foreign key check failed: {column} = {value} does not exist in {fk.Table}.{fk.PrimaryKey}.",
                    400,
                    @ref,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = $"{column} must reference an existing row in {fk.Table}.{fk.PrimaryKey}",
                        ["hint"] = $"Insert a row in {fk.Table} first, then use the returned id here. Cross-mutation refs ({{\"ref\": \"prev\", \"value\": \"id\"}}) work in transactional batches."
                    });
            }

            if (_tenancy?.ValidateFks == true
                && !string.IsNullOrWhiteSpace(scopedTenantColumn)
                && currentTenant != null)
            {
                var scopedParameters = new Dictionary<string, object?>(parameters)
                {
                    ["__tenant"] = currentTenant
                };
                var scopedSql = $"SELECT {_database.TopClause(1)}1 FROM {_database.QuoteIdentifier(fk.Table)} " +
                                $"{baseWhere} AND {_database.QuoteIdentifier(scopedTenantColumn)} = @__tenant{_database.RowLimitSuffix(1)}";
                var scopedResult = _database.Run(scopedSql, scopedParameters);
                if (scopedResult.Rows.Count == 0)
                {
                    throw new ApiException(
                        "VALIDATION_ERROR",
                        $"Foreign key check failed: {column} = {value} belongs to a different tenant.",
                        400,
                        @ref,
                        new Dictionary<string, object?>
                        {
                            ["detail"] = "TENANT_FK_MISMATCH",
                            ["expected"] = $"{column} must reference a row in {fk.Table} owned by the caller's tenant",
                            ["hint"] = $"Insert a row in {fk.Table} for the caller's tenant, then use the returned id here."
                        });
                }
            }
        }
    }

    // ---------------------------------------------------------------------
    // SQL emission
    // ---------------------------------------------------------------------

    private (int Affected, long? NewId) PerformInsert(string table, Dictionary<string, object?> data, Mask mask)
    {
        if (data.Count == 0)
            throw new ApiException("MUTATION_VALIDATION_ERROR",
                "Insert requires at least one column.", 400);

        var pkColumn = mask.Pk ?? "id";

        // Manual-PK table and the client didn't send the id (null counts as absent —
        // matches PHP isset() semantics): allocate MAX(pk)+1 atomically. An explicit
        // client-supplied id always wins and flows through the regular path below —
        // that keeps data migration / import flows working on manual-PK tables.
        if (mask.PkStrategy == "manual"
            && (!data.TryGetValue(pkColumn, out var suppliedPk) || suppliedPk is null))
        {
            var rowData = data.Where(kv => kv.Key != pkColumn)
                .ToDictionary(kv => kv.Key.Trim(), kv => kv.Value);
            if (rowData.Keys.Any(string.IsNullOrEmpty))
                throw new ApiException("MUTATION_VALIDATION_ERROR", "Column name cannot be empty.", 400);

            var allocatedId = _database.InsertWithManualPk(table, pkColumn, rowData);
            return (1, allocatedId);
        }

        var columns = new List<string>();
        var placeholders = new List<string>();
        var parameters = new Dictionary<string, object?>();

        foreach (var (col, value) in data)
        {
            var colName = col.Trim();
            if (string.IsNullOrEmpty(colName))
                throw new ApiException("MUTATION_VALIDATION_ERROR", "Column name cannot be empty.", 400);
            columns.Add(colName);
            placeholders.Add("@" + colName);
            parameters[colName] = value;
        }

        var sql = $"INSERT INTO {QuoteName(table)} ({string.Join(", ", columns.Select(QuoteName))}) " +
                  $"VALUES ({string.Join(", ", placeholders)})";

        // §2.5 fix: thread the mask's real PK through so PG RETURNING / MSSQL id
        // plumbing stops silently assuming the column is named `id`.
        var newId = _database.InsertReturningId(sql, parameters, pkColumn);
        var affected = newId > 0 ? 1 : 0;
        return (affected, newId > 0 ? newId : (long?)null);
    }

    private int PerformUpdate(
        string table,
        Dictionary<string, object?> filter,
        Dictionary<string, object?> data,
        Mask mask,
        Dictionary<string, object?> authContext)
    {
        if (data.Count == 0)
            throw new ApiException("MUTATION_VALIDATION_ERROR",
                "Update requires at least one column to modify.", 400);

        var setClauses = new List<string>();
        var parameters = new Dictionary<string, object?>();
        int paramIndex = 0;

        foreach (var (col, value) in data)
        {
            var colName = col.Trim();
            if (string.IsNullOrEmpty(colName))
                throw new ApiException("MUTATION_VALIDATION_ERROR", "Column name cannot be empty.", 400);
            var paramName = $"set_{paramIndex}";
            setClauses.Add($"{QuoteName(colName)} = @{paramName}");
            parameters[paramName] = value;
            paramIndex++;
        }

        var (whereSql, whereParams) = BuildWhereClause(filter, mask, authContext);
        foreach (var (k, v) in whereParams)
            parameters[k] = v;

        var sql = $"UPDATE {QuoteName(table)} SET {string.Join(", ", setClauses)} WHERE {whereSql}";
        return _database.Exec(sql, parameters);
    }

    private int PerformDelete(
        string table,
        Dictionary<string, object?> filter,
        Mask mask,
        Dictionary<string, object?> authContext)
    {
        if (filter.Count == 0)
            throw new ApiException("MUTATION_VALIDATION_ERROR",
                "Delete requires a filter (no unfiltered deletes allowed).", 400);

        var (whereSql, parameters) = BuildWhereClause(filter, mask, authContext);

        // Soft-delete: mask carries a column name. DELETE becomes UPDATE; the
        // extra `<col> IS NULL` guard prevents re-stamping already-deleted rows
        // so a second delete returns affected = 0 (correct semantics).
        if (!string.IsNullOrEmpty(mask.SoftDelete))
        {
            var qCol = QuoteName(mask.SoftDelete);
            var sql = $"UPDATE {QuoteName(table)} SET {qCol} = CURRENT_TIMESTAMP " +
                      $"WHERE {whereSql} AND {qCol} IS NULL";
            return _database.Exec(sql, parameters);
        }

        var deleteSql = $"DELETE FROM {QuoteName(table)} WHERE {whereSql}";
        return _database.Exec(deleteSql, parameters);
    }

    private (string Sql, Dictionary<string, object?> Parameters) BuildWhereClause(
        Dictionary<string, object?> filter,
        Mask? mask = null,
        Dictionary<string, object?>? authContext = null)
    {
        if (filter.Count == 0)
            throw new ApiException("MUTATION_VALIDATION_ERROR", "Filter cannot be empty.", 400);

        var clauses = new List<string>();
        var parameters = new Dictionary<string, object?>();
        int paramIndex = 0;

        foreach (var (col, value) in filter)
        {
            var colName = col.Trim();
            if (string.IsNullOrEmpty(colName))
                throw new ApiException("MUTATION_VALIDATION_ERROR",
                    "Filter column name cannot be empty.", 400);

            if (value == null)
            {
                clauses.Add($"{QuoteName(colName)} IS NULL");
            }
            else
            {
                var paramName = $"where_{paramIndex}";
                clauses.Add($"{QuoteName(colName)} = @{paramName}");
                parameters[paramName] = value;
                paramIndex++;
            }
        }

        if (_tenancy != null && mask?.TenantScope is string tenantColumn && !string.IsNullOrWhiteSpace(tenantColumn))
        {
            if (IsUnscopedCaller(authContext))
            {
                // Superadmins can cross tenant boundaries; the caller
                // explicitly opted into the unscoped role.
            }
            else
            {
                var tenantValue = ResolveTenantId(authContext);
                if (tenantValue == null)
                    throw MissingTenantClaim(null);

                clauses.Add($"{QuoteName(tenantColumn)} = @__tenant");
                parameters["__tenant"] = tenantValue;
            }
        }

        // Row-scope guards (ROW-SCOPES-PLAN §2.4): UPDATE/DELETE WHERE gains one
        // predicate per dimension the caller is subject to — cross-dimension ids
        // yield affected-rows 0, no existence oracle.
        if (mask?.RowScopes is { Count: > 0 } rowScopes)
        {
            foreach (var spec in _scopeSpecs)
            {
                if (spec.Kind != ScopeKind.Dimension || !rowScopes.ContainsKey(spec.Column))
                    continue;
                if (IsUnscopedCaller(spec, authContext))
                    continue;

                var scopeValue = CallerScopeValue(spec, authContext);
                if (scopeValue == null)
                    throw MissingScopeClaim(spec, null);

                clauses.Add(spec.Multi
                    ? $"{QuoteName(spec.Column)} IN @{spec.ParamName}"
                    : $"{QuoteName(spec.Column)} = @{spec.ParamName}");
                parameters[spec.ParamName] = scopeValue;
            }
        }

        return (string.Join(" AND ", clauses), parameters);
    }

    private string QuoteName(string name) => _database.QuoteIdentifier(name);

    // ---------------------------------------------------------------------
    // Audit columns + auth user resolution
    // ---------------------------------------------------------------------

    /// <summary>
    /// Inject convention-driven audit values. The mask carries an audit map
    /// (column → kind) detected at boot. For each column:
    ///   - Dev's explicit value always wins.
    ///   - INSERT stamps every audit column.
    ///   - UPDATE never restamps created_* columns.
    ///   - 'timestamp' kind → current UTC time as 'yyyy-MM-dd HH:mm:ss'.
    ///   - 'user_id' kind → authenticated caller's id (id / oid / sub).
    ///     If no authenticated user is in scope, the column is left alone.
    /// </summary>
    internal static Dictionary<string, object?> ApplyAuditColumns(
        Dictionary<string, object?> data,
        string action,
        Mask mask,
        Dictionary<string, object?> authContext)
    {
        var audit = mask.Audit;
        if (audit == null || audit.Count == 0)
            return data;

        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        var userId = AuthUserId(authContext);

        var result = new Dictionary<string, object?>(data);
        foreach (var (col, kind) in audit)
        {
            if (action == "update" && col.StartsWith("created_", StringComparison.Ordinal))
                continue;
            if (result.ContainsKey(col))
                continue;

            if (kind == "timestamp")
                result[col] = now;
            else if (kind == "user_id" && userId != null)
                result[col] = userId;
        }
        return result;
    }

    /// <summary>
    /// Stable user identifier from auth context. Tries id (OIDC verifier sets
    /// this), oid (Entra), then sub (Shape's local HS256). Returns null when
    /// no authenticated user is in scope (NullGuard or anonymous request).
    /// </summary>
    internal static object? AuthUserId(Dictionary<string, object?> authContext)
    {
        foreach (var key in new[] { "id", "oid", "sub" })
        {
            if (!authContext.TryGetValue(key, out var val) || val is null)
                continue;

            switch (val)
            {
                case int i when i > 0:
                    return i;
                case long l when l > 0:
                    return l;
                case string s when !string.IsNullOrEmpty(s) && s != "0":
                    return s.All(char.IsDigit) ? long.Parse(s, CultureInfo.InvariantCulture) : (object)s;
            }
        }
        return null;
    }

    /// <summary>
    /// Rows an update/delete is about to touch (same WHERE + scope/tenant guards as the mutation).
    /// One extra row signals "more than the cap".
    /// </summary>
    private List<Dictionary<string, object?>> SnapshotRows(
        string table,
        Dictionary<string, object?> filter,
        Mask mask,
        Dictionary<string, object?> authContext,
        string action)
    {
        var rows = new List<Dictionary<string, object?>>();
        if (filter.Count == 0)
            return rows;   // the mutation itself rejects an empty filter

        var (whereSql, parameters) = BuildWhereClause(filter, mask, authContext);
        if (action == "delete" && !string.IsNullOrEmpty(mask.SoftDelete))
            whereSql += $" AND {QuoteName(mask.SoftDelete)} IS NULL";

        var cap = AuditDiffMaxRows + 1;
        var sql = $"SELECT {_database.TopClause(cap)}* FROM {QuoteName(table)} WHERE {whereSql}{_database.RowLimitSuffix(cap)}";
        var res = _database.Run(sql, parameters);
        foreach (var r in res.Rows)
        {
            var o = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < res.Columns.Count; i++) o[res.Columns[i]] = r[i];
            rows.Add(o);
        }
        return rows;
    }

    /// <summary>
    /// Audit entries with values: insert → new values; update → old/new of the columns that really changed,
    /// per affected row; delete → the full old row.
    /// </summary>
    private void LogWithDiff(
        string table,
        string action,
        string? rowId,
        Dictionary<string, object?> authContext,
        List<string> changed,
        Dictionary<string, object?> data,
        List<Dictionary<string, object?>>? snapshot,
        Mask mask)
    {
        var log = _auditValues!;
        var pk = string.IsNullOrEmpty(mask.Pk) ? "id" : mask.Pk;

        if (action == "insert")
        {
            var added = new Dictionary<string, object?>(data, StringComparer.Ordinal);
            if (rowId != null && !added.ContainsKey(pk)) added[pk] = rowId;
            log(table, "insert", rowId, authContext, changed, null, Redact(table, added));
            return;
        }

        var rows = snapshot ?? new();
        if (rows.Count == 0 || rows.Count > AuditDiffMaxRows)
        {
            // Nothing to compare (or too many rows to snapshot): Tier-1 style entry, flagged.
            var note = rows.Count > AuditDiffMaxRows
                ? new Dictionary<string, object?> { ["_note"] = $"more than {AuditDiffMaxRows} rows matched; values not captured" }
                : null;
            log(table, action, rowId, authContext, changed, null, note);
            return;
        }

        foreach (var row in rows)
        {
            var rid = row.TryGetValue(pk, out var pv) && pv != null && IsScalar(pv)
                ? Convert.ToString(pv, CultureInfo.InvariantCulture)
                : rowId;

            if (action == "delete")
            {
                log(table, "delete", rid, authContext, new List<string>(), Redact(table, row), null);
                continue;
            }

            var oldV = new Dictionary<string, object?>(StringComparer.Ordinal);
            var newV = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (col, value) in data)
            {
                row.TryGetValue(col, out var before);
                if (SameValue(before, value)) continue;
                oldV[col] = before;
                newV[col] = value;
            }
            log(table, "update", rid, authContext, changed, Redact(table, oldV), Redact(table, newV));
        }
    }

    private static bool SameValue(object? a, object? b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (IsScalar(a) && IsScalar(b))
            return string.Equals(
                Convert.ToString(a, CultureInfo.InvariantCulture),
                Convert.ToString(b, CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        return Equals(a, b);
    }

    private static readonly System.Text.RegularExpressions.Regex SensitiveColumn =
        new("(password|passwd|secret|token|api_?key|credential)|_hash$", System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Replace values of sensitive columns (passwords, tokens, secrets, API keys, <c>*_hash</c>, plus the
    /// configured exclude list) with a marker; the column NAME stays visible so the audit still shows it changed.
    /// </summary>
    private Dictionary<string, object?> Redact(string table, Dictionary<string, object?> values)
    {
        var result = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        foreach (var col in values.Keys)
        {
            if (SensitiveColumn.IsMatch(col) || _diffExclude.Contains(col) || _diffExclude.Contains(table + "." + col))
                result[col] = "[redacted]";
        }
        return result;
    }

    /// <summary>
    /// Best-effort row-id extraction for audit logging on update/delete. Picks
    /// 'id' if present in the filter; otherwise the first scalar value.
    /// </summary>
    private static string? ExtractRowId(Dictionary<string, object?> filter)
    {
        if (filter.TryGetValue("id", out var idVal) && idVal != null && IsScalar(idVal))
            return idVal.ToString();
        foreach (var v in filter.Values)
        {
            if (v != null && IsScalar(v))
                return v.ToString();
        }
        return null;
    }

    private static bool IsScalar(object value) =>
        value is string or int or long or double or float or decimal or bool or Guid;

    // ---------------------------------------------------------------------
    // Queries
    // ---------------------------------------------------------------------

    /// <summary>
    /// A server-compiled OData <c>$filter</c> keyed by query ref. This is a
    /// TRUSTED channel: it is only ever populated by the OData GET path in
    /// <c>ShapeRouter</c> after <see cref="Http.ODataFilter.Compile"/> has
    /// validated columns and parameterised values. It must never be built from
    /// client request bodies — see <see cref="ReadOptions"/>, which rejects any
    /// client-supplied <c>__</c>-prefixed option key so the filter SQL cannot be
    /// injected through the query <c>options</c> map.
    /// </summary>
    public sealed record PrecompiledFilter(string Sql, Dictionary<string, object?> Parameters);

    public Dictionary<string, object?> Execute(
        List<Dictionary<string, object?>> queries,
        Dictionary<string, object?>? authContext = null,
        Dictionary<string, PrecompiledFilter>? precompiledFilters = null)
    {
        authContext ??= new();

        if (queries.Count > MaxBatchItems)
            throw new ApiException(
                "QUERY_VALIDATION_ERROR",
                $"Batch exceeds the maximum of {MaxBatchItems} queries.",
                400,
                null,
                new Dictionary<string, object?>
                {
                    ["expected"] = $"at most {MaxBatchItems} queries per request",
                    ["hint"] = "Split the work across multiple requests."
                });

        if (queries.Count == 0)
            throw new ApiException(
                "QUERY_VALIDATION_ERROR",
                "Request must include at least one query.",
                400,
                null,
                new Dictionary<string, object?>
                {
                    ["expected"] = "non-empty array of query objects",
                    ["example"] = new List<object?>
                    {
                        new Dictionary<string, object?>
                        {
                            ["ref"] = "q1",
                            ["sql"] = "<mask name>",
                            ["params"] = new Dictionary<string, object?>()
                        }
                    }
                });

        var results = new List<Dictionary<string, object?>>();
        var refMap = new Dictionary<string, ResolvedRef>();
        var seenRefs = new HashSet<string>();

        foreach (var query in queries)
        {
            var @ref = RequiredString(query, "ref");
            if (!seenRefs.Add(@ref))
                throw new ApiException("DUPLICATE_REF", $"Duplicate ref '{@ref}'.", 400, @ref);

            var sqlOrMask = RequiredString(query, "sql");
            var mask = _registry.Resolve(sqlOrMask, authContext, @ref);
            mask = SelectScopeVariant(mask, sqlOrMask, authContext, @ref, out var scopeParams);

            var rawParams = GetDict(query, "params") ?? new();
            var resolvedParams = _resolver.Resolve(rawParams, mask.Params, refMap, @ref);
            foreach (var (scopeParam, scopeValue) in scopeParams)
                resolvedParams[scopeParam] = scopeValue;

            var options = ReadOptions(query, @ref);

            // Optional pre-compiled $filter from the OData GET path. Wrap the
            // mask SQL with `WHERE <filter>` before pagination so push-down still
            // applies. The compiler has already validated columns + parameterised
            // values, so we just splice the SQL fragment and merge params. This
            // comes ONLY from the trusted `precompiledFilters` channel — never
            // from the client-controlled query `options` map (that path was an
            // SQL-injection vector via a client-supplied `__filterSql` key).
            if (precompiledFilters != null && precompiledFilters.TryGetValue(@ref, out var precompiled))
            {
                mask = WrapMaskWithFilter(mask, precompiled.Sql);
                foreach (var (k, v) in precompiled.Parameters)
                    resolvedParams[k] = v;
            }

            // Push LIMIT/OFFSET into the SQL when safe (no orderby/groupby/aggregate).
            // Without this, large mask results OOM via in-memory paging.
            var (pushed, planSql) = PlanPagination(mask, options, @ref);

            int? totalCount = null;
            if (pushed && options.TryGetValue("count", out var cv) && cv is true)
            {
                var countSql = $"SELECT COUNT(*) AS __cnt FROM ({mask.Sql}) AS __c";
                var countResult = _database.Run(countSql, resolvedParams);
                totalCount = countResult.Rows.Count > 0 && countResult.Rows[0].Length > 0
                    ? Convert.ToInt32(countResult.Rows[0][0], CultureInfo.InvariantCulture)
                    : 0;
            }

            var dbResult = _database.Run(planSql, resolvedParams);
            var processed = ApplyOptions(dbResult, options, @ref, pushed);
            if (totalCount.HasValue)
                processed.TotalCount = totalCount.Value;

            var meta = new Dictionary<string, object?>
            {
                ["columns"] = processed.Columns,
                ["ms"] = dbResult.Ms,
                ["rows"] = processed.Count
            };

            var result = new Dictionary<string, object?>
            {
                ["ref"] = @ref,
                ["meta"] = meta,
                ["rows"] = processed.Rows,
                ["count"] = processed.Count
            };

            if (processed.TotalCount.HasValue)
            {
                result["total_count"] = processed.TotalCount.Value;
                meta["total_rows"] = processed.TotalCount.Value;
            }

            results.Add(result);
            refMap[@ref] = new ResolvedRef
            {
                Columns = processed.Columns,
                Rows = processed.Rows
            };
        }

        return new Dictionary<string, object?> { ["results"] = results };
    }

    /// <summary>
    /// Wrap a mask's SQL with <c>WHERE &lt;filter&gt;</c>. Used when the OData
    /// <c>$filter</c> path supplies a pre-compiled filter clause. The wrap
    /// uses an inner alias so column references in the filter resolve against
    /// the mask's projected columns.
    /// </summary>
    // A filtered mask is just a clone with the SQL wrapped — delegate to
    // CloneMask so the full property copy lives in exactly one place.
    private static Mask WrapMaskWithFilter(Mask mask, string filterSql) =>
        CloneMask(mask, $"SELECT * FROM ({mask.Sql}) AS __filt WHERE ({filterSql})");

    /// <summary>
    /// Scope specs applicable to a mask: the tenant spec when the mask carries
    /// <c>tenant_scope</c>, plus one dimension spec per <c>row_scopes</c> entry.
    /// </summary>
    private List<ScopeSpec> SpecsFor(Mask mask)
    {
        if (_scopeSpecs.Count == 0)
            return _scopeSpecs;

        var applicable = new List<ScopeSpec>();
        foreach (var spec in _scopeSpecs)
        {
            if (spec.Kind == ScopeKind.Tenant)
            {
                if (TenancyResolver.TenantColumnFor(mask) != null)
                    applicable.Add(spec);
            }
            else if (mask.RowScopes != null && mask.RowScopes.ContainsKey(spec.Column))
            {
                applicable.Add(spec);
            }
        }
        return applicable;
    }

    /// <summary>The physical column a spec's predicate targets on this mask.</summary>
    private static string ColumnFor(Mask mask, ScopeSpec spec) =>
        spec.Kind == ScopeKind.Tenant
            ? TenancyResolver.TenantColumnFor(mask) ?? spec.Column
            : spec.Column;

    /// <summary>
    /// Pick the SQL variant + reserved-param binds for the caller (ROW-SCOPES-PLAN §2.3).
    /// Fast path: the caller is scoped for every applicable spec → execute the
    /// fully compiled <see cref="Mask.Sql"/> and bind <c>__tenant</c> /
    /// <c>__scope_&lt;column&gt;</c> per spec. Exception path: the caller is
    /// unscoped for ≥1 spec → start from <see cref="Mask.SqlUnscoped"/>
    /// (predicate-free) and wrap it with only the predicates the caller IS
    /// subject to (the sanctioned <see cref="WrapMaskWithFilter"/> pattern).
    /// </summary>
    private Mask SelectScopeVariant(
        Mask mask,
        string maskName,
        Dictionary<string, object?> authContext,
        string? @ref,
        out Dictionary<string, object?> scopeParams)
    {
        scopeParams = new Dictionary<string, object?>();
        var specs = SpecsFor(mask);
        if (specs.Count == 0)
            return mask;

        var bound = new List<(ScopeSpec Spec, object? Value)>();
        var anyUnscoped = false;
        foreach (var spec in specs)
        {
            if (IsUnscopedCaller(spec, authContext))
            {
                anyUnscoped = true;
                continue;
            }

            var value = CallerScopeValue(spec, authContext);
            if (value == null)
                throw MissingScopeClaim(spec, @ref);
            bound.Add((spec, value));
        }

        if (!anyUnscoped)
        {
            // Fast path — every predicate is already compiled into Sql.
            foreach (var (spec, value) in bound)
                scopeParams[spec.ParamName] = value;
            return mask;
        }

        if (string.IsNullOrEmpty(mask.SqlUnscoped))
        {
            var kind = TenancyResolver.TenantColumnFor(mask) != null ? "Tenant-scoped" : "Row-scoped";
            throw new ApiException(
                "CONFIG_INVALID",
                $"{kind} mask '{maskName}' is missing `sql_unscoped` for unscoped roles.",
                500,
                @ref,
                new Dictionary<string, object?>
                {
                    ["hint"] = "Generated scope-aware masks should emit both SQL variants when unscoped roles are configured."
                });
        }

        var variant = CloneMask(mask, mask.SqlUnscoped);
        if (bound.Count == 0)
            return variant;

        // Wrap path: append only the predicates the caller is subject to.
        // Multi form is parenthesis-free — Dapper expands `IN @x`, not `IN (@x)`
        // (see MaskGenerator.ScopeClause).
        var clauses = bound.Select(b => b.Spec.Multi
            ? $"{QuoteName(ColumnFor(mask, b.Spec))} IN @{b.Spec.ParamName}"
            : $"{QuoteName(ColumnFor(mask, b.Spec))} = @{b.Spec.ParamName}");
        variant = WrapMaskWithFilter(variant, string.Join(" AND ", clauses));
        foreach (var (spec, value) in bound)
            scopeParams[spec.ParamName] = value;
        return variant;
    }

    private static Mask CloneMask(Mask mask, string? sql = null) => new()
    {
        Sql = sql ?? mask.Sql,
        Params = mask.Params,
        Roles = mask.Roles,
        Writable = mask.Writable,
        SoftDelete = mask.SoftDelete,
        Description = mask.Description,
        CacheControl = mask.CacheControl,
        Audit = mask.Audit,
        Relations = mask.Relations,
        DefaultLimit = mask.DefaultLimit,
        FkChecks = mask.FkChecks,
        WritableMode = mask.WritableMode,
        TenantScope = mask.TenantScope,
        RowScopes = mask.RowScopes,
        SqlUnscoped = mask.SqlUnscoped,
        Pk = mask.Pk,
        PkStrategy = mask.PkStrategy,
        RolesByAction = mask.RolesByAction,
        WritableColumns = mask.WritableColumns
    };

    /// <summary>
    /// Per-spec write stamping/guarding (ROW-SCOPES-PLAN §2.4). The tenant spec
    /// keeps its original behavior (TENANT_* error details) verbatim; dimension
    /// specs stamp single-valued scopes from the claim, require an in-membership
    /// value for <c>multi</c> scopes, and reject cross-dimension values with
    /// <c>SCOPE_MISMATCH</c> / missing required values with <c>SCOPE_REQUIRED</c>.
    /// </summary>
    private Dictionary<string, object?> ApplyScopeColumns(
        Dictionary<string, object?> data,
        string action,
        Mask mask,
        Dictionary<string, object?> authContext,
        string? @ref,
        out object? tenantValue)
    {
        data = ApplyTenantColumn(data, mask, authContext, @ref, out tenantValue);
        ApplyDimensionColumns(data, action, mask, authContext, @ref);
        return data;
    }

    private void ApplyDimensionColumns(
        Dictionary<string, object?> data,
        string action,
        Mask mask,
        Dictionary<string, object?> authContext,
        string? @ref)
    {
        if (mask.RowScopes is not { Count: > 0 } rowScopes)
            return;

        foreach (var spec in _scopeSpecs)
        {
            if (spec.Kind != ScopeKind.Dimension || !rowScopes.ContainsKey(spec.Column))
                continue;

            var column = spec.Column;
            if (IsUnscopedCaller(spec, authContext))
            {
                // Unscoped for this dimension: any value is allowed, but the
                // framework never guesses — an insert must carry the column.
                if (action == "insert"
                    && (!data.TryGetValue(column, out var unscopedValue) || unscopedValue == null))
                {
                    throw ScopeRequired(spec, @ref, multi: false);
                }
                continue;
            }

            var claim = CallerScopeValue(spec, authContext);
            if (claim == null)
                throw MissingScopeClaim(spec, @ref);

            if (spec.Multi)
            {
                // The framework cannot pick among N memberships: the client
                // must supply the column on insert, and any supplied value must
                // be within the claim set.
                var memberships = claim as IList<object?> ?? new List<object?> { claim };
                if (!data.TryGetValue(column, out var supplied) || supplied == null)
                {
                    if (action == "insert")
                        throw ScopeRequired(spec, @ref, multi: true);
                    continue;
                }

                if (!memberships.Any(m => ScopeValuesMatch(m, supplied)))
                    throw ScopeMismatch(spec, @ref);
            }
            else
            {
                if (data.TryGetValue(column, out var supplied)
                    && supplied != null
                    && !ScopeValuesMatch(supplied, claim))
                {
                    throw ScopeMismatch(spec, @ref);
                }

                data[column] = claim;
            }
        }
    }

    // Wire wording kept verbatim-identical to the PHP reference
    // (ShapeRuntime\Core\BatchExecutor::scopeMismatch / scopeRequired) — the
    // machine-readable contract (code / detail / scope) plus the human message
    // and hint match across both stacks.
    private static ApiException ScopeMismatch(ScopeSpec spec, string? @ref) =>
        new(
            "VALIDATION_ERROR",
            $"Cross-scope write rejected: '{spec.Column}' is outside your {spec.Column} scope.",
            400,
            @ref,
            new Dictionary<string, object?>
            {
                ["detail"] = "SCOPE_MISMATCH",
                ["scope"] = spec.Column,
                ["hint"] = $"The '{spec.Column}' value must match your claim (one of your memberships for multi-valued scopes)."
            });

    private static ApiException ScopeRequired(ScopeSpec spec, string? @ref, bool multi) =>
        new(
            "VALIDATION_ERROR",
            $"Column '{spec.Column}' is required.",
            400,
            @ref,
            new Dictionary<string, object?>
            {
                ["detail"] = "SCOPE_REQUIRED",
                ["scope"] = spec.Column,
                ["hint"] = multi
                    ? $"Supply '{spec.Column}' explicitly — you belong to multiple {spec.Column} values and the framework never guesses among memberships."
                    : $"A caller unscoped for '{spec.Column}' must name the target value explicitly when inserting."
            });

    private Dictionary<string, object?> ApplyTenantColumn(
        Dictionary<string, object?> data,
        Mask mask,
        Dictionary<string, object?> authContext,
        string? @ref,
        out object? tenantValue)
    {
        tenantValue = null;
        if (_tenancy == null || mask.TenantScope is not string tenantColumn || string.IsNullOrWhiteSpace(tenantColumn))
            return data;

        if (IsUnscopedCaller(authContext))
        {
            var inheritedTenant = ResolveTenantId(authContext);
            if (inheritedTenant != null)
            {
                if (data.TryGetValue(tenantColumn, out tenantValue)
                    && tenantValue != null
                    && !TenantValuesMatch(tenantValue, inheritedTenant))
                {
                    throw new ApiException(
                        "VALIDATION_ERROR",
                        "tenant_id does not match the inherited tenant.",
                        400,
                        @ref,
                        new Dictionary<string, object?>
                        {
                            ["detail"] = "TENANT_MISMATCH",
                            ["expected"] = inheritedTenant
                        });
                }

                data[tenantColumn] = inheritedTenant;
                tenantValue = inheritedTenant;
                return data;
            }

            if (!data.TryGetValue(tenantColumn, out tenantValue)
                || tenantValue == null
                || string.IsNullOrWhiteSpace(TenancyResolver.TenantValueAsString(tenantValue)))
            {
                throw new ApiException(
                    "VALIDATION_ERROR",
                    "tenant_id required for unscoped writes.",
                    400,
                    @ref,
                    new Dictionary<string, object?>
                    {
                        ["detail"] = "TENANT_MISSING",
                        ["expected"] = $"`{tenantColumn}` is required when writing through an unscoped role."
                    });
            }

            return data;
        }

        var ctxTenant = ResolveTenantId(authContext);
        if (ctxTenant == null)
        {
            throw MissingTenantClaim(@ref);
        }

        if (data.TryGetValue(tenantColumn, out var supplied)
            && supplied != null
            && !TenantValuesMatch(supplied, ctxTenant))
        {
            throw new ApiException(
                "VALIDATION_ERROR",
                "tenant_id does not match the caller's tenant.",
                400,
                @ref,
                new Dictionary<string, object?>
                {
                    ["detail"] = "TENANT_MISMATCH",
                    ["expected"] = ctxTenant
                });
        }

        data[tenantColumn] = ctxTenant;
        tenantValue = ctxTenant;
        return data;
    }

    private bool IsUnscopedCaller(Dictionary<string, object?>? authContext)
    {
        if (_tenancy == null)
            return false;

        var role = authContext != null && authContext.TryGetValue("role", out var value)
            ? value?.ToString() ?? ""
            : "";
        return TenancyResolver.IsUnscopedRole(_tenancy, role);
    }

    private static bool IsUnscopedCaller(ScopeSpec spec, Dictionary<string, object?>? authContext)
    {
        var role = authContext != null && authContext.TryGetValue("role", out var value)
            ? value?.ToString() ?? ""
            : "";
        return spec.IsUnscopedRole(role);
    }

    /// <summary>
    /// The caller's claim value for one spec. Tenant reads the canonical
    /// <c>tenant_id</c> key with the original semantics; dimensions read the
    /// configured claim key, unwrap <see cref="JsonElement"/> payload values,
    /// and normalize <c>Multi</c> claims to a non-empty list (null when the
    /// claim is missing / empty — the caller must re-login).
    /// </summary>
    private static object? CallerScopeValue(ScopeSpec spec, Dictionary<string, object?>? authContext)
    {
        if (spec.Kind == ScopeKind.Tenant)
            return ResolveTenantId(authContext);

        if (authContext == null || !authContext.TryGetValue(spec.ClaimKey, out var value) || value is null)
            return null;

        if (value is JsonElement je)
            value = ParamResolver.UnwrapJsonElement(je);

        if (spec.Multi)
        {
            var list = value is IList<object?> raw
                ? raw.Select(v => v is JsonElement e ? ParamResolver.UnwrapJsonElement(e) : v).ToList()
                : new List<object?> { value };
            var values = list
                .Where(v => v != null && !(v is string s && string.IsNullOrWhiteSpace(s)))
                .ToList();
            return values.Count == 0 ? null : values;
        }

        if (value is string text && string.IsNullOrWhiteSpace(text))
            return null;
        return value;
    }

    private static bool ScopeValuesMatch(object? left, object? right) => TenantValuesMatch(left, right);

    /// <summary>
    /// A scoped operation reached with no claim for a spec in the auth context —
    /// the token predates the feature. 401 so clients treat it as a re-login signal.
    /// </summary>
    private static ApiException MissingScopeClaim(ScopeSpec spec, string? @ref) =>
        spec.Kind == ScopeKind.Tenant
            ? MissingTenantClaim(@ref)
            : new ApiException(
                "AUTH_INVALID",
                $"Missing {spec.ClaimKey} claim. Re-login required after row scopes were enabled.",
                401,
                @ref,
                new Dictionary<string, object?>
                {
                    ["hint"] = "Re-login required after row scopes were enabled."
                });

    /// <summary>
    /// A scoped operation reached with no tenant in the auth context — the token
    /// predates tenancy. 401 so clients treat it as a re-login signal.
    /// </summary>
    private static ApiException MissingTenantClaim(string? @ref) =>
        new(
            "AUTH_INVALID",
            "Missing tenant_id claim. Re-login required after tenancy was enabled.",
            401,
            @ref,
            new Dictionary<string, object?>
            {
                ["hint"] = "Re-login required after tenancy was enabled."
            });

    private static object? ResolveTenantId(Dictionary<string, object?>? values)
    {
        if (values == null)
            return null;
        if (!values.TryGetValue("tenant_id", out var tenantValue) || tenantValue is null)
            return null;
        if (tenantValue is string s && string.IsNullOrWhiteSpace(s))
            return null;
        return tenantValue;
    }

    private static bool TenantValuesMatch(object? left, object? right)
    {
        var leftText = TenancyResolver.TenantValueAsString(left);
        var rightText = TenancyResolver.TenantValueAsString(right);
        if (leftText == null || rightText == null)
            return false;
        return string.Equals(leftText, rightText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Decide whether top/skip can be pushed into the SQL. Pushing means the DB
    /// returns at most `top` rows — bounded memory regardless of table size.
    /// Skip when groupby/aggregate/orderby is present (those need the full set
    /// or column-validation against the inner SQL we don't do yet).
    /// </summary>
    private (bool Pushed, string Sql) PlanPagination(Mask mask, Dictionary<string, object?> options, string? @ref)
    {
        if (HasOption(options, "orderby") || HasOption(options, "groupby") || HasOption(options, "aggregate"))
            return (false, mask.Sql);

        var skip = ReadNonNegativeIntOption(options, "skip", @ref, 0);

        int? explicitTop = options.ContainsKey("top")
            ? ReadNonNegativeIntOption(options, "top", @ref, MaxTop)
            : null;
        var defaultLimit = mask.DefaultLimit ?? 0;
        var effectiveTop = explicitTop ?? defaultLimit;

        if (effectiveTop > MaxTop)
            throw new ApiException(
                "QUERY_VALIDATION_ERROR",
                $"Option top exceeds allowed maximum ({MaxTop}).",
                400,
                @ref);

        if (effectiveTop <= 0 && skip <= 0)
            return (false, mask.Sql);

        if (_database.Driver == DbDriver.Mssql)
            return (true, BuildMssqlPagination(mask.Sql, skip, effectiveTop));

        var limitClause = effectiveTop > 0 ? $" LIMIT {effectiveTop}" : " LIMIT 9223372036854775807";
        var offsetClause = skip > 0 ? $" OFFSET {skip}" : "";

        var wrapped = $"SELECT * FROM ({mask.Sql}) AS __m{limitClause}{offsetClause}";
        return (true, wrapped);
    }

    /// <summary>
    /// SQL Server has no LIMIT/OFFSET; it pages with OFFSET … ROWS [FETCH NEXT … ROWS ONLY],
    /// which mandates an ORDER BY — and rejects ORDER BY inside a derived table unless
    /// TOP/OFFSET/FOR XML accompanies it. So: a mask with its own top-level ORDER BY gets
    /// OFFSET/FETCH appended directly (its ordering drives the paging — deterministic pages);
    /// a mask without one is wrapped with the standard no-op `ORDER BY (SELECT NULL)`.
    /// FETCH NEXT is omitted when only skip is set (MSSQL allows a bare OFFSET) — which
    /// also avoids the bigint sentinel the LIMIT drivers need.
    /// </summary>
    internal static string BuildMssqlPagination(string maskSql, int skip, int top)
    {
        var fetchClause = top > 0 ? $" FETCH NEXT {top} ROWS ONLY" : "";
        var baseSql = maskSql.TrimEnd().TrimEnd(';').TrimEnd();
        return HasTopLevelOrderBy(baseSql)
            ? $"{baseSql} OFFSET {skip} ROWS{fetchClause}"
            : $"SELECT * FROM ({baseSql}) AS __m ORDER BY (SELECT NULL) OFFSET {skip} ROWS{fetchClause}";
    }

    /// <summary>
    /// True when <paramref name="sql"/> contains an ORDER BY at parenthesis depth 0
    /// (i.e. one that orders the outermost SELECT, not a subquery). Skips string
    /// literals and quoted identifiers so their contents can't false-positive.
    /// </summary>
    internal static bool HasTopLevelOrderBy(string sql)
    {
        var depth = 0;
        for (var i = 0; i < sql.Length; i++)
        {
            switch (sql[i])
            {
                case '\'':
                    i = SkipQuoted(sql, i, '\'');
                    break;
                case '"':
                    i = SkipQuoted(sql, i, '"');
                    break;
                case '`':
                    i = SkipQuoted(sql, i, '`');
                    break;
                case '[':
                    var close = sql.IndexOf(']', i + 1);
                    i = close < 0 ? sql.Length : close;
                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case 'o' or 'O' when depth == 0 && IsWordBoundary(sql, i, 5):
                    if (string.Compare(sql, i, "ORDER", 0, 5, StringComparison.OrdinalIgnoreCase) != 0)
                        break;
                    var j = i + 5;
                    while (j < sql.Length && char.IsWhiteSpace(sql[j])) j++;
                    if (j + 2 <= sql.Length
                        && string.Compare(sql, j, "BY", 0, 2, StringComparison.OrdinalIgnoreCase) == 0
                        && (j + 2 == sql.Length || !char.IsLetterOrDigit(sql[j + 2])))
                        return true;
                    break;
            }
        }
        return false;
    }

    /// <summary>Index of the closing delimiter (doubled delimiters escape), or end of string.</summary>
    private static int SkipQuoted(string sql, int start, char delim)
    {
        for (var i = start + 1; i < sql.Length; i++)
        {
            if (sql[i] != delim) continue;
            if (i + 1 < sql.Length && sql[i + 1] == delim) { i++; continue; }
            return i;
        }
        return sql.Length;
    }

    private static bool IsWordBoundary(string sql, int start, int length) =>
        (start == 0 || (!char.IsLetterOrDigit(sql[start - 1]) && sql[start - 1] != '_'))
        && (start + length >= sql.Length || (!char.IsLetterOrDigit(sql[start + length]) && sql[start + length] != '_'));

    private static bool HasOption(Dictionary<string, object?> options, string key)
    {
        if (!options.TryGetValue(key, out var v) || v == null) return false;
        if (v is IList<object?> list) return list.Count > 0;
        return true;
    }

    private static string RequiredString(Dictionary<string, object?> query, string key)
    {
        var value = GetString(query, key);
        if (string.IsNullOrWhiteSpace(value))
            throw new ApiException("QUERY_VALIDATION_ERROR",
                $"Query field '{key}' must be a non-empty string.", 400);
        return value;
    }

    private static Dictionary<string, object?> ReadOptions(Dictionary<string, object?> query, string @ref)
    {
        if (!query.TryGetValue("options", out var optVal) || optVal == null)
            return new();

        if (optVal is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.Null) return new();
            if (je.ValueKind != JsonValueKind.Object)
                throw new ApiException("QUERY_VALIDATION_ERROR", "Query options must be an object.", 400, @ref);
            var dict = new Dictionary<string, object?>();
            foreach (var prop in je.EnumerateObject())
                dict[prop.Name] = ParamResolver.UnwrapJsonElement(prop.Value);
            optVal = dict;
        }

        if (optVal is not Dictionary<string, object?> options)
            throw new ApiException("QUERY_VALIDATION_ERROR", "Query options must be an object.", 400, @ref);

        var allowed = new HashSet<string> { "orderby", "skip", "top", "count", "groupby", "aggregate" };
        foreach (var key in options.Keys)
        {
            // Reject `__`-prefixed option keys outright. They used to be treated
            // as trusted internal hints (the OData path stashed `__filterSql` /
            // `__filterParams` here), but the query object comes straight from the
            // client request body, so accepting them let a caller splice raw SQL
            // into the mask via `__filterSql`. The compiled filter now travels the
            // out-of-band `precompiledFilters` argument to Execute instead, so any
            // `__` key seen here is client-supplied and must be refused.
            if (key.StartsWith("__", StringComparison.Ordinal))
                throw new ApiException("QUERY_VALIDATION_ERROR",
                    $"Reserved query option '{key}' is not permitted.", 400, @ref);
            if (!allowed.Contains(key))
                throw new ApiException("QUERY_VALIDATION_ERROR",
                    $"Unsupported query option '{key}'.", 400, @ref);
        }

        return options;
    }

    private ProcessedResult ApplyOptions(QueryResult dbResult, Dictionary<string, object?> options, string @ref, bool paginatedInSql)
    {
        if (options.Count == 0)
            return new ProcessedResult
            {
                Columns = dbResult.Columns,
                Rows = dbResult.Rows,
                Count = dbResult.Count
            };

        var columns = dbResult.Columns;
        var rowsAssoc = RowsToAssoc(columns, dbResult.Rows);

        var groupby = ReadStringListOption(options, "groupby", @ref);
        var aggregate = ReadAggregateOption(options, @ref);

        if (aggregate.Count > 0 && groupby.Count == 0)
            throw new ApiException("QUERY_VALIDATION_ERROR",
                "Option aggregate requires non-empty groupby.", 400, @ref);

        if (groupby.Count > 0 || aggregate.Count > 0)
        {
            AssertFieldsExist(columns, groupby, @ref, "groupby");
            foreach (var agg in aggregate)
            {
                if (agg.Fn != "count")
                    AssertFieldsExist(columns, new List<string> { agg.Field }, @ref, "aggregate");
            }
            (rowsAssoc, columns) = ApplyGrouping(rowsAssoc, groupby, aggregate);
        }

        var orderby = ReadOrderByOption(options, @ref);
        if (orderby.Count > 0)
        {
            AssertFieldsExist(columns, orderby.Select(o => o.Field).ToList(), @ref, "orderby");
            ApplyOrderBy(rowsAssoc, orderby);
        }

        var result = new ProcessedResult { Columns = columns };

        if (!paginatedInSql)
        {
            // SQL push-down didn't run, so trim in memory.
            var skip = ReadNonNegativeIntOption(options, "skip", @ref, 0);
            var top = ReadNonNegativeIntOption(options, "top", @ref, MaxTop);
            if (top > MaxTop)
                throw new ApiException("QUERY_VALIDATION_ERROR",
                    "Option top exceeds allowed maximum.", 400, @ref);

            var totalCount = rowsAssoc.Count;
            if (skip > 0)
                rowsAssoc = rowsAssoc.Skip(skip).ToList();
            if (top > 0)
                rowsAssoc = rowsAssoc.Take(top).ToList();

            result.Rows = AssocToRows(columns, rowsAssoc);
            result.Count = result.Rows.Count;

            if (options.TryGetValue("count", out var countVal) && countVal is true)
                result.TotalCount = totalCount;
        }
        else
        {
            // Push-down already trimmed; total_count comes from the separate
            // COUNT(*) query upstream in Execute().
            result.Rows = AssocToRows(columns, rowsAssoc);
            result.Count = result.Rows.Count;
        }

        return result;
    }

    private static List<Dictionary<string, object?>> RowsToAssoc(List<string> columns, List<object?[]> rows)
    {
        return rows.Select(row =>
        {
            var assoc = new Dictionary<string, object?>();
            for (int i = 0; i < columns.Count; i++)
                assoc[columns[i]] = i < row.Length ? row[i] : null;
            return assoc;
        }).ToList();
    }

    private static List<object?[]> AssocToRows(List<string> columns, List<Dictionary<string, object?>> rows)
    {
        return rows.Select(row =>
            columns.Select(col => row.GetValueOrDefault(col)).ToArray()
        ).ToList();
    }

    private static List<string> ReadStringListOption(Dictionary<string, object?> options, string key, string @ref)
    {
        if (!options.TryGetValue(key, out var value) || value == null)
            return new();

        if (value is not IList<object?> list)
            throw new ApiException("QUERY_VALIDATION_ERROR", $"Option {key} must be an array.", 400, @ref);

        var result = new List<string>();
        foreach (var item in list)
        {
            if (item is not string s || string.IsNullOrWhiteSpace(s))
                throw new ApiException("QUERY_VALIDATION_ERROR",
                    $"Option {key} values must be non-empty strings.", 400, @ref);
            result.Add(s);
        }
        return result;
    }

    private static List<OrderByItem> ReadOrderByOption(Dictionary<string, object?> options, string @ref)
    {
        if (!options.TryGetValue("orderby", out var value) || value == null)
            return new();

        if (value is not IList<object?> list)
            throw new ApiException("QUERY_VALIDATION_ERROR", "Option orderby must be an array.", 400, @ref);

        var result = new List<OrderByItem>();
        foreach (var item in list)
        {
            if (item is not Dictionary<string, object?> dict)
                throw new ApiException("QUERY_VALIDATION_ERROR",
                    "Each orderby item must be an object.", 400, @ref);

            var field = GetString(dict, "field")?.Trim() ?? "";
            var dir = (GetString(dict, "dir") ?? "asc").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(field) || dir is not ("asc" or "desc"))
                throw new ApiException("QUERY_VALIDATION_ERROR", "Invalid orderby item.", 400, @ref);
            result.Add(new OrderByItem { Field = field, Dir = dir });
        }
        return result;
    }

    private static List<AggregateItem> ReadAggregateOption(Dictionary<string, object?> options, string @ref)
    {
        if (!options.TryGetValue("aggregate", out var value) || value == null)
            return new();

        if (value is not IList<object?> list)
            throw new ApiException("QUERY_VALIDATION_ERROR", "Option aggregate must be an array.", 400, @ref);

        var result = new List<AggregateItem>();
        var seenAliases = new HashSet<string>();

        foreach (var item in list)
        {
            if (item is not Dictionary<string, object?> dict)
                throw new ApiException("QUERY_VALIDATION_ERROR",
                    "Each aggregate item must be an object.", 400, @ref);

            var field = GetString(dict, "field")?.Trim() ?? "";
            var fn = (GetString(dict, "fn") ?? "").Trim().ToLowerInvariant();
            var asAlias = GetString(dict, "as")?.Trim() ?? "";

            if (fn is not ("count" or "sum" or "avg" or "min" or "max") || string.IsNullOrEmpty(asAlias))
                throw new ApiException("QUERY_VALIDATION_ERROR", "Invalid aggregate item.", 400, @ref);
            if (fn != "count" && string.IsNullOrEmpty(field))
                throw new ApiException("QUERY_VALIDATION_ERROR", "Aggregate field is required.", 400, @ref);
            if (!seenAliases.Add(asAlias))
                throw new ApiException("QUERY_VALIDATION_ERROR", "Aggregate alias must be unique.", 400, @ref);

            result.Add(new AggregateItem { Field = field, Fn = fn, As = asAlias });
        }
        return result;
    }

    private static int ReadNonNegativeIntOption(Dictionary<string, object?> options, string key, string? @ref, int defaultVal)
    {
        if (!options.TryGetValue(key, out var value))
            return defaultVal;
        if (value is long l) return l < 0
            ? throw new ApiException("QUERY_VALIDATION_ERROR", $"Option {key} must be >= 0.", 400, @ref)
            : (int)l;
        if (value is int i) return i < 0
            ? throw new ApiException("QUERY_VALIDATION_ERROR", $"Option {key} must be >= 0.", 400, @ref)
            : i;
        throw new ApiException("QUERY_VALIDATION_ERROR", $"Option {key} must be an integer.", 400, @ref);
    }

    private static void AssertFieldsExist(List<string> available, List<string> requested, string @ref, string option)
    {
        var set = new HashSet<string>(available);
        foreach (var field in requested)
        {
            if (!set.Contains(field))
                throw new ApiException("QUERY_VALIDATION_ERROR",
                    $"Option {option} uses unknown field '{field}'.", 400, @ref);
        }
    }

    private static (List<Dictionary<string, object?>> rows, List<string> columns) ApplyGrouping(
        List<Dictionary<string, object?>> rows, List<string> groupby, List<AggregateItem> aggregate)
    {
        var groups = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var row in rows)
        {
            var keyValues = groupby.Select(f => row.GetValueOrDefault(f)).ToArray();
            var groupKey = JsonSerializer.Serialize(keyValues);
            if (!groups.ContainsKey(groupKey))
                groups[groupKey] = new();
            groups[groupKey].Add(row);
        }

        var outRows = new List<Dictionary<string, object?>>();
        foreach (var groupRows in groups.Values)
        {
            var first = groupRows[0];
            var line = new Dictionary<string, object?>();
            foreach (var field in groupby)
                line[field] = first.GetValueOrDefault(field);
            foreach (var agg in aggregate)
                line[agg.As] = ComputeAggregate(groupRows, agg.Fn, agg.Field);
            outRows.Add(line);
        }

        var newColumns = new List<string>(groupby);
        newColumns.AddRange(aggregate.Select(a => a.As));

        return (outRows, newColumns);
    }

    private static object? ComputeAggregate(List<Dictionary<string, object?>> rows, string fn, string field)
    {
        if (fn == "count") return rows.Count;

        var values = rows.Select(r => r.GetValueOrDefault(field)).ToList();

        switch (fn)
        {
            case "sum":
                double sum = 0;
                foreach (var v in values)
                    if (v != null && double.TryParse(v.ToString(), CultureInfo.InvariantCulture, out var d))
                        sum += d;
                return sum;

            case "avg":
                double avgSum = 0;
                int n = 0;
                foreach (var v in values)
                    if (v != null && double.TryParse(v.ToString(), CultureInfo.InvariantCulture, out var d))
                    { avgSum += d; n++; }
                return n > 0 ? avgSum / n : null;

            case "min":
                var minFiltered = values.Where(v => v != null).ToList();
                if (minFiltered.Count == 0) return null;
                minFiltered.Sort((a, b) => Comparer<object>.Default.Compare(a, b));
                return minFiltered[0];

            case "max":
                var maxFiltered = values.Where(v => v != null).ToList();
                if (maxFiltered.Count == 0) return null;
                maxFiltered.Sort((a, b) => Comparer<object>.Default.Compare(b, a));
                return maxFiltered[0];

            default:
                return null;
        }
    }

    private static void ApplyOrderBy(List<Dictionary<string, object?>> rows, List<OrderByItem> orderby)
    {
        rows.Sort((a, b) =>
        {
            foreach (var order in orderby)
            {
                var dir = order.Dir == "desc" ? -1 : 1;
                var av = a.GetValueOrDefault(order.Field);
                var bv = b.GetValueOrDefault(order.Field);

                if (Equals(av, bv)) continue;
                if (av == null) return -1 * dir;
                if (bv == null) return 1 * dir;

                var cmp = Comparer<object>.Default.Compare(av, bv);
                if (cmp != 0) return cmp * dir;
            }
            return 0;
        });
    }

    private static string? GetString(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val)) return null;
        if (val is string s) return s;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.String)
            return je.GetString();
        return val?.ToString();
    }

    private static Dictionary<string, object?>? GetDict(Dictionary<string, object?> dict, string key)
    {
        if (!dict.TryGetValue(key, out var val) || val == null) return null;
        if (val is Dictionary<string, object?> d) return d;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            var result = new Dictionary<string, object?>();
            foreach (var prop in je.EnumerateObject())
                result[prop.Name] = ParamResolver.UnwrapJsonElement(prop.Value);
            return result;
        }
        return null;
    }
}

internal sealed class OrderByItem
{
    public string Field { get; set; } = "";
    public string Dir { get; set; } = "asc";
}

internal sealed class AggregateItem
{
    public string Field { get; set; } = "";
    public string Fn { get; set; } = "";
    public string As { get; set; } = "";
}

internal sealed class ProcessedResult
{
    public List<string> Columns { get; set; } = new();
    public List<object?[]> Rows { get; set; } = new();
    public int Count { get; set; }
    public int? TotalCount { get; set; }
}
