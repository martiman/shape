using Shape.Core;
using Shape.Generator;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

/// <summary>
/// Pins the MSSQL dialect of *generated* SQL — the sites BatchExecutor never
/// touches, so a wrong dialect here only surfaces on a live SQL Server.
/// </summary>
public sealed class MssqlDialectGeneratorTests
{
    private sealed class Order { }

    private static EntityMeta OrderMeta() => new()
    {
        ClrType = typeof(Order),
        Table = "orders",
        Writable = true,
        Fields = new()
        {
            new FieldMeta { Name = "id", ClrType = typeof(int), ClrTypeName = "Int32", DbType = "int", IsPrimary = true, IsAutoIncrement = true },
            new FieldMeta { Name = "title", ClrType = typeof(string), ClrTypeName = "String", DbType = "varchar", Length = 100 }
        },
        ReadRoles = new() { "user" },
        WriteRoles = new() { "admin" }
    };

    [Fact]
    public void Mssql_driver_emits_bracket_identifiers()
    {
        var gen = new MaskGenerator(driver: "mssql");
        var masks = gen.Generate(new[] { OrderMeta() });

        Assert.Contains("[orders]", masks["orders.list"].Sql);
        Assert.Contains("[title]", masks["orders.list"].Sql);
        Assert.DoesNotContain("`", masks["orders.list"].Sql);
    }

    [Theory]
    [InlineData("sqlsrv")]
    [InlineData("sqlserver")]
    [InlineData("MSSQL")]
    public void Mssql_driver_aliases_also_get_brackets(string alias)
    {
        var gen = new MaskGenerator(driver: alias);
        var masks = gen.Generate(new[] { OrderMeta() });

        Assert.Contains("[orders]", masks["orders.list"].Sql);
        Assert.DoesNotContain("`", masks["orders.list"].Sql);
    }

    [Fact]
    public void Writable_mask_uses_TOP_1_not_LIMIT_on_mssql()
    {
        var gen = new MaskGenerator(driver: "mssql");
        var masks = gen.Generate(new[] { OrderMeta() });

        Assert.Equal("SELECT TOP 1 [id] FROM [orders]", masks["orders"].Sql);
    }

    [Fact]
    public void No_generated_mask_contains_LIMIT_on_mssql()
    {
        var gen = new MaskGenerator(driver: "mssql", defaultListLimit: 50);
        var masks = gen.Generate(new[] { OrderMeta() });

        foreach (var (name, mask) in masks)
            Assert.DoesNotContain("LIMIT", mask.Sql ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Writable_mask_keeps_LIMIT_1_on_other_drivers()
    {
        var gen = new MaskGenerator(driver: "mysql");
        var masks = gen.Generate(new[] { OrderMeta() });

        Assert.Equal("SELECT `id` FROM `orders` LIMIT 1", masks["orders"].Sql);
    }

    [Fact]
    public void Admin_masks_use_brackets_on_mssql()
    {
        var masks = AdminMaskGenerator.Generate(
            new[] { new TableSchema("users", new[] { "id", "email", "password_hash" }) },
            new[] { "users" },
            driver: "mssql");

        Assert.Contains("[users]", masks["users.adminList"].Sql);
        Assert.Contains("[email]", masks["users.adminList"].Sql);
        Assert.Contains("WHERE [id] = @id", masks["users.adminGet"].Sql);
        Assert.DoesNotContain("`", masks["users.adminList"].Sql);
    }

    // ── Pagination (BatchExecutor.BuildMssqlPagination) ─────────────────

    [Fact]
    public void Mssql_pagination_wraps_orderless_sql_with_noop_order_by()
    {
        Assert.Equal(
            "SELECT * FROM (SELECT a FROM [t]) AS __m ORDER BY (SELECT NULL) OFFSET 10 ROWS FETCH NEXT 5 ROWS ONLY",
            BatchExecutor.BuildMssqlPagination("SELECT a FROM [t]", skip: 10, top: 5));
    }

    [Fact]
    public void Mssql_pagination_omits_fetch_when_only_skip_is_set()
    {
        Assert.Equal(
            "SELECT * FROM (SELECT a FROM [t]) AS __m ORDER BY (SELECT NULL) OFFSET 10 ROWS",
            BatchExecutor.BuildMssqlPagination("SELECT a FROM [t]", skip: 10, top: 0));
    }

    [Fact]
    public void Mssql_pagination_appends_offset_directly_when_mask_has_order_by()
    {
        // A derived table can't carry ORDER BY on SQL Server; the mask's own
        // ordering must drive the paging instead of being wrapped.
        Assert.Equal(
            "SELECT a FROM [t] ORDER BY a DESC OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY",
            BatchExecutor.BuildMssqlPagination("SELECT a FROM [t] ORDER BY a DESC;", skip: 0, top: 5));
    }

    [Fact]
    public void Mssql_pagination_ignores_order_by_inside_subqueries_and_literals()
    {
        // ORDER BY inside a subquery is not top-level → wrapper applies.
        var subquery = "SELECT a, (SELECT TOP 1 b FROM [u] ORDER BY b) AS best FROM [t]";
        Assert.StartsWith("SELECT * FROM (", BatchExecutor.BuildMssqlPagination(subquery, 0, 5));
        Assert.False(BatchExecutor.HasTopLevelOrderBy(subquery));

        // "ORDER BY" inside a string literal or quoted identifier must not count.
        Assert.False(BatchExecutor.HasTopLevelOrderBy("SELECT 'ORDER BY x' FROM [t]"));
        Assert.False(BatchExecutor.HasTopLevelOrderBy("SELECT [ORDER BY] FROM [t]"));

        // ...but a real trailing one does, case-insensitively.
        Assert.True(BatchExecutor.HasTopLevelOrderBy("SELECT a FROM [t] order   by a"));
        // Column names merely containing the word (reorder) must not count.
        Assert.False(BatchExecutor.HasTopLevelOrderBy("SELECT reorder_by FROM [t]"));
    }

    [Fact]
    public void Mssql_insert_returning_id_runs_in_one_batch()
    {
        // SCOPE_IDENTITY() is batch-scoped: issued as a second command it returns
        // NULL (→ 0). The INSERT and the id SELECT must be fused into one batch.
        Assert.Equal(
            "INSERT INTO [t] (a) VALUES (@a); SELECT CAST(SCOPE_IDENTITY() AS bigint)",
            Database.MssqlInsertReturningIdBatch("INSERT INTO [t] (a) VALUES (@a)"));

        // Trailing semicolons/whitespace on the caller's INSERT must not produce an empty statement.
        Assert.Equal(
            "INSERT INTO [t] (a) VALUES (@a); SELECT CAST(SCOPE_IDENTITY() AS bigint)",
            Database.MssqlInsertReturningIdBatch("INSERT INTO [t] (a) VALUES (@a); "));
    }
}
