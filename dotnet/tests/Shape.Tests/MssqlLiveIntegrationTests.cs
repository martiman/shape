using Shape.Core;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

/// <summary>
/// Live SQL Server integration tests — the deferred verification pass for everything
/// marked "code complete — needs live-server verification" in MSSQL-CHECKLIST.md.
/// The fixture is the plan's §6 shared schema: identity PK, FK, composite UNIQUE,
/// bit, nvarchar(100), nvarchar(max), MS_Description — exercising identity detection,
/// unique/FK introspection, description surfacing, type mapping, insert-id, pagination,
/// and unique-violation error codes in one pass.
///
/// Facts no-op (vacuously green) unless SHAPE_MSSQL_DSN is set, so default
/// `dotnet test` needs no Docker. To run for real:
/// <code>
///   docker run -d --name shape-mssql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Shape!Pass1' \
///     -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
///   export SHAPE_MSSQL_DSN='sqlsrv:host=localhost;port=1433;dbname=master'
///   export SHAPE_MSSQL_USER=sa SHAPE_MSSQL_PASSWORD='Shape!Pass1'
///   dotnet test tests/Shape.Tests --filter "db=mssql"
/// </code>
/// </summary>
[Trait("db", "mssql")]
public sealed class MssqlLiveIntegrationTests : IDisposable
{
    private const string Authors = "shape_it_authors";
    private const string Books = "shape_it_books";

    private readonly Database? _db;

    public MssqlLiveIntegrationTests()
    {
        var dsn = Environment.GetEnvironmentVariable("SHAPE_MSSQL_DSN");
        if (string.IsNullOrEmpty(dsn)) return; // no live server — facts no-op

        _db = new Database(DatabaseConfig.FromDsn(
            dsn,
            Environment.GetEnvironmentVariable("SHAPE_MSSQL_USER"),
            Environment.GetEnvironmentVariable("SHAPE_MSSQL_PASSWORD")));
        DropFixture();
        CreateFixture();
    }

    public void Dispose()
    {
        if (_db == null) return;
        try { DropFixture(); } catch { /* best effort */ }
        _db.Dispose();
    }

    private void CreateFixture()
    {
        var none = new Dictionary<string, object?>();
        _db!.Exec($@"
            CREATE TABLE dbo.{Authors} (
              id     int IDENTITY(1,1) NOT NULL CONSTRAINT PK_{Authors} PRIMARY KEY,
              name   nvarchar(100) NOT NULL,
              bio    nvarchar(max) NULL,
              active bit NOT NULL CONSTRAINT DF_{Authors}_active DEFAULT (1)
            )", none);
        _db.Exec($@"
            CREATE TABLE dbo.{Books} (
              id        int IDENTITY(1,1) NOT NULL CONSTRAINT PK_{Books} PRIMARY KEY,
              author_id int NOT NULL CONSTRAINT FK_{Books}_author REFERENCES dbo.{Authors}(id),
              title     nvarchar(100) NOT NULL,
              published datetime2 NULL,
              CONSTRAINT UQ_{Books}_author_title UNIQUE (author_id, title)
            )", none);
        _db.Exec($@"
            EXEC sp_addextendedproperty @name=N'MS_Description', @value=N'Authors fixture table',
              @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'{Authors}'", none);
        _db.Exec($@"
            EXEC sp_addextendedproperty @name=N'MS_Description', @value=N'Display name',
              @level0type=N'SCHEMA', @level0name=N'dbo', @level1type=N'TABLE', @level1name=N'{Authors}',
              @level2type=N'COLUMN', @level2name=N'name'", none);
    }

    private void DropFixture()
    {
        var none = new Dictionary<string, object?>();
        _db!.Exec($"IF OBJECT_ID(N'dbo.{Books}', N'U') IS NOT NULL DROP TABLE dbo.{Books}", none);
        _db.Exec($"IF OBJECT_ID(N'dbo.{Authors}', N'U') IS NOT NULL DROP TABLE dbo.{Authors}", none);
    }

    private long InsertAuthor(string name) => _db!.InsertReturningId(
        $"INSERT INTO dbo.{Authors} (name) VALUES (@name)",
        new Dictionary<string, object?> { ["name"] = name });

    [Fact]
    public void Introspection_reads_the_whole_fixture_schema() // N2.1–N2.6
    {
        if (_db == null) return;
        var introspector = new DatabaseIntrospector(_db);

        var tables = introspector.ListTables();
        Assert.Contains(Authors, tables);
        Assert.Contains(Books, tables);

        var entities = introspector.Introspect(includeTables: new[] { Authors, Books });
        var authors = entities.Single(e => e.Table == Authors);
        var books = entities.Single(e => e.Table == Books);

        // identity + PK detection
        var id = authors.Fields.Single(f => f.Name == "id");
        Assert.True(id.IsPrimary);
        Assert.True(id.IsAutoIncrement);

        // nvarchar(100) → string with maxLen; MS_Description surfaced
        var name = authors.Fields.Single(f => f.Name == "name");
        Assert.Equal(typeof(string), name.ClrType);
        Assert.Equal(100, name.Length);
        Assert.False(name.Nullable);
        Assert.Equal("Display name", name.Description);
        Assert.Contains("notEmpty", name.Validators);
        Assert.Contains("maxLen:100", name.Validators);

        // nvarchar(max) reports -1 → no length, no maxLen
        var bio = authors.Fields.Single(f => f.Name == "bio");
        Assert.Null(bio.Length);
        Assert.DoesNotContain(bio.Validators, v => v.StartsWith("maxLen"));

        // bit → bool, with default
        var active = authors.Fields.Single(f => f.Name == "active");
        Assert.Equal(typeof(bool), active.ClrType);
        Assert.True(active.HasDefault);

        // table comment
        Assert.Equal("Authors fixture table", authors.Description);

        // FK: books.author_id → authors.id
        var fk = books.Fields.Single(f => f.Name == "author_id");
        Assert.Equal("id", fk.FkColumn);

        // composite UNIQUE (author_id, title), PK excluded
        var unique = Assert.Single(books.UniqueConstraints);
        Assert.Equal(new[] { "author_id", "title" }, unique);

        // datetime2 → DateTime
        Assert.Equal(typeof(DateTime), books.Fields.Single(f => f.Name == "published").ClrType);
    }

    [Fact]
    public void InsertReturningId_returns_real_scope_identity() // N3.2 / review C1
    {
        if (_db == null) return;

        var first = InsertAuthor("First");
        var second = InsertAuthor("Second");
        Assert.True(first > 0);
        Assert.True(second > first);

        // ...and inside an explicit transaction (the LoginService refresh-rotation shape).
        _db.BeginTransaction();
        try
        {
            var inTx = InsertAuthor("InTx");
            Assert.True(inTx > second);
        }
        finally
        {
            _db.Rollback();
        }
    }

    [Fact]
    public void Pagination_pages_with_and_without_mask_level_order_by() // N3.1 / review M2
    {
        if (_db == null) return;
        foreach (var n in new[] { "A", "B", "C", "D", "E" })
            InsertAuthor(n);

        // Orderless mask → ORDER BY (SELECT NULL) wrapper.
        var wrapped = _db.Run(
            BatchExecutor.BuildMssqlPagination($"SELECT id, name FROM dbo.{Authors}", skip: 1, top: 2),
            new());
        Assert.Equal(2, wrapped.Count);

        // Mask with its own ORDER BY → OFFSET/FETCH appended directly, order preserved.
        var ordered = _db.Run(
            BatchExecutor.BuildMssqlPagination(
                $"SELECT id, name FROM dbo.{Authors} ORDER BY name DESC", skip: 1, top: 2),
            new());
        Assert.Equal(2, ordered.Count);
        Assert.Equal("D", ordered.Rows[0][ordered.Columns.IndexOf("name")]);

        // Skip-only → bare OFFSET, no FETCH.
        var skipOnly = _db.Run(
            BatchExecutor.BuildMssqlPagination($"SELECT id FROM dbo.{Authors}", skip: 3, top: 0),
            new());
        Assert.Equal(2, skipOnly.Count);
    }

    [Fact]
    public void Unique_violation_surfaces_error_code_2627() // N4.1–N4.2
    {
        if (_db == null) return;
        var author = InsertAuthor("Dup");
        var insert = $"INSERT INTO dbo.{Books} (author_id, title) VALUES (@a, @t)";
        var args = new Dictionary<string, object?> { ["a"] = author, ["t"] = "Same Title" };

        _db.Exec(insert, args);
        var ex = Assert.Throws<ApiException>(() => _db.Exec(insert, args));

        var code = ex.Details?.GetValueOrDefault("driver_error_code")?.ToString();
        Assert.True(code is "2627" or "2601", $"expected unique-violation code, got '{code}'");
    }
}
