using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class DatabaseConfigTests
{
    [Fact]
    public void Driver_defaults_to_mysql()
    {
        Assert.Equal("mysql", new DatabaseConfig().Driver);
    }

    [Fact]
    public void Sqlite_in_memory_opens_successfully()
    {
        // Phase 1 wired Microsoft.Data.Sqlite. ":memory:" is the fastest sanity
        // check that the connection factory hands back a usable connection.
        using var db = new Database(new DatabaseConfig
        {
            Driver = "sqlite",
            ConnectionString = "Data Source=:memory:"
        });

        Assert.Equal(DbDriver.Sqlite, db.Driver);
    }

    [Fact]
    public void Pgsql_alias_postgres_resolves()
    {
        // We can't actually open a Pg connection without a server, but the parse
        // step must accept all three aliases ("pgsql", "postgres", "postgresql")
        // before any TCP attempt. Use a deliberately broken connection string
        // and assert the failure is the connection-time error, not a parse-time
        // unknown-driver error.
        var ex = Assert.ThrowsAny<Exception>(() => new Database(new DatabaseConfig
        {
            Driver = "postgres",
            ConnectionString = "Host=does-not-exist-1234;Port=1;Username=u;Password=p;Database=x;Timeout=1"
        }));

        Assert.IsNotType<NotSupportedException>(ex);
    }

    [Fact]
    public void Mssql_aliases_resolve_past_parse_to_connection_error()
    {
        // No local SQL Server, so the connection can't open — but ParseDriver must
        // accept "mssql"/"sqlsrv"/"sqlserver" before any TCP attempt. Assert the
        // failure is a connection-time error, not a parse-time NotSupportedException.
        foreach (var driver in new[] { "mssql", "sqlsrv", "sqlserver" })
        {
            var ex = Assert.ThrowsAny<Exception>(() => new Database(new DatabaseConfig
            {
                Driver = driver,
                ConnectionString = "Server=does-not-exist-1234,1;Database=x;TrustServerCertificate=true;Connect Timeout=1"
            }));

            Assert.IsNotType<NotSupportedException>(ex);
        }
    }

    [Fact]
    public void Unknown_driver_string_throws_with_supported_list()
    {
        var config = new DatabaseConfig
        {
            Driver = "oracle",
            ConnectionString = ""
        };

        var ex = Assert.Throws<NotSupportedException>(() => new Database(config));

        Assert.Contains("'oracle'", ex.Message);
        Assert.Contains("mysql", ex.Message);
    }

    [Fact]
    public void Sqlite_quotes_with_double_quotes()
    {
        using var db = new Database(new DatabaseConfig
        {
            Driver = "sqlite",
            ConnectionString = "Data Source=:memory:"
        });

        Assert.Equal("\"orders\"", db.QuoteIdentifier("orders"));
    }
}
