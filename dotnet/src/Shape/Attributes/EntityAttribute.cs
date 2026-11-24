namespace Shape.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class EntityAttribute : Attribute
{
    public EntityAttribute(string table)
    {
        Table = table;
    }

    public string Table { get; }
    public bool Writable { get; set; } = true;

    /// <summary>
    /// Human / agent-readable description of the entity. Surfaces in the
    /// manifest, in OpenAPI tag descriptions, and in the LLM tool list so
    /// agents picking between similar tables get prose disambiguation.
    /// In <c>FromDatabase</c> mode this is auto-populated from
    /// <c>pg_description</c> (Postgres) or <c>INFORMATION_SCHEMA.TABLES.TABLE_COMMENT</c>
    /// (MySQL) when not set explicitly.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Mutation policy for the auto-generated writable mask:
    /// <list type="bullet">
    ///   <item><c>"full"</c> (default when <c>Writable=true</c>) — INSERT, UPDATE, DELETE.</item>
    ///   <item><c>"append_only"</c> — INSERT only. UPDATE / DELETE are rejected at the executor (audit / event / log tables).</item>
    ///   <item><c>"no_delete"</c> — INSERT + UPDATE; DELETE rejected.</item>
    ///   <item><c>"none"</c> (or <c>Writable=false</c>) — no writable mask emitted.</item>
    /// </list>
    /// When set, this wins over <see cref="Writable"/>. When null,
    /// <see cref="Writable"/>=true → <c>"full"</c>, false → <c>"none"</c>.
    /// </summary>
    public string? WritableMode { get; set; }
}
