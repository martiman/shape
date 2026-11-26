using System.Globalization;
using System.Text.Json;
using Shape.Core;

namespace Shape.Audit;

/// <summary>
/// Tier-1 append-only audit logger. Writes one row to <c>audit_log</c> per
/// successful mutation, capturing WHEN, WHO, WHAT was touched, WHICH columns
/// changed (column names only — values stay out of the log by default).
///
/// Schema convention (dev creates this table; <c>ShapeApp</c> detects it at boot):
/// <code>
/// CREATE TABLE audit_log (
///   id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
///   occurred_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
///   actor_id    VARCHAR(64) NULL,
///   actor_email VARCHAR(190) NULL,
///   actor_role  VARCHAR(40) NULL,
///   table_name  VARCHAR(64) NOT NULL,
///   row_id      VARCHAR(64) NULL,
///   action      VARCHAR(16) NOT NULL,
///   changed     TEXT NULL
/// );
/// </code>
///
/// Invoked by <see cref="BatchExecutor"/>'s audit-callback hook. Inside a
/// transaction the audit write is atomic with the data change; outside,
/// best-effort.
/// </summary>
public sealed class AuditLogger
{
    private readonly Database _database;
    private readonly string _tableName;

    private readonly bool _withValues;

    public AuditLogger(Database database, string tableName = "audit_log", bool withValues = false)
    {
        _database = database;
        _tableName = tableName;
        _withValues = withValues;
    }

    /// <summary>
    /// Tier 2 guard: the audit table needs nullable <c>old_values</c> / <c>new_values</c> columns. Called once
    /// before the first mutation so a missing column fails loudly before any data changes.
    /// </summary>
    public void AssertValueColumns()
    {
        try
        {
            // Bare identifiers on purpose: SQLite treats a double-quoted unknown identifier as a string
            // literal, which would make this probe pass even when the columns are missing.
            _database.Run(
                $"SELECT old_values, new_values FROM {_database.QuoteIdentifier(_tableName)} WHERE 1 = 0", new());
        }
        catch (Exception)
        {
            throw new ApiException(
                "AUDIT_CONFIG_ERROR",
                $"audit_diff is enabled but '{_tableName}' has no old_values / new_values columns.",
                500,
                null,
                new Dictionary<string, object?>
                {
                    ["hint"] = $"ALTER TABLE {_tableName} ADD old_values TEXT NULL, ADD new_values TEXT NULL;  (MySQL; use your dialect's ADD COLUMN syntax elsewhere.)"
                });
        }
    }

    /// <summary>
    /// Compatible with <see cref="BatchExecutor.AuditLogger"/> delegate signature
    /// — call <c>executor.SetAuditLogger(auditLogger.Log)</c> to wire it.
    /// </summary>
    public void Log(
        string targetTable,
        string action,
        string? rowId,
        Dictionary<string, object?> authContext,
        List<string> changedColumns) =>
        LogWithValues(targetTable, action, rowId, authContext, changedColumns, null, null);

    /// <summary>Tier 2: same entry plus the before/after values (only written when constructed with values).</summary>
    public void LogWithValues(
        string targetTable,
        string action,
        string? rowId,
        Dictionary<string, object?> authContext,
        List<string> changedColumns,
        Dictionary<string, object?>? oldValues,
        Dictionary<string, object?>? newValues)
    {
        var row = new Dictionary<string, object?>
        {
            ["occurred_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["actor_id"] = ResolveActorId(authContext),
            ["actor_email"] = StringOrNull(authContext.GetValueOrDefault("email")),
            ["actor_role"] = StringOrNull(authContext.GetValueOrDefault("role")),
            ["table_name"] = targetTable,
            ["row_id"] = rowId,
            ["action"] = action,
            ["changed"] = changedColumns.Count == 0
                ? null
                : JsonSerializer.Serialize(changedColumns)
        };

        if (_withValues)
        {
            static string? Enc(Dictionary<string, object?>? v) =>
                v == null || v.Count == 0 ? null : JsonSerializer.Serialize(v, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            row["old_values"] = Enc(oldValues);
            row["new_values"] = Enc(newValues);
        }

        var cols = row.Keys.ToList();
        var quoted = cols.Select(c => _database.QuoteIdentifier(c));
        var placeholders = cols.Select(c => "@" + c);

        var sql = "INSERT INTO " + _database.QuoteIdentifier(_tableName)
            + " (" + string.Join(", ", quoted) + ")"
            + " VALUES (" + string.Join(", ", placeholders) + ")";

        _database.Exec(sql, row);
    }

    /// <summary>
    /// Pick a stable actor identifier from the auth context (id / oid / sub).
    /// Mirrors <see cref="BatchExecutor.AuthUserId"/> so the same caller shows
    /// up identically across audit rows and per-row audit columns.
    /// </summary>
    private static string? ResolveActorId(Dictionary<string, object?> authContext)
    {
        foreach (var key in new[] { "id", "oid", "sub" })
        {
            if (!authContext.TryGetValue(key, out var val) || val is null)
                continue;

            switch (val)
            {
                case int i when i > 0: return i.ToString(CultureInfo.InvariantCulture);
                case long l when l > 0: return l.ToString(CultureInfo.InvariantCulture);
                case string s when !string.IsNullOrEmpty(s) && s != "0": return s;
            }
        }
        return null;
    }

    private static string? StringOrNull(object? value)
    {
        if (value == null) return null;
        var s = value.ToString();
        return string.IsNullOrEmpty(s) ? null : s;
    }
}
