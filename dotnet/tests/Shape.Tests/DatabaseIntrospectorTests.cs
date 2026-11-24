using Shape.Core;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

public sealed class DatabaseIntrospectorTests
{
    private static Database NewSqlite(Action<Database> seed)
    {
        var db = new Database(new DatabaseConfig
        {
            Driver = "sqlite",
            ConnectionString = "Data Source=:memory:"
        });
        seed(db);
        return db;
    }

    [Fact]
    public void Lists_user_tables_excluding_sqlite_internals()
    {
        using var db = NewSqlite(d =>
        {
            d.Exec("CREATE TABLE orders (id INTEGER PRIMARY KEY, total REAL)", new());
            d.Exec("CREATE TABLE items (id INTEGER PRIMARY KEY, order_id INTEGER REFERENCES orders(id))", new());
        });
        var ix = new DatabaseIntrospector(db);

        var tables = ix.ListTables();

        Assert.Contains("orders", tables);
        Assert.Contains("items", tables);
        Assert.DoesNotContain(tables, t => t.StartsWith("sqlite_"));
    }

    [Fact]
    public void Describes_columns_with_types_nullability_and_pk()
    {
        using var db = NewSqlite(d => d.Exec(
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, total REAL NOT NULL, note TEXT)",
            new()));
        var ix = new DatabaseIntrospector(db);

        var cols = ix.DescribeColumns("orders");
        var byName = cols.ToDictionary(c => c.Name);

        Assert.True(byName["id"].IsPrimary);
        Assert.True(byName["id"].AutoIncrement);
        Assert.False(byName["total"].Nullable);
        Assert.True(byName["note"].Nullable);
    }

    [Fact]
    public void Describes_foreign_keys()
    {
        using var db = NewSqlite(d =>
        {
            d.Exec("CREATE TABLE orders (id INTEGER PRIMARY KEY)", new());
            d.Exec("CREATE TABLE items (id INTEGER PRIMARY KEY, order_id INTEGER NOT NULL REFERENCES orders(id))", new());
        });
        var ix = new DatabaseIntrospector(db);

        var fks = ix.DescribeForeignKeys("items");

        Assert.True(fks.ContainsKey("order_id"));
        Assert.Equal("orders", fks["order_id"].ReferencedTable);
        Assert.Equal("id", fks["order_id"].ReferencedColumn);
    }

    [Fact]
    public void Introspect_produces_EntityMeta_with_softDelete_detection()
    {
        using var db = NewSqlite(d => d.Exec(
            "CREATE TABLE orders (id INTEGER PRIMARY KEY, title TEXT NOT NULL, deleted_at TIMESTAMP NULL)",
            new()));
        var ix = new DatabaseIntrospector(db);

        var entities = ix.Introspect();
        var orders = entities.Single(e => e.Table == "orders");

        Assert.Equal("deleted_at", orders.SoftDeleteColumn);
    }

    [Fact]
    public void Introspect_detects_audit_columns()
    {
        using var db = NewSqlite(d => d.Exec(@"
            CREATE TABLE orders (
              id INTEGER PRIMARY KEY,
              created_at TIMESTAMP,
              updated_at TIMESTAMP,
              created_by INT,
              updated_by INT
            )", new()));
        var ix = new DatabaseIntrospector(db);

        var entities = ix.Introspect();
        var orders = entities.Single(e => e.Table == "orders");

        Assert.Equal("timestamp", orders.AuditColumns["created_at"]);
        Assert.Equal("user_id", orders.AuditColumns["created_by"]);
        Assert.Equal("user_id", orders.AuditColumns["updated_by"]);
    }

    [Fact]
    public void Introspect_honors_excludeTables()
    {
        using var db = NewSqlite(d =>
        {
            d.Exec("CREATE TABLE orders (id INTEGER PRIMARY KEY)", new());
            d.Exec("CREATE TABLE secrets (id INTEGER PRIMARY KEY)", new());
        });
        var ix = new DatabaseIntrospector(db);

        var entities = ix.Introspect(excludeTables: new[] { "secrets" });

        Assert.Contains(entities, e => e.Table == "orders");
        Assert.DoesNotContain(entities, e => e.Table == "secrets");
    }

    [Fact]
    public void AsTableSchemas_returns_table_to_columns_map()
    {
        using var db = NewSqlite(d => d.Exec(
            "CREATE TABLE users (id INTEGER PRIMARY KEY, email TEXT, password_hash TEXT)",
            new()));
        var ix = new DatabaseIntrospector(db);

        var schemas = ix.AsTableSchemas();
        var users = schemas.Single(s => s.Table == "users");

        Assert.Equal(new[] { "id", "email", "password_hash" }, users.Columns);
    }
}
