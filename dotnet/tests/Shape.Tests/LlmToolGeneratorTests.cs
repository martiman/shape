using Shape.Core;
using Shape.Generator;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

public sealed class LlmToolGeneratorTests
{
    private sealed class Order { }

    private static EntityMeta OrderMeta(string? softDelete = null) => new()
    {
        ClrType = typeof(Order),
        Table = "orders",
        Writable = true,
        SoftDeleteColumn = softDelete,
        Fields = new()
        {
            new FieldMeta { Name = "id", ClrType = typeof(int), ClrTypeName = "Int32", DbType = "int", IsPrimary = true },
            new FieldMeta { Name = "title", ClrType = typeof(string), ClrTypeName = "String", DbType = "varchar" }
        }
    };

    private static Dictionary<string, MaskConfig> Masks() => new()
    {
        ["orders.list"] = new() { Sql = "SELECT * FROM orders", Description = "List orders" },
        ["orders.get"] = new()
        {
            Sql = "SELECT * FROM orders WHERE id = @id",
            Params = new() { ["id"] = new() { Type = "int", Required = true } }
        },
        ["orders"] = new() { Sql = "SELECT id FROM orders LIMIT 1", Writable = true }
    };

    [Fact]
    public void Anthropic_shape_emits_name_description_input_schema()
    {
        var tools = new LlmToolGenerator().GenerateAnthropic(new[] { OrderMeta() }, Masks(), Array.Empty<string>());
        var listTool = tools.Single(t => t["name"]?.ToString() == "orders_list");

        Assert.True(listTool.ContainsKey("description"));
        Assert.True(listTool.ContainsKey("input_schema"));
        Assert.False(listTool.ContainsKey("type"));
    }

    [Fact]
    public void OpenAi_shape_wraps_in_function_object()
    {
        var tools = new LlmToolGenerator().GenerateOpenAi(new[] { OrderMeta() }, Masks(), Array.Empty<string>());
        var listTool = tools.Single(t => ((Dictionary<string, object?>)t["function"]!)["name"]?.ToString() == "orders_list");

        Assert.Equal("function", listTool["type"]);
        var fn = (Dictionary<string, object?>)listTool["function"]!;
        Assert.True(fn.ContainsKey("parameters"));
    }

    [Fact]
    public void Writable_mask_becomes_mutate_tool_with_action_enum()
    {
        var tools = new LlmToolGenerator().GenerateAnthropic(new[] { OrderMeta() }, Masks(), Array.Empty<string>());
        var mutateTool = tools.Single(t => t["name"]?.ToString() == "orders_mutate");

        var schema = (Dictionary<string, object?>)mutateTool["input_schema"]!;
        var props = (Dictionary<string, object?>)schema["properties"]!;
        var action = (Dictionary<string, object?>)props["action"]!;
        var enumValues = (List<object?>)action["enum"]!;

        Assert.Equal(new object?[] { "insert", "update", "delete" }, enumValues);
    }

    [Fact]
    public void Soft_delete_table_mentions_soft_delete_in_mutate_description()
    {
        var tools = new LlmToolGenerator().GenerateAnthropic(
            new[] { OrderMeta(softDelete: "deleted_at") }, Masks(), Array.Empty<string>());
        var mutateTool = tools.Single(t => t["name"]?.ToString() == "orders_mutate");

        Assert.Contains("soft-delete", (string)mutateTool["description"]!);
    }

    [Fact]
    public void Required_param_lands_in_required_array()
    {
        var tools = new LlmToolGenerator().GenerateAnthropic(new[] { OrderMeta() }, Masks(), Array.Empty<string>());
        var getTool = tools.Single(t => t["name"]?.ToString() == "orders_get");
        var schema = (Dictionary<string, object?>)getTool["input_schema"]!;
        var required = (List<string>)schema["required"]!;

        Assert.Contains("id", required);
    }

    [Fact]
    public void Param_type_int_maps_to_integer()
    {
        var tools = new LlmToolGenerator().GenerateAnthropic(new[] { OrderMeta() }, Masks(), Array.Empty<string>());
        var getTool = tools.Single(t => t["name"]?.ToString() == "orders_get");
        var schema = (Dictionary<string, object?>)getTool["input_schema"]!;
        var props = (Dictionary<string, object?>)schema["properties"]!;
        var idParam = (Dictionary<string, object?>)props["id"]!;

        Assert.Equal("integer", idParam["type"]);
    }

    [Fact]
    public void Commands_emit_tools_with_open_input_schema()
    {
        var tools = new LlmToolGenerator().GenerateAnthropic(
            Array.Empty<EntityMeta>(),
            new Dictionary<string, MaskConfig>(),
            new[] { "order.process" });
        var cmdTool = tools.Single(t => t["name"]?.ToString() == "order_process");

        var schema = (Dictionary<string, object?>)cmdTool["input_schema"]!;
        Assert.True((bool)schema["additionalProperties"]!);
    }

    [Fact]
    public void Tool_names_replace_dots_and_strip_invalid_chars()
    {
        var tools = new LlmToolGenerator().GenerateAnthropic(
            Array.Empty<EntityMeta>(),
            new Dictionary<string, MaskConfig> { ["weird name!"] = new() { Sql = "SELECT 1" } },
            Array.Empty<string>());

        var name = tools[0]["name"]?.ToString() ?? "";
        Assert.DoesNotContain(' ', name);
        Assert.DoesNotContain('!', name);
    }
}
