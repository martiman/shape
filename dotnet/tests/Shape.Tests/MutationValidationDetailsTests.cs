using Shape.Core;
using Xunit;

namespace Shape.Tests;

/// <summary>
/// Validation-only paths in BatchExecutor — they fail before touching the DB,
/// so the dispatcher can be exercised without a live connection. We stand up
/// a BatchExecutor with a registry that has writable masks for the relevant
/// table; each test exercises the throw and asserts the detail envelope.
/// </summary>
public sealed class MutationValidationDetailsTests
{
    private static BatchExecutor BuildExecutor()
    {
        // We never reach the DB because validation fails first; the connection
        // factory is only invoked lazily by Database, and we won't construct one.
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["books"] = new() { Sql = "SELECT id FROM books LIMIT 1", Writable = true }
        });
        return new BatchExecutor(database: null!, registry, validators: null);
    }

    [Fact]
    public void Empty_mutations_throws_with_example()
    {
        var ex = Assert.Throws<ApiException>(() =>
            BuildExecutor().ExecuteMutations(new List<Dictionary<string, object?>>(), useTransaction: false));

        Assert.Equal("MUTATION_VALIDATION_ERROR", ex.ApiCode);
        Assert.NotNull(ex.Details);
        Assert.NotNull(ex.Details!["expected"]);
        Assert.NotNull(ex.Details["example"]);
    }

    [Fact]
    public void Empty_queries_throws_with_example()
    {
        var ex = Assert.Throws<ApiException>(() =>
            BuildExecutor().Execute(new List<Dictionary<string, object?>>()));

        Assert.Equal("QUERY_VALIDATION_ERROR", ex.ApiCode);
        Assert.NotNull(ex.Details);
        Assert.NotNull(ex.Details!["expected"]);
        Assert.NotNull(ex.Details["example"]);
    }

    [Fact]
    public void Missing_table_throws_with_expected_and_example()
    {
        var ex = Assert.Throws<ApiException>(() =>
            BuildExecutor().ExecuteMutations(
                new List<Dictionary<string, object?>>
                {
                    new() { ["action"] = "insert", ["data"] = new Dictionary<string, object?> { ["x"] = 1 } }
                }));

        Assert.Equal("MUTATION_VALIDATION_ERROR", ex.ApiCode);
        var expected = (Dictionary<string, object?>)ex.Details!["expected"]!;
        Assert.Equal("string (required)", expected["table"]);
    }

    [Fact]
    public void Bad_action_throws_with_oneof_expected()
    {
        var ex = Assert.Throws<ApiException>(() =>
            BuildExecutor().ExecuteMutations(
                new List<Dictionary<string, object?>>
                {
                    new() { ["table"] = "books", ["action"] = "wat" }
                }));

        Assert.Equal("MUTATION_VALIDATION_ERROR", ex.ApiCode);
        var expected = (Dictionary<string, object?>)ex.Details!["expected"]!;
        Assert.Contains("'insert'", (string)expected["action"]!);
    }

    [Fact]
    public void Update_without_filter_throws_with_filter_required()
    {
        var ex = Assert.Throws<ApiException>(() =>
            BuildExecutor().ExecuteMutations(
                new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["table"] = "books",
                        ["action"] = "update",
                        ["data"] = new Dictionary<string, object?> { ["title"] = "X" }
                    }
                }));

        Assert.Equal("MUTATION_VALIDATION_ERROR", ex.ApiCode);
        Assert.Contains("filter", ex.Message);
        var expected = (Dictionary<string, object?>)ex.Details!["expected"]!;
        Assert.Contains("required", (string)expected["filter"]!);
    }

    [Fact]
    public void Insert_without_data_throws_with_data_required()
    {
        var ex = Assert.Throws<ApiException>(() =>
            BuildExecutor().ExecuteMutations(
                new List<Dictionary<string, object?>>
                {
                    new() { ["table"] = "books", ["action"] = "insert" }
                }));

        var expected = (Dictionary<string, object?>)ex.Details!["expected"]!;
        Assert.Contains("required", (string)expected["data"]!);
    }

    [Fact]
    public void Non_writable_table_throws_with_hint()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["books"] = new() { Sql = "SELECT id FROM books LIMIT 1", Writable = false }
        });
        var executor = new BatchExecutor(database: null!, registry);

        var ex = Assert.Throws<ApiException>(() =>
            executor.ExecuteMutations(
                new List<Dictionary<string, object?>>
                {
                    new()
                    {
                        ["table"] = "books",
                        ["action"] = "insert",
                        ["data"] = new Dictionary<string, object?> { ["title"] = "X" }
                    }
                }));

        Assert.Equal("AUTHORIZATION_ERROR", ex.ApiCode);
        Assert.Contains("Writable", (string)ex.Details!["hint"]!);
    }
}
