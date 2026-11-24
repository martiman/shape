using Shape.Core;
using Shape.Generator;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

public sealed class AccessMapTests
{
    private sealed class Order { }
    private sealed class Ledger { }
    private sealed class Customer { }

    private static EntityMeta MakeMeta<T>(string table) => new()
    {
        ClrType = typeof(T),
        Table = table,
        Writable = true,
        Fields = new()
        {
            new FieldMeta { Name = "id", ClrType = typeof(int), ClrTypeName = "Int32", DbType = "int", IsPrimary = true, IsAutoIncrement = true },
            new FieldMeta { Name = "name", ClrType = typeof(string), ClrTypeName = "String", DbType = "varchar", Length = 100 }
        }
    };

    [Fact]
    public void ResolveAccessMap_normalizes_role_first_shorthand()
    {
        var config = new ShapeConfig
        {
            AccessByRole = new()
            {
                ["admin"] = new() { "orders", "ledger" },
                ["accountant"] = new() { "ledger" }
            }
        };

        var map = ShapeApp.ResolveAccessMap(config);

        Assert.Equal(2, map.Count);
        Assert.Equal(new[] { "admin" }, map["orders"]);
        Assert.Equal(new[] { "admin", "accountant" }, map["ledger"]);
    }

    [Fact]
    public void ResolveAccessMap_merges_entity_first_and_role_first()
    {
        var config = new ShapeConfig
        {
            Access = new()
            {
                ["orders"] = new() { "user" }
            },
            AccessByRole = new()
            {
                ["admin"] = new() { "orders", "ledger" }
            }
        };

        var map = ShapeApp.ResolveAccessMap(config);

        Assert.Equal(new[] { "user", "admin" }, map["orders"]);
        Assert.Equal(new[] { "admin" }, map["ledger"]);
    }

    [Fact]
    public void MaskGenerator_skips_tables_not_in_access_map()
    {
        var gen = new MaskGenerator();
        var entities = new List<EntityMeta>
        {
            MakeMeta<Order>("orders"),
            MakeMeta<Ledger>("ledger"),
            MakeMeta<Customer>("customers")
        };
        var access = new Dictionary<string, List<string>>
        {
            ["orders"] = new() { "admin" }
        };

        var masks = gen.Generate(entities, access);

        Assert.Contains("orders.list", masks.Keys);
        Assert.Contains("orders", masks.Keys); // writable
        Assert.DoesNotContain("ledger.list", masks.Keys);
        Assert.DoesNotContain("customers.list", masks.Keys);
    }

    [Fact]
    public void MaskGenerator_stamps_access_roles_on_every_generated_mask()
    {
        var gen = new MaskGenerator();
        var entities = new List<EntityMeta>
        {
            new()
            {
                ClrType = typeof(Order),
                Table = "orders",
                Writable = true,
                Fields = new()
                {
                    new FieldMeta { Name = "id", ClrType = typeof(int), ClrTypeName = "Int32", DbType = "int", IsPrimary = true },
                    new FieldMeta { Name = "title", ClrType = typeof(string), ClrTypeName = "String", DbType = "varchar", Length = 100 }
                },
                ReadRoles = new() { "ignored_read" },
                WriteRoles = new() { "ignored_write" }
            }
        };
        var access = new Dictionary<string, List<string>>
        {
            ["orders"] = new() { "admin", "ucetni" }
        };

        var masks = gen.Generate(entities, access);

        Assert.Equal(new[] { "admin", "ucetni" }, masks["orders.list"].Roles);
        Assert.Equal(new[] { "admin", "ucetni" }, masks["orders.get"].Roles);
        Assert.Equal(new[] { "admin", "ucetni" }, masks["orders"].Roles); // writable inherits same roles
    }

    [Fact]
    public void MaskGenerator_with_null_access_falls_back_to_entity_meta_roles()
    {
        var gen = new MaskGenerator();
        var entities = new List<EntityMeta>
        {
            new()
            {
                ClrType = typeof(Order),
                Table = "orders",
                Writable = true,
                Fields = new()
                {
                    new FieldMeta { Name = "id", ClrType = typeof(int), ClrTypeName = "Int32", DbType = "int", IsPrimary = true }
                },
                ReadRoles = new() { "user" },
                WriteRoles = new() { "admin" }
            }
        };

        var masks = gen.Generate(entities, accessMap: null);

        Assert.Equal(new[] { "user" }, masks["orders.list"].Roles);
        Assert.Equal(new[] { "admin" }, masks["orders"].Roles);
    }

    [Fact]
    public void MaskGenerator_empty_role_list_locks_table_to_no_callers()
    {
        // An entry with an empty role list is a misconfiguration that we surface
        // as "mask exists but no role can hit it" rather than silently opening it.
        var gen = new MaskGenerator();
        var entities = new List<EntityMeta> { MakeMeta<Order>("orders") };
        var access = new Dictionary<string, List<string>>
        {
            ["orders"] = new()
        };

        var masks = gen.Generate(entities, access);

        Assert.Contains("orders.list", masks.Keys);
        Assert.Empty(masks["orders.list"].Roles!);
    }
}
