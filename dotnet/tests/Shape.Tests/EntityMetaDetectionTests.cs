using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

public sealed class EntityMetaDetectionTests
{
    private static FieldMeta F(string name, string dbType, bool nullable = false) => new()
    {
        Name = name,
        ClrType = typeof(object),
        ClrTypeName = "Object",
        DbType = dbType,
        Nullable = nullable
    };

    [Fact]
    public void DetectSoftDeleteColumn_picks_nullable_deleted_at_timestamp()
    {
        var col = EntityMeta.DetectSoftDeleteColumn(new[]
        {
            F("id", "int"),
            F("deleted_at", "timestamp", nullable: true)
        });
        Assert.Equal("deleted_at", col);
    }

    [Fact]
    public void DetectSoftDeleteColumn_rejects_non_nullable()
    {
        var col = EntityMeta.DetectSoftDeleteColumn(new[]
        {
            F("deleted_at", "timestamp", nullable: false)
        });
        Assert.Null(col);
    }

    [Fact]
    public void DetectSoftDeleteColumn_rejects_wrong_type()
    {
        var col = EntityMeta.DetectSoftDeleteColumn(new[]
        {
            F("deleted_at", "varchar", nullable: true)
        });
        Assert.Null(col);
    }

    [Fact]
    public void DetectSoftDeleteColumn_rejects_wrong_name()
    {
        var col = EntityMeta.DetectSoftDeleteColumn(new[]
        {
            F("deleted", "timestamp", nullable: true)
        });
        Assert.Null(col);
    }

    [Fact]
    public void DetectSoftDeleteColumn_accepts_datetime()
    {
        var col = EntityMeta.DetectSoftDeleteColumn(new[]
        {
            F("deleted_at", "datetime", nullable: true)
        });
        Assert.Equal("deleted_at", col);
    }

    [Fact]
    public void DetectAuditColumns_picks_created_updated_timestamp_pairs()
    {
        var audit = EntityMeta.DetectAuditColumns(new[]
        {
            F("id", "int"),
            F("created_at", "timestamp"),
            F("updated_at", "datetime")
        });

        Assert.Equal("timestamp", audit["created_at"]);
        Assert.Equal("timestamp", audit["updated_at"]);
    }

    [Fact]
    public void DetectAuditColumns_picks_user_id_pairs()
    {
        var audit = EntityMeta.DetectAuditColumns(new[]
        {
            F("created_by", "int"),
            F("updated_by", "varchar")
        });

        Assert.Equal("user_id", audit["created_by"]);
        Assert.Equal("user_id", audit["updated_by"]);
    }

    [Fact]
    public void DetectAuditColumns_ignores_unrelated_columns()
    {
        var audit = EntityMeta.DetectAuditColumns(new[]
        {
            F("title", "varchar"),
            F("created", "timestamp"),  // wrong name
            F("created_at", "varchar")  // wrong type
        });

        Assert.Empty(audit);
    }

    [Fact]
    public void DetectAuditColumns_empty_when_none_match()
    {
        Assert.Empty(EntityMeta.DetectAuditColumns(new[] { F("id", "int") }));
    }
}
