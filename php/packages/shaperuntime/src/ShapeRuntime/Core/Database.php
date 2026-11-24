<?php

declare(strict_types=1);

namespace ShapeRuntime\Core;

use PDO;
use PDOException;

final class Database
{
    private PDO $pdo;
    private bool $inTransaction = false;
    private string $driver;
    private ?string $schema = null;

    /** @param array<string, mixed> $config */
    public function __construct(array $config)
    {
        $dsn = (string)($config['dsn'] ?? '');
        $user = (string)($config['user'] ?? '');
        $pass = (string)($config['pass'] ?? '');
        $options = is_array($config['options'] ?? null) ? $config['options'] : [];

        $options[PDO::ATTR_ERRMODE] = PDO::ERRMODE_EXCEPTION;
        $options[PDO::ATTR_DEFAULT_FETCH_MODE] = PDO::FETCH_NUM;

        // Opt-in persistent connections. With PHP-FPM / mod_php / litespeed
        // each worker reuses the same PDO instance across requests, so the
        // TCP/TLS/auth handshake (~150–200 ms over public-IP remote PG)
        // collapses to ~0 on the warm path. SQLite + ephemeral CLI usage
        // ignore the flag; the cost only hurts where it can.
        // DB-FIRST.md documents the
        // transaction-state caveat operators must know about.
        if (($config['persistent'] ?? false) === true) {
            $options[PDO::ATTR_PERSISTENT] = true;
        }

        try {
            $this->pdo = new PDO($dsn, $user, $pass, $options);
            $this->driver = $this->pdo->getAttribute(PDO::ATTR_DRIVER_NAME) ?: 'mysql';
            $schema = isset($config['schema']) && is_string($config['schema']) ? trim($config['schema']) : '';
            $this->schema = $schema !== '' ? $schema : null;
        } catch (PDOException $e) {
            throw new ApiException('DB_ERROR', 'Database connection failed.', 500);
        }

        // Defensive: if a worker died mid-transaction on a previous request
        // and the persistent connection is now reused by us, close out the
        // stranded transaction so this request starts clean. Idempotent
        // when no transaction is in progress.
        if (($config['persistent'] ?? false) === true && $this->pdo->inTransaction()) {
            try {
                $this->pdo->rollBack();
            } catch (PDOException $e) {
                // Nothing reasonable to do here — log and continue. The
                // worker will be replaced if state stays corrupt.
                @error_log('[Shape] Failed to roll back stranded transaction on persistent PDO: ' . $e->getMessage());
            }
        }
    }

    /** Returns PDO driver name: 'mysql', 'sqlite', 'pgsql', 'sqlsrv' */
    public function getDriver(): string
    {
        return $this->driver;
    }

    /** Return the configured SQL Server schema name, or null for the default schema. */
    public function getSchema(): ?string
    {
        return $this->schema;
    }

    /** Quote an identifier (table/column name) for the current driver. */
    public function quoteIdentifier(string $name): string
    {
        return SqlDialect::quoteIdentifier($this->driver, $name);
    }

    public function beginTransaction(): void
    {
        if ($this->inTransaction) {
            throw new ApiException('DB_ERROR', 'Transaction already started.', 500);
        }
        try {
            $this->pdo->beginTransaction();
            $this->inTransaction = true;
        } catch (PDOException $e) {
            throw new ApiException('DB_ERROR', 'Failed to start transaction.', 500);
        }
    }

    public function commit(): void
    {
        if (!$this->inTransaction) {
            throw new ApiException('DB_ERROR', 'No transaction in progress.', 500);
        }
        try {
            $this->pdo->commit();
            $this->inTransaction = false;
        } catch (PDOException $e) {
            throw new ApiException('DB_ERROR', 'Failed to commit transaction.', 500);
        }
    }

    public function rollback(): void
    {
        if (!$this->inTransaction) {
            throw new ApiException('DB_ERROR', 'No transaction in progress.', 500);
        }
        try {
            $this->pdo->rollBack();
            $this->inTransaction = false;
        } catch (PDOException $e) {
            throw new ApiException('DB_ERROR', 'Failed to rollback transaction.', 500);
        }
    }

    public function isInTransaction(): bool
    {
        return $this->inTransaction;
    }

    /**
     * Execute a DML statement (INSERT, UPDATE, DELETE) and return affected row count.
     * @param array<string, mixed> $params
     */
    public function exec(string $sql, array $params): int
    {
        [$preparedSql, $flatParams] = $this->expandArrayParams($sql, $params);

        try {
            $stmt = $this->pdo->prepare($preparedSql);
            foreach ($flatParams as $key => $value) {
                $stmt->bindValue(':' . $key, $value);
            }
            $stmt->execute();
            return $stmt->rowCount();
        } catch (PDOException $e) {
            throw new ApiException('DB_ERROR', 'Database mutation failed.', 500);
        }
    }

    /**
     * Run an INSERT and return the new row's ID, portably across drivers.
     *
     * MySQL/SQLite: relies on the connection's last-insert-id. PostgreSQL:
     * appends `RETURNING <idCol>` to the SQL and reads the value from the
     * statement (PDO's lastInsertId requires a sequence name there). SQL Server
     * has neither `LAST_INSERT_ID()` nor `RETURNING`, so we inject
     * `OUTPUT INSERTED.<idCol>` before `VALUES` (single round trip,
     * trigger-safe — unlike `@@IDENTITY`) and read it back the same way.
     *
     * @param array<string, mixed> $params
     */
    public function insertReturningId(string $sql, array $params, string $idCol = 'id'): int
    {
        if ($this->driver === 'pgsql') {
            $sql .= ' RETURNING ' . $this->quoteIdentifier($idCol);
        } elseif ($this->driver === 'sqlsrv') {
            // OUTPUT sits between the column list and VALUES, not at the tail.
            // Framework-generated inserts always carry a `VALUES (...)` clause.
            $output = ' OUTPUT INSERTED.' . $this->quoteIdentifier($idCol) . ' VALUES (';
            $sql = preg_replace('/\s+VALUES\s*\(/i', $output, $sql, 1) ?? $sql;
        }
        [$preparedSql, $flatParams] = $this->expandArrayParams($sql, $params);

        try {
            $stmt = $this->pdo->prepare($preparedSql);
            foreach ($flatParams as $key => $value) {
                $stmt->bindValue(':' . $key, $value);
            }
            $stmt->execute();

            if ($this->driver === 'pgsql' || $this->driver === 'sqlsrv') {
                $row = $stmt->fetch(PDO::FETCH_NUM);
                return is_array($row) ? (int)($row[0] ?? 0) : 0;
            }
            return (int)$this->pdo->lastInsertId();
        } catch (PDOException $e) {
            throw new ApiException('DB_ERROR', 'Database mutation failed.', 500);
        }
    }

    /**
     * INSERT into a table whose PK the framework must allocate (`MAX(pk)+1`,
     * pk_strategy 'manual' — see docs/shape/MANUAL-PK-PLAN.md). Returns the
     * allocated id. $data must not contain the PK; callers route explicit
     * client-supplied ids through the normal insert path instead.
     *
     * @param array<string, mixed> $data
     */
    public function insertWithManualPk(string $table, string $pk, array $data): int
    {
        return self::insertWithManualPkOnPdo($this->pdo, $this->driver, $table, $pk, $data);
    }

    /**
     * Shared allocate-and-insert routine on a raw PDO, so LoginService (which
     * manages its own PDO and transactions) and Database run one code path.
     *
     * Driver split:
     *   sqlsrv/pgsql — single SqlDialect::insertAllocatingPk statement; the
     *   id comes back in-band (OUTPUT INSERTED / RETURNING). pgsql takes
     *   pg_advisory_xact_lock first so framework allocators serialize without
     *   touching the table.
     *   mysql/sqlite — neither can return a non-identity column from an
     *   INSERT, and lastInsertId() only reports AUTO_INCREMENT / rowid
     *   values, so the id is allocated with a locked SELECT MAX+1 first
     *   (SqlDialect::selectNextPk) and inserted explicitly. That makes the
     *   returned id exact by construction instead of re-read after the fact
     *   (a post-insert MAX() could pick up a racing external writer's row).
     *
     * Each attempt runs in its own transaction when the caller hasn't opened
     * one — the range/gap locks above only outlive a statement inside a
     * transaction. External writers that don't honor our lock discipline (the
     * legacy app computing its own MAX+1) surface as unique violations; those
     * and MSSQL deadlock victims (1205) are retried up to 3 times, then
     * reported as DB_ERROR with detail PK_ALLOCATION_RACE.
     *
     * @param array<string, mixed> $data
     */
    public static function insertWithManualPkOnPdo(PDO $pdo, string $driver, string $table, string $pk, array $data): int
    {
        unset($data[$pk]);

        for ($attempt = 1; $attempt <= 3; $attempt++) {
            $ownTx = !$pdo->inTransaction();
            // Inside a caller-held pgsql transaction any error aborts the
            // whole transaction, so a retry needs a savepoint to roll back
            // to. The other drivers keep the transaction usable after a
            // constraint violation (sqlsrv deadlocks are the exception —
            // the server rolls the transaction back and the retry fails
            // upward, which the batch-level rollback handles).
            $useSavepoint = !$ownTx && $driver === 'pgsql';
            try {
                if ($ownTx) {
                    $pdo->beginTransaction();
                } elseif ($useSavepoint) {
                    $pdo->exec('SAVEPOINT shape_pk_alloc');
                }

                if ($driver === 'pgsql') {
                    $lock = $pdo->prepare('SELECT pg_advisory_xact_lock(hashtext(:shape_pk_key))');
                    $lock->execute(['shape_pk_key' => 'shape_pk:' . $table]);
                }

                $id = $driver === 'sqlsrv' || $driver === 'pgsql'
                    ? self::allocatePkInStatement($pdo, $driver, $table, $pk, $data)
                    : self::allocatePkThenInsert($pdo, $driver, $table, $pk, $data);

                if ($ownTx) {
                    $pdo->commit();
                } elseif ($useSavepoint) {
                    $pdo->exec('RELEASE SAVEPOINT shape_pk_alloc');
                }
                return $id;
            } catch (PDOException $e) {
                if ($ownTx) {
                    // Best-effort: sqlsrv rolls deadlock victims back
                    // server-side, so there may be no transaction left to
                    // close client-side.
                    try {
                        $pdo->rollBack();
                    } catch (PDOException) {
                    }
                } elseif ($useSavepoint) {
                    $pdo->exec('ROLLBACK TO SAVEPOINT shape_pk_alloc');
                }
                if (!self::isPkAllocationRace($e)) {
                    throw new ApiException('DB_ERROR', 'Database mutation failed.', 500);
                }
            }
        }

        throw new ApiException(
            'DB_ERROR',
            "Failed to allocate a primary key for table '{$table}' after 3 attempts.",
            500,
            null,
            ['detail' => 'PK_ALLOCATION_RACE']
        );
    }

    /**
     * Resolve the configured `pk_assignment` mode for one table. Accepts the
     * raw config value: string shorthand (whole database) or a map with a
     * '*' default and per-table overrides. Runtime consumers (LoginService /
     * OidcVerifier, whose auth tables are never introspected) treat anything
     * but an explicit 'manual' as db-assigned — the 'auto' schema detection
     * is a build-time concern (ShapeApp) that needs introspection.
     */
    public static function resolvePkAssignmentMode(mixed $pkAssignment, string $table): string
    {
        if (is_string($pkAssignment)) {
            return $pkAssignment;
        }
        if (is_array($pkAssignment)) {
            $mode = $pkAssignment[$table] ?? $pkAssignment['*'] ?? 'auto';
            return is_string($mode) ? $mode : 'auto';
        }
        return 'auto';
    }

    /** @param array<string, mixed> $data */
    private static function allocatePkInStatement(PDO $pdo, string $driver, string $table, string $pk, array $data): int
    {
        $sql = SqlDialect::insertAllocatingPk($driver, $table, $pk, array_keys($data));
        $stmt = $pdo->prepare($sql);
        foreach ($data as $col => $value) {
            $stmt->bindValue(':' . $col, $value);
        }
        $stmt->execute();
        $row = $stmt->fetch(PDO::FETCH_NUM);
        return is_array($row) ? (int)($row[0] ?? 0) : 0;
    }

    /** @param array<string, mixed> $data */
    private static function allocatePkThenInsert(PDO $pdo, string $driver, string $table, string $pk, array $data): int
    {
        $next = $pdo->query(SqlDialect::selectNextPk($driver, $table, $pk));
        $row = $next !== false ? $next->fetch(PDO::FETCH_NUM) : false;
        $id = is_array($row) ? (int)($row[0] ?? 1) : 1;

        $quote = static fn(string $n): string => SqlDialect::quoteIdentifier($driver, $n);
        $columns = array_merge([$pk], array_keys($data));
        $sql = 'INSERT INTO ' . $quote($table) . ' (' . implode(', ', array_map($quote, $columns)) . ') VALUES ('
            . implode(', ', array_map(static fn(string $c): string => ':' . $c, $columns)) . ')';

        $stmt = $pdo->prepare($sql);
        $stmt->bindValue(':' . $pk, $id, PDO::PARAM_INT);
        foreach ($data as $col => $value) {
            $stmt->bindValue(':' . $col, $value);
        }
        $stmt->execute();
        return $id;
    }

    /**
     * Unique violation (SQLSTATE 23000/23505; sqlsrv 2627/2601, mysql 1062)
     * or sqlsrv deadlock victim (1205) — the errors the allocation loop may
     * retry. Everything else is a real failure and must not be masked.
     */
    private static function isPkAllocationRace(PDOException $e): bool
    {
        $sqlState = is_array($e->errorInfo) && isset($e->errorInfo[0]) ? (string)$e->errorInfo[0] : (string)$e->getCode();
        $driverCode = is_array($e->errorInfo) && isset($e->errorInfo[1]) ? (int)$e->errorInfo[1] : 0;
        return in_array($sqlState, ['23000', '23505'], true)
            || in_array($driverCode, [2627, 2601, 1062, 1205], true);
    }

    /**
     * @param array<string, mixed> $params
     * @return array{columns:list<string>, rows:list<list<mixed>>, count:int, ms:float}
     */
    public function run(string $sql, array $params): array
    {
        [$preparedSql, $flatParams] = $this->expandArrayParams($sql, $params);

        try {
            $start = microtime(true);
            $stmt = $this->pdo->prepare($preparedSql);
            foreach ($flatParams as $key => $value) {
                $stmt->bindValue(':' . $key, $value);
            }
            $stmt->execute();
            // Fetch as ASSOC: derives column names from PHP array keys instead
            // of pdo_pgsql's per-column getColumnMeta() loop, which fires one
            // pg_attribute round trip per column. On remote PG with 29 cols
            // and 60 ms RTT that loop dominated request latency (~1.8s vs
            // 0.1s execute+fetch). Hot-path reads now pay 0 round trips for
            // column-name discovery; the slow getColumnMeta path only fires
            // on empty result sets, which are rare and cheap regardless.
            $assocRows = $stmt->fetchAll(PDO::FETCH_ASSOC);
            $ms = (microtime(true) - $start) * 1000;

            if (!is_array($assocRows)) {
                $assocRows = [];
            }

            $columns = $assocRows !== [] ? array_keys($assocRows[0]) : [];
            if ($columns === [] && $stmt->columnCount() > 0) {
                // Fallback for empty result sets — rare; bounded N×RTT.
                $countCols = $stmt->columnCount();
                for ($i = 0; $i < $countCols; $i++) {
                    $meta = $stmt->getColumnMeta($i);
                    $columns[] = is_array($meta) && isset($meta['name']) ? (string)$meta['name'] : 'col_' . $i;
                }
            }

            $rows = self::normalizeRows(array_map('array_values', $assocRows));

            return [
                'columns' => $columns,
                'rows' => $rows,
                'count' => count($rows),
                'ms' => round($ms, 3),
            ];
        } catch (PDOException $e) {
            throw new ApiException('DB_ERROR', 'Database query failed.', 500);
        }
    }

    /**
     * Cast PHP `bool` to `int 0/1` so JSON output is identical across drivers
     * (PG returns native bool for BOOLEAN; MySQL/SQLite return int for TINYINT).
     *
     * @param list<list<mixed>> $rows
     * @return list<list<mixed>>
     */
    private static function normalizeRows(array $rows): array
    {
        foreach ($rows as &$row) {
            if (!is_array($row)) {
                continue;
            }
            foreach ($row as &$val) {
                if (is_bool($val)) {
                    $val = $val ? 1 : 0;
                }
            }
            unset($val);
        }
        unset($row);
        return $rows;
    }

    /**
     * @param array<string, mixed> $params
     * @return array{0:string, 1:array<string, mixed>}
     */
    private function expandArrayParams(string $sql, array $params): array
    {
        $flat = [];
        foreach ($params as $name => $value) {
            if (!is_array($value)) {
                $flat[$name] = $value;
                continue;
            }
            if ($value === []) {
                throw new ApiException('PARAM_VALIDATION_ERROR', "Array parameter '{$name}' cannot be empty.", 400);
            }

            $placeholders = [];
            foreach (array_values($value) as $i => $item) {
                $ph = $name . '_' . $i;
                $placeholders[] = ':' . $ph;
                $flat[$ph] = $item;
            }

            $pattern = '/:' . preg_quote($name, '/') . '(?![A-Za-z0-9_])/';
            $sql = preg_replace($pattern, implode(',', $placeholders), $sql, 1) ?? $sql;
        }

        return [$sql, $flat];
    }
}
