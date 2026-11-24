using Shape.Attributes;
using Shape.Core;
using Shape.Generator;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

public sealed class DescriptionAndConstraintTests
{
    [Entity("orders", Description = "Customer purchase orders, one row per checkout.")]
    [Unique("customer_id", "external_ref")]
    private sealed class OrderEntity
    {
        [Id, AutoIncrement]
        public int Id { get; set; }

        [Column(Description = "External order reference from the upstream payment provider.")]
        public string ExternalRef { get; set; } = "";

        public int CustomerId { get; set; }
    }

    [Entity("event_log", WritableMode = "append_only", Description = "Immutable per-event audit trail.")]
    private sealed class EventLogEntity
    {
        [Id, AutoIncrement]
        public int Id { get; set; }

        public string Kind { get; set; } = "";
    }

    [Fact]
    public void EntityReader_picks_up_attribute_descriptions()
    {
        var entities = new EntityReader().ReadTypes(new[] { typeof(OrderEntity) });
        var meta = entities.Single();

        Assert.Equal("Customer purchase orders, one row per checkout.", meta.Description);
        var ext = meta.Fields.Single(f => f.Name == "external_ref");
        Assert.Equal("External order reference from the upstream payment provider.", ext.Description);
    }

    [Fact]
    public void EntityReader_picks_up_unique_attribute_into_constraints()
    {
        var entities = new EntityReader().ReadTypes(new[] { typeof(OrderEntity) });
        var meta = entities.Single();

        Assert.Single(meta.UniqueConstraints);
        Assert.Equal(new[] { "customer_id", "external_ref" }, meta.UniqueConstraints[0]);
    }

    [Fact]
    public void EntityReader_picks_up_WritableMode()
    {
        var entities = new EntityReader().ReadTypes(new[] { typeof(EventLogEntity) });
        var meta = entities.Single();

        Assert.Equal("append_only", meta.WritableMode);
        Assert.Equal("append_only", meta.EffectiveWritableMode());
    }

    [Fact]
    public void EffectiveWritableMode_falls_back_to_bool()
    {
        var entities = new EntityReader().ReadTypes(new[] { typeof(OrderEntity) });
        var meta = entities.Single();

        // [Entity(...)] without WritableMode + Writable=true (the default) → "full".
        Assert.Null(meta.WritableMode);
        Assert.Equal("full", meta.EffectiveWritableMode());
    }

    [Fact]
    public void MaskGenerator_stamps_writable_mode_on_writable_mask_for_append_only()
    {
        var entities = new EntityReader().ReadTypes(new[] { typeof(EventLogEntity) });
        var masks = new MaskGenerator().Generate(entities);

        // Writable mask still emitted (operator wants INSERT) but tagged.
        var writable = masks["event_log"];
        Assert.True(writable.Writable);
        Assert.Equal("append_only", writable.WritableMode);
    }

    [Fact]
    public void MaskGenerator_omits_writable_mask_for_none_mode()
    {
        var entities = new List<EntityMeta>
        {
            new()
            {
                ClrType = typeof(OrderEntity),
                Table = "orders",
                Writable = false,
                WritableMode = "none",
                Fields = new()
                {
                    new FieldMeta { Name = "id", ClrType = typeof(int), ClrTypeName = "Int32", DbType = "int", IsPrimary = true }
                }
            }
        };

        var masks = new MaskGenerator().Generate(entities);

        Assert.False(masks.ContainsKey("orders"));
        Assert.Contains("orders.list", masks.Keys); // reads still emitted
    }

    [Fact]
    public void Manifest_surfaces_description_unique_and_writableMode()
    {
        var entities = new EntityReader().ReadTypes(new[] { typeof(OrderEntity), typeof(EventLogEntity) });
        var masks = new MaskGenerator().Generate(entities);
        var manifest = new ManifestGenerator().Generate(entities, masks, Array.Empty<string>());

        var entityList = (List<Dictionary<string, object?>>)manifest["entities"]!;
        var orders = entityList.Single(e => (string)e["table"]! == "orders");

        Assert.Equal("Customer purchase orders, one row per checkout.", orders["description"]);
        var unique = (List<List<string>>)orders["uniqueConstraints"]!;
        Assert.Single(unique);

        var fields = (List<Dictionary<string, object?>>)orders["fields"]!;
        var ext = fields.Single(f => (string)f["name"]! == "external_ref");
        Assert.Equal("External order reference from the upstream payment provider.", ext["description"]);

        var eventLog = entityList.Single(e => (string)e["table"]! == "event_log");
        Assert.Equal("append_only", eventLog["writableMode"]);
    }
}
