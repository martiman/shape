namespace Shape.Core;

/// <summary>
/// Translates a PHP/PDO-style DSN (<c>pgsql:host=…;dbname=…</c>,
/// <c>mysql:host=…;dbname=…</c>, <c>sqlite:/path/to/file.db</c>) into a
/// native ADO.NET connection string for the matching .NET driver. Lets a
/// shared <c>env.local.php</c>-style config file boot both <c>papi-lib</c>
/// (PHP) and <c>Shape</c> (C#) without per-runtime rewrites.
/// </summary>
public static class DsnTranslator
{
    /// <summary>
    /// Parse the DSN and return (driver, connectionString). User and password
    /// are appended as Username/Password (or User Id/Password on MySQL) when
    /// supplied — sqlite ignores them.
    /// </summary>
    public static (string Driver, string ConnectionString) Translate(string dsn, string? user = null, string? password = null)
    {
        if (string.IsNullOrWhiteSpace(dsn))
            throw new ArgumentException("DSN cannot be empty.", nameof(dsn));

        var colonIdx = dsn.IndexOf(':');
        if (colonIdx <= 0)
            throw new ArgumentException(
                $"DSN must start with '<driver>:' (e.g. 'pgsql:host=...;dbname=...'). Got: {dsn}",
                nameof(dsn));

        var prefix = dsn.Substring(0, colonIdx).Trim().ToLowerInvariant();
        var body = dsn.Substring(colonIdx + 1);

        return prefix switch
        {
            "pgsql" or "postgres" or "postgresql" => ("pgsql", BuildPgsql(body, user, password)),
            "mysql" => ("mysql", BuildMysql(body, user, password)),
            "sqlite" => ("sqlite", BuildSqlite(body)),
            "sqlsrv" or "mssql" or "sqlserver" => ("mssql", BuildMssql(body, user, password)),
            _ => throw new ArgumentException(
                $"Unsupported DSN prefix '{prefix}'. Supported: pgsql, mysql, sqlite, mssql.",
                nameof(dsn))
        };
    }

    private static string BuildPgsql(string body, string? user, string? password)
    {
        var pairs = ParseKv(body);
        var parts = new List<string>();

        if (pairs.TryGetValue("host", out var host) && !string.IsNullOrEmpty(host))
            parts.Add($"Host={host}");

        // Cloud SQL Unix-socket hosts (/cloudsql/<instance>) reject explicit port.
        var isUnixSocket = host != null && host.StartsWith("/");
        if (!isUnixSocket && pairs.TryGetValue("port", out var port) && !string.IsNullOrEmpty(port))
            parts.Add($"Port={port}");

        if (pairs.TryGetValue("dbname", out var dbname) && !string.IsNullOrEmpty(dbname))
            parts.Add($"Database={dbname}");
        if (pairs.TryGetValue("sslmode", out var sslmode) && !string.IsNullOrEmpty(sslmode))
            parts.Add($"SSL Mode={sslmode}");

        if (!string.IsNullOrEmpty(user)) parts.Add($"Username={user}");
        if (!string.IsNullOrEmpty(password)) parts.Add($"Password={password}");

        return string.Join(";", parts);
    }

    private static string BuildMysql(string body, string? user, string? password)
    {
        var pairs = ParseKv(body);
        var parts = new List<string>();

        if (pairs.TryGetValue("host", out var host) && !string.IsNullOrEmpty(host))
            parts.Add($"Server={host}");
        if (pairs.TryGetValue("port", out var port) && !string.IsNullOrEmpty(port))
            parts.Add($"Port={port}");
        if (pairs.TryGetValue("dbname", out var dbname) && !string.IsNullOrEmpty(dbname))
            parts.Add($"Database={dbname}");
        if (pairs.TryGetValue("unix_socket", out var sock) && !string.IsNullOrEmpty(sock))
            parts.Add($"Server={sock}");

        if (!string.IsNullOrEmpty(user)) parts.Add($"User Id={user}");
        if (!string.IsNullOrEmpty(password)) parts.Add($"Password={password}");

        return string.Join(";", parts);
    }

    private static string BuildMssql(string body, string? user, string? password)
    {
        // Accepts both the PDO sqlsrv spelling (`Server=host,port;Database=db`) and
        // the generic host/port/dbname keys the other drivers share, so one DSN can
        // drive all engines. SQL Server puts the port after a COMMA on the host
        // (`Server=host,1433`) — never a separate `Port=` key.
        var pairs = ParseKv(body);
        var parts = new List<string>();

        var host = pairs.GetValueOrDefault("server") ?? pairs.GetValueOrDefault("host");
        pairs.TryGetValue("port", out var port);
        if (!string.IsNullOrEmpty(host))
        {
            parts.Add(!string.IsNullOrEmpty(port) && !host.Contains(',')
                ? $"Server={host},{port}"
                : $"Server={host}");
        }

        var dbname = pairs.GetValueOrDefault("database") ?? pairs.GetValueOrDefault("dbname");
        if (!string.IsNullOrEmpty(dbname))
            parts.Add($"Database={dbname}");

        // Microsoft.Data.SqlClient defaults Encrypt=true, which fails against dev/local
        // servers lacking a CA-trusted cert. Honor explicit Encrypt / TrustServerCertificate
        // from the DSN (normalizing PDO-style 1/0/yes/no booleans SqlClient rejects);
        // otherwise default TrustServerCertificate=true for LOCAL hosts only, so a bare
        // `sqlsrv:host=localhost;dbname=app` connects out of the box without silently
        // disabling cert validation for remote (production) servers.
        if (pairs.TryGetValue("encrypt", out var enc) && !string.IsNullOrEmpty(enc))
            parts.Add($"Encrypt={NormalizeBool(enc)}");
        if (pairs.TryGetValue("trustservercertificate", out var trust) && !string.IsNullOrEmpty(trust))
            parts.Add($"TrustServerCertificate={NormalizeBool(trust)}");
        else if (IsLocalHost(host))
            parts.Add("TrustServerCertificate=true");

        // Pass any remaining options (LoginTimeout, ApplicationIntent, MARS, …) through
        // verbatim — SqlClient shares the sqlsrv key vocabulary. Credential keys are
        // excluded: user/password arrive via the dedicated arguments below.
        foreach (var (key, value) in pairs)
        {
            if (ConsumedMssqlKeys.Contains(key) || string.IsNullOrEmpty(value)) continue;
            parts.Add($"{key}={value}");
        }

        if (!string.IsNullOrEmpty(user)) parts.Add($"User Id={user}");
        if (!string.IsNullOrEmpty(password)) parts.Add($"Password={password}");

        return string.Join(";", parts);
    }

    private static readonly HashSet<string> ConsumedMssqlKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "server", "host", "port", "database", "dbname", "encrypt", "trustservercertificate",
        // credential spellings — superseded by the user/password arguments
        "user", "user id", "uid", "password", "pwd"
    };

    /// <summary>PDO DSNs write booleans as 1/0/yes/no; SqlClient only parses true/false (or Strict/Mandatory/Optional for Encrypt).</summary>
    private static string NormalizeBool(string value) => value.Trim().ToLowerInvariant() switch
    {
        "1" or "yes" or "true" => "true",
        "0" or "no" or "false" => "false",
        _ => value
    };

    /// <summary>True for hosts where a self-signed dev cert is the norm: localhost, loopback IPs, LocalDB, named pipes to the local machine.</summary>
    private static bool IsLocalHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        var name = host.Split(',')[0].Split('\\')[0].Trim().ToLowerInvariant();
        return name is "localhost" or "127.0.0.1" or "::1" or "(local)" or "."
            || name.StartsWith("(localdb)", StringComparison.Ordinal);
    }

    private static string BuildSqlite(string body)
    {
        // PHP form is `sqlite:/abs/path.db` or `sqlite::memory:`. Both map to
        // Microsoft.Data.Sqlite's `Data Source=` directly.
        var path = body.Trim();
        if (string.IsNullOrEmpty(path))
            throw new ArgumentException("sqlite DSN requires a path (e.g. 'sqlite:/tmp/app.db' or 'sqlite::memory:').");
        return $"Data Source={path}";
    }

    private static Dictionary<string, string> ParseKv(string body)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(body)) return dict;

        foreach (var raw in body.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = raw.Trim();
            if (pair.Length == 0) continue;
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var key = pair.Substring(0, eq).Trim();
            var value = pair.Substring(eq + 1).Trim();
            dict[key] = value;
        }
        return dict;
    }
}
