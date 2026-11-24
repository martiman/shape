<?php

declare(strict_types=1);

namespace ShapeRuntime\Core;

/**
 * Per-driver SQL fragments that differ between engines. Keeps the
 * MySQL / PostgreSQL / SQLite / SQL Server (`sqlsrv`) decisions in one place
 * so the four quoting copies and the pagination wrapper don't drift.
 *
 * Pure/static: the driver token (a PDO driver name) is passed in by the
 * caller, which already knows it from Database::getDriver() or the build-time
 * DSN sniff (ShapeApp::detectDriver).
 */
final class SqlDialect
{
    /** Quote an identifier (table/column name) for the given driver. */
    public static function quoteIdentifier(string $driver, string $name): string
    {
        return match ($driver) {
            // SQL Server: brackets are unconditionally safe (double-quote only
            // works under SET QUOTED_IDENTIFIER ON); escape a literal `]`.
            'sqlsrv' => '[' . str_replace(']', ']]', $name) . ']',
            'pgsql' => '"' . str_replace('"', '""', $name) . '"',
            default => '`' . str_replace('`', '``', $name) . '`',
        };
    }

    /**
     * Wrap a subquery with driver-correct pagination.
     *
     * SQL Server has no `LIMIT`/`OFFSET n` — it uses `OFFSET n ROWS
     * [FETCH NEXT m ROWS ONLY]`, which is a **syntax error without an
     * `ORDER BY`**. The wrapper carries no ordering, so we shim the standard
     * no-op `ORDER BY (SELECT NULL)`. `FETCH NEXT` is omitted when only `skip`
     * is set (SQL Server allows `OFFSET n ROWS` alone), which also avoids the
     * bigint sentinel the other engines need for skip-only paging.
     *
     * @param int $top  Row cap; <= 0 means "no cap" (skip-only paging).
     * @param int $skip Rows to offset; must be >= 0.
     */
    public static function paginate(string $driver, string $innerSql, int $top, int $skip): string
    {
        if ($driver === 'sqlsrv') {
            $clause = ' ORDER BY (SELECT NULL) OFFSET ' . $skip . ' ROWS';
            if ($top > 0) {
                $clause .= ' FETCH NEXT ' . $top . ' ROWS ONLY';
            }
            return "SELECT * FROM ({$innerSql}) AS __m{$clause}";
        }

        // LIMIT/OFFSET values are validated non-negative integers — safe to
        // interpolate. Bound parameters would force PDO type juggling on
        // MySQL native prepared statements; the literal form is portable.
        $limitClause = $top > 0
            ? " LIMIT {$top}"
            : ' LIMIT 9223372036854775807';
        $offsetClause = $skip > 0 ? " OFFSET {$skip}" : '';

        return "SELECT * FROM ({$innerSql}) AS __m{$limitClause}{$offsetClause}";
    }

    /**
     * Build the allocate-and-insert statement for manual-PK tables (legacy
     * schemas whose int PK has no identity/autoincrement/default): the
     * framework computes `MAX(pk) + 1` in the INSERT itself so allocation
     * and insert are one statement — no round trip a racing writer can
     * slip between. See docs/shape/MANUAL-PK-PLAN.md §2.1.
     *
     *   sqlsrv  `WITH (UPDLOCK, HOLDLOCK)` takes a key-range lock at the top
     *           of the PK index (blocks concurrent allocators until commit);
     *           `OUTPUT INSERTED.pk` returns the allocated id in-band.
     *   pgsql   `RETURNING pk` returns the id; callers additionally take
     *           `pg_advisory_xact_lock` to serialize framework allocators.
     *   mysql   InnoDB's INSERT…SELECT locks the scanned index records; the
     *           statement can't return a non-identity id, so the runtime
     *           (Database::insertWithManualPkOnPdo) allocates via
     *           selectNextPk() + explicit insert instead — this form is the
     *           documented generic shape.
     *   sqlite  single-writer by design; same caveat as mysql for the id.
     *
     * @param list<string> $columns Non-PK columns, bound as :name placeholders.
     */
    public static function insertAllocatingPk(string $driver, string $table, string $pk, array $columns): string
    {
        $qt = self::quoteIdentifier($driver, $table);
        $qpk = self::quoteIdentifier($driver, $pk);
        $colList = implode(', ', array_map(
            static fn(string $c): string => self::quoteIdentifier($driver, $c),
            $columns
        ));
        $placeholders = implode(', ', array_map(
            static fn(string $c): string => ':' . $c,
            $columns
        ));
        $intoCols = $colList !== '' ? ", {$colList}" : '';
        $selectVals = $placeholders !== '' ? ", {$placeholders}" : '';
        $nextPk = $driver === 'sqlsrv'
            ? "ISNULL(MAX({$qpk}), 0) + 1"
            : "COALESCE(MAX({$qpk}), 0) + 1";

        if ($driver === 'sqlsrv') {
            return "INSERT INTO {$qt} ({$qpk}{$intoCols}) OUTPUT INSERTED.{$qpk} "
                . "SELECT {$nextPk}{$selectVals} FROM {$qt} WITH (UPDLOCK, HOLDLOCK)";
        }

        $sql = "INSERT INTO {$qt} ({$qpk}{$intoCols}) SELECT {$nextPk}{$selectVals} FROM {$qt}";
        if ($driver === 'pgsql') {
            $sql .= " RETURNING {$qpk}";
        }
        return $sql;
    }

    /**
     * Locked next-id read for the drivers whose INSERT can't return a
     * non-identity PK (mysql/sqlite). On MySQL, `FOR UPDATE` inside a
     * transaction takes the same top-of-index gap lock the sqlsrv
     * UPDLOCK/HOLDLOCK form does, serializing framework allocators; SQLite
     * has no FOR UPDATE but is single-writer, and the caller's retry loop
     * covers the remaining read-then-insert race.
     */
    public static function selectNextPk(string $driver, string $table, string $pk): string
    {
        $qt = self::quoteIdentifier($driver, $table);
        $qpk = self::quoteIdentifier($driver, $pk);
        $sql = "SELECT COALESCE(MAX({$qpk}), 0) + 1 FROM {$qt}";
        if ($driver === 'mysql') {
            $sql .= ' FOR UPDATE';
        }
        return $sql;
    }
}
