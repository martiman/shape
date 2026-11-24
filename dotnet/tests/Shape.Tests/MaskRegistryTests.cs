using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class MaskRegistryTests
{
    [Fact]
    public void Resolve_by_name_returns_mask()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["orders.list"] = new() { Sql = "SELECT * FROM orders" }
        });

        var mask = registry.Resolve("orders.list");

        Assert.Equal("SELECT * FROM orders", mask.Sql);
        Assert.False(mask.Writable);
    }

    [Fact]
    public void Resolve_by_sql_returns_mask()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["orders.list"] = new() { Sql = "SELECT id FROM orders" }
        });

        var mask = registry.Resolve("SELECT id FROM orders");

        Assert.Equal("SELECT id FROM orders", mask.Sql);
    }

    [Fact]
    public void Resolve_unknown_throws_MASK_NOT_ALLOWED()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>());

        var ex = Assert.Throws<ApiException>(() => registry.Resolve("nope"));

        Assert.Equal("MASK_NOT_ALLOWED", ex.ApiCode);
        Assert.Equal(400, ex.Status);
    }

    [Fact]
    public void Resolve_with_role_restriction_allows_matching_role()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["orders.list"] = new() { Sql = "SELECT id FROM orders", Roles = new() { "admin" } }
        });

        var mask = registry.Resolve("orders.list", new Dictionary<string, object?> { ["role"] = "admin" });

        Assert.NotNull(mask);
    }

    [Fact]
    public void Resolve_with_role_restriction_rejects_mismatch()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["orders.list"] = new() { Sql = "SELECT id FROM orders", Roles = new() { "admin" } }
        });

        var ex = Assert.Throws<ApiException>(() =>
            registry.Resolve("orders.list", new Dictionary<string, object?> { ["role"] = "user" }));

        Assert.Equal("AUTH_FORBIDDEN", ex.ApiCode);
        Assert.Equal(403, ex.Status);
    }

    [Fact]
    public void Resolve_with_no_role_in_authContext_rejects_role_restricted_mask()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["orders.list"] = new() { Sql = "SELECT id FROM orders", Roles = new() { "admin" } }
        });

        var ex = Assert.Throws<ApiException>(() => registry.Resolve("orders.list"));

        Assert.Equal("AUTH_FORBIDDEN", ex.ApiCode);
    }

    [Fact]
    public void Empty_or_blank_mask_entries_are_skipped()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            [""] = new() { Sql = "SELECT 1" },
            ["valid"] = new() { Sql = "" }
        });

        Assert.Throws<ApiException>(() => registry.Resolve("valid"));
        Assert.Throws<ApiException>(() => registry.Resolve("SELECT 1"));
    }

    [Fact]
    public void Writable_flag_is_preserved()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["orders"] = new() { Sql = "SELECT id FROM orders LIMIT 1", Writable = true }
        });

        var mask = registry.Resolve("orders");

        Assert.True(mask.Writable);
    }

    [Fact]
    public void Mask_not_allowed_carries_manifest_hint()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>());

        var ex = Assert.Throws<ApiException>(() => registry.Resolve("nope", null, "q1"));

        Assert.Equal("q1", ex.Ref);
        Assert.NotNull(ex.Details);
        Assert.Contains("manifest", (string)ex.Details!["hint"]!);
    }

    [Fact]
    public void Auth_forbidden_unauthenticated_hint_says_authenticate_first()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["orders.list"] = new() { Sql = "SELECT id FROM orders", Roles = new() { "admin" } }
        });

        var ex = Assert.Throws<ApiException>(() => registry.Resolve("orders.list"));

        var hint = (string)ex.Details!["hint"]!;
        Assert.Contains("Authenticate first", hint);
        var expected = (Dictionary<string, object?>)ex.Details!["expected"]!;
        Assert.Equal("one of: admin", expected["role"]);
    }

    [Fact]
    public void Auth_forbidden_wrong_role_hint_lists_allowed_roles()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["orders.list"] = new() { Sql = "SELECT id FROM orders", Roles = new() { "admin", "auditor" } }
        });

        var ex = Assert.Throws<ApiException>(() =>
            registry.Resolve("orders.list", new Dictionary<string, object?> { ["role"] = "user" }));

        var hint = (string)ex.Details!["hint"]!;
        Assert.Contains("'user'", hint);
        Assert.Contains("admin, auditor", hint);
    }

    [Fact]
    public void Mask_propagates_widened_fields()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["orders"] = new()
            {
                Sql = "SELECT * FROM orders",
                Writable = true,
                SoftDelete = "deleted_at",
                Description = "All orders",
                CacheControl = "max-age=60",
                Audit = new() { ["created_at"] = "timestamp", ["created_by"] = "user_id" },
                Relations = new() { ["items"] = new RelationMeta { FkColumns = new() { "order_id" } } },
                DefaultLimit = 50
            }
        });

        var mask = registry.Resolve("orders");

        Assert.Equal("deleted_at", mask.SoftDelete);
        Assert.Equal("All orders", mask.Description);
        Assert.Equal("max-age=60", mask.CacheControl);
        Assert.Equal("timestamp", mask.Audit!["created_at"]);
        Assert.Equal("user_id", mask.Audit["created_by"]);
        Assert.Single(mask.Relations!);
        Assert.Equal("order_id", mask.Relations!["items"].FkColumns[0]);
        Assert.Equal(50, mask.DefaultLimit);
    }

    [Fact]
    public void Empty_or_blank_widened_fields_are_dropped()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["orders"] = new()
            {
                Sql = "SELECT id FROM orders",
                SoftDelete = "",
                Description = "",
                CacheControl = "",
                Audit = new(),
                Relations = new(),
                DefaultLimit = 0
            }
        });

        var mask = registry.Resolve("orders");

        Assert.Null(mask.SoftDelete);
        Assert.Null(mask.Description);
        Assert.Null(mask.CacheControl);
        Assert.Null(mask.Audit);
        Assert.Null(mask.Relations);
        Assert.Null(mask.DefaultLimit);
    }

    [Fact]
    public void RewriteColonParams_converts_pdo_placeholders()
    {
        Assert.Equal(
            "SELECT * FROM t WHERE id = @id AND status = @status",
            MaskRegistry.RewriteColonParams("SELECT * FROM t WHERE id = :id AND status = :status"));
    }

    [Fact]
    public void RewriteColonParams_preserves_postgres_double_colon_cast()
    {
        Assert.Equal(
            "SELECT (value)::int FROM t WHERE id = @id",
            MaskRegistry.RewriteColonParams("SELECT (value)::int FROM t WHERE id = :id"));
    }

    [Fact]
    public void RewriteColonParams_preserves_colons_inside_string_literals()
    {
        Assert.Equal(
            "SELECT 'time:00' FROM t WHERE id = @id",
            MaskRegistry.RewriteColonParams("SELECT 'time:00' FROM t WHERE id = :id"));
    }

    [Fact]
    public void RewriteColonParams_preserves_string_with_doubled_quote_escape()
    {
        Assert.Equal(
            "SELECT 'a''b:c' FROM t WHERE id = @id",
            MaskRegistry.RewriteColonParams("SELECT 'a''b:c' FROM t WHERE id = :id"));
    }

    [Fact]
    public void RewriteColonParams_preserves_double_quoted_identifier()
    {
        Assert.Equal(
            "SELECT \"my:col\" FROM t WHERE id = @id",
            MaskRegistry.RewriteColonParams("SELECT \"my:col\" FROM t WHERE id = :id"));
    }

    [Fact]
    public void RewriteColonParams_preserves_line_comment()
    {
        Assert.Equal(
            "SELECT 1 -- :nope\nFROM t WHERE id = @id",
            MaskRegistry.RewriteColonParams("SELECT 1 -- :nope\nFROM t WHERE id = :id"));
    }

    [Fact]
    public void RewriteColonParams_preserves_block_comment()
    {
        Assert.Equal(
            "SELECT /* :nope */ * FROM t WHERE id = @id",
            MaskRegistry.RewriteColonParams("SELECT /* :nope */ * FROM t WHERE id = :id"));
    }

    [Fact]
    public void RewriteColonParams_short_circuits_when_no_colon()
    {
        var sql = "SELECT * FROM t WHERE id = @id";
        Assert.Same(sql, MaskRegistry.RewriteColonParams(sql));
    }

    [Fact]
    public void Resolve_by_pdo_style_sql_resolves_after_rewrite()
    {
        var registry = new MaskRegistry(new Dictionary<string, MaskConfig>
        {
            ["orders.byId"] = new() { Sql = "SELECT id FROM orders WHERE id = :id" }
        });

        var rewritten = registry.Resolve("orders.byId");
        Assert.Equal("SELECT id FROM orders WHERE id = @id", rewritten.Sql);

        // Both the original PDO-form and the rewritten form remain resolvable
        // by literal SQL so legacy callers and the rewritten path coexist.
        Assert.Same(rewritten, registry.Resolve("SELECT id FROM orders WHERE id = :id"));
        Assert.Same(rewritten, registry.Resolve("SELECT id FROM orders WHERE id = @id"));
    }
}
