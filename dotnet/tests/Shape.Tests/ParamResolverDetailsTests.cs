using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class ParamResolverDetailsTests
{
    private static Dictionary<string, ParamSchema> Schema() => new()
    {
        ["id"] = new() { Type = "int", Required = true },
        ["tag"] = new() { Type = "string" }
    };

    [Fact]
    public void Missing_required_param_attaches_full_schema_envelope()
    {
        var resolver = new ParamResolver();

        var ex = Assert.Throws<ApiException>(() => resolver.Resolve(
            new Dictionary<string, object?>(),
            Schema(),
            new Dictionary<string, ResolvedRef>(),
            "q1"));

        Assert.Equal("PARAM_VALIDATION_ERROR", ex.ApiCode);
        var expected = (Dictionary<string, string>)ex.Details!["expected"]!;
        Assert.Equal("int (required)", expected["id"]);
        Assert.Equal("string", expected["tag"]);

        var example = (Dictionary<string, object?>)ex.Details["example"]!;
        Assert.Equal(42, example["id"]);
        Assert.False(example.ContainsKey("tag"));

        Assert.Contains("'id'", (string)ex.Details["hint"]!);
    }

    [Fact]
    public void Unknown_param_attaches_full_schema_envelope()
    {
        var resolver = new ParamResolver();

        // Provide required `id` so we get past the missing-required check
        // and reach the unknown-param branch.
        var ex = Assert.Throws<ApiException>(() => resolver.Resolve(
            new Dictionary<string, object?> { ["id"] = 1, ["wrong"] = 1 },
            Schema(),
            new Dictionary<string, ResolvedRef>(),
            "q1"));

        Assert.NotNull(ex.Details);
        Assert.Contains("'wrong'", (string)ex.Details!["hint"]!);
    }

    [Fact]
    public void Bad_int_value_attaches_envelope_with_hint()
    {
        var resolver = new ParamResolver();

        var ex = Assert.Throws<ApiException>(() => resolver.Resolve(
            new Dictionary<string, object?> { ["id"] = "not-an-int" },
            Schema(),
            new Dictionary<string, ResolvedRef>(),
            "q1"));

        Assert.Contains("integer", (string)ex.Details!["hint"]!);
    }
}
