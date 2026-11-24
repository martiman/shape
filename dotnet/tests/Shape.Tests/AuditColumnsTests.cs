using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class AuditColumnsTests
{
    private static Mask MaskWithAudit(Dictionary<string, string> audit) => new()
    {
        Sql = "SELECT * FROM x",
        Audit = audit
    };

    [Fact]
    public void Insert_stamps_all_audit_columns()
    {
        var mask = MaskWithAudit(new()
        {
            ["created_at"] = "timestamp",
            ["updated_at"] = "timestamp",
            ["created_by"] = "user_id",
            ["updated_by"] = "user_id"
        });

        var data = new Dictionary<string, object?> { ["title"] = "X" };
        var auth = new Dictionary<string, object?> { ["sub"] = "user-7" };

        var result = BatchExecutor.ApplyAuditColumns(data, "insert", mask, auth);

        Assert.Equal("X", result["title"]);
        Assert.NotNull(result["created_at"]);
        Assert.NotNull(result["updated_at"]);
        Assert.Equal("user-7", result["created_by"]);
        Assert.Equal("user-7", result["updated_by"]);
    }

    [Fact]
    public void Update_skips_created_columns()
    {
        var mask = MaskWithAudit(new()
        {
            ["created_at"] = "timestamp",
            ["updated_at"] = "timestamp"
        });

        var result = BatchExecutor.ApplyAuditColumns(
            new Dictionary<string, object?> { ["title"] = "X" },
            "update",
            mask,
            new Dictionary<string, object?>());

        Assert.False(result.ContainsKey("created_at"));
        Assert.NotNull(result["updated_at"]);
    }

    [Fact]
    public void Dev_provided_value_wins()
    {
        var mask = MaskWithAudit(new() { ["created_at"] = "timestamp" });

        var result = BatchExecutor.ApplyAuditColumns(
            new Dictionary<string, object?> { ["created_at"] = "2020-01-01 00:00:00" },
            "insert",
            mask,
            new Dictionary<string, object?>());

        Assert.Equal("2020-01-01 00:00:00", result["created_at"]);
    }

    [Fact]
    public void User_id_is_omitted_when_no_authenticated_user()
    {
        var mask = MaskWithAudit(new() { ["created_by"] = "user_id" });

        var result = BatchExecutor.ApplyAuditColumns(
            new Dictionary<string, object?> { ["title"] = "X" },
            "insert",
            mask,
            new Dictionary<string, object?>());

        Assert.False(result.ContainsKey("created_by"));
    }

    [Fact]
    public void Empty_audit_map_is_a_noop()
    {
        var mask = new Mask { Sql = "SELECT *" };
        var data = new Dictionary<string, object?> { ["title"] = "X" };

        var result = BatchExecutor.ApplyAuditColumns(data, "insert", mask, new());

        Assert.Same(data, result);
    }

    [Theory]
    [InlineData("id", 7L)]
    [InlineData("oid", "oid-value")]
    [InlineData("sub", "user-1")]
    public void AuthUserId_picks_first_non_empty_in_priority_order(string key, object expected)
    {
        var ctx = new Dictionary<string, object?> { [key] = expected };
        Assert.Equal(expected, BatchExecutor.AuthUserId(ctx));
    }

    [Fact]
    public void AuthUserId_prefers_id_over_oid_over_sub()
    {
        var ctx = new Dictionary<string, object?>
        {
            ["sub"] = "sub-user",
            ["oid"] = "oid-user",
            ["id"] = 99L
        };
        Assert.Equal(99L, BatchExecutor.AuthUserId(ctx));
    }

    [Fact]
    public void AuthUserId_skips_zero_and_empty()
    {
        var ctx = new Dictionary<string, object?>
        {
            ["id"] = 0,
            ["oid"] = "",
            ["sub"] = "actual-user"
        };
        Assert.Equal("actual-user", BatchExecutor.AuthUserId(ctx));
    }

    [Fact]
    public void AuthUserId_returns_null_when_none_set()
    {
        Assert.Null(BatchExecutor.AuthUserId(new Dictionary<string, object?>()));
    }

    [Fact]
    public void AuthUserId_parses_numeric_string_to_long()
    {
        var ctx = new Dictionary<string, object?> { ["sub"] = "12345" };
        Assert.Equal(12345L, BatchExecutor.AuthUserId(ctx));
    }
}
