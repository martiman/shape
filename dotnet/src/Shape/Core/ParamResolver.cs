using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shape.Core;

public sealed class ParamResolver
{
    public Dictionary<string, object?> Resolve(
        Dictionary<string, object?> incomingParams,
        Dictionary<string, ParamSchema> schema,
        Dictionary<string, ResolvedRef> resolvedRefs,
        string? currentRef = null)
    {
        var result = new Dictionary<string, object?>();

        foreach (var (name, rule) in schema)
        {
            if (!incomingParams.ContainsKey(name))
            {
                if (rule.Required)
                    throw new ApiException(
                        "PARAM_VALIDATION_ERROR",
                        $"Missing required parameter '{name}'.",
                        400,
                        currentRef,
                        BuildParamDetails(schema, $"Provide a value for '{name}' (type: {rule.Type})."));
                continue;
            }

            var resolved = ResolveValue(incomingParams[name], resolvedRefs, currentRef);
            result[name] = CoerceType(resolved, rule.Type, name, currentRef, schema);
        }

        foreach (var name in incomingParams.Keys)
        {
            // Reserved-param hygiene (MULTI-TENANCY-PLAN §2.4): the entire `__`
            // namespace is framework-only, so ANY client-supplied `__` param is
            // rejected before the schema check — even one a mask happened to
            // declare. This intentionally closes the "declared, so sendable" hole
            // and matches the PHP runtime's ParamResolver.
            if (name.StartsWith("__", StringComparison.Ordinal))
                throw new ApiException(
                    "PARAM_VALIDATION_ERROR",
                    $"Parameter '{name}' is reserved.",
                    400,
                    currentRef,
                    BuildParamDetails(schema, "Parameters whose names start with `__` are reserved for framework use."));
            if (!schema.ContainsKey(name))
                throw new ApiException(
                    "PARAM_VALIDATION_ERROR",
                    $"Unknown parameter '{name}'.",
                    400,
                    currentRef,
                    BuildParamDetails(schema, $"Mask does not declare a '{name}' parameter."));
        }

        return result;
    }

    private object? ResolveValue(object? value, Dictionary<string, ResolvedRef> resolvedRefs, string? currentRef)
    {
        if (value is not JsonElement je || je.ValueKind != JsonValueKind.Object)
        {
            if (value is JsonElement jePrimitive)
                return UnwrapJsonElement(jePrimitive);
            return value;
        }

        if (!je.TryGetProperty("ref", out var refProp) || refProp.ValueKind != JsonValueKind.String)
            return UnwrapJsonElement(je);

        var refName = refProp.GetString() ?? "";
        if (string.IsNullOrEmpty(refName))
            return UnwrapJsonElement(je);

        if (!resolvedRefs.TryGetValue(refName, out var source))
            throw new ApiException(
                "PARAM_REFERENCE_ERROR",
                $"Reference '{refName}' not found.",
                400,
                currentRef,
                new Dictionary<string, object?>
                {
                    ["hint"] = "Refs must point at the `ref` of a query that ran earlier in the same batch."
                });

        var indexByColumn = new Dictionary<string, int>();
        for (int i = 0; i < source.Columns.Count; i++)
            indexByColumn[source.Columns[i]] = i;

        if (je.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == JsonValueKind.String)
        {
            var column = valueProp.GetString()!;
            if (!indexByColumn.TryGetValue(column, out var idx))
                throw new ApiException(
                    "PARAM_REFERENCE_ERROR",
                    $"Column '{column}' not found in ref '{refName}'.",
                    400,
                    currentRef,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = new Dictionary<string, object?> { ["column"] = "one of: " + string.Join(", ", source.Columns) }
                    });
            if (source.Rows.Count == 0)
                throw new ApiException(
                    "PARAM_REFERENCE_ERROR",
                    $"Ref '{refName}' has no rows for scalar reference.",
                    400,
                    currentRef);
            return source.Rows[0][idx];
        }

        if (je.TryGetProperty("list", out var listProp) && listProp.ValueKind == JsonValueKind.String)
        {
            var column = listProp.GetString()!;
            if (!indexByColumn.TryGetValue(column, out var idx))
                throw new ApiException(
                    "PARAM_REFERENCE_ERROR",
                    $"Column '{column}' not found in ref '{refName}'.",
                    400,
                    currentRef,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = new Dictionary<string, object?> { ["column"] = "one of: " + string.Join(", ", source.Columns) }
                    });
            return source.Rows.Select(row => row[idx]).ToList();
        }

        return UnwrapJsonElement(je);
    }

    private object? CoerceType(
        object? value,
        string type,
        string paramName,
        string? currentRef,
        Dictionary<string, ParamSchema> schema)
    {
        return type switch
        {
            "int" => ToInt(value, paramName, currentRef, schema),
            "float" => ToFloat(value, paramName, currentRef, schema),
            "string" => ToString(value, paramName, currentRef, schema),
            "bool" => ToBool(value),
            "array_int" => ToArrayInt(value, paramName, currentRef, schema),
            "array_string" => ToArrayString(value, paramName, currentRef, schema),
            _ => value
        };
    }

    private static int ToInt(object? value, string paramName, string? currentRef, Dictionary<string, ParamSchema> schema)
    {
        if (value is int i) return i;
        if (value is long l) return (int)l;
        if (value is string s && Regex.IsMatch(s, @"^-?\d+$"))
            return int.Parse(s);
        throw new ApiException(
            "PARAM_VALIDATION_ERROR",
            $"Parameter '{paramName}' must be int.",
            400,
            currentRef,
            BuildParamDetails(schema, $"'{paramName}' expects an integer (e.g. 42)."));
    }

    private static double ToFloat(object? value, string paramName, string? currentRef, Dictionary<string, ParamSchema> schema)
    {
        if (value is double d) return d;
        if (value is float f) return f;
        if (value is int i) return i;
        if (value is long l) return l;
        if (value is string s && double.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;
        throw new ApiException(
            "PARAM_VALIDATION_ERROR",
            $"Parameter '{paramName}' must be float.",
            400,
            currentRef,
            BuildParamDetails(schema, $"'{paramName}' expects a number (e.g. 3.14)."));
    }

    private static string ToString(object? value, string paramName, string? currentRef, Dictionary<string, ParamSchema> schema)
    {
        if (value is string s) return s;
        if (value is int or long or float or double or bool or decimal)
            return value.ToString()!;
        throw new ApiException(
            "PARAM_VALIDATION_ERROR",
            $"Parameter '{paramName}' must be string.",
            400,
            currentRef,
            BuildParamDetails(schema, $"'{paramName}' expects a string."));
    }

    private static bool ToBool(object? value)
    {
        if (value is bool b) return b;
        if (value is int i) return i != 0;
        if (value is long l) return l != 0;
        if (value is string s) return !string.IsNullOrEmpty(s) && s != "0" && s != "false";
        return value != null;
    }

    private static List<int> ToArrayInt(object? value, string paramName, string? currentRef, Dictionary<string, ParamSchema> schema)
    {
        if (value is not IList<object?> list)
            throw new ApiException(
                "PARAM_VALIDATION_ERROR",
                $"Parameter '{paramName}' must be int array.",
                400,
                currentRef,
                BuildParamDetails(schema, $"'{paramName}' expects an array of integers (e.g. [1, 2, 3])."));
        if (list.Count == 0)
            throw new ApiException(
                "PARAM_VALIDATION_ERROR",
                $"Parameter '{paramName}' cannot be an empty array.",
                400,
                currentRef,
                BuildParamDetails(schema, $"'{paramName}' must contain at least one element."));
        return list.Select(v => ToInt(v, paramName, currentRef, schema)).ToList();
    }

    private static List<string> ToArrayString(object? value, string paramName, string? currentRef, Dictionary<string, ParamSchema> schema)
    {
        if (value is not IList<object?> list)
            throw new ApiException(
                "PARAM_VALIDATION_ERROR",
                $"Parameter '{paramName}' must be string array.",
                400,
                currentRef,
                BuildParamDetails(schema, $"'{paramName}' expects an array of strings (e.g. [\"foo\", \"bar\"])."));
        if (list.Count == 0)
            throw new ApiException(
                "PARAM_VALIDATION_ERROR",
                $"Parameter '{paramName}' cannot be an empty array.",
                400,
                currentRef,
                BuildParamDetails(schema, $"'{paramName}' must contain at least one element."));
        return list.Select(v => ToString(v, paramName, currentRef, schema)).ToList();
    }

    /// <summary>
    /// Build the standard <c>expected</c> + <c>example</c> + <c>hint</c> envelope for
    /// a 400 thrown about a mask param. Reuses <see cref="ParamSchema"/>'s static
    /// helpers so the text matches the manifest.
    /// </summary>
    private static Dictionary<string, object?> BuildParamDetails(Dictionary<string, ParamSchema> schema, string hint)
    {
        return new Dictionary<string, object?>
        {
            ["expected"] = ParamSchema.Expected(schema),
            ["example"] = ParamSchema.ExamplePayload(schema),
            ["hint"] = hint
        };
    }

    internal static object? UnwrapJsonElement(JsonElement je)
    {
        return je.ValueKind switch
        {
            JsonValueKind.String => je.GetString(),
            JsonValueKind.Number => je.TryGetInt64(out var l) ? (object)l : je.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => je.EnumerateArray().Select(UnwrapJsonElement).ToList(),
            JsonValueKind.Object => je.EnumerateObject()
                .ToDictionary(p => p.Name, p => UnwrapJsonElement(p.Value)),
            _ => null
        };
    }
}

public sealed class ResolvedRef
{
    public List<string> Columns { get; set; } = new();
    public List<object?[]> Rows { get; set; } = new();
}
