using Shape.Auth;
using Shape.Core;
using Shape.Hosting;
using Xunit;

namespace Shape.Tests;

public sealed class EnvLoaderTests
{
    private static Func<string, string?> EnvFrom(Dictionary<string, string?> map) =>
        k => map.TryGetValue(k, out var v) ? v : null;

    [Fact]
    public void DB_DSN_overlays_jsons_db_block()
    {
        var cfg = new ShapeConfig { Db = new DatabaseConfig { Driver = "sqlite", ConnectionString = "Data Source=:memory:" } };
        var env = EnvFrom(new()
        {
            ["DB_DSN"] = "pgsql:host=db.internal;port=5432;dbname=app",
            ["DB_USER"] = "u",
            ["DB_PASS"] = "p"
        });

        EnvLoader.ApplyEnv(cfg, env, _ => false);

        Assert.Equal("pgsql", cfg.Db.Driver);
        Assert.Equal("Host=db.internal;Port=5432;Database=app;Username=u;Password=p", cfg.Db.ConnectionString);
    }

    [Fact]
    public void Cloud_SQL_instance_synthesises_socket_dsn_when_socket_exists()
    {
        var cfg = new ShapeConfig { Db = new DatabaseConfig { Driver = "sqlite", ConnectionString = "Data Source=:memory:" } };
        var env = EnvFrom(new()
        {
            ["CLOUD_SQL_INSTANCE"] = "to-aps:europe-central2:to-psql1",
            ["DB_NAME"] = "aps_dev",
            ["DB_USER"] = "aps",
            ["DB_PASS"] = "secret"
        });

        EnvLoader.ApplyEnv(cfg, env, dirExists: path => path == "/cloudsql/to-aps:europe-central2:to-psql1");

        Assert.Equal("pgsql", cfg.Db.Driver);
        Assert.Equal(
            "Host=/cloudsql/to-aps:europe-central2:to-psql1;Database=aps_dev;Username=aps;Password=secret",
            cfg.Db.ConnectionString);
    }

    [Fact]
    public void Cloud_SQL_instance_falls_through_when_socket_absent()
    {
        // CLOUD_SQL_INSTANCE present but the socket isn't there (local dev) →
        // we should NOT synthesise the broken DSN; fall through to DB_DSN.
        var cfg = new ShapeConfig { Db = new DatabaseConfig { Driver = "sqlite", ConnectionString = "Data Source=:memory:" } };
        var env = EnvFrom(new()
        {
            ["CLOUD_SQL_INSTANCE"] = "ghost-instance",
            ["DB_DSN"] = "pgsql:host=localhost;port=5432;dbname=app"
        });

        EnvLoader.ApplyEnv(cfg, env, dirExists: _ => false);

        Assert.Equal("pgsql", cfg.Db.Driver);
        Assert.Equal("Host=localhost;Port=5432;Database=app", cfg.Db.ConnectionString);
    }

    [Fact]
    public void No_db_env_leaves_json_db_block_alone()
    {
        var cfg = new ShapeConfig { Db = new DatabaseConfig { Driver = "sqlite", ConnectionString = "Data Source=untouched.db" } };
        var env = EnvFrom(new());

        EnvLoader.ApplyEnv(cfg, env, _ => false);

        Assert.Equal("sqlite", cfg.Db.Driver);
        Assert.Equal("Data Source=untouched.db", cfg.Db.ConnectionString);
    }

    [Fact]
    public void JWT_SECRET_overlays_auth_block()
    {
        var cfg = new ShapeConfig { Auth = new AuthConfig { Driver = "jwt", JwtSecret = "json-value" } };
        var env = EnvFrom(new() { ["JWT_SECRET"] = "env-value" });

        EnvLoader.ApplyEnv(cfg, env, _ => false);

        Assert.Equal("env-value", cfg.Auth.JwtSecret);
    }

    [Fact]
    public void JWT_SECRET_creates_auth_when_absent()
    {
        var cfg = new ShapeConfig { Auth = null! };
        var env = EnvFrom(new() { ["JWT_SECRET"] = "from-env" });

        EnvLoader.ApplyEnv(cfg, env, _ => false);

        Assert.NotNull(cfg.Auth);
        Assert.Equal("from-env", cfg.Auth!.JwtSecret);
    }
}
