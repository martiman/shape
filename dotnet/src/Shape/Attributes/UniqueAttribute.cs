namespace Shape.Attributes;

/// <summary>
/// Declares a single-column or composite-column unique constraint on an
/// <see cref="EntityAttribute"/>-decorated class. Repeatable for tables
/// with several unique sets (e.g. an <c>actors</c> table that's unique on
/// both <c>(email, kind)</c> and <c>external_id</c>).
///
/// In <c>FromDatabase</c> mode the introspector discovers UNIQUE
/// constraints from <c>INFORMATION_SCHEMA.TABLE_CONSTRAINTS</c> /
/// <c>KEY_COLUMN_USAGE</c> automatically, so this attribute is only
/// needed for entity-first authoring or to override the discovered set.
///
/// Surfaces in the manifest as <c>entities[].uniqueConstraints</c> so
/// downstream consumers (LLMs, OpenAPI, schema-validators) know about
/// the constraint without having to round-trip to the DB.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class UniqueAttribute : Attribute
{
    public UniqueAttribute(params string[] columns)
    {
        Columns = columns ?? Array.Empty<string>();
    }

    public string[] Columns { get; }
}
