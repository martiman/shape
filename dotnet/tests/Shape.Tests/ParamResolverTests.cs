using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class ParamResolverTests
{
    [Fact]
    public void Resolve_returns_only_schema_keys()
    {
        var resolver = new ParamResolver();
        var schema = new Dictionary<string, ParamSchema>
        {
            ["id"] = new() { Type = "int", Required = true }
        };

        var result = resolver.Resolve(
            new Dictionary<string, object?> { ["id"] = 7 },
            schema,
            new Dictionary<string, ResolvedRef>(),
            "q1");

        Assert.Single(result);
        Assert.Equal(7, result["id"]);
    }

    [Fact]
    public void Resolve_throws_on_missing_required_param()
    {
        var resolver = new ParamResolver();
        var schema = new Dictionary<string, ParamSchema>
        {
            ["id"] = new() { Type = "int", Required = true }
        };

        var ex = Assert.Throws<ApiException>(() => resolver.Resolve(
            new Dictionary<string, object?>(),
            schema,
            new Dictionary<string, ResolvedRef>(),
            "q1"));

        Assert.Equal("PARAM_VALIDATION_ERROR", ex.ApiCode);
        Assert.Equal("q1", ex.Ref);
    }

    [Fact]
    public void Resolve_throws_on_unknown_param()
    {
        var resolver = new ParamResolver();
        var schema = new Dictionary<string, ParamSchema>();

        var ex = Assert.Throws<ApiException>(() => resolver.Resolve(
            new Dictionary<string, object?> { ["unknown"] = 1 },
            schema,
            new Dictionary<string, ResolvedRef>(),
            "q1"));

        Assert.Equal("PARAM_VALIDATION_ERROR", ex.ApiCode);
    }

    [Fact]
    public void Coerce_int_accepts_string()
    {
        var resolver = new ParamResolver();
        var schema = new Dictionary<string, ParamSchema>
        {
            ["id"] = new() { Type = "int", Required = true }
        };

        var result = resolver.Resolve(
            new Dictionary<string, object?> { ["id"] = "42" },
            schema,
            new Dictionary<string, ResolvedRef>(),
            "q1");

        Assert.Equal(42, result["id"]);
    }

    [Fact]
    public void Coerce_array_int_rejects_empty()
    {
        var resolver = new ParamResolver();
        var schema = new Dictionary<string, ParamSchema>
        {
            ["ids"] = new() { Type = "array_int", Required = true }
        };

        var ex = Assert.Throws<ApiException>(() => resolver.Resolve(
            new Dictionary<string, object?> { ["ids"] = new List<object?>() },
            schema,
            new Dictionary<string, ResolvedRef>(),
            "q1"));

        Assert.Equal("PARAM_VALIDATION_ERROR", ex.ApiCode);
    }
}
