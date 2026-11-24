namespace Shape.Attributes;

/// <summary>
/// Marks the entity's primary key as framework-allocated: the column is a plain
/// (non-identity) int PK on a legacy table, and Shape assigns <c>MAX(pk)+1</c>
/// atomically on insert. Code-first counterpart of the DB-first
/// <c>ShapeConfig.PkAssignment</c> <c>"manual"</c> mode — build-time error if the
/// annotated entity's PK is composite or not int-family. Combine with
/// <c>[Id]</c>; do not combine with <c>[AutoIncrement]</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class GeneratedIdAttribute : Attribute
{
}
