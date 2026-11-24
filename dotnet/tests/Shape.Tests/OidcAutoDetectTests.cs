using Shape;
using Shape.Auth;
using Shape.Core;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

public sealed class OidcAutoDetectTests
{
    private static (Database db, DatabaseIntrospector ix) NewSqlite(string usersDdl)
    {
        var db = new Database(new DatabaseConfig
        {
            Driver = "sqlite",
            ConnectionString = "Data Source=:memory:"
        });
        db.Exec(usersDdl, new());
        return (db, new DatabaseIntrospector(db));
    }

    [Fact]
    public void Detects_single_match_oid()
    {
        var (db, ix) = NewSqlite("CREATE TABLE users (id INTEGER PRIMARY KEY, oid TEXT, email TEXT, role TEXT)");
        using var _ = db;
        var cfg = new ShapeConfig
        {
            Auth = new AuthConfig { Driver = "jwt", Oidc = new OidcConfig { /* UsersLookupColumn left null */ } }
        };

        ShapeApp.AutoDetectOidcLookupColumn(cfg, ix);

        Assert.Equal("oid", cfg.Auth.Oidc!.UsersLookupColumn);
    }

    [Fact]
    public void Detects_single_match_azure_oid()
    {
        var (db, ix) = NewSqlite("CREATE TABLE users (id INTEGER PRIMARY KEY, azure_oid TEXT, email TEXT, role TEXT)");
        using var _ = db;
        var cfg = new ShapeConfig
        {
            Auth = new AuthConfig { Driver = "jwt", Oidc = new OidcConfig() }
        };

        ShapeApp.AutoDetectOidcLookupColumn(cfg, ix);

        Assert.Equal("azure_oid", cfg.Auth.Oidc!.UsersLookupColumn);
    }

    [Fact]
    public void Multi_match_falls_back_to_oid()
    {
        var (db, ix) = NewSqlite("CREATE TABLE users (id INTEGER PRIMARY KEY, oid TEXT, sub TEXT, email TEXT)");
        using var _ = db;
        var cfg = new ShapeConfig
        {
            Auth = new AuthConfig { Driver = "jwt", Oidc = new OidcConfig() }
        };
        var stderr = new StringWriter();
        var prev = Console.Error;
        Console.SetError(stderr);
        try
        {
            ShapeApp.AutoDetectOidcLookupColumn(cfg, ix);
        }
        finally { Console.SetError(prev); }

        Assert.Equal("oid", cfg.Auth.Oidc!.UsersLookupColumn);
        Assert.Contains("Ambiguous OIDC users_lookup_column", stderr.ToString());
    }

    [Fact]
    public void No_match_falls_back_to_oid()
    {
        var (db, ix) = NewSqlite("CREATE TABLE users (id INTEGER PRIMARY KEY, email TEXT, role TEXT)");
        using var _ = db;
        var cfg = new ShapeConfig
        {
            Auth = new AuthConfig { Driver = "jwt", Oidc = new OidcConfig() }
        };

        ShapeApp.AutoDetectOidcLookupColumn(cfg, ix);

        Assert.Equal("oid", cfg.Auth.Oidc!.UsersLookupColumn);
    }

    [Fact]
    public void Explicit_value_is_preserved()
    {
        var (db, ix) = NewSqlite("CREATE TABLE users (id INTEGER PRIMARY KEY, oid TEXT, azure_oid TEXT)");
        using var _ = db;
        var cfg = new ShapeConfig
        {
            Auth = new AuthConfig
            {
                Driver = "jwt",
                Oidc = new OidcConfig { UsersLookupColumn = "azure_oid" }
            }
        };

        ShapeApp.AutoDetectOidcLookupColumn(cfg, ix);

        Assert.Equal("azure_oid", cfg.Auth.Oidc!.UsersLookupColumn);
    }

    [Fact]
    public void No_oidc_config_is_a_noop()
    {
        var (db, ix) = NewSqlite("CREATE TABLE users (id INTEGER PRIMARY KEY, oid TEXT)");
        using var _ = db;
        var cfg = new ShapeConfig { Auth = new AuthConfig { Driver = "jwt" } };

        ShapeApp.AutoDetectOidcLookupColumn(cfg, ix);

        Assert.Null(cfg.Auth.Oidc);
    }
}
