using Shape.Audit;
using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class AuditLoggerTests
{
    private static (Database Database, AuditLogger Logger) Build()
    {
        var db = new Database(new DatabaseConfig
        {
            Driver = "sqlite",
            ConnectionString = "Data Source=:memory:"
        });
        db.Exec(@"CREATE TABLE audit_log (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            occurred_at TEXT NOT NULL,
            actor_id TEXT,
            actor_email TEXT,
            actor_role TEXT,
            table_name TEXT NOT NULL,
            row_id TEXT,
            action TEXT NOT NULL,
            changed TEXT)", new());
        return (db, new AuditLogger(db));
    }

    [Fact]
    public void Log_writes_a_row_with_actor_id_email_role()
    {
        var (db, logger) = Build();
        try
        {
            var auth = new Dictionary<string, object?>
            {
                ["id"] = 7,
                ["email"] = "alice@corp.com",
                ["role"] = "admin"
            };
            logger.Log("orders", "insert", "42", auth, new List<string> { "title", "total" });

            var rows = db.Run("SELECT actor_id, actor_email, actor_role, table_name, row_id, action, changed FROM audit_log", new()).Rows;
            Assert.Single(rows);
            Assert.Equal("7", rows[0][0]?.ToString());
            Assert.Equal("alice@corp.com", rows[0][1]);
            Assert.Equal("admin", rows[0][2]);
            Assert.Equal("orders", rows[0][3]);
            Assert.Equal("42", rows[0][4]);
            Assert.Equal("insert", rows[0][5]);
            Assert.Contains("title", (string)rows[0][6]!);
        }
        finally { db.Dispose(); }
    }

    [Fact]
    public void Delete_action_records_no_changed_columns()
    {
        var (db, logger) = Build();
        try
        {
            logger.Log("orders", "delete", "42", new Dictionary<string, object?>(), new List<string>());

            var rows = db.Run("SELECT changed FROM audit_log", new()).Rows;
            Assert.Single(rows);
            Assert.Null(rows[0][0]);
        }
        finally { db.Dispose(); }
    }

    [Fact]
    public void Anonymous_caller_logs_null_actor_fields()
    {
        var (db, logger) = Build();
        try
        {
            logger.Log("orders", "update", null, new Dictionary<string, object?>(), new List<string> { "title" });

            var rows = db.Run("SELECT actor_id, actor_email, row_id FROM audit_log", new()).Rows;
            Assert.Single(rows);
            Assert.Null(rows[0][0]);
            Assert.Null(rows[0][1]);
            Assert.Null(rows[0][2]);
        }
        finally { db.Dispose(); }
    }

    [Fact]
    public void Falls_back_to_oid_then_sub_for_actor_id()
    {
        var (db, logger) = Build();
        try
        {
            logger.Log("t", "insert", null, new Dictionary<string, object?> { ["sub"] = "user-99" }, new());

            var rows = db.Run("SELECT actor_id FROM audit_log", new()).Rows;
            Assert.Equal("user-99", rows[0][0]);
        }
        finally { db.Dispose(); }
    }
}
