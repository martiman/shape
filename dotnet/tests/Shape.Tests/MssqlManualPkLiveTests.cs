using Shape.Core;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

/// <summary>
/// Live SQL Server verification for manual PK allocation (MANUAL-PK-PLAN.md §6):
/// the UPDLOCK/HOLDLOCK range lock must serialize concurrent allocators so parallel
/// writers produce distinct ids, and the auto rule must recognize a non-IDENTITY
/// int PK from real introspection output.
///
/// Facts no-op (vacuously green) unless SHAPE_MSSQL_DSN is set — same env-gate
/// protocol as <see cref="MssqlLiveIntegrationTests"/>:
/// <code>
///   docker run -d --name shape-mssql -e ACCEPT_EULA=Y -e MSSQL_SA_PASSWORD='Shape!Pass1' \
///     -p 1433:1433 mcr.microsoft.com/mssql/server:2022-latest
///   export SHAPE_MSSQL_DSN='sqlsrv:host=localhost;port=1433;dbname=master'
///   export SHAPE_MSSQL_USER=sa SHAPE_MSSQL_PASSWORD='Shape!Pass1'
///   dotnet test tests/Shape.Tests --filter "db=mssql"
/// </code>
/// </summary>
[Trait("db", "mssql")]
public sealed class MssqlManualPkLiveTests : IDisposable
{
    private const string Table = "shape_it_manual_pk";

    private readonly DatabaseConfig? _dbConfig;
    private readonly Database? _db;

    public MssqlManualPkLiveTests()
    {
        var dsn = Environment.GetEnvironmentVariable("SHAPE_MSSQL_DSN");
        if (string.IsNullOrEmpty(dsn)) return; // no live server — facts no-op

        _dbConfig = DatabaseConfig.FromDsn(
            dsn,
            Environment.GetEnvironmentVariable("SHAPE_MSSQL_USER"),
            Environment.GetEnvironmentVariable("SHAPE_MSSQL_PASSWORD"));
        _db = new Database(_dbConfig);
        DropFixture();
        // The legacy shape this feature exists for: plain int PK, no IDENTITY,
        // no default — the original app computed ids in code.
        _db.Exec($@"
            CREATE TABLE dbo.{Table} (
              id   int NOT NULL CONSTRAINT PK_{Table} PRIMARY KEY,
              name nvarchar(50) NOT NULL
            )", new());
    }

    public void Dispose()
    {
        if (_db == null) return;
        try { DropFixture(); } catch { /* best effort */ }
        _db.Dispose();
    }

    private void DropFixture() =>
        _db!.Exec($"IF OBJECT_ID(N'dbo.{Table}', N'U') IS NOT NULL DROP TABLE dbo.{Table}", new());

    [Fact]
    public void Auto_rule_detects_manual_from_live_introspection()
    {
        if (_db == null) return;

        var entities = new DatabaseIntrospector(_db).Introspect(includeTables: new[] { Table });
        PkStrategyResolver.Apply(entities, null);
        Assert.Equal("manual", entities.Single().PkStrategy);
    }

    [Fact]
    public void Allocation_is_sequential_and_returns_the_id_via_output_inserted()
    {
        if (_db == null) return;

        foreach (var expected in new long[] { 1, 2, 3 })
        {
            var id = _db.InsertWithManualPk(Table, "id", new Dictionary<string, object?>
            {
                ["name"] = "row " + expected
            });
            Assert.Equal(expected, id);
        }
    }

    [Fact]
    public void Parallel_allocators_produce_distinct_ids_under_the_range_lock()
    {
        if (_db == null) return;

        const int writers = 8;
        const int insertsPerWriter = 10;

        var ids = new System.Collections.Concurrent.ConcurrentBag<long>();
        var tasks = Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            using var db = new Database(_dbConfig!);
            for (var i = 0; i < insertsPerWriter; i++)
            {
                ids.Add(db.InsertWithManualPk(Table, "id", new Dictionary<string, object?>
                {
                    ["name"] = $"w{w}-{i}"
                }));
            }
        })).ToArray();
        Task.WaitAll(tasks);

        // Distinct + dense: MAX+1 under UPDLOCK/HOLDLOCK admits no gaps and no duplicates.
        Assert.Equal(writers * insertsPerWriter, ids.Count);
        Assert.Equal(writers * insertsPerWriter, ids.Distinct().Count());
        Assert.Equal(1, ids.Min());
        Assert.Equal(writers * insertsPerWriter, ids.Max());
    }
}
