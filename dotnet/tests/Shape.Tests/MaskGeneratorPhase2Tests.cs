using Shape.Generator;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

public sealed class MaskGeneratorPhase2Tests
{
    private sealed class Order { }
    private sealed class Item { }

    private static EntityMeta OrderMeta(string? softDelete = null, Dictionary<string, string>? audit = null) => new()
    {
        ClrType = typeof(Order),
        Table = "orders",
        Writable = true,
        Fields = new()
        {
            new FieldMeta { Name = "id", ClrType = typeof(int), ClrTypeName = "Int32", DbType = "int", IsPrimary = true, IsAutoIncrement = true },
            new FieldMeta { Name = "title", ClrType = typeof(string), ClrTypeName = "String", DbType = "varchar", Length = 100 }
        },
        ReadRoles = new() { "user", "admin" },
        WriteRoles = new() { "admin" },
        SoftDeleteColumn = softDelete,
        AuditColumns = audit ?? new()
    };

    private static EntityMeta ItemMeta() => new()
    {
        ClrType = typeof(Item),
        Table = "items",
        Writable = false,
        Fields = new()
        {
            new FieldMeta { Name = "id", ClrType = typeof(int), ClrTypeName = "Int32", DbType = "int", IsPrimary = true },
            new FieldMeta
            {
                Name = "order_id",
                ClrType = typeof(int),
                ClrTypeName = "Int32",
                DbType = "int",
                FkEntityType = typeof(Order)
            }
        }
    };

    [Fact]
    public void Soft_delete_adds_WHERE_to_read_masks()
    {
        var gen = new MaskGenerator();
        var masks = gen.Generate(new[] { OrderMeta(softDelete: "deleted_at") });

        Assert.Contains("WHERE `deleted_at` IS NULL", masks["orders.list"].Sql);
        Assert.Contains("AND `deleted_at` IS NULL", masks["orders.get"].Sql);
        Assert.Contains("AND `deleted_at` IS NULL", masks["orders.getMany"].Sql);
    }

    [Fact]
    public void Default_list_limit_lands_on_list_and_byFk_masks()
    {
        var gen = new MaskGenerator(defaultListLimit: 50);
        var masks = gen.Generate(new[] { ItemMeta() });

        Assert.Equal(50, masks["items.list"].DefaultLimit);
        Assert.Equal(50, masks["items.byOrderId"].DefaultLimit);
        // .get / .getMany should NOT have the cap (PK lookups return ≤ 1 row anyway).
        Assert.Equal(0, masks["items.get"].DefaultLimit);
    }

    [Fact]
    public void Default_list_limit_zero_disables_field()
    {
        var gen = new MaskGenerator(defaultListLimit: 0);
        var masks = gen.Generate(new[] { OrderMeta() });
        Assert.Equal(0, masks["orders.list"].DefaultLimit);
    }

    [Fact]
    public void Writable_mask_carries_audit_map()
    {
        var gen = new MaskGenerator();
        var masks = gen.Generate(new[] { OrderMeta(audit: new() { ["created_at"] = "timestamp" }) });

        Assert.NotNull(masks["orders"].Audit);
        Assert.Equal("timestamp", masks["orders"].Audit!["created_at"]);
    }

    [Fact]
    public void Writable_mask_carries_soft_delete_field()
    {
        var gen = new MaskGenerator();
        var masks = gen.Generate(new[] { OrderMeta(softDelete: "deleted_at") });

        Assert.Equal("deleted_at", masks["orders"].SoftDelete);
    }

    [Fact]
    public void Writable_mask_collects_inverse_FK_relations()
    {
        var gen = new MaskGenerator();
        var masks = gen.Generate(new[] { OrderMeta(), ItemMeta() });

        Assert.NotNull(masks["orders"].Relations);
        Assert.True(masks["orders"].Relations!.ContainsKey("items"));
        Assert.Equal("order_id", masks["orders"].Relations!["items"].FkColumns[0]);
    }

    [Fact]
    public void Description_mentions_soft_delete_and_audit_in_writable_mask()
    {
        var gen = new MaskGenerator();
        var masks = gen.Generate(new[] { OrderMeta(
            softDelete: "deleted_at",
            audit: new() { ["created_at"] = "timestamp" }) });

        var desc = masks["orders"].Description ?? "";
        Assert.Contains("DELETE soft-deletes", desc);
        Assert.Contains("Audit columns", desc);
    }

    [Fact]
    public void Read_masks_carry_descriptions()
    {
        var gen = new MaskGenerator();
        var masks = gen.Generate(new[] { OrderMeta() });

        Assert.Contains("List rows from orders", masks["orders.list"].Description);
        Assert.Contains("Get one row from orders", masks["orders.get"].Description);
    }

    [Fact]
    public void Sqlite_driver_emits_double_quoted_identifiers()
    {
        var gen = new MaskGenerator(driver: "sqlite");
        var masks = gen.Generate(new[] { OrderMeta() });

        Assert.Contains("\"orders\"", masks["orders.list"].Sql);
        Assert.DoesNotContain("`orders`", masks["orders.list"].Sql);
    }
}
