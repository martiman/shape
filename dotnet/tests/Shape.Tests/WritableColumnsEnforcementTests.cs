using Shape.Core;
using Xunit;

namespace Shape.Tests;

/// <summary>
/// SECURITY-REVIEW H1 (field-level write authorization) and C1 (the
/// client-supplied <c>__filterSql</c> SQL-injection channel). Both gates reject
/// before any SQL runs, so a null database is fine.
/// </summary>
public sealed class WritableColumnsEnforcementTests
{
    private static BatchExecutor WritableUsersExecutor() =>
        new(database: null!, new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["users"] = new()
            {
                Sql = "SELECT id FROM users LIMIT 1",
                Writable = true,
                WritableColumns = new List<string> { "email", "display_name" }
            }
        }), validators: null);

    private static BatchExecutor QueryExecutor() =>
        new(database: null!, new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["books.list"] = new() { Sql = "SELECT id, title FROM books" }
        }), validators: null);

    [Theory]
    [InlineData("role")]
    [InlineData("is_active")]
    [InlineData("password_hash")]
    public void Insert_rejects_column_outside_the_writable_allowlist(string forbidden)
    {
        var ex = Assert.Throws<ApiException>(() =>
            WritableUsersExecutor().ExecuteMutations(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["table"] = "users",
                    ["action"] = "insert",
                    ["data"] = new Dictionary<string, object?>
                    {
                        ["email"] = "a@b.c",
                        [forbidden] = "x"
                    }
                }
            }, useTransaction: false));

        Assert.Equal("WRITE_COLUMN_FORBIDDEN", ex.ApiCode);
        Assert.Equal(403, ex.Status);
        Assert.Contains(forbidden, ex.Message);
    }

    [Fact]
    public void Update_rejects_privilege_escalation_via_role()
    {
        var ex = Assert.Throws<ApiException>(() =>
            WritableUsersExecutor().ExecuteMutations(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["table"] = "users",
                    ["action"] = "update",
                    ["filter"] = new Dictionary<string, object?> { ["id"] = 1 },
                    ["data"] = new Dictionary<string, object?> { ["role"] = "admin" }
                }
            }, useTransaction: false));

        Assert.Equal("WRITE_COLUMN_FORBIDDEN", ex.ApiCode);
    }

    [Fact]
    public void Null_allowlist_stays_unrestricted_for_hand_written_masks()
    {
        // A hand-written writable mask with no WritableColumns must not be gated
        // (legacy behavior). The insert proceeds past the allowlist check and
        // only fails later on the null database — proving the gate let it through.
        var exec = new BatchExecutor(database: null!, new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["things"] = new() { Sql = "SELECT id FROM things LIMIT 1", Writable = true }
        }), validators: null);

        var ex = Record.Exception(() =>
            exec.ExecuteMutations(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["table"] = "things",
                    ["action"] = "insert",
                    ["data"] = new Dictionary<string, object?> { ["anything"] = "ok" }
                }
            }, useTransaction: false));

        // It gets past the (absent) allowlist gate and only dies later on the
        // null database — so it must NOT be the WRITE_COLUMN_FORBIDDEN ApiException.
        Assert.False(ex is ApiException { ApiCode: "WRITE_COLUMN_FORBIDDEN" });
    }

    [Fact]
    public void Client_supplied_filterSql_option_is_rejected_as_reserved()
    {
        // C1: a client must not be able to smuggle raw SQL through a
        // `__filterSql` query option — that used to be spliced into the WHERE.
        var ex = Assert.Throws<ApiException>(() =>
            QueryExecutor().Execute(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["ref"] = "q1",
                    ["sql"] = "books.list",
                    ["options"] = new Dictionary<string, object?>
                    {
                        ["__filterSql"] = "1=1 UNION SELECT password_hash FROM users--"
                    }
                }
            }));

        Assert.Equal("QUERY_VALIDATION_ERROR", ex.ApiCode);
        Assert.Contains("__filterSql", ex.Message);
    }
}
