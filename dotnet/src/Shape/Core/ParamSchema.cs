namespace Shape.Core;

/// <summary>
/// Param schema rule used in mask `Params` dictionaries plus static helpers
/// for turning a schema into agent-friendly error hints (`expected`, `example`).
/// Any 400 thrown for a bad param attaches these so an LLM can paste a
/// corrected request without re-reading the manifest.
/// </summary>
public sealed class ParamSchema
{
    public string Type { get; set; } = "mixed";
    public bool Required { get; set; }

    /// <summary>"int (required)" or just "int" — text used in <c>expected</c> error details.</summary>
    public string Describe() => Required ? $"{Type} (required)" : Type;

    /// <summary>
    /// Sample value for a single type — obviously fake but structurally valid
    /// so an agent can replace it with a real value.
    /// </summary>
    public static object? Example(string type) => type.ToLowerInvariant() switch
    {
        "int" or "integer" => 42,
        "float" or "double" or "decimal" or "number" => 3.14,
        "bool" or "boolean" => true,
        "array_int" => new List<object?> { 1, 2, 3 },
        "array_string" => new List<object?> { "foo", "bar" },
        "string" or "varchar" or "char" or "text" => "example",
        _ => null
    };

    /// <summary>
    /// Whole-schema text description for use as <c>details["expected"]</c>.
    /// Returns <c>{ "id": "int (required)", "tag": "string" }</c>.
    /// </summary>
    public static Dictionary<string, string> Expected(Dictionary<string, ParamSchema> schema)
    {
        var output = new Dictionary<string, string>();
        foreach (var (name, rule) in schema)
            output[name] = rule.Describe();
        return output;
    }

    /// <summary>
    /// Whole-schema sample payload for use as <c>details["example"]</c>.
    /// Defaults to required fields only — the smallest valid object an agent can submit.
    /// </summary>
    public static Dictionary<string, object?> ExamplePayload(Dictionary<string, ParamSchema> schema, bool requiredOnly = true)
    {
        var output = new Dictionary<string, object?>();
        foreach (var (name, rule) in schema)
        {
            if (requiredOnly && !rule.Required)
                continue;
            output[name] = Example(rule.Type);
        }
        return output;
    }
}
