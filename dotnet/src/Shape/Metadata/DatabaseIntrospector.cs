using Dapper;
using Shape.Core;
using Shape.Generator;

namespace Shape.Metadata;

/// <summary>
/// Reads database schema via INFORMATION_SCHEMA (MySQL/PostgreSQL) or
/// <c>PRAGMA</c> (SQLite) and produces an <see cref="EntityMeta"/> list
/// in the same shape as <see cref="EntityReader"/>. Powers DB-first mode.
/// </summary>
public sealed class DatabaseIntrospector
{
    private readonly Database _database;

    public DatabaseIntrospector(Database database)
    {
        _database = database;
    }

    /// <summary>List physical tables in the database (driver-specific catalog query).</summary>
    public List<string> ListTables() => _database.Driver switch
    {
        DbDriver.Mysql => _database.Run(
            "SELECT TABLE_NAME AS name FROM information_schema.TABLES " +
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME",
            new()).Rows.Select(r => r[0]?.ToString() ?? "").ToList(),

        DbDriver.Pgsql => _database.Run(
            "SELECT table_name AS name FROM information_schema.tables " +
            "WHERE table_schema = current_schema() AND table_type = 'BASE TABLE' ORDER BY table_name",
            new()).Rows.Select(r => r[0]?.ToString() ?? "").ToList(),

        DbDriver.Sqlite => _database.Run(
            "SELECT name FROM sqlite_master WHERE type = 'table' " +
            "AND name NOT LIKE 'sqlite_%' ORDER BY name",
            new()).Rows.Select(r => r[0]?.ToString() ?? "").ToList(),

        // INFORMATION_SCHEMA spelled uppercase throughout the MSSQL arms: catalog-view
        // names follow the server collation, and a case-sensitive one only resolves
        // the canonical uppercase spelling.
        DbDriver.Mssql => _database.Run(
            "SELECT TABLE_NAME AS name FROM INFORMATION_SCHEMA.TABLES " +
            "WHERE TABLE_SCHEMA = SCHEMA_NAME() AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME",
            new()).Rows.Select(r => r[0]?.ToString() ?? "").ToList(),

        _ => new List<string>()
    };

    /// <summary>Column metadata for one table, normalized across drivers.</summary>
    public List<ColumnInfo> DescribeColumns(string table) => _database.Driver switch
    {
        DbDriver.Mysql => DescribeColumnsMysql(table),
        DbDriver.Pgsql => DescribeColumnsPgsql(table),
        DbDriver.Sqlite => DescribeColumnsSqlite(table),
        DbDriver.Mssql => DescribeColumnsMssql(table),
        _ => new List<ColumnInfo>()
    };

    /// <summary>FK columns for one table → (referenced table, referenced column).</summary>
    public Dictionary<string, ForeignKeyInfo> DescribeForeignKeys(string table) => _database.Driver switch
    {
        DbDriver.Mysql => DescribeForeignKeysMysql(table),
        DbDriver.Pgsql => DescribeForeignKeysPgsql(table),
        DbDriver.Sqlite => DescribeForeignKeysSqlite(table),
        DbDriver.Mssql => DescribeForeignKeysMssql(table),
        _ => new Dictionary<string, ForeignKeyInfo>()
    };

    /// <summary>
    /// Build the full <see cref="EntityMeta"/> list. <paramref name="includeTables"/>,
    /// when set, replaces the catalog scan; <paramref name="excludeTables"/> filters
    /// the catalog scan when no include list is supplied.
    /// </summary>
    public List<EntityMeta> Introspect(
        IReadOnlyList<string>? includeTables = null,
        IReadOnlyList<string>? excludeTables = null,
        bool autoValidators = true,
        TenancyConfig? tenancy = null,
        Dictionary<string, ScopeConfig>? scopes = null)
    {
        var tables = includeTables ?? ListTables();
        var exclude = new HashSet<string>(excludeTables ?? Array.Empty<string>(), StringComparer.Ordinal);
        var targetTables = tables
            .Where(table => includeTables != null || !exclude.Contains(table))
            .ToList();
        var entities = new List<EntityMeta>();

        Dictionary<string, List<ColumnInfo>> columnsByTable;
        Dictionary<string, Dictionary<string, ForeignKeyInfo>> foreignKeysByTable;
        Dictionary<string, string?> tableCommentsByTable;
        Dictionary<string, List<List<string>>> uniqueConstraintsByTable;

        if (_database.Driver == DbDriver.Sqlite)
        {
            columnsByTable = targetTables.ToDictionary(
                table => table,
                table => DescribeColumns(table),
                StringComparer.Ordinal);
            foreignKeysByTable = targetTables.ToDictionary(
                table => table,
                table => DescribeForeignKeys(table),
                StringComparer.Ordinal);
            tableCommentsByTable = targetTables.ToDictionary(
                table => table,
                _ => (string?)null,
                StringComparer.Ordinal);
            uniqueConstraintsByTable = targetTables.ToDictionary(
                table => table,
                table => DescribeUniqueConstraints(table),
                StringComparer.Ordinal);
        }
        else
        {
            columnsByTable = DescribeColumnsBulk(targetTables);
            foreignKeysByTable = DescribeForeignKeysBulk(targetTables);
            tableCommentsByTable = DescribeTableCommentsBulk(targetTables);
            uniqueConstraintsByTable = DescribeUniqueConstraintsBulk(targetTables);
        }

        foreach (var table in targetTables)
        {
            var columns = columnsByTable.TryGetValue(table, out var tableColumns)
                ? tableColumns
                : DescribeColumns(table);
            if (columns.Count == 0) continue;

            var fks = foreignKeysByTable.TryGetValue(table, out var tableFks)
                ? tableFks
                : DescribeForeignKeys(table);
            var tableComment = tableCommentsByTable.TryGetValue(table, out var comment)
                ? comment
                : DescribeTableComment(table);
            var uniques = uniqueConstraintsByTable.TryGetValue(table, out var tableUniques)
                ? tableUniques
                : DescribeUniqueConstraints(table);

            // DetectTenantColumn only matches on column name, so check the raw
            // column list directly instead of allocating a throwaway FieldMeta
            // per column (the real fields are built just below).
            var tenantColumn = tenancy != null
                && columns.Any(col => string.Equals(col.Name, tenancy.Column, StringComparison.Ordinal))
                ? tenancy.Column
                : null;

            var fields = columns.Select(col =>
            {
                fks.TryGetValue(col.Name, out var fk);
                var field = new FieldMeta
                {
                    Name = col.Name,
                    ClrType = ClrTypeFor(col.DbType),
                    ClrTypeName = ClrTypeFor(col.DbType).Name,
                    DbType = col.DbType,
                    Nullable = col.Nullable,
                    IsPrimary = col.IsPrimary,
                    IsAutoIncrement = col.AutoIncrement,
                    Unsigned = col.Unsigned,
                    Length = col.Length,
                    Precision = col.Precision,
                    Scale = col.Scale,
                    HasDefault = col.HasDefault,
                    DefaultValue = col.Default,
                    FkColumn = fk?.ReferencedColumn,
                    Description = string.IsNullOrEmpty(col.Description) ? null : col.Description,
                    IsTenantColumn = tenancy != null && string.Equals(col.Name, tenancy.Column, StringComparison.Ordinal),
                    // Flag scope columns before AutoValidators runs so they get
                    // the same never-required treatment as the tenancy column.
                    // TenancyResolver.Apply re-flags later (owns detection for
                    // both entity-first and DB-first paths).
                    IsScopeColumn = scopes != null && scopes.ContainsKey(col.Name)
                };
                if (autoValidators)
                {
                    foreach (var rule in AutoValidators.FromField(field))
                        field.Validators.Add(rule);
                }
                return field;
            }).ToList();

            entities.Add(new EntityMeta
            {
                ClrType = typeof(object), // DB-first: no CLR class
                Table = table,
                Writable = false,
                Description = string.IsNullOrEmpty(tableComment) ? null : tableComment,
                Fields = fields,
                SoftDeleteColumn = EntityMeta.DetectSoftDeleteColumn(fields),
                AuditColumns = EntityMeta.DetectAuditColumns(fields),
                TenantColumn = tenantColumn,
                UniqueConstraints = uniques
            });
        }

        return entities;
    }

    /// <summary>Table-level comment (Postgres pg_description / MySQL TABLE_COMMENT). SQLite returns null.</summary>
    public string? DescribeTableComment(string table) => _database.Driver switch
    {
        DbDriver.Mysql => DescribeTableCommentMysql(table),
        DbDriver.Pgsql => DescribeTableCommentPgsql(table),
        DbDriver.Mssql => DescribeTableCommentMssql(table),
        _ => null
    };

    /// <summary>
    /// Unique constraints — each inner list is one constraint's column set,
    /// in declaration order. PRIMARY KEY is excluded (already represented by
    /// <see cref="ColumnInfo.IsPrimary"/>).
    /// </summary>
    public List<List<string>> DescribeUniqueConstraints(string table) => _database.Driver switch
    {
        DbDriver.Mysql => DescribeUniqueConstraintsMysql(table),
        DbDriver.Pgsql => DescribeUniqueConstraintsPgsql(table),
        DbDriver.Sqlite => DescribeUniqueConstraintsSqlite(table),
        DbDriver.Mssql => DescribeUniqueConstraintsMssql(table),
        _ => new List<List<string>>()
    };

    private Dictionary<string, List<ColumnInfo>> DescribeColumnsBulkMysql(IReadOnlyList<string> tables)
    {
        const string sql = @"
            SELECT TABLE_NAME AS table_name,
                   COLUMN_NAME AS name,
                   DATA_TYPE AS data_type,
                   IS_NULLABLE AS is_nullable,
                   COLUMN_KEY AS column_key,
                   EXTRA AS extra,
                   COLUMN_TYPE AS column_type,
                   CHARACTER_MAXIMUM_LENGTH AS char_len,
                   NUMERIC_PRECISION AS num_precision,
                   NUMERIC_SCALE AS num_scale,
                   COLUMN_DEFAULT AS col_default,
                   COLUMN_COMMENT AS col_comment
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN @tables
            ORDER BY TABLE_NAME, ORDINAL_POSITION";
        var rows = QueryDynamic(sql, new { tables });
        var result = tables.ToDictionary(table => table, _ => new List<ColumnInfo>(), StringComparer.Ordinal);

        foreach (var r in rows)
        {
            var table = (string)r["table_name"]!;
            var columnType = (r["column_type"]?.ToString() ?? "").ToLowerInvariant();
            var extra = (r["extra"]?.ToString() ?? "").ToLowerInvariant();
            result[table].Add(new ColumnInfo
            {
                Name = (string)r["name"]!,
                DbType = ((string)r["data_type"]!).ToLowerInvariant(),
                Nullable = string.Equals((string)r["is_nullable"]!, "YES", StringComparison.OrdinalIgnoreCase),
                IsPrimary = string.Equals(r["column_key"]?.ToString(), "PRI", StringComparison.Ordinal),
                AutoIncrement = extra.Contains("auto_increment", StringComparison.Ordinal),
                Unsigned = columnType.Contains("unsigned", StringComparison.Ordinal),
                Length = ToInt(r["char_len"]),
                Precision = ToInt(r["num_precision"]),
                Scale = ToInt(r["num_scale"]),
                HasDefault = r["col_default"] != null,
                Default = r["col_default"],
                Description = r["col_comment"]?.ToString()
            });
        }

        return result;
    }

    private Dictionary<string, List<ColumnInfo>> DescribeColumnsBulkPgsql(IReadOnlyList<string> tables)
    {
        const string sql = @"
            SELECT c.table_name AS table_name,
                   c.column_name AS name,
                   c.data_type AS data_type,
                   c.is_nullable AS is_nullable,
                   c.character_maximum_length AS char_len,
                   c.numeric_precision AS num_precision,
                   c.numeric_scale AS num_scale,
                   c.column_default AS col_default,
                   EXISTS (
                       SELECT 1 FROM information_schema.table_constraints tc
                         JOIN information_schema.constraint_column_usage ccu
                           ON ccu.constraint_name = tc.constraint_name
                        WHERE tc.constraint_type = 'PRIMARY KEY'
                          AND tc.table_schema = c.table_schema
                          AND tc.table_name = c.table_name
                          AND ccu.column_name = c.column_name
                   ) AS is_primary,
                   c.is_identity AS is_identity,
                   col_description(format('%I.%I', c.table_schema, c.table_name)::regclass, c.ordinal_position) AS col_description
            FROM information_schema.columns c
            WHERE c.table_schema = current_schema() AND c.table_name IN @tables
            ORDER BY c.table_name, c.ordinal_position";
        var rows = QueryDynamic(sql, new { tables });
        var result = tables.ToDictionary(table => table, _ => new List<ColumnInfo>(), StringComparer.Ordinal);

        foreach (var r in rows)
        {
            var table = (string)r["table_name"]!;
            var dataType = ((string)r["data_type"]!).ToLowerInvariant();
            var defaultValue = r["col_default"]?.ToString();
            var autoIncrement = ToBool(r["is_identity"]) || (defaultValue != null && defaultValue.Contains("nextval(", StringComparison.OrdinalIgnoreCase));

            result[table].Add(new ColumnInfo
            {
                Name = (string)r["name"]!,
                DbType = dataType,
                Nullable = string.Equals((string)r["is_nullable"]!, "YES", StringComparison.OrdinalIgnoreCase),
                IsPrimary = ToBool(r["is_primary"]),
                AutoIncrement = autoIncrement,
                Unsigned = false,
                Length = ToInt(r["char_len"]),
                Precision = ToInt(r["num_precision"]),
                Scale = ToInt(r["num_scale"]),
                HasDefault = r["col_default"] != null,
                Default = autoIncrement ? null : r["col_default"],
                Description = r["col_description"]?.ToString()
            });
        }

        return result;
    }

    private Dictionary<string, List<ColumnInfo>> DescribeColumnsBulkMssql(IReadOnlyList<string> tables)
    {
        const string sql = @"
            SELECT c.TABLE_NAME AS table_name,
                   c.COLUMN_NAME AS name,
                   c.DATA_TYPE AS data_type,
                   c.IS_NULLABLE AS is_nullable,
                   c.CHARACTER_MAXIMUM_LENGTH AS char_len,
                   c.NUMERIC_PRECISION AS num_precision,
                   c.NUMERIC_SCALE AS num_scale,
                   c.COLUMN_DEFAULT AS col_default,
                   COLUMNPROPERTY(
                     OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)),
                     c.COLUMN_NAME, 'IsIdentity') AS is_identity,
                   CASE WHEN EXISTS (
                     SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                       JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                         ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                        AND kcu.TABLE_SCHEMA = tc.TABLE_SCHEMA
                      WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                        AND tc.TABLE_SCHEMA = c.TABLE_SCHEMA
                        AND tc.TABLE_NAME = c.TABLE_NAME
                        AND kcu.COLUMN_NAME = c.COLUMN_NAME
                   ) THEN 1 ELSE 0 END AS is_primary,
                   CAST(ep.value AS nvarchar(max)) AS col_description
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN sys.columns sc
                   ON sc.object_id = OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME))
                  AND sc.name = c.COLUMN_NAME
            LEFT JOIN sys.extended_properties ep
                   ON ep.class = 1
                  AND ep.major_id = sc.object_id
                  AND ep.minor_id = sc.column_id
                  AND ep.name = 'MS_Description'
            WHERE c.TABLE_SCHEMA = SCHEMA_NAME() AND c.TABLE_NAME IN @tables
            ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION";
        var rows = QueryDynamic(sql, new { tables });
        var result = tables.ToDictionary(table => table, _ => new List<ColumnInfo>(), StringComparer.Ordinal);

        foreach (var r in rows)
        {
            var table = (string)r["table_name"]!;
            var len = ToInt(r["char_len"]);
            if (len is < 0) len = null;

            result[table].Add(new ColumnInfo
            {
                Name = (string)r["name"]!,
                DbType = ((string)r["data_type"]!).ToLowerInvariant(),
                Nullable = string.Equals((string)r["is_nullable"]!, "YES", StringComparison.OrdinalIgnoreCase),
                IsPrimary = ToInt(r["is_primary"]) == 1,
                AutoIncrement = ToInt(r["is_identity"]) == 1,
                Unsigned = false,
                Length = len,
                Precision = ToInt(r["num_precision"]),
                Scale = ToInt(r["num_scale"]),
                HasDefault = r["col_default"] != null,
                Default = r["col_default"],
                Description = r["col_description"]?.ToString()
            });
        }

        return result;
    }

    private Dictionary<string, Dictionary<string, ForeignKeyInfo>> DescribeForeignKeysBulkMysql(IReadOnlyList<string> tables)
    {
        const string sql = @"
            SELECT TABLE_NAME AS table_name,
                   COLUMN_NAME AS column_name,
                   REFERENCED_TABLE_NAME AS ref_table,
                   REFERENCED_COLUMN_NAME AS ref_column
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN @tables AND REFERENCED_TABLE_NAME IS NOT NULL";
        var rows = QueryDynamic(sql, new { tables });
        var result = tables.ToDictionary(
            table => table,
            _ => new Dictionary<string, ForeignKeyInfo>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var r in rows)
        {
            var table = (string)r["table_name"]!;
            result[table][(string)r["column_name"]!] = new ForeignKeyInfo
            {
                ReferencedTable = (string)r["ref_table"]!,
                ReferencedColumn = (string)r["ref_column"]!
            };
        }

        return result;
    }

    private Dictionary<string, Dictionary<string, ForeignKeyInfo>> DescribeForeignKeysBulkPgsql(IReadOnlyList<string> tables)
    {
        const string sql = @"
            SELECT tc.table_name AS table_name,
                   kcu.column_name AS column_name,
                   ccu.table_name AS ref_table,
                   ccu.column_name AS ref_column
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                 ON tc.constraint_name = kcu.constraint_name
                AND tc.table_schema = kcu.table_schema
            JOIN information_schema.constraint_column_usage ccu
                 ON ccu.constraint_name = tc.constraint_name
                AND ccu.table_schema = tc.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_schema = current_schema()
              AND tc.table_name IN @tables";
        var rows = QueryDynamic(sql, new { tables });
        var result = tables.ToDictionary(
            table => table,
            _ => new Dictionary<string, ForeignKeyInfo>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var r in rows)
        {
            var table = (string)r["table_name"]!;
            result[table][(string)r["column_name"]!] = new ForeignKeyInfo
            {
                ReferencedTable = (string)r["ref_table"]!,
                ReferencedColumn = (string)r["ref_column"]!
            };
        }

        return result;
    }

    private Dictionary<string, Dictionary<string, ForeignKeyInfo>> DescribeForeignKeysBulkMssql(IReadOnlyList<string> tables)
    {
        // sys.foreign_key_columns — see DescribeForeignKeysMssql for why
        // INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE is wrong here.
        const string sql = @"
            SELECT tp.name AS table_name,
                   cp.name AS column_name,
                   tr.name AS ref_table,
                   cr.name AS ref_column
            FROM sys.foreign_key_columns fkc
            JOIN sys.tables tp  ON tp.object_id = fkc.parent_object_id
            JOIN sys.schemas sp ON sp.schema_id = tp.schema_id
            JOIN sys.columns cp ON cp.object_id = fkc.parent_object_id AND cp.column_id = fkc.parent_column_id
            JOIN sys.tables tr  ON tr.object_id = fkc.referenced_object_id
            JOIN sys.columns cr ON cr.object_id = fkc.referenced_object_id AND cr.column_id = fkc.referenced_column_id
            WHERE sp.name = SCHEMA_NAME()
              AND tp.name IN @tables";
        var rows = QueryDynamic(sql, new { tables });
        var result = tables.ToDictionary(
            table => table,
            _ => new Dictionary<string, ForeignKeyInfo>(StringComparer.Ordinal),
            StringComparer.Ordinal);

        foreach (var r in rows)
        {
            var table = (string)r["table_name"]!;
            result[table][(string)r["column_name"]!] = new ForeignKeyInfo
            {
                ReferencedTable = (string)r["ref_table"]!,
                ReferencedColumn = (string)r["ref_column"]!
            };
        }

        return result;
    }

    private Dictionary<string, List<List<string>>> DescribeUniqueConstraintsBulkMysql(IReadOnlyList<string> tables)
    {
        const string sql = @"
            SELECT TABLE_NAME AS table_name,
                   INDEX_NAME AS idx,
                   COLUMN_NAME AS col,
                   SEQ_IN_INDEX AS seq
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN @tables
              AND NON_UNIQUE = 0
              AND INDEX_NAME <> 'PRIMARY'
            ORDER BY TABLE_NAME, INDEX_NAME, SEQ_IN_INDEX";
        var rows = QueryDynamic(sql, new { tables });
        return GroupBulkConstraintColumns(rows, tables, "table_name", "idx", "col");
    }

    private Dictionary<string, List<List<string>>> DescribeUniqueConstraintsBulkPgsql(IReadOnlyList<string> tables)
    {
        const string sql = @"
            SELECT tc.table_name AS table_name,
                   tc.constraint_name AS idx,
                   kcu.column_name AS col,
                   kcu.ordinal_position AS seq
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                 ON tc.constraint_name = kcu.constraint_name
                AND tc.table_schema = kcu.table_schema
            WHERE tc.constraint_type = 'UNIQUE'
              AND tc.table_schema = current_schema()
              AND tc.table_name IN @tables
            ORDER BY tc.table_name, tc.constraint_name, kcu.ordinal_position";
        var rows = QueryDynamic(sql, new { tables });
        return GroupBulkConstraintColumns(rows, tables, "table_name", "idx", "col");
    }

    private Dictionary<string, List<List<string>>> DescribeUniqueConstraintsBulkMssql(IReadOnlyList<string> tables)
    {
        const string sql = @"
            SELECT tc.TABLE_NAME AS table_name,
                   tc.CONSTRAINT_NAME AS idx,
                   kcu.COLUMN_NAME AS col,
                   kcu.ORDINAL_POSITION AS seq
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                 ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
                AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA
            WHERE tc.CONSTRAINT_TYPE = 'UNIQUE'
              AND tc.TABLE_SCHEMA = SCHEMA_NAME()
              AND tc.TABLE_NAME IN @tables
            ORDER BY tc.TABLE_NAME, tc.CONSTRAINT_NAME, kcu.ORDINAL_POSITION";
        var rows = QueryDynamic(sql, new { tables });
        return GroupBulkConstraintColumns(rows, tables, "table_name", "idx", "col");
    }

    private Dictionary<string, string?> DescribeTableCommentsBulkMysql(IReadOnlyList<string> tables)
    {
        const string sql = @"
            SELECT TABLE_NAME AS table_name,
                   TABLE_COMMENT AS comment
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN @tables";
        var rows = QueryDynamic(sql, new { tables });
        var result = tables.ToDictionary(table => table, _ => (string?)null, StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var table = (string)r["table_name"]!;
            result[table] = r["comment"]?.ToString();
        }
        return result;
    }

    private Dictionary<string, string?> DescribeTableCommentsBulkPgsql(IReadOnlyList<string> tables)
    {
        const string sql = @"
            SELECT c.relname AS table_name,
                   obj_description(c.oid, 'pg_class') AS comment
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = current_schema()
              AND c.relkind = 'r'
              AND c.relname IN @tables";
        var rows = QueryDynamic(sql, new { tables });
        var result = tables.ToDictionary(table => table, _ => (string?)null, StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var table = (string)r["table_name"]!;
            result[table] = r["comment"]?.ToString();
        }
        return result;
    }

    private Dictionary<string, string?> DescribeTableCommentsBulkMssql(IReadOnlyList<string> tables)
    {
        const string sql = @"
            SELECT t.TABLE_NAME AS table_name,
                   CAST(ep.value AS nvarchar(max)) AS comment
            FROM INFORMATION_SCHEMA.TABLES t
            LEFT JOIN sys.extended_properties ep
                   ON ep.class = 1
                  AND ep.minor_id = 0
                  AND ep.name = 'MS_Description'
                  AND ep.major_id = OBJECT_ID(QUOTENAME(t.TABLE_SCHEMA) + '.' + QUOTENAME(t.TABLE_NAME))
            WHERE t.TABLE_SCHEMA = SCHEMA_NAME()
              AND t.TABLE_TYPE = 'BASE TABLE'
              AND t.TABLE_NAME IN @tables";
        var rows = QueryDynamic(sql, new { tables });
        var result = tables.ToDictionary(table => table, _ => (string?)null, StringComparer.Ordinal);
        foreach (var r in rows)
        {
            var table = (string)r["table_name"]!;
            result[table] = r["comment"]?.ToString();
        }
        return result;
    }

    /// <summary>Convenience for <see cref="AdminMaskGenerator"/> — table → column-name list.</summary>
    public List<TableSchema> AsTableSchemas()
    {
        var schemas = new List<TableSchema>();
        var tables = ListTables();
        var columnsByTable = DescribeColumnsBulk(tables);

        foreach (var table in tables)
        {
            var cols = columnsByTable.TryGetValue(table, out var columns)
                ? columns.Select(c => c.Name).ToList()
                : DescribeColumns(table).Select(c => c.Name).ToList();
            schemas.Add(new TableSchema(table, cols));
        }
        return schemas;
    }

    private Dictionary<string, List<ColumnInfo>> DescribeColumnsBulk(IReadOnlyList<string> tables)
    {
        if (tables.Count == 0)
            return new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal);

        return _database.Driver switch
        {
            DbDriver.Mysql => DescribeColumnsBulkMysql(tables),
            DbDriver.Pgsql => DescribeColumnsBulkPgsql(tables),
            DbDriver.Mssql => DescribeColumnsBulkMssql(tables),
            DbDriver.Sqlite => tables.ToDictionary(table => table, DescribeColumns, StringComparer.Ordinal),
            _ => new Dictionary<string, List<ColumnInfo>>(StringComparer.Ordinal)
        };
    }

    private Dictionary<string, Dictionary<string, ForeignKeyInfo>> DescribeForeignKeysBulk(IReadOnlyList<string> tables)
    {
        if (tables.Count == 0)
            return new Dictionary<string, Dictionary<string, ForeignKeyInfo>>(StringComparer.Ordinal);

        return _database.Driver switch
        {
            DbDriver.Mysql => DescribeForeignKeysBulkMysql(tables),
            DbDriver.Pgsql => DescribeForeignKeysBulkPgsql(tables),
            DbDriver.Mssql => DescribeForeignKeysBulkMssql(tables),
            DbDriver.Sqlite => tables.ToDictionary(table => table, DescribeForeignKeys, StringComparer.Ordinal),
            _ => new Dictionary<string, Dictionary<string, ForeignKeyInfo>>(StringComparer.Ordinal)
        };
    }

    private Dictionary<string, List<List<string>>> DescribeUniqueConstraintsBulk(IReadOnlyList<string> tables)
    {
        if (tables.Count == 0)
            return new Dictionary<string, List<List<string>>>(StringComparer.Ordinal);

        return _database.Driver switch
        {
            DbDriver.Mysql => DescribeUniqueConstraintsBulkMysql(tables),
            DbDriver.Pgsql => DescribeUniqueConstraintsBulkPgsql(tables),
            DbDriver.Mssql => DescribeUniqueConstraintsBulkMssql(tables),
            DbDriver.Sqlite => tables.ToDictionary(table => table, DescribeUniqueConstraints, StringComparer.Ordinal),
            _ => new Dictionary<string, List<List<string>>>(StringComparer.Ordinal)
        };
    }

    private Dictionary<string, string?> DescribeTableCommentsBulk(IReadOnlyList<string> tables)
    {
        if (tables.Count == 0)
            return new Dictionary<string, string?>(StringComparer.Ordinal);

        return _database.Driver switch
        {
            DbDriver.Mysql => DescribeTableCommentsBulkMysql(tables),
            DbDriver.Pgsql => DescribeTableCommentsBulkPgsql(tables),
            DbDriver.Mssql => DescribeTableCommentsBulkMssql(tables),
            DbDriver.Sqlite => tables.ToDictionary(table => table, _ => (string?)null, StringComparer.Ordinal),
            _ => new Dictionary<string, string?>(StringComparer.Ordinal)
        };
    }

    // ── MySQL ─────────────────────────────────────────────────────────

    private List<ColumnInfo> DescribeColumnsMysql(string table)
    {
        const string sql = @"
            SELECT
              COLUMN_NAME                  AS name,
              DATA_TYPE                    AS data_type,
              IS_NULLABLE                  AS is_nullable,
              COLUMN_KEY                   AS column_key,
              EXTRA                        AS extra,
              COLUMN_TYPE                  AS column_type,
              CHARACTER_MAXIMUM_LENGTH     AS char_len,
              NUMERIC_PRECISION            AS num_precision,
              NUMERIC_SCALE                AS num_scale,
              COLUMN_DEFAULT               AS col_default,
              COLUMN_COMMENT               AS col_comment
            FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t
            ORDER BY ORDINAL_POSITION";
        var rows = QueryDynamic(sql, new { t = table });

        return rows.Select(r => new ColumnInfo
        {
            Name = (string)r["name"]!,
            DbType = ((string)r["data_type"]!).ToLowerInvariant(),
            Nullable = string.Equals((string)r["is_nullable"]!, "YES", StringComparison.OrdinalIgnoreCase),
            IsPrimary = string.Equals(r["column_key"]?.ToString(), "PRI", StringComparison.Ordinal),
            AutoIncrement = (r["extra"]?.ToString() ?? "").IndexOf("auto_increment", StringComparison.OrdinalIgnoreCase) >= 0,
            Unsigned = (r["column_type"]?.ToString() ?? "").IndexOf("unsigned", StringComparison.OrdinalIgnoreCase) >= 0,
            Length = ToInt(r["char_len"]),
            Precision = ToInt(r["num_precision"]),
            Scale = ToInt(r["num_scale"]),
            HasDefault = r["col_default"] != null,
            Default = r["col_default"],
            Description = r["col_comment"]?.ToString()
        }).ToList();
    }

    private string? DescribeTableCommentMysql(string table)
    {
        const string sql = @"
            SELECT TABLE_COMMENT AS comment
            FROM information_schema.TABLES
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t";
        var rows = QueryDynamic(sql, new { t = table });
        return rows.Count == 0 ? null : rows[0]["comment"]?.ToString();
    }

    private List<List<string>> DescribeUniqueConstraintsMysql(string table)
    {
        // INDEX_NAME groups columns within one composite UNIQUE INDEX.
        const string sql = @"
            SELECT INDEX_NAME AS idx, COLUMN_NAME AS col, SEQ_IN_INDEX AS seq
            FROM information_schema.STATISTICS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t
              AND NON_UNIQUE = 0
              AND INDEX_NAME <> 'PRIMARY'
            ORDER BY INDEX_NAME, SEQ_IN_INDEX";
        var rows = QueryDynamic(sql, new { t = table });
        return GroupConstraintColumns(rows, "idx", "col");
    }

    private Dictionary<string, ForeignKeyInfo> DescribeForeignKeysMysql(string table)
    {
        const string sql = @"
            SELECT
              COLUMN_NAME              AS column_name,
              REFERENCED_TABLE_NAME    AS ref_table,
              REFERENCED_COLUMN_NAME   AS ref_column
            FROM information_schema.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND REFERENCED_TABLE_NAME IS NOT NULL";
        var rows = QueryDynamic(sql, new { t = table });

        var result = new Dictionary<string, ForeignKeyInfo>();
        foreach (var r in rows)
        {
            result[(string)r["column_name"]!] = new ForeignKeyInfo
            {
                ReferencedTable = (string)r["ref_table"]!,
                ReferencedColumn = (string)r["ref_column"]!
            };
        }
        return result;
    }

    // ── Postgres ──────────────────────────────────────────────────────

    private List<ColumnInfo> DescribeColumnsPgsql(string table)
    {
        const string sql = @"
            SELECT
              c.column_name                                                  AS name,
              c.data_type                                                    AS data_type,
              c.is_nullable                                                  AS is_nullable,
              c.character_maximum_length                                     AS char_len,
              c.numeric_precision                                            AS num_precision,
              c.numeric_scale                                                AS num_scale,
              c.column_default                                               AS col_default,
              EXISTS (
                SELECT 1 FROM information_schema.table_constraints tc
                  JOIN information_schema.constraint_column_usage ccu
                       ON ccu.constraint_name = tc.constraint_name
                 WHERE tc.constraint_type = 'PRIMARY KEY'
                   AND tc.table_schema = c.table_schema
                   AND tc.table_name   = c.table_name
                   AND ccu.column_name = c.column_name
              )                                                              AS is_primary,
              col_description(
                format('%I.%I', c.table_schema, c.table_name)::regclass,
                c.ordinal_position
              )                                                              AS col_description
            FROM information_schema.columns c
            WHERE c.table_schema = current_schema() AND c.table_name = @t
            ORDER BY c.ordinal_position";
        var rows = QueryDynamic(sql, new { t = table });

        return rows.Select(r => new ColumnInfo
        {
            Name = (string)r["name"]!,
            DbType = ((string)r["data_type"]!).ToLowerInvariant(),
            Nullable = string.Equals((string)r["is_nullable"]!, "YES", StringComparison.OrdinalIgnoreCase),
            IsPrimary = ToBool(r["is_primary"]),
            // Postgres marks SERIAL columns by a 'nextval(...)' default — sufficient signal here.
            AutoIncrement = (r["col_default"]?.ToString() ?? "").Contains("nextval(", StringComparison.OrdinalIgnoreCase),
            Length = ToInt(r["char_len"]),
            Precision = ToInt(r["num_precision"]),
            Scale = ToInt(r["num_scale"]),
            HasDefault = r["col_default"] != null,
            Default = r["col_default"],
            Description = r["col_description"]?.ToString()
        }).ToList();
    }

    private string? DescribeTableCommentPgsql(string table)
    {
        const string sql = @"
            SELECT obj_description(format('%I.%I', current_schema(), @t)::regclass, 'pg_class') AS comment";
        var rows = QueryDynamic(sql, new { t = table });
        return rows.Count == 0 ? null : rows[0]["comment"]?.ToString();
    }

    private List<List<string>> DescribeUniqueConstraintsPgsql(string table)
    {
        const string sql = @"
            SELECT tc.constraint_name AS idx,
                   kcu.column_name    AS col,
                   kcu.ordinal_position AS seq
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                 ON tc.constraint_name = kcu.constraint_name
                AND tc.table_schema = kcu.table_schema
            WHERE tc.constraint_type = 'UNIQUE'
              AND tc.table_schema = current_schema()
              AND tc.table_name = @t
            ORDER BY tc.constraint_name, kcu.ordinal_position";
        var rows = QueryDynamic(sql, new { t = table });
        return GroupConstraintColumns(rows, "idx", "col");
    }

    private Dictionary<string, ForeignKeyInfo> DescribeForeignKeysPgsql(string table)
    {
        const string sql = @"
            SELECT
              kcu.column_name              AS column_name,
              ccu.table_name               AS ref_table,
              ccu.column_name              AS ref_column
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                 ON tc.constraint_name = kcu.constraint_name
            JOIN information_schema.constraint_column_usage ccu
                 ON ccu.constraint_name = tc.constraint_name
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_schema = current_schema()
              AND tc.table_name = @t";
        var rows = QueryDynamic(sql, new { t = table });

        var result = new Dictionary<string, ForeignKeyInfo>();
        foreach (var r in rows)
        {
            result[(string)r["column_name"]!] = new ForeignKeyInfo
            {
                ReferencedTable = (string)r["ref_table"]!,
                ReferencedColumn = (string)r["ref_column"]!
            };
        }
        return result;
    }

    // ── SQL Server ────────────────────────────────────────────────────

    private List<ColumnInfo> DescribeColumnsMssql(string table)
    {
        // SQL Server exposes the ANSI INFORMATION_SCHEMA, so this mirrors the
        // Postgres reader. Identity lives outside information_schema (COLUMNPROPERTY
        // 'IsIdentity'); column descriptions live in sys.extended_properties
        // (MS_Description), the analogue of PG col_description / MySQL COLUMN_COMMENT.
        const string sql = @"
            SELECT
              c.COLUMN_NAME              AS name,
              c.DATA_TYPE                AS data_type,
              c.IS_NULLABLE              AS is_nullable,
              c.CHARACTER_MAXIMUM_LENGTH AS char_len,
              c.NUMERIC_PRECISION        AS num_precision,
              c.NUMERIC_SCALE            AS num_scale,
              c.COLUMN_DEFAULT           AS col_default,
              COLUMNPROPERTY(
                OBJECT_ID(QUOTENAME(SCHEMA_NAME()) + '.' + QUOTENAME(c.TABLE_NAME)),
                c.COLUMN_NAME, 'IsIdentity')                                    AS is_identity,
              CASE WHEN EXISTS (
                SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                  JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                       ON kcu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME
                      AND kcu.TABLE_SCHEMA   = tc.TABLE_SCHEMA
                 WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'
                   AND tc.TABLE_SCHEMA = c.TABLE_SCHEMA
                   AND tc.TABLE_NAME   = c.TABLE_NAME
                   AND kcu.COLUMN_NAME = c.COLUMN_NAME
              ) THEN 1 ELSE 0 END                                              AS is_primary,
              CAST(ep.value AS nvarchar(max))                                  AS col_description
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN sys.columns sc
                   ON sc.object_id = OBJECT_ID(QUOTENAME(SCHEMA_NAME()) + '.' + QUOTENAME(c.TABLE_NAME))
                  AND sc.name = c.COLUMN_NAME
            LEFT JOIN sys.extended_properties ep
                   ON ep.class = 1
                  AND ep.major_id = sc.object_id
                  AND ep.minor_id = sc.column_id
                  AND ep.name = 'MS_Description'
            WHERE c.TABLE_SCHEMA = SCHEMA_NAME() AND c.TABLE_NAME = @t
            ORDER BY c.ORDINAL_POSITION";
        var rows = QueryDynamic(sql, new { t = table });

        return rows.Select(r =>
        {
            // nvarchar(max)/varbinary(max) report CHARACTER_MAXIMUM_LENGTH = -1,
            // meaning unbounded — not a real max length.
            var len = ToInt(r["char_len"]);
            if (len is < 0) len = null;
            return new ColumnInfo
            {
                Name = (string)r["name"]!,
                DbType = ((string)r["data_type"]!).ToLowerInvariant(),
                Nullable = string.Equals((string)r["is_nullable"]!, "YES", StringComparison.OrdinalIgnoreCase),
                IsPrimary = ToInt(r["is_primary"]) == 1,
                AutoIncrement = ToInt(r["is_identity"]) == 1,
                Length = len,
                Precision = ToInt(r["num_precision"]),
                Scale = ToInt(r["num_scale"]),
                HasDefault = r["col_default"] != null,
                Default = r["col_default"],
                Description = r["col_description"]?.ToString()
            };
        }).ToList();
    }

    private string? DescribeTableCommentMssql(string table)
    {
        const string sql = @"
            SELECT CAST(ep.value AS nvarchar(max)) AS comment
            FROM sys.extended_properties ep
            WHERE ep.class = 1
              AND ep.minor_id = 0
              AND ep.name = 'MS_Description'
              AND ep.major_id = OBJECT_ID(QUOTENAME(SCHEMA_NAME()) + '.' + QUOTENAME(@t))";
        var rows = QueryDynamic(sql, new { t = table });
        return rows.Count == 0 ? null : rows[0]["comment"]?.ToString();
    }

    private List<List<string>> DescribeUniqueConstraintsMssql(string table)
    {
        const string sql = @"
            SELECT tc.CONSTRAINT_NAME   AS idx,
                   kcu.COLUMN_NAME      AS col,
                   kcu.ORDINAL_POSITION AS seq
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                 ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
                AND tc.TABLE_SCHEMA    = kcu.TABLE_SCHEMA
            WHERE tc.CONSTRAINT_TYPE = 'UNIQUE'
              AND tc.TABLE_SCHEMA = SCHEMA_NAME()
              AND tc.TABLE_NAME = @t
            ORDER BY tc.CONSTRAINT_NAME, kcu.ORDINAL_POSITION";
        var rows = QueryDynamic(sql, new { t = table });
        return GroupConstraintColumns(rows, "idx", "col");
    }

    private Dictionary<string, ForeignKeyInfo> DescribeForeignKeysMssql(string table)
    {
        // sys.foreign_key_columns, NOT INFORMATION_SCHEMA: on SQL Server,
        // CONSTRAINT_COLUMN_USAGE joined on a FOREIGN KEY constraint returns
        // the *referencing* table's own columns — ref_table came back as the
        // table itself, so every FK looked self-referential and FK-derived
        // features (relations, validate_fks, TenantExposurePlanner) saw no
        // foreign keys at all on MSSQL.
        const string sql = @"
            SELECT
              cp.name AS column_name,
              tr.name AS ref_table,
              cr.name AS ref_column
            FROM sys.foreign_key_columns fkc
            JOIN sys.tables tp  ON tp.object_id = fkc.parent_object_id
            JOIN sys.schemas sp ON sp.schema_id = tp.schema_id
            JOIN sys.columns cp ON cp.object_id = fkc.parent_object_id AND cp.column_id = fkc.parent_column_id
            JOIN sys.tables tr  ON tr.object_id = fkc.referenced_object_id
            JOIN sys.columns cr ON cr.object_id = fkc.referenced_object_id AND cr.column_id = fkc.referenced_column_id
            WHERE sp.name = SCHEMA_NAME()
              AND tp.name = @t";
        var rows = QueryDynamic(sql, new { t = table });

        var result = new Dictionary<string, ForeignKeyInfo>();
        foreach (var r in rows)
        {
            result[(string)r["column_name"]!] = new ForeignKeyInfo
            {
                ReferencedTable = (string)r["ref_table"]!,
                ReferencedColumn = (string)r["ref_column"]!
            };
        }
        return result;
    }

    // ── SQLite ────────────────────────────────────────────────────────

    private List<ColumnInfo> DescribeColumnsSqlite(string table)
    {
        // PRAGMA table_info returns: cid, name, type, notnull, dflt_value, pk
        var rows = QueryDynamic($"PRAGMA table_info({QuoteSqliteIdent(table)})", null);

        return rows.Select(r =>
        {
            var raw = (string)(r["type"] ?? "");
            var (baseType, length, precision, scale) = ParseSqliteType(raw);
            return new ColumnInfo
            {
                Name = (string)r["name"]!,
                DbType = baseType,
                Nullable = ToInt(r["notnull"]) == 0,
                IsPrimary = ToInt(r["pk"]) > 0,
                // SQLite auto-increment lives only on INTEGER PRIMARY KEY (rowid alias).
                AutoIncrement = ToInt(r["pk"]) > 0 && string.Equals(baseType, "integer", StringComparison.OrdinalIgnoreCase),
                Length = length,
                Precision = precision,
                Scale = scale,
                HasDefault = r["dflt_value"] != null,
                Default = r["dflt_value"]
            };
        }).ToList();
    }

    private List<List<string>> DescribeUniqueConstraintsSqlite(string table)
    {
        // PRAGMA index_list returns indexes; PRAGMA index_info gives columns
        // for each. Filter to unique=1 indexes that aren't auto-created for
        // the primary key (origin != 'pk').
        var indexes = QueryDynamic($"PRAGMA index_list({QuoteSqliteIdent(table)})", null);
        var result = new List<List<string>>();
        foreach (var idx in indexes)
        {
            var unique = ToInt(idx["unique"]) == 1;
            if (!unique) continue;
            var origin = idx["origin"]?.ToString() ?? "";
            if (string.Equals(origin, "pk", StringComparison.OrdinalIgnoreCase)) continue;
            var name = idx["name"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(name)) continue;

            var cols = QueryDynamic($"PRAGMA index_info({QuoteSqliteIdent(name)})", null);
            var ordered = cols
                .OrderBy(r => ToInt(r["seqno"]) ?? 0)
                .Select(r => r["name"]?.ToString() ?? "")
                .Where(c => c.Length > 0)
                .ToList();
            if (ordered.Count > 0) result.Add(ordered);
        }
        return result;
    }

    private static List<List<string>> GroupConstraintColumns(
        List<Dictionary<string, object?>> rows, string indexKey, string colKey)
    {
        var grouped = rows
            .GroupBy(r => r[indexKey]?.ToString() ?? "")
            .Where(g => g.Key.Length > 0)
            .Select(g => g.Select(r => r[colKey]?.ToString() ?? "").Where(c => c.Length > 0).ToList())
            .Where(c => c.Count > 0)
            .ToList();
        return grouped;
    }

    private static Dictionary<string, List<List<string>>> GroupBulkConstraintColumns(
        List<Dictionary<string, object?>> rows,
        IReadOnlyList<string> tables,
        string tableKey,
        string indexKey,
        string colKey)
    {
        var result = tables.ToDictionary(
            table => table,
            _ => new List<List<string>>(),
            StringComparer.Ordinal);

        foreach (var tableGroup in rows
                     .GroupBy(r => r[tableKey]?.ToString() ?? "")
                     .Where(g => g.Key.Length > 0))
        {
            var constraints = tableGroup
                .GroupBy(r => r[indexKey]?.ToString() ?? "")
                .Where(g => g.Key.Length > 0)
                .Select(g => g.Select(r => r[colKey]?.ToString() ?? "")
                    .Where(c => c.Length > 0)
                    .ToList())
                .Where(cols => cols.Count > 0)
                .ToList();
            if (result.ContainsKey(tableGroup.Key))
                result[tableGroup.Key] = constraints;
        }

        return result;
    }

    private Dictionary<string, ForeignKeyInfo> DescribeForeignKeysSqlite(string table)
    {
        // pragma_foreign_key_list: id, seq, table, from, to, on_update, on_delete, match
        var rows = QueryDynamic($"PRAGMA foreign_key_list({QuoteSqliteIdent(table)})", null);

        var result = new Dictionary<string, ForeignKeyInfo>();
        foreach (var r in rows)
        {
            var col = r["from"]?.ToString() ?? "";
            if (col.Length == 0) continue;
            result[col] = new ForeignKeyInfo
            {
                ReferencedTable = r["table"]?.ToString() ?? "",
                ReferencedColumn = r["to"]?.ToString() ?? ""
            };
        }
        return result;
    }

    private static (string Type, int? Length, int? Precision, int? Scale) ParseSqliteType(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return ("text", null, null, null);

        var open = raw.IndexOf('(');
        if (open < 0)
            return (raw.ToLowerInvariant(), null, null, null);

        var name = raw[..open].Trim().ToLowerInvariant();
        var close = raw.IndexOf(')', open + 1);
        if (close <= open) return (name, null, null, null);

        var inside = raw[(open + 1)..close];
        var parts = inside.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1 && int.TryParse(parts[0], out var len))
            return (name, len, null, null);
        if (parts.Length == 2 && int.TryParse(parts[0], out var p) && int.TryParse(parts[1], out var s))
            return (name, null, p, s);
        return (name, null, null, null);
    }

    private static string QuoteSqliteIdent(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    // ── Helpers ───────────────────────────────────────────────────────

    private List<Dictionary<string, object?>> QueryDynamic(string sql, object? parameters)
    {
        // Use Dapper directly for catalog reads — the column shape varies per
        // query and we don't need our column-keyed QueryResult here.
        var rows = _database.GetType()
            .GetField("_connection", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(_database);
        var conn = (System.Data.Common.DbConnection)rows!;

        return conn.Query<dynamic>(sql, parameters)
            .Cast<IDictionary<string, object>>()
            .Select(d => new Dictionary<string, object?>(d!))
            .ToList();
    }

    private static Type ClrTypeFor(string dbType) => dbType.ToLowerInvariant() switch
    {
        "int" or "integer" or "smallint" or "mediumint" or "bigint" or "tinyint" => typeof(long),
        "float" or "real" or "double" or "decimal" or "numeric" or "money" or "smallmoney" => typeof(double),
        // MSSQL has no native boolean — `bit` is the logical boolean (0/1).
        "bool" or "boolean" or "bit" => typeof(bool),
        "date" or "datetime" or "timestamp" or "datetime2" or "smalldatetime" => typeof(DateTime),
        "datetimeoffset" => typeof(DateTimeOffset),
        "uniqueidentifier" => typeof(Guid),
        // nvarchar/nchar/varchar/char and everything else fall through to string.
        _ => typeof(string)
    };

    private static int? ToInt(object? value)
    {
        if (value == null) return null;
        return value switch
        {
            int i => i,
            long l => (int)l,
            short s => s,
            byte b => b,
            string str when int.TryParse(str, out var parsed) => parsed,
            _ => null
        };
    }

    private static bool ToBool(object? value)
    {
        return value switch
        {
            bool b => b,
            int i => i != 0,
            long l => l != 0,
            string s => s == "t" || s == "true" || s == "1",
            _ => false
        };
    }
}

public sealed class ColumnInfo
{
    public required string Name { get; init; }
    public required string DbType { get; init; }
    public bool Nullable { get; init; }
    public bool IsPrimary { get; init; }
    public bool AutoIncrement { get; init; }
    public bool Unsigned { get; init; }
    public int? Length { get; init; }
    public int? Precision { get; init; }
    public int? Scale { get; init; }
    public bool HasDefault { get; init; }
    public object? Default { get; init; }

    /// <summary>
    /// Native column comment from the database catalog. Postgres
    /// <c>pg_description</c>, MySQL <c>INFORMATION_SCHEMA.COLUMNS.COLUMN_COMMENT</c>,
    /// SQLite has no native column comments.
    /// </summary>
    public string? Description { get; init; }
}

public sealed class ForeignKeyInfo
{
    public required string ReferencedTable { get; init; }
    public required string ReferencedColumn { get; init; }
}
