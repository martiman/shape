using System.Reflection;
using System.Runtime.CompilerServices;
using Shape.Core;
using Xunit;

namespace Shape.Tests;

/// <summary>
/// QuoteIdentifier is driver-specific but doesn't need an open connection. Build a
/// Database via reflection (skip the constructor that opens the connection) and
/// assert the per-driver quoting rules. Reflection is the simplest way to skip the
/// connection-opening constructor without leaking test seams into the public API.
/// </summary>
public sealed class DatabaseQuotingTests
{
    private static Database UninitializedFor(DbDriver driver)
    {
        var db = (Database)RuntimeHelpers.GetUninitializedObject(typeof(Database));
        typeof(Database).GetField("_driver", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(db, driver);
        return db;
    }

    [Fact]
    public void Mysql_uses_backticks()
    {
        var db = UninitializedFor(DbDriver.Mysql);
        Assert.Equal("`orders`", db.QuoteIdentifier("orders"));
    }

    [Fact]
    public void Pgsql_uses_double_quotes()
    {
        var db = UninitializedFor(DbDriver.Pgsql);
        Assert.Equal("\"orders\"", db.QuoteIdentifier("orders"));
    }

    [Fact]
    public void Sqlite_uses_double_quotes()
    {
        var db = UninitializedFor(DbDriver.Sqlite);
        Assert.Equal("\"orders\"", db.QuoteIdentifier("orders"));
    }

    [Fact]
    public void Mysql_doubles_embedded_backticks()
    {
        var db = UninitializedFor(DbDriver.Mysql);
        Assert.Equal("`a``b`", db.QuoteIdentifier("a`b"));
    }

    [Fact]
    public void Pgsql_doubles_embedded_double_quotes()
    {
        var db = UninitializedFor(DbDriver.Pgsql);
        Assert.Equal("\"a\"\"b\"", db.QuoteIdentifier("a\"b"));
    }

    [Fact]
    public void Mssql_uses_brackets()
    {
        var db = UninitializedFor(DbDriver.Mssql);
        Assert.Equal("[orders]", db.QuoteIdentifier("orders"));
    }

    [Fact]
    public void Mssql_doubles_embedded_closing_brackets()
    {
        var db = UninitializedFor(DbDriver.Mssql);
        Assert.Equal("[a]]b]", db.QuoteIdentifier("a]b"));
    }

    [Fact]
    public void Mssql_caps_rows_with_top_not_limit()
    {
        var db = UninitializedFor(DbDriver.Mssql);
        Assert.Equal("TOP 1 ", db.TopClause(1));
        Assert.Equal("", db.RowLimitSuffix(1));
    }

    [Fact]
    public void Other_drivers_cap_rows_with_limit_not_top()
    {
        var db = UninitializedFor(DbDriver.Pgsql);
        Assert.Equal("", db.TopClause(1));
        Assert.Equal(" LIMIT 1", db.RowLimitSuffix(1));
    }
}
