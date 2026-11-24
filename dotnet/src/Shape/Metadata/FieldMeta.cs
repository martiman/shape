namespace Shape.Metadata;

public sealed class FieldMeta
{
    public required string Name { get; init; }
    public required Type ClrType { get; init; }
    public required string ClrTypeName { get; init; }
    public required string DbType { get; init; }
    public bool Nullable { get; init; }
    public bool IsPrimary { get; init; }
    public bool IsAutoIncrement { get; init; }
    public bool Unsigned { get; init; }
    public int? Length { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public bool HasDefault { get; init; }
    public object? DefaultValue { get; init; }
    public Type? FkEntityType { get; init; }
    public string? FkColumn { get; init; }
    public string FkOnDelete { get; init; } = "cascade";
    public string FkOnUpdate { get; init; } = "cascade";
    public List<string> Validators { get; init; } = new();

    /// <summary>Closed value set for enum-style columns; null when the field isn't an enum.</summary>
    public List<string>? EnumValues { get; init; }

    /// <summary>True when this field is the tenant discriminator column.</summary>
    public bool IsTenantColumn { get; set; }

    /// <summary>True when this field is a row-scope dimension column (see <see cref="EntityMeta.ScopeColumns"/>).</summary>
    public bool IsScopeColumn { get; set; }

    /// <summary>
    /// Prose description of the column. Sourced from
    /// <c>[Column(Description=…)]</c> in entity-first mode or from
    /// <c>pg_description</c>/<c>COLUMN_COMMENT</c> in DB-first mode. Surfaces
    /// in the manifest, OpenAPI parameter docs, and LLM tool parameter hints.
    /// Null when no description is declared.
    /// </summary>
    public string? Description { get; init; }

    public bool IsFk => FkEntityType != null;

    public string ParamType()
    {
        if (ClrType == typeof(int) || ClrType == typeof(long) || ClrType == typeof(short))
            return "int";
        if (ClrType == typeof(float) || ClrType == typeof(double) || ClrType == typeof(decimal))
            return "float";
        if (ClrType == typeof(bool))
            return "bool";
        return "string";
    }
}
