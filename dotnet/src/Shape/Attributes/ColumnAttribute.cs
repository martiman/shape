namespace Shape.Attributes;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class ColumnAttribute : Attribute
{
    public string? Type { get; set; }
    public int Length { get; set; }
    public int Precision { get; set; }
    public int Scale { get; set; }
    public bool Unsigned { get; set; }

    /// <summary>
    /// Closed value set for enum-style columns. Setting this causes:
    /// <list type="bullet">
    ///   <item><c>ValidatorGenerator</c> emits a <c>one-of</c> check on insert/update.</item>
    ///   <item><c>MaskGenerator</c> emits a <c>.by{Field}</c> filter mask.</item>
    ///   <item><c>ManifestGenerator</c> and <c>OpenApiGenerator</c> expose the value set as <c>enum</c> in the field schema.</item>
    /// </list>
    /// Pair with <c>Type = "enum"</c> for clarity, but the <c>Values</c> array alone is enough to trigger the behavior.
    /// </summary>
    public string[]? Values { get; set; }

    /// <summary>
    /// Human / agent-readable description of the column intent. Surfaces in
    /// the manifest, OpenAPI parameter descriptions, and LLM tool parameter
    /// hints. In <c>FromDatabase</c> mode this is auto-populated from
    /// <c>pg_description</c> (Postgres) or
    /// <c>INFORMATION_SCHEMA.COLUMNS.COLUMN_COMMENT</c> (MySQL) when not set.
    /// </summary>
    public string? Description { get; set; }
}
