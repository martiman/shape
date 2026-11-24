using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class ParamSchemaHelpersTests
{
    [Fact]
    public void Describe_required_appends_marker()
    {
        Assert.Equal("int (required)", new ParamSchema { Type = "int", Required = true }.Describe());
        Assert.Equal("string", new ParamSchema { Type = "string" }.Describe());
    }

    [Theory]
    [InlineData("int", 42)]
    [InlineData("integer", 42)]
    [InlineData("bool", true)]
    [InlineData("string", "example")]
    [InlineData("varchar", "example")]
    [InlineData("unknown", null)]
    public void Example_returns_canonical_value_per_type(string type, object? expected)
    {
        Assert.Equal(expected, ParamSchema.Example(type));
    }

    [Fact]
    public void Example_float_returns_double()
    {
        Assert.Equal(3.14, ParamSchema.Example("float"));
    }

    [Fact]
    public void Example_array_int_returns_three_int_list()
    {
        var v = ParamSchema.Example("array_int");
        var list = Assert.IsType<List<object?>>(v);
        Assert.Equal(new object?[] { 1, 2, 3 }, list);
    }

    [Fact]
    public void Expected_renders_full_schema()
    {
        var schema = new Dictionary<string, ParamSchema>
        {
            ["id"] = new() { Type = "int", Required = true },
            ["tag"] = new() { Type = "string" }
        };

        var expected = ParamSchema.Expected(schema);

        Assert.Equal("int (required)", expected["id"]);
        Assert.Equal("string", expected["tag"]);
    }

    [Fact]
    public void ExamplePayload_required_only_skips_optional()
    {
        var schema = new Dictionary<string, ParamSchema>
        {
            ["id"] = new() { Type = "int", Required = true },
            ["tag"] = new() { Type = "string" }
        };

        var sample = ParamSchema.ExamplePayload(schema);

        Assert.Single(sample);
        Assert.Equal(42, sample["id"]);
    }

    [Fact]
    public void ExamplePayload_with_requiredOnly_false_includes_optional()
    {
        var schema = new Dictionary<string, ParamSchema>
        {
            ["id"] = new() { Type = "int", Required = true },
            ["tag"] = new() { Type = "string" }
        };

        var sample = ParamSchema.ExamplePayload(schema, requiredOnly: false);

        Assert.Equal(2, sample.Count);
        Assert.Equal(42, sample["id"]);
        Assert.Equal("example", sample["tag"]);
    }
}
