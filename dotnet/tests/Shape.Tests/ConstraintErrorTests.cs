using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class ConstraintErrorTests
{
    private static Database Build()
    {
        var db = new Database(new DatabaseConfig { Driver = "sqlite", ConnectionString = "Data Source=:memory:" });
        db.Exec("PRAGMA foreign_keys = ON", new());
        db.Exec("CREATE TABLE parent (id INTEGER PRIMARY KEY, email TEXT NOT NULL UNIQUE)", new());
        db.Exec("CREATE TABLE child (id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL REFERENCES parent(id))", new());
        db.Exec("INSERT INTO parent (id, email) VALUES (1, 'a@x.test')", new());
        return db;
    }

    private static ApiException Fail(Database db, string sql)
        => Assert.Throws<ApiException>(() => db.Exec(sql, new()));

    [Theory]
    [InlineData("INSERT INTO parent (id, email) VALUES (2, 'a@x.test')", "UNIQUE_VIOLATION")]
    [InlineData("INSERT INTO child (id, parent_id) VALUES (1, 99)", "FK_VIOLATION")]
    [InlineData("INSERT INTO parent (id, email) VALUES (3, NULL)", "NOT_NULL_VIOLATION")]
    public void Constraint_failures_map_to_409_with_detail_and_hint(string sql, string detail)
    {
        using var db = Build();
        var ex = Fail(db, sql);
        Assert.Equal("CONSTRAINT_VIOLATION", ex.ApiCode);
        Assert.Equal(409, ex.Status);
        Assert.Equal(detail, ex.Details!["detail"]);
        Assert.False(string.IsNullOrEmpty((string?)ex.Details["hint"]));
    }

    [Fact]
    public void Other_failures_stay_opaque_db_error()
    {
        using var db = Build();
        var ex = Fail(db, "INSERT INTO missing_table (id) VALUES (1)");
        Assert.Equal("DB_ERROR", ex.ApiCode);
        Assert.Equal(500, ex.Status);
    }
}
