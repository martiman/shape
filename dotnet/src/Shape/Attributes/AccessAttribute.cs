namespace Shape.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class AccessAttribute : Attribute
{
    public string[] Roles { get; set; } = Array.Empty<string>();
    public string[] Read { get; set; } = Array.Empty<string>();
    public string[] Write { get; set; } = Array.Empty<string>();

    /// <summary>Override roles for INSERT (falls back to Write, then Roles).</summary>
    public string[] Create { get; set; } = Array.Empty<string>();

    /// <summary>Override roles for UPDATE (falls back to Write, then Roles).</summary>
    public string[] Update { get; set; } = Array.Empty<string>();

    /// <summary>Override roles for DELETE (falls back to Write, then Roles).</summary>
    public string[] Delete { get; set; } = Array.Empty<string>();
}
