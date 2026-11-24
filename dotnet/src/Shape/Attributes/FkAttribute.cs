namespace Shape.Attributes;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = false)]
public sealed class FkAttribute : Attribute
{
    public FkAttribute(Type entityType)
    {
        EntityType = entityType;
    }

    public Type EntityType { get; }
    public string? Column { get; set; }
    public string OnDelete { get; set; } = "cascade";
    public string OnUpdate { get; set; } = "cascade";
}
