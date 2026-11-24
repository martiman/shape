using Shape;
using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class ShapeAppFromDatabaseTests
{
    [Fact]
    public void FromDatabase_against_sqlite_generates_masks_admin_masks_and_audit_wiring()
    {
        // Build a fresh in-memory DB, persist a connection so the schema survives,
        // and point ShapeConfig at it via a shared cache. SQLite's :memory:
        // database is per-connection — we need one with file: URI to share.
        var dbFile = Path.Combine(Path.GetTempPath(), "shape_app_test_" + Guid.NewGuid().ToString("N") + ".db");
        var connectionString = $"Data Source={dbFile}";

        try
        {
            using (var bootstrap = new Database(new DatabaseConfig { Driver = "sqlite", ConnectionString = connectionString }))
            {
                bootstrap.Exec(@"CREATE TABLE orders (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  title TEXT NOT NULL,
                  total REAL NOT NULL,
                  created_at TIMESTAMP,
                  deleted_at TIMESTAMP NULL
                )", new());
                bootstrap.Exec(@"CREATE TABLE users (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  email TEXT,
                  password_hash TEXT,
                  role TEXT
                )", new());
                bootstrap.Exec(@"CREATE TABLE audit_log (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  occurred_at TEXT NOT NULL,
                  actor_id TEXT,
                  actor_email TEXT,
                  actor_role TEXT,
                  table_name TEXT NOT NULL,
                  row_id TEXT,
                  action TEXT NOT NULL,
                  changed TEXT
                )", new());
            }

            var config = ShapeApp.FromDatabase(new ShapeConfig
            {
                Db = new DatabaseConfig { Driver = "sqlite", ConnectionString = connectionString },
                AdminMasks = true,
                Audit = true,
                SecretTables = new() { "users", "audit_log" },
                DefaultListLimit = 50
            });

            // Domain entity (orders) got auto-CRUD masks
            Assert.Contains("orders.list", config.Masks);
            Assert.Contains("orders.get", config.Masks);

            // soft-delete + audit detected on orders
            var ordersEntity = config.Runtime.Entities.Single(e => e.Table == "orders");
            Assert.Equal("deleted_at", ordersEntity.SoftDeleteColumn);
            Assert.Equal("timestamp", ordersEntity.AuditColumns["created_at"]);

            // Secret tables NOT in auto-CRUD
            Assert.DoesNotContain("users.list", config.Masks);
            Assert.DoesNotContain("audit_log.list", config.Masks);

            // Admin masks generated for users (has id) — adminList + adminGet
            Assert.Contains("users.adminList", config.Masks);
            Assert.Contains("users.adminGet", config.Masks);
            // password_hash stripped from the admin mask SQL
            Assert.DoesNotContain("password_hash", config.Masks["users.adminList"].Sql);

            // audit_log table detected
            Assert.Equal("audit_log", config.Runtime.AuditLogTable);

            // DefaultListLimit propagated
            Assert.Equal(50, config.Masks["orders.list"].DefaultLimit);
        }
        finally
        {
            try { File.Delete(dbFile); } catch { }
        }
    }
}
