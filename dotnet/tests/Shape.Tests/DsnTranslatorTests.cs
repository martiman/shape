using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class DsnTranslatorTests
{
    [Fact]
    public void Pgsql_tcp_translates_to_npgsql_form()
    {
        var (driver, conn) = DsnTranslator.Translate(
            "pgsql:host=34.118.115.29;port=5432;dbname=aps_dev",
            user: "aps",
            password: "secret");

        Assert.Equal("pgsql", driver);
        Assert.Equal("Host=34.118.115.29;Port=5432;Database=aps_dev;Username=aps;Password=secret", conn);
    }

    [Fact]
    public void Pgsql_cloud_sql_unix_socket_omits_port()
    {
        var (driver, conn) = DsnTranslator.Translate(
            "pgsql:host=/cloudsql/to-aps:europe-central2:to-psql1;port=5432;dbname=aps_dev",
            user: "aps",
            password: "secret");

        Assert.Equal("pgsql", driver);
        Assert.Equal(
            "Host=/cloudsql/to-aps:europe-central2:to-psql1;Database=aps_dev;Username=aps;Password=secret",
            conn);
    }

    [Fact]
    public void Pgsql_passes_through_sslmode()
    {
        var (_, conn) = DsnTranslator.Translate(
            "pgsql:host=db.internal;dbname=app;sslmode=require",
            user: "u",
            password: "p");

        Assert.Contains("SSL Mode=require", conn);
    }

    [Fact]
    public void Pgsql_aliases_postgres_and_postgresql_prefixes()
    {
        var (d1, _) = DsnTranslator.Translate("postgres:host=localhost;dbname=app");
        var (d2, _) = DsnTranslator.Translate("postgresql:host=localhost;dbname=app");

        Assert.Equal("pgsql", d1);
        Assert.Equal("pgsql", d2);
    }

    [Fact]
    public void Mysql_translates_to_mysqlconnector_form()
    {
        var (driver, conn) = DsnTranslator.Translate(
            "mysql:host=db;port=3306;dbname=app",
            user: "root",
            password: "secret");

        Assert.Equal("mysql", driver);
        Assert.Equal("Server=db;Port=3306;Database=app;User Id=root;Password=secret", conn);
    }

    [Fact]
    public void Sqlite_file_path_translates_to_data_source()
    {
        var (driver, conn) = DsnTranslator.Translate("sqlite:/tmp/app.db");

        Assert.Equal("sqlite", driver);
        Assert.Equal("Data Source=/tmp/app.db", conn);
    }

    [Fact]
    public void Sqlite_in_memory_translates()
    {
        var (_, conn) = DsnTranslator.Translate("sqlite::memory:");
        Assert.Equal("Data Source=:memory:", conn);
    }

    [Fact]
    public void Mssql_translates_to_sqlclient_form_with_comma_port()
    {
        var (driver, conn) = DsnTranslator.Translate(
            "sqlsrv:host=db;port=1433;dbname=app",
            user: "sa",
            password: "secret");

        Assert.Equal("mssql", driver);
        // SQL Server puts the port after a comma on Server, never a Port= key.
        // "db" is a remote host, so no TrustServerCertificate default is injected.
        Assert.Equal(
            "Server=db,1433;Database=app;User Id=sa;Password=secret",
            conn);
    }

    [Fact]
    public void Mssql_accepts_pdo_sqlsrv_server_database_keys()
    {
        var (driver, conn) = DsnTranslator.Translate(
            "sqlsrv:Server=localhost,1433;Database=app");

        Assert.Equal("mssql", driver);
        Assert.Equal("Server=localhost,1433;Database=app;TrustServerCertificate=true", conn);
    }

    [Fact]
    public void Mssql_aliases_mssql_and_sqlserver_prefixes()
    {
        var (d1, _) = DsnTranslator.Translate("mssql:host=h;dbname=d");
        var (d2, _) = DsnTranslator.Translate("sqlserver:host=h;dbname=d");

        Assert.Equal("mssql", d1);
        Assert.Equal("mssql", d2);
    }

    [Fact]
    public void Mssql_honors_explicit_encrypt_and_trust_settings()
    {
        var (_, conn) = DsnTranslator.Translate(
            "sqlsrv:host=h;dbname=d;Encrypt=true;TrustServerCertificate=false");

        Assert.Contains("Encrypt=true", conn);
        Assert.Contains("TrustServerCertificate=false", conn);
        Assert.DoesNotContain("TrustServerCertificate=true", conn);
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("(local)")]
    [InlineData(@"(localdb)\MSSQLLocalDB")]
    public void Mssql_defaults_trust_for_local_hosts_only(string host)
    {
        var (_, local) = DsnTranslator.Translate($"sqlsrv:host={host};dbname=d");
        Assert.Contains("TrustServerCertificate=true", local);

        var (_, remote) = DsnTranslator.Translate("sqlsrv:host=db.example.com;dbname=d");
        Assert.DoesNotContain("TrustServerCertificate", remote);
    }

    [Fact]
    public void Mssql_normalizes_pdo_style_booleans()
    {
        // pdo_sqlsrv accepts Encrypt=1/0; SqlClient only parses true/false — the
        // translator must normalize or the connection string throws at parse time.
        var (_, conn) = DsnTranslator.Translate(
            "sqlsrv:host=h;dbname=d;Encrypt=1;TrustServerCertificate=no");

        Assert.Contains("Encrypt=true", conn);
        Assert.Contains("TrustServerCertificate=false", conn);
    }

    [Fact]
    public void Mssql_passes_extra_sqlclient_options_through()
    {
        var (_, conn) = DsnTranslator.Translate(
            "sqlsrv:host=localhost;dbname=d;LoginTimeout=5;ApplicationIntent=ReadOnly");

        Assert.Contains("LoginTimeout=5", conn);
        Assert.Contains("ApplicationIntent=ReadOnly", conn);
    }

    [Fact]
    public void Mssql_does_not_duplicate_credential_keys_from_dsn_body()
    {
        // uid/pwd in the DSN body are superseded by the explicit user/password args.
        var (_, conn) = DsnTranslator.Translate(
            "sqlsrv:host=localhost;dbname=d;uid=ignored;pwd=ignored",
            user: "sa", password: "s3cret");

        Assert.DoesNotContain("ignored", conn);
        Assert.Contains("User Id=sa", conn);
        Assert.Contains("Password=s3cret", conn);
    }

    [Fact]
    public void Empty_dsn_throws()
    {
        Assert.Throws<ArgumentException>(() => DsnTranslator.Translate(""));
        Assert.Throws<ArgumentException>(() => DsnTranslator.Translate("   "));
    }

    [Fact]
    public void Missing_colon_throws()
    {
        Assert.Throws<ArgumentException>(() => DsnTranslator.Translate("Host=localhost;Database=app"));
    }

    [Fact]
    public void Unknown_prefix_throws()
    {
        Assert.Throws<ArgumentException>(() =>
            DsnTranslator.Translate("oracle:host=ora.internal;sid=app"));
    }

    [Fact]
    public void DatabaseConfig_FromDsn_factory_round_trips()
    {
        var cfg = DatabaseConfig.FromDsn("pgsql:host=h;dbname=d", "u", "p");
        Assert.Equal("pgsql", cfg.Driver);
        Assert.Equal("Host=h;Database=d;Username=u;Password=p", cfg.ConnectionString);
    }

    [Fact]
    public void Pgsql_omits_user_when_not_supplied()
    {
        var (_, conn) = DsnTranslator.Translate("pgsql:host=h;dbname=d");
        Assert.Equal("Host=h;Database=d", conn);
    }
}
