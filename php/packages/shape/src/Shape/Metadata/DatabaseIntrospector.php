<?php

declare(strict_types=1);

namespace Shape\Metadata;

use ShapeRuntime\Core\Database;

/**
 * Reads database schema via INFORMATION_SCHEMA (MySQL/PostgreSQL/SQL Server)
 * or PRAGMA (SQLite) and produces EntityMeta[] — the same format as EntityReader.
 */
final class DatabaseIntrospector
{
    private string $driver;
    private ?string $schema;

    public function __construct(private readonly Database $database)
    {
        $this->driver = $database->getDriver();
        $this->schema = $database->getSchema();
    }

    /**
     * Normalize a role list from raw access config: accepts a single role
     * string or a list; anything that isn't a non-empty string is dropped.
     *
     * @return list<string>
     */
    private static function roleList(mixed $value): array
    {
        if (is_string($value)) {
            $value = [$value];
        }
        if (!is_array($value)) {
            return [];
        }
        $out = [];
        foreach ($value as $role) {
            if (is_string($role) && $role !== '') {
                $out[] = $role;
            }
        }
        return $out;
    }

    /**
     * @param array<string, mixed> $accessConfig Table => roles (flat list, or a
     *                              read/write/create/update/delete split map) —
     *                              raw config, re-validated per entry.
     * @param array<string, string> $validateConfig  e.g. ['books.price' => 'positive']
     * @param list<string> $excludeTables
     * @param list<string>|null $includeTables When set, only these tables are introspected
     *                                          (SHOW TABLES is skipped). Overrides $excludeTables.
     * @param bool $autoValidators When true (default), derive notEmpty + maxLen rules
     *                              from column metadata; manual validateConfig appends on top.
     * @return array<string, EntityMeta> keyed by table name
     */
    public function introspect(
        array $accessConfig,
        array $validateConfig,
        array $excludeTables,
        ?array $includeTables = null,
        bool $autoValidators = true,
    ): array {
        $tables = $includeTables ?? $this->getTables();
        $entities = [];

        // Determine which tables actually need metadata so we don't bulk-fetch
        // for tables we'll skip via excludeTables. The pre-filter shrinks the
        // IN-list and avoids needless work on apps that exclude most tables.
        $targetTables = [];
        foreach ($tables as $table) {
            if ($includeTables === null && in_array($table, $excludeTables, true)) {
                continue;
            }
            $targetTables[] = $table;
        }

        // Bulk-fetch columns / PKs / FKs for the whole target set in a fixed
        // number of round trips (3 each for MySQL + pgsql). The previous
        // per-table flow ran 3 round trips per table — 50ms × 3 × N — which
        // showed up as ~75s cold boot on a 83-table remote PG schema
        // SQLite keeps per-table because PRAGMA
        // can't filter by multi-table set and queries are local anyway.
        $bulkColumns = $this->bulkColumns($targetTables);
        $bulkPks = $this->bulkPrimaryKeys($targetTables);
        $bulkFks = $this->bulkForeignKeys($targetTables);

        foreach ($targetTables as $table) {
            $columns = $bulkColumns[$table] ?? [];
            $primaryKeys = $bulkPks[$table] ?? [];
            $foreignKeys = $bulkFks[$table] ?? [];
            $tableComment = $this->describeTableComment($table);
            $uniqueConstraints = $this->describeUniqueConstraints($table);

            $fields = [];
            foreach ($columns as $col) {
                $name = $col['name'];
                $isPrimary = in_array($name, $primaryKeys, true);
                $fk = $foreignKeys[$name] ?? null;

                $validators = $autoValidators ? AutoValidators::fromColumn($col) : [];
                $configKey = $table . '.' . $name;
                if (isset($validateConfig[$configKey])) {
                    $rules = $validateConfig[$configKey];
                    $manualRules = is_array($rules) ? $rules : [$rules];
                    $validators = array_merge($validators, $manualRules);
                }

                $fields[] = new FieldMeta(
                    name: $name,
                    phpType: $this->dbTypeToPhp($col['type']),
                    dbType: $col['type'],
                    nullable: $col['nullable'],
                    isPrimary: $isPrimary,
                    isAutoIncrement: $col['auto_increment'],
                    unsigned: $col['unsigned'],
                    length: $col['length'],
                    precision: $col['precision'],
                    scale: $col['scale'],
                    hasDefault: $col['has_default'],
                    default: $col['default'],
                    fkEntity: $fk['table'] ?? null,
                    fkColumn: $fk['column'] ?? null,
                    validators: $validators,
                    enumValues: null,
                    description: $col['description'] ?? null,
                );
            }

            $access = $accessConfig[$table] ?? $accessConfig['*'] ?? [];
            $readRoles = [];
            $writeRoles = [];
            $writeRolesByAction = [];
            if (is_array($access)) {
                $isSplit = isset($access['read']) || isset($access['write'])
                    || isset($access['create']) || isset($access['update']) || isset($access['delete']);
                if ($isSplit) {
                    $readRoles = self::roleList($access['read'] ?? []);
                    $writeRoles = self::roleList($access['write'] ?? []);
                    // Per-operation split: each action resolves via the
                    // action → write cascade once ANY action key is explicit;
                    // an empty resolved list then means "nobody".
                    $create = self::roleList($access['create'] ?? []);
                    $update = self::roleList($access['update'] ?? []);
                    $delete = self::roleList($access['delete'] ?? []);
                    if ($create !== [] || $update !== [] || $delete !== []) {
                        $writeRolesByAction = [
                            'create' => $create !== [] ? $create : $writeRoles,
                            'update' => $update !== [] ? $update : $writeRoles,
                            'delete' => $delete !== [] ? $delete : $writeRoles,
                        ];
                    }
                } else {
                    $readRoles = self::roleList($access);
                    $writeRoles = $readRoles;
                }
            }

            $entities[$table] = new EntityMeta(
                class: $table,
                table: $table,
                writable: $writeRoles !== [] || $writeRolesByAction !== [],
                fields: $fields,
                readRoles: $readRoles,
                writeRoles: $writeRoles,
                softDeleteColumn: EntityMeta::detectSoftDeleteColumn($fields),
                auditColumns: EntityMeta::detectAuditColumns($fields),
                uniqueConstraints: $uniqueConstraints,
                description: $tableComment,
                writeRolesByAction: $writeRolesByAction,
            );
        }

        return $entities;
    }

    /** @return list<string> Live list of tables in the database. */
    public function listTables(): array
    {
        return $this->getTables();
    }

    /**
     * Public column reader for tables not part of the main entity introspection.
     *
     * @return list<array{name:string,type:string,nullable:bool,auto_increment:bool,unsigned:bool,length:?int,precision:?int,scale:?int,has_default:bool,default:mixed,description:?string}>
     */
    public function describeColumns(string $table): array
    {
        return $this->getColumns($table);
    }

    /**
     * Table comment / description from the catalog. Returns null on SQLite or
     * when the table has no comment.
     */
    public function describeTableComment(string $table): ?string
    {
        return match ($this->driver) {
            'pgsql' => $this->getPgsqlTableComment($table),
            'sqlsrv' => $this->getMssqlTableComment($table),
            'mysql' => $this->getMysqlTableComment($table),
            default => null,
        };
    }

    /**
     * UNIQUE constraints, each inner list is one constraint's column set in
     * declaration order. PRIMARY KEY is excluded.
     *
     * @return list<list<string>>
     */
    public function describeUniqueConstraints(string $table): array
    {
        return match ($this->driver) {
            'pgsql' => $this->getPgsqlUniqueConstraints($table),
            'sqlsrv' => $this->getMssqlUniqueConstraints($table),
            'sqlite' => $this->getSqliteUniqueConstraints($table),
            default => $this->getMysqlUniqueConstraints($table),
        };
    }

    public function driver(): string
    {
        return $this->driver;
    }

    // ── Table listing ─────────────────────────────────────────────────

    /** @return list<string> */
    private function getTables(): array
    {
        $result = match ($this->driver) {
            'sqlite' => $this->database->run(
                "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name",
                []
            ),
            'pgsql' => $this->database->run(
                "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' ORDER BY table_name",
                []
            ),
            'sqlsrv' => $this->database->run(
                "SELECT TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = " . $this->mssqlSchemaFilter() . " AND TABLE_TYPE = 'BASE TABLE' ORDER BY TABLE_NAME",
                ['schema' => $this->schema]
            ),
            default => $this->database->run('SHOW TABLES', []),
        };

        return array_map(fn($row) => (string)$row[0], $result['rows']);
    }

    // ── Column reading ────────────────────────────────────────────────

    /**
     * @return list<array{name:string,type:string,nullable:bool,auto_increment:bool,unsigned:bool,length:?int,precision:?int,scale:?int,has_default:bool,default:mixed,description:?string}>
     */
    private function getColumns(string $table): array
    {
        return match ($this->driver) {
            'sqlite' => $this->getSqliteColumns($table),
            'pgsql' => $this->getPgsqlColumns($table),
            'sqlsrv' => $this->getMssqlColumns($table),
            default => $this->getMysqlColumns($table),
        };
    }

    private function getMysqlColumns(string $table): array
    {
        $result = $this->database->run(
            "SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT, EXTRA, COLUMN_TYPE, " .
            "CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, COLUMN_COMMENT " .
            "FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = :table AND TABLE_SCHEMA = DATABASE() ORDER BY ORDINAL_POSITION",
            ['table' => $table]
        );

        $columns = [];
        foreach ($result['rows'] as $row) {
            $dataType = strtolower((string)$row[1]);
            $columnType = strtolower((string)$row[5]);
            $extra = strtolower((string)$row[4]);

            $columns[] = [
                'name' => (string)$row[0],
                'type' => $dataType,
                'nullable' => ((string)$row[2]) === 'YES',
                'auto_increment' => str_contains($extra, 'auto_increment'),
                'unsigned' => str_contains($columnType, 'unsigned'),
                'length' => $row[6] !== null ? (int)$row[6] : null,
                'precision' => $row[7] !== null ? (int)$row[7] : null,
                'scale' => $row[8] !== null ? (int)$row[8] : null,
                'has_default' => $row[3] !== null || ((string)$row[2]) === 'YES',
                'default' => $row[3],
                'description' => $row[9] !== null ? (string)$row[9] : null,
            ];
        }
        return $columns;
    }

    private function getPgsqlColumns(string $table): array
    {
        $result = $this->database->run(
            "SELECT c.column_name, c.data_type, c.is_nullable, c.column_default, " .
            "c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.is_identity, " .
            "col_description(format('%I.%I', c.table_schema, c.table_name)::regclass, c.ordinal_position) AS col_description " .
            "FROM information_schema.columns c " .
            "WHERE c.table_name = :table AND c.table_schema = 'public' " .
            "ORDER BY c.ordinal_position",
            ['table' => $table]
        );

        $columns = [];
        foreach ($result['rows'] as $row) {
            $colName = (string)$row[0];
            $dataType = strtolower((string)$row[1]);
            $nullable = ((string)$row[2]) === 'YES';
            $default = $row[3] !== null ? (string)$row[3] : null;
            $charMaxLen = $row[4] !== null ? (int)$row[4] : null;
            $numPrecision = $row[5] !== null ? (int)$row[5] : null;
            $numScale = $row[6] !== null ? (int)$row[6] : null;
            $isIdentity = strtoupper((string)$row[7]) === 'YES';

            // SERIAL → column_default is "nextval(...)"; IDENTITY → is_identity = 'YES'
            // (column_default is NULL for IDENTITY, so the old substring check missed it).
            $autoIncrement = $isIdentity
                || ($default !== null && str_contains($default, 'nextval('));

            // Normalize PG types
            $normalizedType = match ($dataType) {
                'integer', 'smallint', 'bigint' => 'int',
                'double precision', 'real' => 'double',
                'numeric' => 'decimal',
                'character varying' => 'varchar',
                'character' => 'char',
                'boolean' => 'tinyint',
                'timestamp without time zone', 'timestamp with time zone' => 'timestamp',
                'date' => 'date',
                'time without time zone', 'time with time zone' => 'time',
                default => $dataType,
            };

            $columns[] = [
                'name' => $colName,
                'type' => $normalizedType,
                'nullable' => $nullable,
                'auto_increment' => $autoIncrement,
                'unsigned' => false,
                'length' => $charMaxLen,
                'precision' => $numPrecision,
                'scale' => $numScale,
                'has_default' => $default !== null || $nullable,
                'default' => $autoIncrement ? null : $default,
                'description' => $row[8] !== null ? (string)$row[8] : null,
            ];
        }
        return $columns;
    }

    /**
     * @return list<array{name:string,type:string,nullable:bool,auto_increment:bool,unsigned:bool,length:?int,precision:?int,scale:?int,has_default:bool,default:mixed,description:?string}>
     */
    private function getMssqlColumns(string $table): array
    {
        // Identity/auto-increment isn't in INFORMATION_SCHEMA — COLUMNPROPERTY
        // reads it from the catalog. The schema is configurable and falls
        // back to the caller's default schema when unset.
        $result = $this->database->run(
            "SELECT c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE, c.COLUMN_DEFAULT, " .
            "c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE, " .
            "COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsIdentity') AS is_identity, " .
            "CAST(ep.value AS nvarchar(max)) AS col_description " .
            "FROM INFORMATION_SCHEMA.COLUMNS c " .
            "LEFT JOIN sys.columns sc " .
            "ON sc.object_id = OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)) " .
            "AND sc.name = c.COLUMN_NAME " .
            "LEFT JOIN sys.extended_properties ep " .
            "ON ep.class = 1 AND ep.major_id = sc.object_id AND ep.minor_id = sc.column_id AND ep.name = 'MS_Description' " .
            "WHERE c.TABLE_NAME = :table AND c.TABLE_SCHEMA = " . $this->mssqlSchemaFilter() . " " .
            "ORDER BY c.ORDINAL_POSITION",
            ['table' => $table, 'schema' => $this->schema]
        );

        $columns = [];
        foreach ($result['rows'] as $row) {
            $columns[] = $this->mapMssqlColumn($row);
        }
        return $columns;
    }

    /**
     * Map one MSSQL column row (COLUMN_NAME, DATA_TYPE, IS_NULLABLE,
     * COLUMN_DEFAULT, CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION,
     * NUMERIC_SCALE, is_identity[, description]) to the shared column shape.
     *
     * @param list<mixed> $row
     * @return array{name:string,type:string,nullable:bool,auto_increment:bool,unsigned:bool,length:?int,precision:?int,scale:?int,has_default:bool,default:mixed,description:?string}
     */
    private function mapMssqlColumn(array $row): array
    {
        $colName = (string)$row[0];
        $dataType = strtolower((string)$row[1]);
        $nullable = ((string)$row[2]) === 'YES';
        $default = $row[3] !== null ? (string)$row[3] : null;
        // nvarchar(max)/varbinary(max) report CHARACTER_MAXIMUM_LENGTH = -1 →
        // "no bound", not a literal length of -1.
        $charMaxLen = ($row[4] !== null && (int)$row[4] >= 0) ? (int)$row[4] : null;
        $numPrecision = $row[5] !== null ? (int)$row[5] : null;
        $numScale = $row[6] !== null ? (int)$row[6] : null;
        $isIdentity = (int)($row[7] ?? 0) === 1;
        $isGenerated = $isIdentity || in_array($dataType, ['timestamp', 'rowversion'], true);
        $description = isset($row[8]) ? (string)$row[8] : null;

        return [
            'name' => $colName,
            'type' => $this->normalizeMssqlType($dataType),
            'nullable' => $nullable,
            'auto_increment' => $isGenerated,
            'unsigned' => false,
            'length' => $charMaxLen,
            'precision' => $numPrecision,
            'scale' => $numScale,
            'has_default' => $default !== null || $nullable,
            'default' => $isGenerated ? null : $default,
            'description' => $description,
        ];
    }

    /** Normalize a raw MSSQL DATA_TYPE onto the shared logical buckets. */
    private function normalizeMssqlType(string $dataType): string
    {
        return match ($dataType) {
            'int', 'bigint', 'smallint', 'tinyint' => 'int',
            'bit' => 'boolean',
            'decimal', 'numeric', 'money', 'smallmoney' => 'decimal',
            'float', 'real' => 'double',
            'nvarchar', 'varchar' => 'varchar',
            'nchar', 'char' => 'char',
            'ntext', 'text' => 'text',
            'date' => 'date',
            'time' => 'time',
            'timestamp', 'rowversion' => 'rowversion',
            'datetime', 'datetime2', 'smalldatetime', 'datetimeoffset' => 'timestamp',
            'uniqueidentifier' => 'varchar',
            default => $dataType,
        };
    }

    private function getSqliteColumns(string $table): array
    {
        $result = $this->database->run("PRAGMA table_info(`" . str_replace('`', '``', $table) . "`)", []);
        $autoIncrCol = $this->getSqliteAutoIncrement($table);

        $columns = [];
        foreach ($result['rows'] as $row) {
            $colName = (string)$row[1];
            $rawType = strtolower((string)$row[2]);
            $notNull = (int)$row[3] === 1;
            $default = $row[4];
            $isPk = (int)$row[5] === 1;

            $parsed = $this->parseSqliteType($rawType);
            $autoIncrement = $isPk && $colName === $autoIncrCol;

            $columns[] = [
                'name' => $colName,
                'type' => $parsed['type'],
                'nullable' => !$notNull,
                'auto_increment' => $autoIncrement,
                'unsigned' => false,
                'length' => $parsed['length'],
                'precision' => $parsed['precision'],
                'scale' => $parsed['scale'],
                'has_default' => $default !== null || !$notNull,
                'default' => $default,
                'description' => null,
            ];
        }
        return $columns;
    }

    // ── Primary keys ──────────────────────────────────────────────────

    /** @return list<string> */
    private function getPrimaryKeys(string $table): array
    {
        if ($this->driver === 'sqlite') {
            $result = $this->database->run("PRAGMA table_info(`" . str_replace('`', '``', $table) . "`)", []);
            $pks = [];
            foreach ($result['rows'] as $row) {
                if ((int)$row[5] > 0) {
                    $pks[] = (string)$row[1];
                }
            }
            return $pks;
        }

        if ($this->driver === 'pgsql') {
            $result = $this->database->run(
                "SELECT a.attname FROM pg_index i " .
                "JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey) " .
                "WHERE i.indrelid = :table::regclass AND i.indisprimary " .
                "ORDER BY a.attnum",
                ['table' => $table]
            );
            return array_map(fn($row) => (string)$row[0], $result['rows']);
        }

        if ($this->driver === 'sqlsrv') {
            $result = $this->database->run(
                "SELECT kcu.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc " .
                "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu " .
                "ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA " .
                "WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY' AND tc.TABLE_NAME = :table AND tc.TABLE_SCHEMA = " . $this->mssqlSchemaFilter() . " " .
                "ORDER BY kcu.ORDINAL_POSITION",
                ['table' => $table, 'schema' => $this->schema]
            );
            return array_map(fn($row) => (string)$row[0], $result['rows']);
        }

        // MySQL
        $result = $this->database->run(
            "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE " .
            "WHERE TABLE_NAME = :table AND TABLE_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'PRIMARY' " .
            "ORDER BY ORDINAL_POSITION",
            ['table' => $table]
        );
        return array_map(fn($row) => (string)$row[0], $result['rows']);
    }

    // ── Foreign keys ──────────────────────────────────────────────────

    /**
     * Public FK reader for tables outside the main entity introspection —
     * mirror of describeColumns(). Keyed by the local column name; value is
     * the referenced (table, column). Consumed by TenantExposurePlanner.
     *
     * @return array<string, array{table:string, column:string}>
     */
    public function describeForeignKeys(string $table): array
    {
        return $this->getForeignKeys($table);
    }

    /** @return array<string, array{table:string, column:string}> */
    private function getForeignKeys(string $table): array
    {
        if ($this->driver === 'sqlite') {
            $result = $this->database->run("PRAGMA foreign_key_list(`" . str_replace('`', '``', $table) . "`)", []);
            $fks = [];
            foreach ($result['rows'] as $row) {
                $fks[(string)$row[3]] = [
                    'table' => (string)$row[2],
                    'column' => (string)$row[4],
                ];
            }
            return $fks;
        }

        if ($this->driver === 'pgsql') {
            $result = $this->database->run(
                "SELECT kcu.column_name, ccu.table_name, ccu.column_name AS ref_column " .
                "FROM information_schema.table_constraints tc " .
                "JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema " .
                "JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema " .
                "WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_name = :table AND tc.table_schema = 'public'",
                ['table' => $table]
            );
            $fks = [];
            foreach ($result['rows'] as $row) {
                $fks[(string)$row[0]] = [
                    'table' => (string)$row[1],
                    'column' => (string)$row[2],
                ];
            }
            return $fks;
        }

        if ($this->driver === 'sqlsrv') {
            // sys.foreign_key_columns, NOT INFORMATION_SCHEMA: on SQL Server,
            // CONSTRAINT_COLUMN_USAGE joined on a FOREIGN KEY constraint
            // returns the *referencing* table's own columns — ref_table came
            // back as the table itself, so every FK looked self-referential
            // and FK-derived features saw no foreign keys at all on MSSQL.
            $result = $this->database->run(
                "SELECT cp.name AS COLUMN_NAME, tr.name AS ref_table, cr.name AS ref_column " .
                "FROM sys.foreign_key_columns fkc " .
                "JOIN sys.tables tp ON tp.object_id = fkc.parent_object_id " .
                "JOIN sys.schemas sp ON sp.schema_id = tp.schema_id " .
                "JOIN sys.columns cp ON cp.object_id = fkc.parent_object_id AND cp.column_id = fkc.parent_column_id " .
                "JOIN sys.tables tr ON tr.object_id = fkc.referenced_object_id " .
                "JOIN sys.columns cr ON cr.object_id = fkc.referenced_object_id AND cr.column_id = fkc.referenced_column_id " .
                "WHERE tp.name = :table AND sp.name = " . $this->mssqlSchemaFilter(),
                ['table' => $table, 'schema' => $this->schema]
            );
            $fks = [];
            foreach ($result['rows'] as $row) {
                $fks[(string)$row[0]] = [
                    'table' => (string)$row[1],
                    'column' => (string)$row[2],
                ];
            }
            return $fks;
        }

        // MySQL
        $result = $this->database->run(
            "SELECT COLUMN_NAME, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME " .
            "FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE " .
            "WHERE TABLE_NAME = :table AND TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_NAME IS NOT NULL",
            ['table' => $table]
        );
        $fks = [];
        foreach ($result['rows'] as $row) {
            $fks[(string)$row[0]] = [
                'table' => (string)$row[1],
                'column' => (string)$row[2],
            ];
        }
        return $fks;
    }

    private function getMysqlTableComment(string $table): ?string
    {
        $result = $this->database->run(
            "SELECT TABLE_COMMENT AS comment FROM INFORMATION_SCHEMA.TABLES " .
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = :table",
            ['table' => $table]
        );
        return $result['rows'] === [] ? null : ($result['rows'][0][0] !== null ? (string)$result['rows'][0][0] : null);
    }

    private function getPgsqlTableComment(string $table): ?string
    {
        $result = $this->database->run(
            "SELECT obj_description(format('%I.%I', current_schema(), :table)::regclass, 'pg_class') AS comment",
            ['table' => $table]
        );
        return $result['rows'] === [] ? null : ($result['rows'][0][0] !== null ? (string)$result['rows'][0][0] : null);
    }

    private function getMssqlTableComment(string $table): ?string
    {
        $result = $this->database->run(
            "SELECT CAST(ep.value AS nvarchar(max)) AS comment " .
            "FROM sys.extended_properties ep " .
            "WHERE ep.class = 1 AND ep.minor_id = 0 AND ep.name = 'MS_Description' " .
            "AND ep.major_id = OBJECT_ID(QUOTENAME(" . $this->mssqlSchemaFilter() . ") + '.' + QUOTENAME(:table))",
            ['table' => $table, 'schema' => $this->schema]
        );
        return $result['rows'] === [] ? null : ($result['rows'][0][0] !== null ? (string)$result['rows'][0][0] : null);
    }

    /**
     * @return list<list<string>>
     */
    private function getMysqlUniqueConstraints(string $table): array
    {
        $result = $this->database->run(
            "SELECT INDEX_NAME AS idx, COLUMN_NAME AS col, SEQ_IN_INDEX AS seq " .
            "FROM INFORMATION_SCHEMA.STATISTICS " .
            "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = :table " .
            "AND NON_UNIQUE = 0 AND INDEX_NAME <> 'PRIMARY' " .
            "ORDER BY INDEX_NAME, SEQ_IN_INDEX",
            ['table' => $table]
        );
        return $this->groupConstraintColumns($result['rows']);
    }

    /**
     * @return list<list<string>>
     */
    private function getPgsqlUniqueConstraints(string $table): array
    {
        $result = $this->database->run(
            "SELECT tc.constraint_name AS idx, kcu.column_name AS col, kcu.ordinal_position AS seq " .
            "FROM information_schema.table_constraints tc " .
            "JOIN information_schema.key_column_usage kcu " .
            "ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema " .
            "WHERE tc.constraint_type = 'UNIQUE' AND tc.table_schema = current_schema() AND tc.table_name = :table " .
            "ORDER BY tc.constraint_name, kcu.ordinal_position",
            ['table' => $table]
        );
        return $this->groupConstraintColumns($result['rows']);
    }

    /**
     * @return list<list<string>>
     */
    private function getMssqlUniqueConstraints(string $table): array
    {
        $result = $this->database->run(
            "SELECT tc.CONSTRAINT_NAME AS idx, kcu.COLUMN_NAME AS col, kcu.ORDINAL_POSITION AS seq " .
            "FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc " .
            "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu " .
            "ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA " .
            "WHERE tc.CONSTRAINT_TYPE = 'UNIQUE' AND tc.TABLE_SCHEMA = " . $this->mssqlSchemaFilter() . " " .
            "AND tc.TABLE_NAME = :table " .
            "ORDER BY tc.CONSTRAINT_NAME, kcu.ORDINAL_POSITION",
            ['table' => $table, 'schema' => $this->schema]
        );
        return $this->groupConstraintColumns($result['rows']);
    }

    /**
     * @return list<list<string>>
     */
    private function getSqliteUniqueConstraints(string $table): array
    {
        $indexes = $this->database->run("PRAGMA index_list(`" . str_replace('`', '``', $table) . "`)", []);
        $result = [];
        foreach ($indexes['rows'] as $idx) {
            $unique = (int)($idx[2] ?? 0) === 1;
            if (!$unique) {
                continue;
            }
            $origin = strtolower((string)($idx[3] ?? ''));
            if ($origin === 'pk') {
                continue;
            }
            $name = (string)($idx[1] ?? '');
            if ($name === '') {
                continue;
            }
            $cols = $this->database->run("PRAGMA index_info(`" . str_replace('`', '``', $name) . "`)", []);
            $ordered = [];
            foreach ($cols['rows'] as $col) {
                $colName = (string)($col[2] ?? '');
                if ($colName !== '') {
                    $ordered[(int)($col[0] ?? 0)] = $colName;
                }
            }
            ksort($ordered);
            if ($ordered !== []) {
                $result[] = array_values($ordered);
            }
        }
        return $result;
    }

    /**
     * Database::run returns positional rows (constraint name at 0, column
     * name at 1 — the ORDER BY in each caller guarantees column order).
     *
     * @param list<list<mixed>> $rows
     * @return list<list<string>>
     */
    private function groupConstraintColumns(array $rows): array
    {
        $grouped = [];
        foreach ($rows as $row) {
            $index = (string)($row[0] ?? '');
            $col = (string)($row[1] ?? '');
            if ($index === '' || $col === '') {
                continue;
            }
            $grouped[$index][] = $col;
        }

        return array_values($grouped);
    }

    // ── Bulk introspection (cold-boot N×RTT collapse) ─────────────────
    //
    // For MySQL and pgsql, fetch metadata for the entire target table set
    // in one round trip per concern (columns, primary keys, foreign keys).
    // SQLite falls through to per-table because PRAGMA can't be filtered by
    // a multi-table set and queries are local — no RTT to amortize.

    /**
     * @param list<string> $tables
     * @return array<string, list<array{name:string,type:string,nullable:bool,auto_increment:bool,unsigned:bool,length:?int,precision:?int,scale:?int,has_default:bool,default:mixed,description:?string}>>
     */
    private function bulkColumns(array $tables): array
    {
        if ($tables === []) {
            return [];
        }
        if ($this->driver === 'sqlite') {
            $out = [];
            foreach ($tables as $table) {
                $out[$table] = $this->getSqliteColumns($table);
            }
            return $out;
        }

        if ($this->driver === 'pgsql') {
            $result = $this->database->run(
                "SELECT c.table_name, c.column_name, c.data_type, c.is_nullable, c.column_default, " .
                "c.character_maximum_length, c.numeric_precision, c.numeric_scale, c.is_identity, " .
                "col_description(format('%I.%I', c.table_schema, c.table_name)::regclass, c.ordinal_position) AS col_description " .
                "FROM information_schema.columns c " .
                "WHERE c.table_name IN (:tables) AND c.table_schema = 'public' " .
                "ORDER BY c.table_name, c.ordinal_position",
                ['tables' => $tables]
            );
            $out = array_fill_keys($tables, []);
            foreach ($result['rows'] as $row) {
                $tbl = (string)$row[0];
                $colName = (string)$row[1];
                $dataType = strtolower((string)$row[2]);
                $nullable = ((string)$row[3]) === 'YES';
                $default = $row[4] !== null ? (string)$row[4] : null;
                $charMaxLen = $row[5] !== null ? (int)$row[5] : null;
                $numPrecision = $row[6] !== null ? (int)$row[6] : null;
                $numScale = $row[7] !== null ? (int)$row[7] : null;
                $isIdentity = strtoupper((string)$row[8]) === 'YES';

                $autoIncrement = $isIdentity
                    || ($default !== null && str_contains($default, 'nextval('));

                $normalizedType = match ($dataType) {
                    'integer', 'smallint', 'bigint' => 'int',
                    'double precision', 'real' => 'double',
                    'numeric' => 'decimal',
                    'character varying' => 'varchar',
                    'character' => 'char',
                    'boolean' => 'tinyint',
                    'timestamp without time zone', 'timestamp with time zone' => 'timestamp',
                    'date' => 'date',
                    'time without time zone', 'time with time zone' => 'time',
                    default => $dataType,
                };

                $out[$tbl][] = [
                    'name' => $colName,
                    'type' => $normalizedType,
                    'nullable' => $nullable,
                    'auto_increment' => $autoIncrement,
                    'unsigned' => false,
                    'length' => $charMaxLen,
                    'precision' => $numPrecision,
                    'scale' => $numScale,
                    'has_default' => $default !== null || $nullable,
                    'default' => $autoIncrement ? null : $default,
                    'description' => $row[9] !== null ? (string)$row[9] : null,
                ];
            }
            return $out;
        }

        if ($this->driver === 'sqlsrv') {
            $result = $this->database->run(
                "SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, c.IS_NULLABLE, c.COLUMN_DEFAULT, " .
                "c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE, " .
                "COLUMNPROPERTY(OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)), c.COLUMN_NAME, 'IsIdentity') AS is_identity, " .
                "CAST(ep.value AS nvarchar(max)) AS col_description " .
                "FROM INFORMATION_SCHEMA.COLUMNS c " .
                "LEFT JOIN sys.columns sc " .
                "ON sc.object_id = OBJECT_ID(QUOTENAME(c.TABLE_SCHEMA) + '.' + QUOTENAME(c.TABLE_NAME)) " .
                "AND sc.name = c.COLUMN_NAME " .
                "LEFT JOIN sys.extended_properties ep " .
                "ON ep.class = 1 AND ep.major_id = sc.object_id AND ep.minor_id = sc.column_id AND ep.name = 'MS_Description' " .
                "WHERE c.TABLE_NAME IN (:tables) AND c.TABLE_SCHEMA = " . $this->mssqlSchemaFilter() . " " .
                "ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION",
                ['tables' => $tables, 'schema' => $this->schema]
            );
            $out = array_fill_keys($tables, []);
            foreach ($result['rows'] as $row) {
                $tbl = (string)$row[0];
                // Drop the leading TABLE_NAME so the shared mapper sees the same
                // column order as the per-table query.
                $out[$tbl][] = $this->mapMssqlColumn(array_slice($row, 1));
            }
            return $out;
        }

        // MySQL
        $result = $this->database->run(
            "SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, IS_NULLABLE, COLUMN_DEFAULT, EXTRA, COLUMN_TYPE, " .
            "CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE, COLUMN_COMMENT " .
            "FROM INFORMATION_SCHEMA.COLUMNS " .
            "WHERE TABLE_NAME IN (:tables) AND TABLE_SCHEMA = DATABASE() " .
            "ORDER BY TABLE_NAME, ORDINAL_POSITION",
            ['tables' => $tables]
        );
        $out = array_fill_keys($tables, []);
        foreach ($result['rows'] as $row) {
            $tbl = (string)$row[0];
            $dataType = strtolower((string)$row[2]);
            $columnType = strtolower((string)$row[6]);
            $extra = strtolower((string)$row[5]);

            $out[$tbl][] = [
                'name' => (string)$row[1],
                'type' => $dataType,
                'nullable' => ((string)$row[3]) === 'YES',
                'auto_increment' => str_contains($extra, 'auto_increment'),
                'unsigned' => str_contains($columnType, 'unsigned'),
                'length' => $row[7] !== null ? (int)$row[7] : null,
                'precision' => $row[8] !== null ? (int)$row[8] : null,
                'scale' => $row[9] !== null ? (int)$row[9] : null,
                'has_default' => $row[4] !== null || ((string)$row[3]) === 'YES',
                'default' => $row[4],
                'description' => $row[10] !== null ? (string)$row[10] : null,
            ];
        }
        return $out;
    }

    /**
     * @param list<string> $tables
     * @return array<string, list<string>>
     */
    private function bulkPrimaryKeys(array $tables): array
    {
        if ($tables === []) {
            return [];
        }
        if ($this->driver === 'sqlite') {
            $out = [];
            foreach ($tables as $table) {
                $out[$table] = $this->getPrimaryKeys($table);
            }
            return $out;
        }

        if ($this->driver === 'pgsql') {
            // Join pg_class + pg_namespace so the table comparison is by name
            // (regclass cast in the per-table query is N×RTT inside the join
            // planner; the bulk form pushes filtering to the WHERE clause).
            $result = $this->database->run(
                "SELECT c.relname AS table_name, a.attname AS column_name, " .
                "       array_position(i.indkey, a.attnum) AS pos " .
                "FROM pg_index i " .
                "JOIN pg_class c ON c.oid = i.indrelid " .
                "JOIN pg_namespace n ON n.oid = c.relnamespace " .
                "JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = ANY(i.indkey) " .
                "WHERE i.indisprimary AND n.nspname = 'public' AND c.relname IN (:tables) " .
                "ORDER BY c.relname, pos",
                ['tables' => $tables]
            );
            $out = array_fill_keys($tables, []);
            foreach ($result['rows'] as $row) {
                $out[(string)$row[0]][] = (string)$row[1];
            }
            return $out;
        }

        if ($this->driver === 'sqlsrv') {
            $result = $this->database->run(
                "SELECT tc.TABLE_NAME, kcu.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc " .
                "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu " .
                "ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA " .
                "WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY' AND tc.TABLE_NAME IN (:tables) AND tc.TABLE_SCHEMA = " . $this->mssqlSchemaFilter() . " " .
                "ORDER BY tc.TABLE_NAME, kcu.ORDINAL_POSITION",
                ['tables' => $tables, 'schema' => $this->schema]
            );
            $out = array_fill_keys($tables, []);
            foreach ($result['rows'] as $row) {
                $out[(string)$row[0]][] = (string)$row[1];
            }
            return $out;
        }

        // MySQL
        $result = $this->database->run(
            "SELECT TABLE_NAME, COLUMN_NAME FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE " .
            "WHERE TABLE_NAME IN (:tables) AND TABLE_SCHEMA = DATABASE() AND CONSTRAINT_NAME = 'PRIMARY' " .
            "ORDER BY TABLE_NAME, ORDINAL_POSITION",
            ['tables' => $tables]
        );
        $out = array_fill_keys($tables, []);
        foreach ($result['rows'] as $row) {
            $out[(string)$row[0]][] = (string)$row[1];
        }
        return $out;
    }

    /**
     * @param list<string> $tables
     * @return array<string, array<string, array{table:string, column:string}>>
     */
    private function bulkForeignKeys(array $tables): array
    {
        if ($tables === []) {
            return [];
        }
        if ($this->driver === 'sqlite') {
            $out = [];
            foreach ($tables as $table) {
                $out[$table] = $this->getForeignKeys($table);
            }
            return $out;
        }

        if ($this->driver === 'pgsql') {
            $result = $this->database->run(
                "SELECT tc.table_name, kcu.column_name, ccu.table_name AS ref_table, ccu.column_name AS ref_column " .
                "FROM information_schema.table_constraints tc " .
                "JOIN information_schema.key_column_usage kcu ON tc.constraint_name = kcu.constraint_name AND tc.table_schema = kcu.table_schema " .
                "JOIN information_schema.constraint_column_usage ccu ON ccu.constraint_name = tc.constraint_name AND ccu.table_schema = tc.table_schema " .
                "WHERE tc.constraint_type = 'FOREIGN KEY' AND tc.table_name IN (:tables) AND tc.table_schema = 'public'",
                ['tables' => $tables]
            );
            $out = array_fill_keys($tables, []);
            foreach ($result['rows'] as $row) {
                $out[(string)$row[0]][(string)$row[1]] = [
                    'table' => (string)$row[2],
                    'column' => (string)$row[3],
                ];
            }
            return $out;
        }

        if ($this->driver === 'sqlsrv') {
            $result = $this->database->run(
                "SELECT tc.TABLE_NAME, kcu.COLUMN_NAME, ccu.TABLE_NAME AS ref_table, ccu.COLUMN_NAME AS ref_column " .
                "FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc " .
                "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu " .
                "ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA " .
                "JOIN INFORMATION_SCHEMA.CONSTRAINT_COLUMN_USAGE ccu " .
                "ON ccu.CONSTRAINT_NAME = tc.CONSTRAINT_NAME AND ccu.TABLE_SCHEMA = tc.TABLE_SCHEMA " .
                "WHERE tc.CONSTRAINT_TYPE = 'FOREIGN KEY' AND tc.TABLE_NAME IN (:tables) AND tc.TABLE_SCHEMA = " . $this->mssqlSchemaFilter(),
                ['tables' => $tables, 'schema' => $this->schema]
            );
            $out = array_fill_keys($tables, []);
            foreach ($result['rows'] as $row) {
                $out[(string)$row[0]][(string)$row[1]] = [
                    'table' => (string)$row[2],
                    'column' => (string)$row[3],
                ];
            }
            return $out;
        }

        // MySQL
        $result = $this->database->run(
            "SELECT TABLE_NAME, COLUMN_NAME, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME " .
            "FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE " .
            "WHERE TABLE_NAME IN (:tables) AND TABLE_SCHEMA = DATABASE() AND REFERENCED_TABLE_NAME IS NOT NULL",
            ['tables' => $tables]
        );
        $out = array_fill_keys($tables, []);
        foreach ($result['rows'] as $row) {
            $out[(string)$row[0]][(string)$row[1]] = [
                'table' => (string)$row[2],
                'column' => (string)$row[3],
            ];
        }
        return $out;
    }

    // ── SQLite helpers ────────────────────────────────────────────────

    private function getSqliteAutoIncrement(string $table): ?string
    {
        $result = $this->database->run(
            "SELECT sql FROM sqlite_master WHERE type='table' AND name=:table",
            ['table' => $table]
        );
        if ($result['rows'] === []) {
            return null;
        }
        $sql = strtolower((string)$result['rows'][0][0]);
        if (preg_match('/(\w+)\s+integer\s+primary\s+key/i', $sql, $m)) {
            return $m[1];
        }
        return null;
    }

    /** @return array{type:string, length:?int, precision:?int, scale:?int} */
    private function parseSqliteType(string $rawType): array
    {
        $type = trim(preg_replace('/\(.*\)/', '', $rawType) ?? $rawType);
        $length = null;
        $precision = null;
        $scale = null;

        if (preg_match('/\((\d+)\)/', $rawType, $m)) {
            $length = (int)$m[1];
        }
        if (preg_match('/\((\d+),\s*(\d+)\)/', $rawType, $m)) {
            $precision = (int)$m[1];
            $scale = (int)$m[2];
            $length = null;
        }

        $type = match (true) {
            $type === 'integer', $type === 'int' => 'int',
            $type === 'real', $type === 'double', $type === 'float' => 'double',
            str_contains($type, 'char'), str_contains($type, 'text'), str_contains($type, 'clob') => $length ? 'varchar' : 'text',
            str_contains($type, 'decimal'), str_contains($type, 'numeric') => 'decimal',
            default => $type ?: 'text',
        };

        return ['type' => $type, 'length' => $length, 'precision' => $precision, 'scale' => $scale];
    }

    // ── Shared helpers ────────────────────────────────────────────────

    private function mssqlSchemaFilter(): string
    {
        return 'COALESCE(:schema, SCHEMA_NAME())';
    }

    private function dbTypeToPhp(string $dbType): string
    {
        return match ($dbType) {
            'int', 'integer', 'bigint', 'smallint', 'tinyint', 'mediumint' => 'int',
            'double', 'float', 'real', 'decimal', 'numeric' => 'float',
            'boolean', 'bool' => 'bool',
            default => 'string',
        };
    }

    private function isStringType(string $dbType): bool
    {
        return in_array($dbType, ['varchar', 'char', 'text', 'tinytext', 'mediumtext', 'longtext'], true);
    }
}
