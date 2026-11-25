using System.Data;
using System.Data.Common;
using System.Diagnostics;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;

namespace Shape.Core;

public enum DbDriver
{
    Mysql,
    Pgsql,
    Sqlite,
    Mssql
}

/// <summary>
/// Thin Dapper-backed wrapper that gives Shape's mask-driven runtime a column-keyed
/// <see cref="QueryResult"/> shape, transaction handling, driver-aware identifier
/// quoting, and an <c>INSERT … returning id</c> helper.
///
/// Dapper handles array-param expansion natively (<c>WHERE id IN @ids</c> with
/// <c>parameters["ids"] = int[]</c> auto-expands across drivers), so Shape no
/// longer needs the regex-driven array expander it used to ship.
/// </summary>
public sealed class Database : IDisposable
{
    private readonly DbDriver _driver;
    private readonly DbConnection _connection;
    private DbTransaction? _transaction;
    private bool _inTransaction;
    private bool _disposed;

    public Database(DatabaseConfig config)
    {
        _driver = ParseDriver(config.Driver);

        try
        {
            _connection = CreateConnection(_driver, config.ConnectionString);
            _connection.Open();
        }
        catch (DbException ex)
        {
            throw DbError("Database connection failed", ex);
        }
    }

    public DbDriver Driver => _driver;
    public bool IsInTransaction => _inTransaction;

    public void BeginTransaction()
    {
        if (_inTransaction)
            throw new ApiException("DB_ERROR", "Transaction already started.", 500);
        try
        {
            _transaction = _connection.BeginTransaction();
            _inTransaction = true;
        }
        catch (DbException ex)
        {
            throw DbError("Failed to start transaction", ex);
        }
    }

    public void Commit()
    {
        if (!_inTransaction)
            throw new ApiException("DB_ERROR", "No transaction in progress.", 500);
        try
        {
            _transaction!.Commit();
            _transaction = null;
            _inTransaction = false;
        }
        catch (DbException ex)
        {
            throw DbError("Failed to commit transaction", ex);
        }
    }

    public void Rollback()
    {
        if (!_inTransaction)
            throw new ApiException("DB_ERROR", "No transaction in progress.", 500);
        try
        {
            _transaction!.Rollback();
            _transaction = null;
            _inTransaction = false;
        }
        catch (DbException ex)
        {
            throw DbError("Failed to rollback transaction", ex);
        }
    }

    public int Exec(string sql, Dictionary<string, object?> parameters)
    {
        try
        {
            return _connection.Execute(sql, AsDapperParams(parameters), _transaction);
        }
        catch (DbException ex)
        {
            throw MutationError("Database mutation failed", ex);
        }
    }

    public QueryResult Run(string sql, Dictionary<string, object?> parameters)
    {
        try
        {
            var sw = Stopwatch.StartNew();
            var rows = _connection
                .Query<dynamic>(sql, AsDapperParams(parameters), _transaction, buffered: true)
                .Cast<IDictionary<string, object>>()
                .ToList();

            var columns = new List<string>();
            var rowArrays = new List<object?[]>();

            if (rows.Count > 0)
                columns.AddRange(rows[0].Keys);

            foreach (var row in rows)
            {
                var arr = new object?[columns.Count];
                for (var i = 0; i < columns.Count; i++)
                    arr[i] = row.TryGetValue(columns[i], out var v) ? v : null;
                rowArrays.Add(arr);
            }

            sw.Stop();
            return new QueryResult
            {
                Columns = columns,
                Rows = rowArrays,
                Count = rowArrays.Count,
                Ms = Math.Round(sw.Elapsed.TotalMilliseconds, 3)
            };
        }
        catch (DbException ex)
        {
            throw DbError("Database query failed", ex);
        }
    }

    /// <summary>
    /// Run an <c>INSERT</c> and return the new row's primary key. Per-driver:
    /// MySQL uses <c>LAST_INSERT_ID()</c>, Postgres relies on a <c>RETURNING</c>
    /// clause appended automatically, SQLite uses <c>last_insert_rowid()</c>,
    /// SQL Server uses <c>SCOPE_IDENTITY()</c>.
    /// </summary>
    public long InsertReturningId(string sql, Dictionary<string, object?> parameters, string idColumn = "id")
    {
        try
        {
            switch (_driver)
            {
                case DbDriver.Mysql:
                    Exec(sql, parameters);
                    return _connection.ExecuteScalar<long>("SELECT LAST_INSERT_ID()", transaction: _transaction);

                case DbDriver.Sqlite:
                    Exec(sql, parameters);
                    return _connection.ExecuteScalar<long>("SELECT last_insert_rowid()", transaction: _transaction);

                case DbDriver.Pgsql:
                    var withReturning = sql.Contains("RETURNING", StringComparison.OrdinalIgnoreCase)
                        ? sql
                        : $"{sql.TrimEnd().TrimEnd(';')} RETURNING {QuoteIdentifier(idColumn)}";
                    return _connection.ExecuteScalar<long>(withReturning, AsDapperParams(parameters), _transaction);

                case DbDriver.Mssql:
                    // SQL Server has neither LAST_INSERT_ID() nor RETURNING. SCOPE_IDENTITY()
                    // is trigger-safe (unlike @@IDENTITY), but its scope is the *batch*, not the
                    // connection or transaction — read from a separate command it returns NULL
                    // (which Dapper coerces to 0) — so the INSERT and the id SELECT must travel
                    // in one batch. It returns numeric(38,0), so cast to bigint for a clean long.
                    return _connection.ExecuteScalar<long>(
                        MssqlInsertReturningIdBatch(sql), AsDapperParams(parameters), _transaction);

                default:
                    throw new NotSupportedException($"InsertReturningId not implemented for driver '{_driver}'.");
            }
        }
        catch (DbException ex)
        {
            throw MutationError("Insert returning id failed", ex);
        }
    }

    /// <summary>
    /// INSERT + <c>SCOPE_IDENTITY()</c> read fused into one batch — SCOPE_IDENTITY()
    /// returns NULL when evaluated in a batch other than the one that ran the INSERT.
    /// </summary>
    internal static string MssqlInsertReturningIdBatch(string insertSql) =>
        $"{insertSql.TrimEnd().TrimEnd(';')}; SELECT CAST(SCOPE_IDENTITY() AS bigint)";

    /// <summary>
    /// Build the allocating <c>INSERT … SELECT MAX(pk)+1</c> statement for tables whose
    /// int PK the framework must assign itself (legacy schemas without IDENTITY /
    /// AUTO_INCREMENT / sequence — see MANUAL-PK-PLAN.md §2.1). Allocation and insert
    /// collapse into one atomic statement so no racing writer fits between the read
    /// and the insert:
    /// <list type="bullet">
    ///   <item>MSSQL: <c>WITH (UPDLOCK, HOLDLOCK)</c> takes a key-range lock at the top
    ///   of the PK index; <c>OUTPUT INSERTED</c> returns the id in the same round trip.</item>
    ///   <item>Postgres: plain <c>COALESCE(MAX)+1</c> + trailing <c>RETURNING</c>;
    ///   the caller serializes via <c>pg_advisory_xact_lock</c> first.</item>
    ///   <item>MySQL/SQLite: <c>COALESCE(MAX)+1</c> without a returning clause — neither
    ///   driver can report a non-identity id from the INSERT itself, so the caller
    ///   reads it back (see <see cref="InsertWithManualPk"/>).</item>
    /// </list>
    /// <paramref name="columns"/> may be empty (row consisting of the PK alone).
    /// </summary>
    internal static string InsertAllocatingPkSql(DbDriver driver, string table, string pk, IReadOnlyCollection<string> columns)
    {
        var qTable = QuoteIdentifier(driver, table);
        var qPk = QuoteIdentifier(driver, pk);
        var columnList = string.Join(", ", new[] { pk }.Concat(columns).Select(c => QuoteIdentifier(driver, c)));
        var valueList = string.Concat(columns.Select(c => ", @" + c));

        return driver switch
        {
            DbDriver.Mssql =>
                $"INSERT INTO {qTable} ({columnList}) OUTPUT INSERTED.{qPk} " +
                $"SELECT ISNULL(MAX({qPk}), 0) + 1{valueList} FROM {qTable} WITH (UPDLOCK, HOLDLOCK)",
            DbDriver.Pgsql =>
                $"INSERT INTO {qTable} ({columnList}) " +
                $"SELECT COALESCE(MAX({qPk}), 0) + 1{valueList} FROM {qTable} RETURNING {qPk}",
            _ =>
                $"INSERT INTO {qTable} ({columnList}) " +
                $"SELECT COALESCE(MAX({qPk}), 0) + 1{valueList} FROM {qTable}"
        };
    }

    /// <summary>
    /// Insert a row into a manual-PK table (see MANUAL-PK-PLAN.md §2.1/§3), allocating
    /// <c>MAX(pk)+1</c> atomically, and return the allocated id. <paramref name="data"/>
    /// must NOT contain <paramref name="pk"/> — a client-supplied id goes through the
    /// regular <see cref="InsertReturningId"/> path instead.
    ///
    /// Runs inside a transaction: the caller's when one is open (batch mutations), an
    /// own short-lived one otherwise. The transaction matters per driver — Postgres'
    /// advisory lock is <c>xact</c>-scoped, and MySQL/SQLite read the allocated id back
    /// via <c>SELECT MAX(pk)</c>, which is only race-free against the same snapshot +
    /// own uncommitted write (the allocating locking read saw at least everything the
    /// snapshot did, so our row is the max within the transaction).
    ///
    /// Retries up to 3× on unique violation / deadlock — the safety net against external
    /// writers (the legacy app allocating its own MAX+1) that don't honor our locks.
    /// Inside a caller-owned transaction the retry re-runs the statement without
    /// rollback; that is sound on MySQL/SQLite/MSSQL unique violations (statement-level
    /// errors), while a Postgres failure aborts the transaction and surfaces as a plain
    /// DB_ERROR on the next attempt — acceptable, since the advisory lock already makes
    /// duplicates among framework writers impossible there.
    /// </summary>
    public long InsertWithManualPk(string table, string pk, Dictionary<string, object?> data)
    {
        if (data.ContainsKey(pk))
            throw new ApiException("DB_ERROR",
                $"InsertWithManualPk: data must not contain the PK column '{pk}'.", 500);

        const int maxAttempts = 3;
        var sql = InsertAllocatingPkSql(_driver, table, pk, data.Keys.ToList());
        var ownTransaction = !_inTransaction;

        for (var attempt = 1; ; attempt++)
        {
            if (ownTransaction)
                BeginTransaction();
            try
            {
                long id;
                switch (_driver)
                {
                    case DbDriver.Mssql:
                        // OUTPUT INSERTED.[pk] yields a one-row result set.
                        id = _connection.ExecuteScalar<long>(sql, AsDapperParams(data), _transaction);
                        break;

                    case DbDriver.Pgsql:
                        // Serialize framework allocators per table up front; the lock
                        // releases at commit/rollback (xact-scoped).
                        _connection.Execute(
                            "SELECT pg_advisory_xact_lock(hashtext(@k))",
                            new { k = "shape_pk:" + table },
                            _transaction);
                        id = _connection.ExecuteScalar<long>(sql, AsDapperParams(data), _transaction);
                        break;

                    default:
                        // MySQL LAST_INSERT_ID() and SQLite last_insert_rowid() only
                        // track identity/rowid columns — useless for a manually
                        // assigned pk. Read the id back with MAX(pk) in the same
                        // transaction (own insert is the transaction-visible max).
                        _connection.Execute(sql, AsDapperParams(data), _transaction);
                        id = _connection.ExecuteScalar<long>(
                            $"SELECT MAX({QuoteIdentifier(pk)}) FROM {QuoteIdentifier(table)}",
                            transaction: _transaction);
                        break;
                }

                if (ownTransaction)
                    Commit();
                return id;
            }
            catch (DbException ex)
            {
                if (ownTransaction && _inTransaction)
                    Rollback();

                if (!IsPkAllocationRace(ex))
                    throw MutationError("Insert with manual PK allocation failed", ex);

                if (attempt >= maxAttempts)
                {
                    throw new ApiException(
                        "DB_ERROR",
                        $"Manual PK allocation on '{table}' failed after {maxAttempts} attempts — concurrent writers keep racing MAX({pk})+1.",
                        500,
                        null,
                        new Dictionary<string, object?>
                        {
                            ["detail"] = "PK_ALLOCATION_RACE",
                            ["table"] = table,
                            ["pk"] = pk,
                            ["attempts"] = maxAttempts,
                            ["sqlstate"] = ex.SqlState,
                            ["driver_error_code"] = DriverErrorCode(ex)
                        },
                        ex);
                }
            }
        }
    }

    /// <summary>
    /// True when the exception is the retryable manual-PK race: a unique-key violation
    /// (an external writer grabbed the same MAX+1) or an MSSQL deadlock victim (range
    /// lock vs. other locks). Codes per MANUAL-PK-PLAN.md §2.1: MSSQL 2627/2601/1205,
    /// MySQL 1062, Postgres SQLSTATE 23505, SQLite error 19 (SQLITE_CONSTRAINT).
    /// </summary>
    internal static bool IsPkAllocationRace(DbException ex) => ex switch
    {
        SqlException ms => ms.Number is 2627 or 2601 or 1205,
        MySqlException my => (int)my.ErrorCode == 1062,
        SqliteException sqlite => sqlite.SqliteErrorCode == 19,
        _ => ex.SqlState == "23505"
    };

    /// <summary>
    /// Quote a column or table identifier with the driver's native delimiter:
    /// MySQL uses backticks, Postgres and SQLite use double-quotes, SQL Server
    /// uses brackets (<c>[name]</c>, escaping <c>]</c> → <c>]]</c>). Embedded
    /// delimiters are doubled per ANSI SQL.
    /// </summary>
    public string QuoteIdentifier(string name) => QuoteIdentifier(_driver, name);

    /// <summary>Static form of <see cref="QuoteIdentifier(string)"/> for connection-free SQL builders.</summary>
    internal static string QuoteIdentifier(DbDriver driver, string name) => driver switch
    {
        DbDriver.Mysql => "`" + name.Replace("`", "``") + "`",
        DbDriver.Pgsql or DbDriver.Sqlite => "\"" + name.Replace("\"", "\"\"") + "\"",
        DbDriver.Mssql => "[" + name.Replace("]", "]]") + "]",
        _ => name
    };

    /// <summary>
    /// Row-limiting prefix inserted immediately after <c>SELECT</c>. SQL Server
    /// has no <c>LIMIT</c>; it caps rows with <c>SELECT TOP n …</c>. All other
    /// drivers use a trailing <c>LIMIT</c> clause (see <see cref="RowLimitSuffix"/>),
    /// so this returns the empty string for them. Pair the two so exactly one
    /// applies per driver.
    /// </summary>
    public string TopClause(int n) => _driver == DbDriver.Mssql ? $"TOP {n} " : "";

    /// <summary>
    /// Row-limiting suffix appended to the end of a query. MySQL/Postgres/SQLite
    /// use <c>LIMIT n</c>; SQL Server caps rows with <see cref="TopClause"/> instead
    /// and returns the empty string here.
    /// </summary>
    public string RowLimitSuffix(int n) => _driver == DbDriver.Mssql ? "" : $" LIMIT {n}";

    public void Dispose()
    {
        if (_disposed) return;
        try
        {
            if (_inTransaction)
            {
                try { _transaction?.Rollback(); }
                catch { /* swallow during dispose */ }
            }
            _transaction?.Dispose();
            _connection.Dispose();
        }
        finally
        {
            _disposed = true;
        }
    }

    private static DbDriver ParseDriver(string raw) => raw.ToLowerInvariant() switch
    {
        "mysql" => DbDriver.Mysql,
        "pgsql" or "postgres" or "postgresql" => DbDriver.Pgsql,
        "sqlite" => DbDriver.Sqlite,
        "sqlsrv" or "mssql" or "sqlserver" => DbDriver.Mssql,
        _ => throw new NotSupportedException(
            $"Driver '{raw}' is not recognized. Supported: mysql, pgsql, sqlite, mssql.")
    };

    private static DbConnection CreateConnection(DbDriver driver, string connectionString) => driver switch
    {
        DbDriver.Mysql => new MySqlConnection(connectionString),
        DbDriver.Pgsql => new NpgsqlConnection(connectionString),
        DbDriver.Sqlite => new SqliteConnection(connectionString),
        DbDriver.Mssql => new SqlConnection(connectionString),
        _ => throw new NotSupportedException($"Unknown driver: {driver}")
    };

    /// <summary>
    /// Convert Shape's untyped <c>Dictionary&lt;string, object?&gt;</c> param map
    /// into a Dapper <see cref="DynamicParameters"/> bag. Dapper handles
    /// <c>IEnumerable</c> values for <c>IN @list</c> expansion automatically.
    /// </summary>
    private static DynamicParameters AsDapperParams(Dictionary<string, object?> parameters)
    {
        var dp = new DynamicParameters();
        foreach (var (key, value) in parameters)
            dp.Add(key, value);
        return dp;
    }

    /// <summary>
    /// Map unique / foreign-key / not-null violations to a 409 CONSTRAINT_VIOLATION
    /// with an actionable hint; anything else stays an opaque DB_ERROR.
    /// </summary>
    private static ApiException MutationError(string summary, DbException ex)
    {
        var code = DriverErrorCode(ex);
        var state = ex.SqlState ?? "";
        var msg = ex.Message.ToLowerInvariant();

        string? kind = null, message = null, hint = null;
        if (code is "1062" or "2627" or "2601" || state == "23505" || msg.Contains("unique constraint failed"))
        {
            kind = "UNIQUE_VIOLATION";
            message = "A row with the same unique value already exists.";
            hint = "Use a different value for the unique column(s), or update the existing row instead.";
        }
        else if (code is "1451" or "1452" or "547" || state == "23503" || msg.Contains("foreign key constraint failed"))
        {
            kind = "FK_VIOLATION";
            message = "Foreign key constraint failed: the referenced row does not exist, or other rows still reference this one.";
            hint = "Check that every *_id value points at an existing row, or delete/detach dependent rows first.";
        }
        else if (code is "1048" or "1364" or "515" || state == "23502" || msg.Contains("not null constraint failed"))
        {
            kind = "NOT_NULL_VIOLATION";
            message = "A required column was null or missing.";
            hint = "Provide a value for every NOT NULL column; see the manifest for nullable flags.";
        }

        if (kind is null)
            return DbError(summary, ex);

        return new ApiException("CONSTRAINT_VIOLATION", message!, 409, null,
            new Dictionary<string, object?> { ["detail"] = kind, ["hint"] = hint }, ex);
    }

    private static ApiException DbError(string summary, DbException ex)
    {
        var details = new Dictionary<string, object?>
        {
            ["sqlstate"] = ex.SqlState,
            ["driver_error_code"] = DriverErrorCode(ex)
        };
        return new ApiException("DB_ERROR", $"{summary}: {ex.Message}", 500, null, details, ex);
    }

    private static string DriverErrorCode(DbException ex) => ex switch
    {
        MySqlException my => my.ErrorCode.ToString(),
        NpgsqlException pg => pg.ErrorCode.ToString(),
        SqliteException sqlite => sqlite.SqliteErrorCode.ToString(),
        // SQL Server carries no SQLSTATE; the engine error number (e.g. 2627 /
        // 2601 for unique violations) is the identifying code.
        SqlException ms => ms.Number.ToString(),
        _ => ex.ErrorCode.ToString()
    };
}

public sealed class DatabaseConfig
{
    public string ConnectionString { get; set; } = "";
    public string Driver { get; set; } = "mysql";

    /// <summary>
    /// Build a <see cref="DatabaseConfig"/> from a PHP/PDO-style DSN
    /// (<c>pgsql:host=…;dbname=…</c>, <c>mysql:host=…;dbname=…</c>,
    /// <c>sqlite:/path/to/file.db</c>). User and password are concatenated
    /// into the native connection string. Lets a shared <c>env.local.php</c>-style
    /// config file boot both <c>papi-lib</c> (PHP) and <c>Shape</c> (C#).
    /// </summary>
    public static DatabaseConfig FromDsn(string dsn, string? user = null, string? password = null)
    {
        var (driver, conn) = DsnTranslator.Translate(dsn, user, password);
        return new DatabaseConfig { Driver = driver, ConnectionString = conn };
    }
}

public sealed class QueryResult
{
    public List<string> Columns { get; set; } = new();
    public List<object?[]> Rows { get; set; } = new();
    public int Count { get; set; }
    public double Ms { get; set; }
}
