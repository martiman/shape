using Shape.Config;
using Shape.Core;

namespace Shape.Hosting;

/// <summary>
/// Reads conventional Shape env vars (<c>DB_DSN</c>, <c>DB_USER</c>,
/// <c>DB_PASS</c>, <c>DB_NAME</c>, <c>CLOUD_SQL_INSTANCE</c>,
/// <c>JWT_SECRET</c>) and overlays them onto a <see cref="ShapeConfig"/>
/// already loaded from JSON. Lets every PHP→C# port skip its own
/// <c>BuildDb()</c> helper.
///
/// Env priority for the DB connection:
/// <list type="number">
///   <item><c>CLOUD_SQL_INSTANCE</c> set AND <c>/cloudsql/&lt;instance&gt;</c>
///   exists → synthesise <c>pgsql:host=/cloudsql/&lt;instance&gt;;dbname=&lt;DB_NAME or JSON db.dbname&gt;</c>.</item>
///   <item><c>DB_DSN</c> set → translate via <see cref="DsnTranslator"/>.</item>
///   <item>Neither → leave whatever's in the JSON config untouched.</item>
/// </list>
/// </summary>
public static class EnvLoader
{
    /// <summary>
    /// Production entry point — reads from
    /// <see cref="Environment.GetEnvironmentVariable(string)"/> and the live
    /// filesystem. Internal overload accepts injectable readers for tests.
    /// </summary>
    public static void ApplyEnv(ShapeConfig cfg)
        => ApplyEnv(cfg, Environment.GetEnvironmentVariable, Directory.Exists);

    internal static void ApplyEnv(
        ShapeConfig cfg,
        Func<string, string?> getEnv,
        Func<string, bool> dirExists)
    {
        var dbUser = getEnv("DB_USER");
        var dbPass = getEnv("DB_PASS");
        var dbName = getEnv("DB_NAME");
        var dbDsn = getEnv("DB_DSN");
        var cloudSqlInstance = getEnv("CLOUD_SQL_INSTANCE");
        var jwtSecret = getEnv("JWT_SECRET");

        // Priority 1: Cloud SQL Unix socket — only when the socket actually
        // exists (Cloud Run mounts it; local boxes don't).
        if (!string.IsNullOrWhiteSpace(cloudSqlInstance) && dirExists($"/cloudsql/{cloudSqlInstance}"))
        {
            var dbnameForSocket = !string.IsNullOrWhiteSpace(dbName) ? dbName : "postgres";
            var synth = $"pgsql:host=/cloudsql/{cloudSqlInstance};dbname={dbnameForSocket}";
            var (driver, conn) = DsnTranslator.Translate(synth, dbUser, dbPass);
            cfg.Db ??= new DatabaseConfig();
            cfg.Db.Driver = driver;
            cfg.Db.ConnectionString = conn;
        }
        // Priority 2: explicit DB_DSN.
        else if (!string.IsNullOrWhiteSpace(dbDsn))
        {
            var (driver, conn) = DsnTranslator.Translate(dbDsn, dbUser, dbPass);
            cfg.Db ??= new DatabaseConfig();
            cfg.Db.Driver = driver;
            cfg.Db.ConnectionString = conn;
        }

        // JWT_SECRET overrides JSON value AND skips JwtSecretMaterializer
        // (Cloud Run's ephemeral FS would rotate the materialised secret on
        // every cold boot — set this explicitly in prod).
        if (!string.IsNullOrWhiteSpace(jwtSecret))
        {
            cfg.Auth ??= new Auth.AuthConfig();
            cfg.Auth.JwtSecret = jwtSecret;
        }
    }
}
