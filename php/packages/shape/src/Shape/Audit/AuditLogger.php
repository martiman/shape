<?php

declare(strict_types=1);

namespace Shape\Audit;

use ShapeRuntime\Core\Database;

/**
 * Tier-1 append-only audit logger.
 *
 * Writes one row to `audit_log` per successful mutation, capturing:
 *  - WHEN the change happened (UTC timestamp)
 *  - WHO did it (actor_id from authContext.id / oid / sub, plus email + role
 *    denormalized for forensic readability)
 *  - WHAT was touched (table_name, row_id when known, action)
 *  - WHICH columns changed (column names only, not values — keeps PII out of
 *    the log by default; Tier 2 will add opt-in full diffs)
 *
 * Schema convention (the dev creates this table, Shape detects it at boot):
 *
 *     CREATE TABLE audit_log (
 *       id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
 *       occurred_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
 *       actor_id    VARCHAR(64) NULL,
 *       actor_email VARCHAR(190) NULL,
 *       actor_role  VARCHAR(40) NULL,
 *       table_name  VARCHAR(64) NOT NULL,
 *       row_id      VARCHAR(64) NULL,
 *       action      VARCHAR(16) NOT NULL,
 *       changed     TEXT NULL
 *     );
 *
 * The logger is invoked by BatchExecutor after each successful insert /
 * update / delete. When the calling mutation batch runs inside a transaction
 * (`transaction: true`), the audit write is part of the same transaction and
 * is therefore atomic with the data change. Outside a transaction, audit
 * writes are best-effort.
 *
 * The audit_log table itself joins SECRET_TABLES_BASELINE — never auto-CRUDed,
 * so it can't be tampered with through the normal mutation surface.
 */
final class AuditLogger
{
    public function __construct(
        private readonly Database $database,
        private readonly string $tableName = 'audit_log',
    ) {
    }

    /**
     * @param array<string, mixed> $authContext
     * @param list<string>         $changedColumns Column names touched (data keys for insert/update; empty for delete)
     */
    public function log(
        string $targetTable,
        string $action,
        ?string $rowId,
        array $authContext,
        array $changedColumns,
    ): void {
        $row = [
            'occurred_at' => gmdate('Y-m-d H:i:s'),
            'actor_id'    => $this->resolveActorId($authContext),
            'actor_email' => self::stringOrNull($authContext['email'] ?? null),
            'actor_role'  => self::stringOrNull($authContext['role'] ?? null),
            'table_name'  => $targetTable,
            'row_id'      => $rowId,
            'action'      => $action,
            'changed'     => $changedColumns === []
                ? null
                : json_encode(array_values($changedColumns), JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES),
        ];

        $cols = array_keys($row);
        $quoted = array_map(fn(string $c): string => $this->database->quoteIdentifier($c), $cols);
        $placeholders = array_map(fn(string $c): string => ':' . $c, $cols);

        $sql = 'INSERT INTO ' . $this->database->quoteIdentifier($this->tableName)
             . ' (' . implode(', ', $quoted) . ')'
             . ' VALUES (' . implode(', ', $placeholders) . ')';

        $this->database->exec($sql, $row);
    }

    /**
     * Pick a stable actor identifier from the auth context.
     * Mirrors the same lookup BatchExecutor uses for the per-row
     * `created_by` / `updated_by` audit columns, so the same caller
     * shows up identically in both places.
     *
     * @param array<string, mixed> $authContext
     */
    private function resolveActorId(array $authContext): ?string
    {
        foreach (['id', 'oid', 'sub'] as $key) {
            $val = $authContext[$key] ?? null;
            if ($val === null) {
                continue;
            }
            if (is_int($val)) {
                if ($val > 0) {
                    return (string)$val;
                }
                continue;
            }
            if (is_string($val) && $val !== '' && $val !== '0') {
                return $val;
            }
        }
        return null;
    }

    private static function stringOrNull(mixed $v): ?string
    {
        if ($v === null) {
            return null;
        }
        if (is_scalar($v)) {
            $s = (string)$v;
            return $s === '' ? null : $s;
        }
        return null;
    }
}
