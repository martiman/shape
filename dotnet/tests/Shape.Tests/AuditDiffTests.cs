using System.Text.Json;
using Shape;
using Shape.Attributes;
using Shape.Audit;
using Shape.Core;
using Xunit;

namespace Shape.Tests;

[Entity("notes", Writable = true)]
[Access(Roles = new[] { "user", "admin" })]
public sealed class AdNote
{
    [Id][AutoIncrement] public int Id { get; set; }
    public int Grp { get; set; }
    [Column(Length = 50)] public string Title { get; set; } = "";
    [Column(Length = 200)] public string? Body { get; set; }
    [Column(Length = 80)] public string? ApiKey { get; set; }
}

[Entity("other", Writable = true)]
[Access(Roles = new[] { "user", "admin" })]
public sealed class AdOther
{
    [Id][AutoIncrement] public int Id { get; set; }
    [Column(Length = 50)] public string Name { get; set; } = "";
}

/// <summary>Audit Tier 2 (before/after values) — mirror of the PHP AuditDiff.phpt.</summary>
public sealed class AuditDiffTests : IDisposable
{
    private readonly List<string> _files = new();

    private (Database Db, BatchExecutor Ex) Make(bool valueColumns, List<string>? tables, List<string> exclude)
    {
        var file = Path.Combine(Path.GetTempPath(), $"shape-ad-{Guid.NewGuid():N}.db");
        _files.Add(file);
        var dbConfig = new DatabaseConfig { Driver = "sqlite", ConnectionString = $"Data Source={file}" };
        var db = new Database(dbConfig);
        db.Exec("CREATE TABLE notes (id INTEGER PRIMARY KEY AUTOINCREMENT, grp INTEGER NOT NULL, title TEXT NOT NULL, body TEXT NULL, api_key TEXT NULL)", new());
        db.Exec("CREATE TABLE other (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL)", new());
        db.Exec("CREATE TABLE audit_log (id INTEGER PRIMARY KEY AUTOINCREMENT, occurred_at TEXT NOT NULL, actor_id TEXT NULL, actor_email TEXT NULL, actor_role TEXT NULL, " +
                "table_name TEXT NOT NULL, row_id TEXT NULL, action TEXT NOT NULL, changed TEXT NULL" + (valueColumns ? ", old_values TEXT NULL, new_values TEXT NULL" : "") + ")", new());

        var config = ShapeApp.FromTypes(new[] { typeof(AdNote), typeof(AdOther) }, new ShapeConfig
        {
            Db = dbConfig,
            Access = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["notes"] = new() { "user", "admin" },
                ["other"] = new() { "user", "admin" }
            }
        });
        var ex = new BatchExecutor(db, new MaskRegistry(config.Masks), new ValidatorRegistry());
        var logger = new AuditLogger(db, "audit_log", withValues: true);
        ex.SetAuditLogger(logger.Log);
        ex.SetAuditDiff(tables, exclude, logger.LogWithValues, logger.AssertValueColumns);
        return (db, ex);
    }

    public void Dispose()
    {
        foreach (var f in _files) { try { File.Delete(f); } catch { } }
    }

    private static readonly Dictionary<string, object?> Ctx = new() { ["id"] = 7, ["role"] = "user", ["email"] = "u@x" };

    private static Dictionary<string, object?> Mut(BatchExecutor ex, Dictionary<string, object?> m)
    {
        m["ref"] = "m";
        var res = ex.ExecuteMutations(new List<Dictionary<string, object?>> { m }, Ctx);
        return ((List<Dictionary<string, object?>>)res["results"]!)[0];
    }

    private sealed record Entry(string Table, string? RowId, string Action, JsonElement? Old, JsonElement? New);

    private static List<Entry> Audit(Database db)
    {
        var r = db.Run("SELECT table_name, row_id, action, old_values, new_values FROM audit_log ORDER BY id", new());
        return r.Rows.Select(row => new Entry(
            Convert.ToString(row[0])!,
            row[1] == null ? null : Convert.ToString(row[1]),
            Convert.ToString(row[2])!,
            row[3] == null ? null : JsonDocument.Parse(Convert.ToString(row[3])!).RootElement.Clone(),
            row[4] == null ? null : JsonDocument.Parse(Convert.ToString(row[4])!).RootElement.Clone())).ToList();
    }

    private static Dictionary<string, object?> Data(params (string, object?)[] kv) =>
        kv.ToDictionary(x => x.Item1, x => x.Item2);

    [Fact]
    public void Insert_update_delete_capture_values_with_redaction_and_per_row_entries()
    {
        var (db, ex) = Make(true, null, new() { "notes.body" });

        var id = Convert.ToInt64(Mut(ex, new() { ["table"] = "notes", ["action"] = "insert",
            ["data"] = Data(("grp", 1), ("title", "first"), ("body", "private text"), ("api_key", "sk-abc")) })["id"]);
        var a = Audit(db);
        Assert.Single(a);
        Assert.Equal("insert", a[0].Action);
        Assert.Equal(id.ToString(), a[0].RowId);
        Assert.Null(a[0].Old);
        Assert.Equal("first", a[0].New!.Value.GetProperty("title").GetString());
        Assert.Equal("[redacted]", a[0].New!.Value.GetProperty("api_key").GetString()); // sensitive by name
        Assert.Equal("[redacted]", a[0].New!.Value.GetProperty("body").GetString());          // excluded by config

        // update: only columns that really changed
        Mut(ex, new() { ["table"] = "notes", ["action"] = "update", ["filter"] = Data(("id", id)), ["data"] = Data(("title", "renamed"), ("grp", 1)) });
        a = Audit(db);
        Assert.Equal(2, a.Count);
        Assert.Equal("first", a[1].Old!.Value.GetProperty("title").GetString());
        Assert.Equal("renamed", a[1].New!.Value.GetProperty("title").GetString());
        Assert.False(a[1].Old!.Value.TryGetProperty("grp", out _));
        Assert.Equal(id.ToString(), a[1].RowId);

        // multi-row update by a non-pk filter: one entry per row
        Mut(ex, new() { ["table"] = "notes", ["action"] = "insert", ["data"] = Data(("grp", 2), ("title", "g2a")) });
        Mut(ex, new() { ["table"] = "notes", ["action"] = "insert", ["data"] = Data(("grp", 2), ("title", "g2b")) });
        var before = Audit(db).Count;
        Mut(ex, new() { ["table"] = "notes", ["action"] = "update", ["filter"] = Data(("grp", 2)), ["data"] = Data(("title", "same")) });
        var multi = Audit(db).Skip(before).ToList();
        Assert.Equal(2, multi.Count);
        Assert.NotEqual(multi[0].RowId, multi[1].RowId);
        Assert.Equal(new[] { "g2a", "g2b" }, multi.Select(e => e.Old!.Value.GetProperty("title").GetString()).ToArray());

        // delete: full old row (redacted)
        before = Audit(db).Count;
        Mut(ex, new() { ["table"] = "notes", ["action"] = "delete", ["filter"] = Data(("id", id)) });
        var del = Audit(db).Skip(before).ToList();
        Assert.Single(del);
        Assert.Equal("delete", del[0].Action);
        Assert.Equal("renamed", del[0].Old!.Value.GetProperty("title").GetString());
        Assert.Equal("[redacted]", del[0].Old!.Value.GetProperty("api_key").GetString());
        Assert.Null(del[0].New);

        // touching nothing still logs a Tier-1 entry without values
        before = Audit(db).Count;
        Mut(ex, new() { ["table"] = "notes", ["action"] = "update", ["filter"] = Data(("id", 9999)), ["data"] = Data(("title", "x")) });
        var none = Audit(db).Skip(before).ToList();
        Assert.Single(none);
        Assert.Null(none[0].Old);
        Assert.Null(none[0].New);
    }

    [Fact]
    public void Per_table_opt_in_keeps_tier1_for_unlisted_tables()
    {
        var (db, ex) = Make(true, new() { "notes" }, new());
        Mut(ex, new() { ["table"] = "other", ["action"] = "insert", ["data"] = Data(("name", "plain")) });
        Mut(ex, new() { ["table"] = "notes", ["action"] = "insert", ["data"] = Data(("grp", 1), ("title", "listed")) });
        var a = Audit(db);
        Assert.Null(a[0].New);
        Assert.Equal("listed", a[1].New!.Value.GetProperty("title").GetString());
    }

    [Fact]
    public void Missing_value_columns_fail_before_the_data_changes()
    {
        var (db, ex) = Make(false, null, new());
        var e = Assert.Throws<ApiException>(() =>
            Mut(ex, new() { ["table"] = "notes", ["action"] = "insert", ["data"] = Data(("grp", 1), ("title", "never")) }));
        Assert.True(e.ApiCode == "AUDIT_CONFIG_ERROR", e.ApiCode + ": " + e.Message);
        Assert.Equal(0L, Convert.ToInt64(db.Run("SELECT COUNT(*) FROM notes", new()).Rows[0][0]));
    }
}
