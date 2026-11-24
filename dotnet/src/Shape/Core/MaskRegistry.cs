namespace Shape.Core;

public sealed class MaskRegistry
{
    private readonly Dictionary<string, Mask> _byName = new();
    private readonly Dictionary<string, Mask> _bySql = new();

    public MaskRegistry(Dictionary<string, MaskConfig> masks)
    {
        foreach (var (name, mask) in masks)
        {
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (string.IsNullOrWhiteSpace(mask.Sql))
                continue;

            var roles = mask.Roles?
                .Where(r => !string.IsNullOrEmpty(r))
                .ToList() ?? new List<string>();

            var rewrittenSql = RewriteColonParams(mask.Sql);

            var normalized = new Mask
            {
                Sql = rewrittenSql,
                Params = mask.Params ?? new Dictionary<string, ParamSchema>(),
                Roles = roles,
                Writable = mask.Writable,
                SoftDelete = string.IsNullOrEmpty(mask.SoftDelete) ? null : mask.SoftDelete,
                Description = string.IsNullOrEmpty(mask.Description) ? null : mask.Description,
                CacheControl = string.IsNullOrEmpty(mask.CacheControl) ? null : mask.CacheControl,
                Audit = mask.Audit is { Count: > 0 } ? new Dictionary<string, string>(mask.Audit) : null,
                Relations = mask.Relations is { Count: > 0 } ? new Dictionary<string, RelationMeta>(mask.Relations) : null,
                DefaultLimit = mask.DefaultLimit > 0 ? mask.DefaultLimit : null,
                FkChecks = mask.FkChecks is { Count: > 0 } ? new Dictionary<string, FkCheck>(mask.FkChecks) : null,
                WritableMode = string.IsNullOrEmpty(mask.WritableMode) ? null : mask.WritableMode,
                TenantScope = mask.TenantScope,
                RowScopes = NormalizeRowScopes(mask.RowScopes),
                SqlUnscoped = string.IsNullOrEmpty(mask.SqlUnscoped) ? null : mask.SqlUnscoped,
                Pk = string.IsNullOrEmpty(mask.Pk) ? null : mask.Pk,
                PkStrategy = string.IsNullOrEmpty(mask.PkStrategy) ? null : mask.PkStrategy,
                RolesByAction = mask.RolesByAction is { Count: > 0 }
                    ? mask.RolesByAction.ToDictionary(kv => kv.Key, kv => new List<string>(kv.Value), StringComparer.Ordinal)
                    : null,
                WritableColumns = mask.WritableColumns != null
                    ? new List<string>(mask.WritableColumns)
                    : null
            };

            _byName[name] = normalized;
            _bySql[rewrittenSql] = normalized;
            // Keep the original SQL aliased too — manual masks are sometimes
            // resolved by the literal SQL string (legacy path), and rewriting
            // would otherwise break those callers.
            if (!ReferenceEquals(rewrittenSql, mask.Sql))
                _bySql[mask.Sql] = normalized;
        }
    }

    /// <summary>
    /// Normalize the config-level <c>row_scopes</c> shapes into the runtime
    /// dictionary: the carrier dict copies through; a declared column list maps
    /// to default metadata (the executor's per-spec <c>multi</c> flag comes from
    /// config, so the default here only affects manifest output of raw-runtime
    /// setups); <c>false</c> (opt-out) and null both mean "no dimensions".
    /// </summary>
    private static Dictionary<string, RowScopeMeta>? NormalizeRowScopes(object? rowScopes) => rowScopes switch
    {
        Dictionary<string, RowScopeMeta> { Count: > 0 } map =>
            map.ToDictionary(kv => kv.Key, kv => new RowScopeMeta { Multi = kv.Value.Multi }, StringComparer.Ordinal),
        List<string> { Count: > 0 } columns =>
            columns.Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.Ordinal)
                .ToDictionary(c => c, _ => new RowScopeMeta(), StringComparer.Ordinal),
        _ => null
    };

    /// <summary>
    /// Rewrite PDO-style <c>:identifier</c> placeholders to Dapper-style
    /// <c>@identifier</c> so masks can be copy-pasted from PHP <c>papi-lib</c>
    /// configs without manual edits. Carefully skips:
    /// <list type="bullet">
    ///   <item><c>::cast</c> — Postgres double-colon casts (e.g. <c>value::int</c>)</item>
    ///   <item>Single-quoted string literals (with <c>''</c> escape)</item>
    ///   <item>Double-quoted identifiers (Postgres/SQLite)</item>
    ///   <item>Backtick-quoted identifiers (MySQL)</item>
    ///   <item><c>--</c> line comments and <c>/* */</c> block comments</item>
    /// </list>
    /// SQL with no <c>:</c> characters short-circuits to the original string.
    /// </summary>
    internal static string RewriteColonParams(string sql)
    {
        if (string.IsNullOrEmpty(sql) || sql.IndexOf(':') < 0)
            return sql;

        var sb = new System.Text.StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];

            // Single-quoted string — copy verbatim, honor '' escape.
            if (c == '\'')
            {
                sb.Append(c);
                i++;
                while (i < sql.Length)
                {
                    sb.Append(sql[i]);
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '\'') { sb.Append(sql[++i]); i++; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            // Double-quoted identifier — copy verbatim, honor "" escape.
            if (c == '"')
            {
                sb.Append(c);
                i++;
                while (i < sql.Length)
                {
                    sb.Append(sql[i]);
                    if (sql[i] == '"')
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == '"') { sb.Append(sql[++i]); i++; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            // Backtick-quoted identifier (MySQL).
            if (c == '`')
            {
                sb.Append(c);
                i++;
                while (i < sql.Length)
                {
                    sb.Append(sql[i]);
                    if (sql[i++] == '`') break;
                }
                continue;
            }

            // -- line comment
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                while (i < sql.Length && sql[i] != '\n') sb.Append(sql[i++]);
                continue;
            }

            // /* block comment */
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                sb.Append(sql[i++]);
                sb.Append(sql[i++]);
                while (i < sql.Length)
                {
                    if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
                    {
                        sb.Append(sql[i++]);
                        sb.Append(sql[i++]);
                        break;
                    }
                    sb.Append(sql[i++]);
                }
                continue;
            }

            // :: Postgres cast — leave as-is.
            if (c == ':' && i + 1 < sql.Length && sql[i + 1] == ':')
            {
                sb.Append(':'); sb.Append(':');
                i += 2;
                continue;
            }

            // :identifier — rewrite to @identifier.
            if (c == ':' && i + 1 < sql.Length && (char.IsLetter(sql[i + 1]) || sql[i + 1] == '_'))
            {
                // Ensure the previous token isn't an identifier char — guards
                // against rewriting things like `time:00` if such a literal
                // ever reached an unquoted spot (defensive; SQL grammar shouldn't allow it).
                var prev = sb.Length > 0 ? sb[sb.Length - 1] : ' ';
                if (char.IsLetterOrDigit(prev) || prev == '_')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }
                sb.Append('@');
                i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_'))
                    sb.Append(sql[i++]);
                continue;
            }

            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    public Mask Resolve(string sqlOrMask, Dictionary<string, object?>? authContext = null, string? @ref = null)
    {
        authContext ??= new();

        if (!_byName.TryGetValue(sqlOrMask, out var mask) &&
            !_bySql.TryGetValue(sqlOrMask, out mask))
        {
            throw new ApiException(
                "MASK_NOT_ALLOWED",
                $"Query '{sqlOrMask}' is not allowed by masks.",
                400,
                @ref,
                new Dictionary<string, object?>
                {
                    ["hint"] = "Fetch GET /api?manifest (or POST {op:'manifest'}) to see available masks. Use the manifest's mask names verbatim."
                });
        }

        if (mask.Roles.Count > 0)
        {
            var role = authContext.TryGetValue("role", out var r) ? r?.ToString() ?? "" : "";
            if (string.IsNullOrEmpty(role) || !mask.Roles.Contains(role))
            {
                var allowed = string.Join(", ", mask.Roles);
                throw new ApiException(
                    "AUTH_FORBIDDEN",
                    "Current role is not allowed for this query.",
                    403,
                    @ref,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = new Dictionary<string, object?> { ["role"] = $"one of: {allowed}" },
                        ["hint"] = role.Length == 0
                            ? "Authenticate first via POST {op:'auth.token'} and pass the access token as Authorization: Bearer <token>."
                            : $"Your role '{role}' cannot access this mask. Need one of: {allowed}."
                    });
            }
        }

        return mask;
    }

    internal Mask? TryGet(string name)
        => _byName.TryGetValue(name, out var mask) ? mask : null;

    internal Mask? FindByTable(string table)
    {
        if (_byName.TryGetValue(table, out var exact))
            return exact;

        if (_byName.TryGetValue($"{table}.list", out var list))
            return list;

        if (_byName.TryGetValue($"{table}.get", out var get))
            return get;

        if (_byName.TryGetValue($"{table}.adminList", out var adminList))
            return adminList;

        if (_byName.TryGetValue($"{table}.adminGet", out var adminGet))
            return adminGet;

        return _byName
            .Where(pair => pair.Key.StartsWith(table + ".", StringComparison.Ordinal)
                && pair.Value.TenantScope != null)
            .Select(pair => pair.Value)
            .FirstOrDefault();
    }
}

public sealed class MaskConfig
{
    public string Sql { get; set; } = "";
    public Dictionary<string, ParamSchema>? Params { get; set; }
    public List<string>? Roles { get; set; }
    public bool Writable { get; set; }

    /// <summary>Column name to write to instead of physically deleting (e.g. "deleted_at"). DELETE becomes UPDATE.</summary>
    public string? SoftDelete { get; set; }

    /// <summary>Human-readable description shown in manifest / OpenAPI.</summary>
    public string? Description { get; set; }

    /// <summary>HTTP <c>Cache-Control</c> header value emitted on this mask's GET responses.</summary>
    public string? CacheControl { get; set; }

    /// <summary>Map of column name → audit kind (<c>"timestamp"</c> | <c>"user_id"</c>) auto-stamped on insert/update.</summary>
    public Dictionary<string, string>? Audit { get; set; }

    /// <summary>FK relations available for nested-create from this mask. Keyed by child table name.</summary>
    public Dictionary<string, RelationMeta>? Relations { get; set; }

    /// <summary>If &gt; 0, applied as <c>LIMIT</c> when no <c>top</c> option is specified.</summary>
    public int DefaultLimit { get; set; }

    /// <summary>FK existence checks per column. See <see cref="Mask.FkChecks"/>.</summary>
    public Dictionary<string, FkCheck>? FkChecks { get; set; }

    /// <summary>
    /// Mutation policy for writable masks: <c>"full"</c> | <c>"append_only"</c> | <c>"no_delete"</c>.
    /// When set, <c>BatchExecutor</c> rejects forbidden actions
    /// (<c>WRITABLE_MODE_FORBIDDEN</c>) instead of silently allowing them.
    /// Null on read-only masks; defaults to <c>"full"</c> on writable masks
    /// when not set.
    /// </summary>
    public string? WritableMode { get; set; }

    /// <summary>
    /// Tenant scoping contract. String = discriminator column name. <c>false</c> =
    /// explicit opt-out. Null = unscoped / not declared.
    /// </summary>
    public object? TenantScope { get; set; }

    /// <summary>
    /// Row-scope contract (manifest key <c>row_scopes</c>).
    /// <c>Dictionary&lt;string, RowScopeMeta&gt;</c> = the compiled carrier
    /// (generated masks / normalized manual declarations);
    /// <c>List&lt;string&gt;</c> = a manual declaration of scope columns
    /// (normalized at boot); <c>false</c> = explicit opt-out; null = undeclared.
    /// </summary>
    public object? RowScopes { get; set; }

    /// <summary>Unscoped SQL variant for tenant-aware read masks.</summary>
    public string? SqlUnscoped { get; set; }

    /// <summary>
    /// PK column name, emitted on every generated writable mask (manifest key <c>pk</c>).
    /// Threads the real PK into <c>InsertReturningId</c> so Postgres <c>RETURNING</c> /
    /// MSSQL id plumbing stops assuming the PK is named <c>id</c>. Null = assume <c>id</c>.
    /// </summary>
    public string? Pk { get; set; }

    /// <summary>
    /// <c>"manual"</c> when the framework allocates <c>MAX(pk)+1</c> on insert (legacy
    /// non-identity int PK — manifest key <c>pk_strategy</c>); absent/null = DB-assigned.
    /// </summary>
    public string? PkStrategy { get; set; }

    /// <summary>
    /// Per-operation write roles (manifest key <c>roles_by_action</c>; keys
    /// <c>create</c> / <c>update</c> / <c>delete</c>). When present,
    /// <see cref="Roles"/> is the union gate and <c>BatchExecutor</c> narrows
    /// per mutation action — an empty list for an action means "nobody".
    /// </summary>
    public Dictionary<string, List<string>>? RolesByAction { get; set; }

    /// <summary>
    /// Allowlist of columns a client may set in an insert/update <c>data</c>
    /// block (manifest key <c>writable_columns</c>). When non-null,
    /// <c>BatchExecutor</c> rejects any client-supplied column not on the list
    /// with <c>WRITE_COLUMN_FORBIDDEN</c> — the field-level write gate that
    /// stops mass-assignment / privilege-escalation (SECURITY-REVIEW H1).
    /// Excludes audit columns, auto-increment PKs, and sensitive/privilege
    /// columns; includes the tenant + row-scope columns (their values are
    /// validated/stamped separately). Null = legacy unrestricted so hand-written
    /// masks keep working.
    /// </summary>
    public List<string>? WritableColumns { get; set; }
}

public sealed class Mask
{
    public string Sql { get; set; } = "";
    public Dictionary<string, ParamSchema> Params { get; set; } = new();
    public List<string> Roles { get; set; } = new();
    public bool Writable { get; set; }
    public string? SoftDelete { get; set; }
    public string? Description { get; set; }
    public string? CacheControl { get; set; }
    public Dictionary<string, string>? Audit { get; set; }
    public Dictionary<string, RelationMeta>? Relations { get; set; }
    public int? DefaultLimit { get; set; }

    /// <summary>
    /// FK existence checks per column. Keyed by the local column name; value
    /// is the referenced <c>(table, pkColumn)</c>. <see cref="BatchExecutor"/>
    /// runs <c>SELECT 1 FROM &lt;table&gt; WHERE &lt;pk&gt; = @value LIMIT 1</c>
    /// before insert/update and returns a typed <c>FK_VIOLATION</c> error if no
    /// row exists, instead of relying on a generic SQL constraint failure.
    /// </summary>
    public Dictionary<string, FkCheck>? FkChecks { get; set; }

    /// <summary>Mutation policy: <c>"full"</c> | <c>"append_only"</c> | <c>"no_delete"</c>. Null = unrestricted (legacy).</summary>
    public string? WritableMode { get; set; }

    /// <summary>Tenant scoping contract. String = discriminator column name. <c>false</c> = explicit opt-out.</summary>
    public object? TenantScope { get; set; }

    /// <summary>
    /// Row-scope dimensions this mask is subject to, keyed by discriminator
    /// column. Drives the per-dimension predicate binds, write stamping, and
    /// WHERE guards in <see cref="BatchExecutor"/>. Null = no dimensions.
    /// </summary>
    public Dictionary<string, RowScopeMeta>? RowScopes { get; set; }

    /// <summary>Unscoped SQL variant emitted for tenant-aware read masks.</summary>
    public string? SqlUnscoped { get; set; }

    /// <summary>PK column name (see <see cref="MaskConfig.Pk"/>). Null = assume <c>id</c>.</summary>
    public string? Pk { get; set; }

    /// <summary>PK assignment strategy (see <see cref="MaskConfig.PkStrategy"/>): <c>"manual"</c> or null.</summary>
    public string? PkStrategy { get; set; }

    /// <summary>Per-operation write roles (see <see cref="MaskConfig.RolesByAction"/>).</summary>
    public Dictionary<string, List<string>>? RolesByAction { get; set; }

    /// <summary>Writable-column allowlist (see <see cref="MaskConfig.WritableColumns"/>). Null = unrestricted.</summary>
    public List<string>? WritableColumns { get; set; }
}

/// <summary>
/// Per-dimension mask metadata (manifest key <c>row_scopes</c>, cross-runtime:
/// <c>{"department_id": {"multi": false}}</c>).
/// </summary>
public sealed class RowScopeMeta
{
    /// <summary>True when the caller's claim is a list and the compiled predicate is <c>IN</c>.</summary>
    public bool Multi { get; set; }
}

/// <summary>FK target pointer used by <see cref="Mask.FkChecks"/>.</summary>
public sealed class FkCheck
{
    public required string Table { get; init; }
    public required string PrimaryKey { get; init; }
}

/// <summary>
/// FK relationship metadata used for nested-create. <see cref="FkColumns"/> lists every FK
/// column on the child table that points at the parent (multiple when the child has more
/// than one FK to the same parent — disambiguated via <c>"child_table.fk_column"</c> keys).
/// </summary>
public sealed class RelationMeta
{
    public List<string> FkColumns { get; set; } = new();
}
