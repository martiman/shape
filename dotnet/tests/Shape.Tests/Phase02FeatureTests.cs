using Shape;
using Shape.Attributes;
using Shape.Auth;
using Shape.Core;
using Shape.Generator;
using Shape.Http;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

// ── Test entities ─────────────────────────────────────────────────────

[Entity("authors", Writable = true)]
public sealed class TestAuthor02
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    [Column(Length = 100)]
    public string Name { get; set; } = "";
}

[Entity("books", Writable = true)]
public sealed class TestBook02
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    [Fk(typeof(TestAuthor02))]
    public int AuthorId { get; set; }

    [Column(Length = 200)]
    public string Title { get; set; } = "";

    [Column(Type = "enum", Values = new[] { "draft", "published", "archived" })]
    public string Status { get; set; } = "draft";

    public decimal Price { get; set; }
}

public sealed class EnumColumnTests
{
    [Fact]
    public void Enum_values_propagate_into_FieldMeta()
    {
        var entities = new EntityReader().ReadTypes(new[] { typeof(TestBook02) });
        var status = entities.Single().Fields.First(f => f.Name == "status");

        Assert.NotNull(status.EnumValues);
        Assert.Equal(new[] { "draft", "published", "archived" }, status.EnumValues);
    }

    [Fact]
    public void ValidatorGenerator_emits_one_of_check_for_enum()
    {
        var entities = new EntityReader().ReadTypes(new[] { typeof(TestBook02) });
        var validators = new ValidatorGenerator().Generate(entities);
        var fn = validators["books.insert"].Validators["status"];

        Assert.Null(fn("draft"));
        Assert.Null(fn("published"));
        Assert.Null(fn(null));
        var error = fn("nonexistent");
        Assert.NotNull(error);
        Assert.Contains("one of:", error);
    }

    [Fact]
    public void MaskGenerator_emits_byStatus_for_enum_column()
    {
        var entities = new EntityReader().ReadTypes(new[] { typeof(TestAuthor02), typeof(TestBook02) });
        var masks = new MaskGenerator().Generate(entities);

        Assert.Contains("books.byStatus", masks);
        var mask = masks["books.byStatus"];
        Assert.Contains("status", mask.Description);
        Assert.True(mask.Params!.ContainsKey("status"));
    }
}

public sealed class CombinedFilterMaskTests
{
    [Fact]
    public void byAuthorIdAndStatus_emitted_when_both_filterable_present()
    {
        var entities = new EntityReader().ReadTypes(new[] { typeof(TestAuthor02), typeof(TestBook02) });
        var masks = new MaskGenerator().Generate(entities);

        Assert.Contains("books.byAuthorIdAndStatus", masks);
        var mask = masks["books.byAuthorIdAndStatus"];
        Assert.Equal(2, mask.Params!.Count);
        Assert.True(mask.Params.ContainsKey("author_id"));
        Assert.True(mask.Params.ContainsKey("status"));
    }
}

public sealed class WithRelationMaskTests
{
    [Fact]
    public void withAuthor_mask_emits_JOIN_with_prefixed_columns()
    {
        var entities = new EntityReader().ReadTypes(new[] { typeof(TestAuthor02), typeof(TestBook02) });
        var masks = new MaskGenerator().Generate(entities);

        Assert.Contains("books.withAuthor", masks);
        var sql = masks["books.withAuthor"].Sql;
        Assert.Contains("INNER JOIN", sql);
        Assert.Contains("authors", sql);
        Assert.Contains("author_name", sql); // parent.name aliased as author_name
    }
}

public sealed class FkExistenceCheckTests
{
    [Fact]
    public void Writable_mask_carries_FkChecks_pointing_at_parent_table()
    {
        var entities = new EntityReader().ReadTypes(new[] { typeof(TestAuthor02), typeof(TestBook02) });
        var masks = new MaskGenerator().Generate(entities);

        var book = masks["books"];
        Assert.NotNull(book.FkChecks);
        Assert.True(book.FkChecks!.ContainsKey("author_id"));
        Assert.Equal("authors", book.FkChecks["author_id"].Table);
        Assert.Equal("id", book.FkChecks["author_id"].PrimaryKey);
    }

    [Fact]
    public void FK_violation_throws_typed_error_against_sqlite()
    {
        using var db = new Database(new DatabaseConfig { Driver = "sqlite", ConnectionString = "Data Source=:memory:" });
        db.Exec("CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT)", new());
        db.Exec("CREATE TABLE books (id INTEGER PRIMARY KEY, author_id INTEGER, title TEXT)", new());
        db.Exec("INSERT INTO authors (id, name) VALUES (1, 'Alice')", new());

        var entities = new EntityReader().ReadTypes(new[] { typeof(TestAuthor02), typeof(TestBook02) });
        var maskConfigs = new MaskGenerator(driver: "sqlite").Generate(entities);
        var registry = new MaskRegistry(maskConfigs);
        var executor = new BatchExecutor(db, registry, new ValidatorRegistry());

        var ex = Assert.Throws<ApiException>(() =>
            executor.ExecuteMutations(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["table"] = "books",
                    ["action"] = "insert",
                    ["data"] = new Dictionary<string, object?> { ["author_id"] = 9999, ["title"] = "X", ["status"] = "draft", ["price"] = 1.0 }
                }
            }));

        Assert.Equal("FK_VIOLATION", ex.ApiCode);
        Assert.Contains("authors.id", ex.Message);
    }

    [Fact]
    public void FK_existence_satisfied_for_real_parent()
    {
        using var db = new Database(new DatabaseConfig { Driver = "sqlite", ConnectionString = "Data Source=:memory:" });
        db.Exec("CREATE TABLE authors (id INTEGER PRIMARY KEY, name TEXT)", new());
        db.Exec("CREATE TABLE books (id INTEGER PRIMARY KEY AUTOINCREMENT, author_id INTEGER, title TEXT, status TEXT, price REAL)", new());
        db.Exec("INSERT INTO authors (id, name) VALUES (1, 'Alice')", new());

        var entities = new EntityReader().ReadTypes(new[] { typeof(TestAuthor02), typeof(TestBook02) });
        var maskConfigs = new MaskGenerator(driver: "sqlite").Generate(entities);
        var registry = new MaskRegistry(maskConfigs);
        var executor = new BatchExecutor(db, registry, new ValidatorRegistry());

        var result = executor.ExecuteMutations(new List<Dictionary<string, object?>>
        {
            new()
            {
                ["table"] = "books",
                ["action"] = "insert",
                ["data"] = new Dictionary<string, object?> { ["author_id"] = 1, ["title"] = "Kindred", ["status"] = "draft", ["price"] = 14.0 }
            }
        });

        var first = (result["results"] as List<Dictionary<string, object?>>)![0];
        Assert.Equal(1, first["affected"]);
    }
}

// ── Hook attribute tests ─────────────────────────────────────────────

public static class HookCounters02
{
    public static int Before;
    public static int After;
    public static void Reset() { Before = 0; After = 0; }
}

[Entity("widgets", Writable = true)]
public sealed class TestWidget02
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    [Column(Length = 50)]
    public string Name { get; set; } = "";

    [BeforeInsert]
    public static void OnBeforeInsert(Dictionary<string, object?> ctx) => HookCounters02.Before++;

    [AfterInsert]
    public static void OnAfterInsert() => HookCounters02.After++;
}

public sealed class HookAttributeTests
{
    [Fact]
    public void EntityReader_picks_up_Before_and_After_hooks()
    {
        var entities = new EntityReader().ReadTypes(new[] { typeof(TestWidget02) });
        var meta = entities.Single();

        Assert.Single(meta.Hooks["before-insert"]);
        Assert.Single(meta.Hooks["after-insert"]);
    }

    [Fact]
    public void Hooks_fire_through_FromAssembly_pipeline()
    {
        HookCounters02.Reset();
        using var db = new Database(new DatabaseConfig { Driver = "sqlite", ConnectionString = "Data Source=:memory:" });
        db.Exec("CREATE TABLE widgets (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT)", new());

        var config = ShapeApp.FromTypes(new[] { typeof(TestWidget02) }, new ShapeConfig
        {
            Db = new DatabaseConfig { Driver = "sqlite", ConnectionString = db.GetType().GetField("_connection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(db)
                ?.GetType().GetProperty("ConnectionString")?.GetValue(db.GetType().GetField("_connection",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(db)) as string ?? "Data Source=:memory:" },
            Auth = new AuthConfig { Driver = "none" }
        });

        // Direct executor path so we don't deal with HTTP.
        var registry = new MaskRegistry(config.Masks);
        var validators = new ValidatorRegistry(config.Validators);
        var executor = new BatchExecutor(db, registry, validators);

        executor.ExecuteMutations(new List<Dictionary<string, object?>>
        {
            new()
            {
                ["table"] = "widgets",
                ["action"] = "insert",
                ["data"] = new Dictionary<string, object?> { ["name"] = "x" }
            }
        });

        Assert.Equal(1, HookCounters02.Before);
        Assert.Equal(1, HookCounters02.After);
    }
}

// ── ODataFilter tests ────────────────────────────────────────────────

public sealed class ODataFilterTests
{
    private static string Quote(string c) => "`" + c + "`";
    private static readonly string[] Cols = { "id", "name", "status", "price", "author_id" };

    [Fact]
    public void Equality_int_and_string_compile_to_parameterised_sql()
    {
        var (sql, ps) = ODataFilter.Compile("status eq 'paid'", Cols, Quote);
        Assert.Contains("`status` =", sql);
        Assert.Single(ps.Values.OfType<string>());
        Assert.Equal("paid", ps.Values.First());

        (sql, ps) = ODataFilter.Compile("id eq 7", Cols, Quote);
        Assert.Contains("`id` =", sql);
        Assert.Equal(7L, ps.Values.First());
    }

    [Fact]
    public void Null_and_not_null_compile_to_IS_NULL_idioms()
    {
        var (sql, _) = ODataFilter.Compile("name eq null", Cols, Quote);
        Assert.Contains("`name` IS NULL", sql);

        (sql, _) = ODataFilter.Compile("name ne null", Cols, Quote);
        Assert.Contains("`name` IS NOT NULL", sql);
    }

    [Fact]
    public void Comparison_operators_map()
    {
        Assert.Contains(" > ", ODataFilter.Compile("price gt 10", Cols, Quote).Sql);
        Assert.Contains(" >= ", ODataFilter.Compile("price ge 10", Cols, Quote).Sql);
        Assert.Contains(" < ", ODataFilter.Compile("price lt 10", Cols, Quote).Sql);
        Assert.Contains(" <= ", ODataFilter.Compile("price le 10", Cols, Quote).Sql);
        Assert.Contains(" <> ", ODataFilter.Compile("status ne 'draft'", Cols, Quote).Sql);
    }

    [Fact]
    public void Contains_startswith_endswith_compile_to_LIKE()
    {
        var (sql, ps) = ODataFilter.Compile("contains(name, 'foo')", Cols, Quote);
        Assert.Contains("`name` LIKE", sql);
        Assert.Equal("%foo%", ps.Values.First());

        (sql, ps) = ODataFilter.Compile("startswith(name, 'foo')", Cols, Quote);
        Assert.Equal("foo%", ps.Values.First());

        (sql, ps) = ODataFilter.Compile("endswith(name, 'foo')", Cols, Quote);
        Assert.Equal("%foo", ps.Values.First());
    }

    [Fact]
    public void And_or_not_compose_with_correct_precedence()
    {
        var (sql, _) = ODataFilter.Compile(
            "status eq 'paid' and price gt 10", Cols, Quote);
        Assert.Contains(" AND ", sql);

        (sql, _) = ODataFilter.Compile(
            "status eq 'paid' or status eq 'shipped'", Cols, Quote);
        Assert.Contains(" OR ", sql);

        (sql, _) = ODataFilter.Compile("not status eq 'draft'", Cols, Quote);
        Assert.Contains("NOT (", sql);
    }

    [Fact]
    public void Disallowed_column_throws_with_expected_list()
    {
        var ex = Assert.Throws<ApiException>(() =>
            ODataFilter.Compile("forbidden_col eq 1", Cols, Quote));
        Assert.Equal("ODATA_FILTER_INVALID", ex.ApiCode);
        Assert.Contains("not allowed", ex.Message);
    }

    [Fact]
    public void Quoted_string_with_escaped_apostrophe_round_trips()
    {
        var (_, ps) = ODataFilter.Compile("name eq 'O''Brien'", Cols, Quote);
        Assert.Equal("O'Brien", ps.Values.First());
    }

    [Fact]
    public void Unsupported_function_rejected()
    {
        var ex = Assert.Throws<ApiException>(() =>
            ODataFilter.Compile("madeup(name, 'x')", Cols, Quote));
        Assert.Equal("ODATA_FILTER_INVALID", ex.ApiCode);
    }

    [Fact]
    public void Like_metacharacters_escaped()
    {
        var (_, ps) = ODataFilter.Compile("contains(name, '50%_off')", Cols, Quote);
        Assert.Equal(@"%50\%\_off%", ps.Values.First());
    }
}
