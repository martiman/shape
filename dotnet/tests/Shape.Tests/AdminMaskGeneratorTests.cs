using Shape.Generator;
using Xunit;

namespace Shape.Tests;

public sealed class AdminMaskGeneratorTests
{
    [Fact]
    public void Generates_adminList_and_adminGet_for_secret_table_with_id()
    {
        var schemas = new[]
        {
            new TableSchema("users", new[] { "id", "email", "role", "password_hash" })
        };

        var masks = AdminMaskGenerator.Generate(schemas, new[] { "users" });

        Assert.True(masks.ContainsKey("users.adminList"));
        Assert.True(masks.ContainsKey("users.adminGet"));

        var list = masks["users.adminList"];
        Assert.Contains("`id`", list.Sql);
        Assert.Contains("`email`", list.Sql);
        Assert.Contains("`role`", list.Sql);
        Assert.DoesNotContain("password_hash", list.Sql);
        Assert.Contains("admin", list.Roles!);
    }

    [Fact]
    public void Skips_table_not_in_schemas()
    {
        var masks = AdminMaskGenerator.Generate(
            new[] { new TableSchema("users", new[] { "id" }) },
            new[] { "missing_table" });

        Assert.Empty(masks);
    }

    [Fact]
    public void Skips_when_only_sensitive_columns_remain()
    {
        var masks = AdminMaskGenerator.Generate(
            new[] { new TableSchema("secrets", new[] { "password_hash", "totp_secret" }) },
            new[] { "secrets" });

        Assert.Empty(masks);
    }

    [Fact]
    public void Omits_adminGet_when_no_id_column()
    {
        var masks = AdminMaskGenerator.Generate(
            new[] { new TableSchema("audit_log", new[] { "occurred_at", "actor_id", "action"}) },
            new[] { "audit_log" });

        Assert.True(masks.ContainsKey("audit_log.adminList"));
        Assert.False(masks.ContainsKey("audit_log.adminGet"));
    }

    [Fact]
    public void Sqlite_driver_uses_double_quoted_identifiers()
    {
        var masks = AdminMaskGenerator.Generate(
            new[] { new TableSchema("users", new[] { "id", "email" }) },
            new[] { "users" },
            driver: "sqlite");

        Assert.Contains("\"users\"", masks["users.adminList"].Sql);
        Assert.DoesNotContain("`users`", masks["users.adminList"].Sql);
    }

    [Theory]
    [InlineData("password", false)]
    [InlineData("password_hash", false)]
    [InlineData("totp_secret", false)]
    [InlineData("salt_value", false)]
    [InlineData("mfa_token", false)]
    [InlineData("api_token", false)]
    [InlineData("session_secret", false)]
    [InlineData("user_token", false)]
    [InlineData("token", false)]
    [InlineData("salt", false)]
    [InlineData("secret", false)]
    [InlineData("email", true)]
    [InlineData("role", true)]
    [InlineData("created_at", true)]
    public void IsSafeColumn_blocks_known_sensitive_patterns(string column, bool expected)
    {
        Assert.Equal(expected, AdminMaskGenerator.IsSafeColumn(column));
    }
}
