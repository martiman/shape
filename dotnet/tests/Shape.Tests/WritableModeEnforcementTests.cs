using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class WritableModeEnforcementTests
{
    private static BatchExecutor BuildExecutor(string mode)
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["events"] = new() { Sql = "SELECT id FROM events LIMIT 1", Writable = true, WritableMode = mode }
        });
        return new BatchExecutor(database: null!, registry, validators: null);
    }

    [Fact]
    public void Append_only_rejects_update_with_typed_error()
    {
        var ex = Assert.Throws<ApiException>(() =>
            BuildExecutor("append_only").ExecuteMutations(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["table"] = "events",
                    ["action"] = "update",
                    ["filter"] = new Dictionary<string, object?> { ["id"] = 1 },
                    ["data"] = new Dictionary<string, object?> { ["kind"] = "x" }
                }
            }, useTransaction: false));

        Assert.Equal("WRITABLE_MODE_FORBIDDEN", ex.ApiCode);
        Assert.Equal(403, ex.Status);
        Assert.Contains("append-only", ex.Message);
    }

    [Fact]
    public void Append_only_rejects_delete_with_typed_error()
    {
        var ex = Assert.Throws<ApiException>(() =>
            BuildExecutor("append_only").ExecuteMutations(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["table"] = "events",
                    ["action"] = "delete",
                    ["filter"] = new Dictionary<string, object?> { ["id"] = 1 }
                }
            }, useTransaction: false));

        Assert.Equal("WRITABLE_MODE_FORBIDDEN", ex.ApiCode);
    }

    [Fact]
    public void No_delete_rejects_delete_only()
    {
        var ex = Assert.Throws<ApiException>(() =>
            BuildExecutor("no_delete").ExecuteMutations(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["table"] = "events",
                    ["action"] = "delete",
                    ["filter"] = new Dictionary<string, object?> { ["id"] = 1 }
                }
            }, useTransaction: false));

        Assert.Equal("WRITABLE_MODE_FORBIDDEN", ex.ApiCode);
        Assert.Contains("no_delete", ex.Message);
    }
}
