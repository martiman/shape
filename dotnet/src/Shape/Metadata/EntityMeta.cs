namespace Shape.Metadata;

public sealed class EntityMeta
{
    public required Type ClrType { get; init; }
    public required string Table { get; init; }
    public bool Writable { get; init; }
    public List<FieldMeta> Fields { get; init; } = new();
    public List<string> ReadRoles { get; init; } = new();
    public List<string> WriteRoles { get; init; } = new();

    /// <summary>
    /// Per-operation write roles (keys <c>create</c> / <c>update</c> /
    /// <c>delete</c>). Non-empty only when at least one action was configured
    /// explicitly on <c>[Access]</c> — then all three keys are present, each
    /// resolved via the action → Write → Roles cascade (an empty list means
    /// "nobody"). The writable mask carries this as <c>roles_by_action</c>;
    /// its mask-level <c>roles</c> gate widens to the union.
    /// </summary>
    public Dictionary<string, List<string>> WriteRolesByAction { get; init; } = new();

    /// <summary>
    /// Prose description of the entity's purpose. From
    /// <c>[Entity(Description=…)]</c> in entity-first mode or from
    /// <c>pg_description</c>/<c>TABLE_COMMENT</c> in DB-first mode.
    /// Surfaces in the manifest and as OpenAPI tag descriptions.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Mutation policy: <c>"full"</c> | <c>"append_only"</c> | <c>"no_delete"</c> | <c>"none"</c>.
    /// When unset, defaults to <c>"full"</c> if <see cref="Writable"/>=true,
    /// <c>"none"</c> if false. <c>BatchExecutor</c> rejects update/delete on
    /// <c>append_only</c>; rejects delete on <c>no_delete</c>.
    /// <see cref="Generator.MaskGenerator"/> emits a writable mask whose role/audit/relations
    /// metadata reflects the actual capabilities — agents reading the manifest
    /// see the honest contract.
    /// </summary>
    public string? WritableMode { get; init; }

    /// <summary>
    /// Resolved PK assignment strategy: <c>"manual"</c> (framework allocates
    /// <c>MAX(pk)+1</c> on insert) or <c>"db"</c> (identity / trigger / sequence
    /// assigns). Settable (unlike the introspected properties) because it is
    /// resolved after introspection, where <see cref="Core.ShapeConfig.PkAssignment"/>
    /// and the schema meet (<see cref="PkStrategyResolver"/>). <c>[GeneratedId]</c>
    /// pre-seeds <c>"manual"</c> in entity-first mode. Null = not resolved yet.
    /// </summary>
    public string? PkStrategy { get; set; }

    /// <summary>
    /// Unique constraints — each inner list is one constraint's column set
    /// (single-column or composite). From <c>[Unique(...)]</c> attributes
    /// in entity-first mode or from
    /// <c>INFORMATION_SCHEMA.TABLE_CONSTRAINTS</c> in DB-first mode.
    /// Surfaces in the manifest as <c>uniqueConstraints</c>.
    /// </summary>
    public List<List<string>> UniqueConstraints { get; init; } = new();

    /// <summary>
    /// Name of the timestamp column flagging soft-deleted rows (typically
    /// <c>deleted_at</c>). When non-null, generated read masks add
    /// <c>WHERE &lt;col&gt; IS NULL</c>, and DELETE mutations on the writable
    /// mask are translated to <c>UPDATE … SET &lt;col&gt; = CURRENT_TIMESTAMP</c>.
    /// </summary>
    public string? SoftDeleteColumn { get; init; }

    /// <summary>
    /// Map of audit column name → kind (<c>"timestamp"</c> | <c>"user_id"</c>).
    /// Detected by name+type convention; BatchExecutor stamps these on insert/
    /// update when the dev's payload doesn't supply its own value.
    /// </summary>
    public Dictionary<string, string> AuditColumns { get; init; } = new();

    /// <summary>Name of the tenancy discriminator column when the table is tenant-scoped.</summary>
    public string? TenantColumn { get; set; }

    /// <summary>
    /// Row-scope dimension columns present on this table (ROW-SCOPES-PLAN §2.1:
    /// scoped iff the column is present — no strict sweep). Populated by
    /// <see cref="TenancyResolver.Apply"/> in config declaration order.
    /// </summary>
    public List<string> ScopeColumns { get; set; } = new();

    /// <summary>
    /// Lifecycle hooks declared on the entity class via <c>[BeforeInsert]</c>
    /// / <c>[AfterUpdate]</c> / etc. attributes. Keyed by event name
    /// (<c>"before-insert"</c>, <c>"after-update"</c>, …); each list element
    /// is a delegate the dispatcher invokes with the standard hook context.
    /// </summary>
    public Dictionary<string, List<Action<Dictionary<string, object?>>>> Hooks { get; init; } = new();

    public FieldMeta? PrimaryKey() => Fields.FirstOrDefault(field => field.IsPrimary);

    /// <summary>
    /// Resolved mutation policy. Honors the explicit <see cref="WritableMode"/>
    /// when set; otherwise translates the legacy <see cref="Writable"/> bool
    /// (<c>true</c> → <c>"full"</c>, <c>false</c> → <c>"none"</c>).
    /// </summary>
    public string EffectiveWritableMode()
    {
        if (!string.IsNullOrEmpty(WritableMode)) return WritableMode!;
        return Writable ? "full" : "none";
    }

    public List<FieldMeta> FkFields() => Fields.Where(field => field.IsFk).ToList();

    /// <summary>True when the entity has a tenant discriminator column.</summary>
    public bool TenantScoped => !string.IsNullOrEmpty(TenantColumn);

    /// <summary>
    /// Detect a soft-delete marker column. Convention: a nullable timestamp
    /// column literally named <c>deleted_at</c>. Returns the column name, or
    /// <c>null</c> if the table is not soft-deletable.
    /// </summary>
    public static string? DetectSoftDeleteColumn(IEnumerable<FieldMeta> fields)
    {
        foreach (var field in fields)
        {
            if (field.Name != "deleted_at") continue;
            if (!field.Nullable) continue;
            var type = (field.DbType ?? "").ToLowerInvariant();
            if (type == "timestamp" || type == "datetime")
                return "deleted_at";
        }
        return null;
    }

    /// <summary>
    /// Detect audit columns by name + type convention.
    /// <list type="bullet">
    ///   <item><c>created_at</c>, <c>updated_at</c> (timestamp/datetime) → <c>"timestamp"</c></item>
    ///   <item><c>created_by</c>, <c>updated_by</c> (int/bigint/varchar/char) → <c>"user_id"</c></item>
    /// </list>
    /// Returns an empty map when no audit columns are present.
    /// </summary>
    public static Dictionary<string, string> DetectAuditColumns(IEnumerable<FieldMeta> fields)
    {
        var audit = new Dictionary<string, string>();
        foreach (var field in fields)
        {
            var name = field.Name;
            var type = (field.DbType ?? "").ToLowerInvariant();

            if ((name == "created_at" || name == "updated_at")
                && (type == "timestamp" || type == "datetime"))
            {
                audit[name] = "timestamp";
                continue;
            }

            if ((name == "created_by" || name == "updated_by")
                && (type == "int" || type == "integer" || type == "bigint" || type == "smallint"
                    || type == "varchar" || type == "char"))
            {
                audit[name] = "user_id";
            }
        }
        return audit;
    }

    /// <summary>
    /// Detect the tenant discriminator column by convention. The default name is
    /// <c>tenant_id</c>; callers can pass an alternate configured column name.
    /// </summary>
    public static string? DetectTenantColumn(IEnumerable<FieldMeta> fields, string columnName = "tenant_id")
    {
        foreach (var field in fields)
        {
            if (field.Name == columnName)
                return columnName;
        }
        return null;
    }
}
