using System.Text.RegularExpressions;
using Shape.Core;
using Shape.Metadata;

namespace Shape.Generator;

/// <summary>
/// Emits the manifest as a ready-to-paste tool-definitions array for an LLM
/// in either Anthropic or OpenAI tool-call shape. Removes the translation
/// step every agent integration would otherwise repeat.
///
/// Read masks become tools that take the mask's params as input. Writable
/// masks become a single <c>&lt;table&gt;_mutate</c> tool with an action enum.
/// Commands are emitted with an open input schema (current C# command handlers
/// don't carry per-parameter typing — see roadmap).
/// </summary>
public sealed class LlmToolGenerator
{
    /// <summary>Anthropic <c>tools</c> shape: <c>{ name, description, input_schema }</c>.</summary>
    public List<Dictionary<string, object?>> GenerateAnthropic(
        IEnumerable<EntityMeta> entities,
        Dictionary<string, MaskConfig> masks,
        IEnumerable<string> commands)
    {
        return BuildTools(entities, masks, commands)
            .Select(tool => new Dictionary<string, object?>
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = tool.InputSchema
            })
            .ToList();
    }

    /// <summary>OpenAI <c>tools</c> shape: <c>{ type: "function", function: { name, description, parameters } }</c>.</summary>
    public List<Dictionary<string, object?>> GenerateOpenAi(
        IEnumerable<EntityMeta> entities,
        Dictionary<string, MaskConfig> masks,
        IEnumerable<string> commands)
    {
        return BuildTools(entities, masks, commands)
            .Select(tool => new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = tool.Name,
                    ["description"] = tool.Description,
                    ["parameters"] = tool.InputSchema
                }
            })
            .ToList();
    }

    private sealed record Tool(string Name, string Description, Dictionary<string, object?> InputSchema);

    private List<Tool> BuildTools(
        IEnumerable<EntityMeta> entities,
        Dictionary<string, MaskConfig> masks,
        IEnumerable<string> commands)
    {
        var entityByTable = entities.ToDictionary(e => e.Table, e => e);
        var tools = new List<Tool>();

        foreach (var (name, mask) in masks)
        {
            if (mask.Writable)
            {
                tools.Add(MutateTool(name, entityByTable.GetValueOrDefault(name), mask));
                continue;
            }

            tools.Add(new Tool(
                ToolName(name),
                ResolveDescription(name, mask, entityByTable),
                ParamsToSchema(mask.Params)));
        }

        foreach (var cmd in commands)
        {
            tools.Add(new Tool(
                ToolName(cmd),
                $"Custom command '{cmd}'. Parameters depend on the implementation — see manifest commands list for details.",
                new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>(),
                    ["additionalProperties"] = true
                }));
        }

        return tools;
    }

    private static Dictionary<string, object?> ParamsToSchema(Dictionary<string, ParamSchema>? @params)
    {
        var properties = new Dictionary<string, object?>();
        var required = new List<string>();

        if (@params != null)
        {
            foreach (var (name, spec) in @params)
            {
                properties[name] = MapParamType(spec.Type);
                if (spec.Required) required.Add(name);
            }
        }

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties
        };
        if (required.Count > 0)
            schema["required"] = required;
        return schema;
    }

    private static Dictionary<string, object?> MapParamType(string type) => type.ToLowerInvariant() switch
    {
        "int" or "integer" => new() { ["type"] = "integer" },
        "float" or "double" or "decimal" or "number" => new() { ["type"] = "number" },
        "bool" or "boolean" => new() { ["type"] = "boolean" },
        "array_int" => new() { ["type"] = "array", ["items"] = new Dictionary<string, object?> { ["type"] = "integer" } },
        "array_string" => new() { ["type"] = "array", ["items"] = new Dictionary<string, object?> { ["type"] = "string" } },
        _ => new() { ["type"] = "string" }
    };

    /// <summary>
    /// Tool names: replace dots with underscores, strip anything outside
    /// <c>[A-Za-z0-9_-]</c>, cap at 64 chars. Both Anthropic and OpenAI
    /// accept this character set.
    /// </summary>
    private static string ToolName(string maskName)
    {
        var name = maskName.Replace('.', '_');
        name = Regex.Replace(name, "[^A-Za-z0-9_-]", "_");
        return name.Length > 64 ? name[..64] : name;
    }

    private static string ResolveDescription(string name, MaskConfig mask, Dictionary<string, EntityMeta> entityByTable)
    {
        // Prefer the operator's mask description verbatim, then layer the
        // entity description (when present) so an agent picking between
        // similar masks gets prose disambiguation.
        var dot = name.IndexOf('.');
        var entityHint = "";
        if (dot > 0 && entityByTable.TryGetValue(name[..dot], out var ent))
        {
            if (!string.IsNullOrEmpty(ent.Description))
                entityHint = $" ({ent.Description})";
            if (ent.TenantScoped || mask.TenantScope is string)
                entityHint += " Rows are automatically limited to the caller's tenant.";
        }

        if (!string.IsNullOrEmpty(mask.Description))
        {
            var cols = ColumnsHint(name, entityByTable);
            var desc = mask.Description.TrimEnd('.', ' ');
            if (cols.Length > 0) desc += $". Returns rows with columns: {cols}.";
            return desc + entityHint;
        }
        return DescribeReadMask(name, entityByTable) + entityHint;
    }

    private static string ColumnsHint(string maskName, Dictionary<string, EntityMeta> entityByTable)
    {
        var dot = maskName.IndexOf('.');
        var table = dot < 0 ? maskName : maskName[..dot];
        return entityByTable.TryGetValue(table, out var entity)
            ? string.Join(", ", entity.Fields.Select(f => f.Name))
            : "";
    }

    private static string DescribeReadMask(string name, Dictionary<string, EntityMeta> entityByTable)
    {
        var dot = name.IndexOf('.');
        if (dot < 0)
            return $"Run mask '{name}'.";

        var table = name[..dot];
        var verb = name[(dot + 1)..];
        var cols = entityByTable.TryGetValue(table, out var entity)
            ? string.Join(", ", entity.Fields.Select(f => f.Name))
            : "";

        var baseText = verb switch
        {
            "list" => $"List rows from {table}",
            "get" => $"Get one row from {table} by primary key",
            "getMany" => $"Get multiple rows from {table} by primary keys",
            "adminList" => $"(admin only) List rows from {table} with sensitive columns stripped",
            "adminGet" => $"(admin only) Get one row from {table} by primary key with sensitive columns stripped",
            _ when verb.StartsWith("by", StringComparison.Ordinal) =>
                $"Filter {table} by " + LcFirst(verb[2..]),
            _ => $"Run mask '{name}' on {table}"
        };

        return cols.Length > 0
            ? baseText + $". Returns rows with columns: {cols}."
            : baseText + ".";
    }

    private static string LcFirst(string s) =>
        s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static Tool MutateTool(string table, EntityMeta? entity, MaskConfig mask)
    {
        var cols = entity != null ? string.Join(", ", entity.Fields.Select(f => f.Name)) : "";

        string description;
        if (!string.IsNullOrEmpty(mask.Description))
        {
            description = mask.Description;
            if (cols.Length > 0 && !description.Contains(cols))
                description += $" Columns: {cols}.";
        }
        else
        {
            description = $"Insert / update / delete rows in {table}.";
            if (cols.Length > 0)
                description += $" Columns: {cols}.";
            if (!string.IsNullOrEmpty(entity?.SoftDeleteColumn))
                description += $" DELETE on this table is a soft-delete (sets {entity!.SoftDeleteColumn} = CURRENT_TIMESTAMP); reads automatically filter out soft-deleted rows.";
        }

        if (entity?.TenantScoped == true)
            description += " Rows are automatically limited to the caller's tenant.";

        return new Tool(
            ToolName(table) + "_mutate",
            description,
            new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>
                {
                    ["action"] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["enum"] = new List<object?> { "insert", "update", "delete" },
                        ["description"] = "Mutation action to perform."
                    },
                    ["data"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["description"] = "Column-value pairs (insert/update only).",
                        ["additionalProperties"] = true
                    },
                    ["filter"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["description"] = "Row filter (update/delete only). At least one column required.",
                        ["additionalProperties"] = true
                    }
                },
                ["required"] = new List<string> { "action" }
            });
    }
}
