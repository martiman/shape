namespace Shape.Attributes;

[AttributeUsage(AttributeTargets.Property, AllowMultiple = true)]
public sealed class ValidateAttribute : Attribute
{
    public ValidateAttribute(string rule)
    {
        Rule = rule;
    }

    public string Rule { get; }
}
