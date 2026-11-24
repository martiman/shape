using Shape.Core;
using Shape.Generator;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

public sealed class ManifestGeneratorPhase2Tests
{
    private sealed class Order { }

    private static EntityMeta OrderEntity() => new()
    {
        ClrType = typeof(Order),
        Table = "orders",
        Writable = true,
        Fields = new()
        {
            new FieldMeta { Name = "id", ClrType = typeof(int), ClrTypeName = "Int32", DbType = "int", IsPrimary = true }
        },
        SoftDeleteColumn = "deleted_at",
        AuditColumns = new() { ["created_at"] = "timestamp" }
    };

    [Fact]
    public void Entity_emits_softDelete_and_audit()
    {
        var gen = new ManifestGenerator();
        var result = gen.Generate(new[] { OrderEntity() }, new Dictionary<string, MaskConfig>(), Array.Empty<string>());

        var entities = (List<Dictionary<string, object?>>)result["entities"]!;
        var entity = entities[0];

        Assert.Equal("deleted_at", entity["softDelete"]);
        var audit = (Dictionary<string, object?>)entity["audit"]!;
        Assert.Equal("timestamp", audit["created_at"]);
    }

    [Fact]
    public void Mask_summary_emits_description_and_defaultLimit()
    {
        var gen = new ManifestGenerator();
        var masks = new Dictionary<string, MaskConfig>
        {
            ["orders.list"] = new()
            {
                Sql = "SELECT 1",
                Description = "List orders",
                DefaultLimit = 50
            }
        };

        var result = gen.Generate(new[] { OrderEntity() }, masks, Array.Empty<string>());
        var entities = (List<Dictionary<string, object?>>)result["entities"]!;
        var entityMasks = (Dictionary<string, object?>)entities[0]["masks"]!;
        var summary = (Dictionary<string, object?>)entityMasks["orders.list"]!;

        Assert.Equal("List orders", summary["description"]);
        Assert.Equal(50, summary["defaultLimit"]);
    }

    [Fact]
    public void Uploads_block_present_for_downstream_tooling()
    {
        var gen = new ManifestGenerator();
        var result = gen.Generate(Array.Empty<EntityMeta>(), new Dictionary<string, MaskConfig>(), Array.Empty<string>());

        Assert.True(result.ContainsKey("uploads"));
    }

    [Fact]
    public void Standalone_mask_with_relations_emits_relations_block()
    {
        var gen = new ManifestGenerator();
        var masks = new Dictionary<string, MaskConfig>
        {
            ["custom"] = new()
            {
                Sql = "SELECT 1",
                Writable = true,
                Relations = new() { ["children"] = new RelationMeta { FkColumns = new() { "parent_id" } } }
            }
        };

        var result = gen.Generate(Array.Empty<EntityMeta>(), masks, Array.Empty<string>());
        var standalone = (Dictionary<string, object?>)result["masks"]!;
        var summary = (Dictionary<string, object?>)standalone["custom"]!;
        var rel = (Dictionary<string, object?>)summary["relations"]!;
        var children = (Dictionary<string, object?>)rel["children"]!;
        var fkColumns = (List<string>)children["fk_columns"]!;

        Assert.Equal("parent_id", fkColumns[0]);
    }
}
