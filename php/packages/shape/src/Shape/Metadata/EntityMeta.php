<?php

declare(strict_types=1);

namespace Shape\Metadata;

final class EntityMeta
{
    /**
     * @param list<FieldMeta> $fields
     * @param list<string> $readRoles
     * @param list<string> $writeRoles
     * @param ?string $softDeleteColumn Name of the timestamp column that flags
     *   rows as soft-deleted (typically 'deleted_at'). When non-null, generated
     *   read masks add `WHERE <col> IS NULL`, and DELETE mutations on the
     *   writable mask are translated to `UPDATE … SET <col> = CURRENT_TIMESTAMP`.
     * @param array<string, string> $auditColumns Map of audit column name →
     *   kind ('timestamp' | 'user_id'). Detected by name+type convention:
     *   `created_at`/`updated_at` → 'timestamp', `created_by`/`updated_by`
     *   → 'user_id'. BatchExecutor stamps these automatically on insert/
     *   update unless the dev's payload supplies its own value.
     */
    public function __construct(
        public readonly string $class,
        public readonly string $table,
        public readonly bool $writable,
        public readonly array $fields,
        public readonly array $readRoles = [],
        public readonly array $writeRoles = [],
        public readonly ?string $softDeleteColumn = null,
        public readonly array $auditColumns = [],
        /** @var list<list<string>> */
        public readonly array $uniqueConstraints = [],
        public readonly ?string $description = null,
        public readonly ?string $writableMode = null,
        /**
         * Per-operation write roles (create/update/delete). Non-empty only
         * when at least one action was configured explicitly — then ALL
         * three keys are present, each resolved by the action → write →
         * roles cascade at read time (an empty list means "nobody").
         * @var array<string, list<string>>
         */
        public readonly array $writeRolesByAction = [],
    ) {}

    /**
     * Per-operation roles, tolerant of pre-upgrade cache payloads: an
     * unserialized EntityMeta from an older cache lacks the property (typed
     * props stay uninitialized — probed via reflection, which static
     * analysis can't wrongly "prove" initialized).
     *
     * @return array<string, list<string>>
     */
    public function writeRolesByAction(): array
    {
        $prop = new \ReflectionProperty($this, 'writeRolesByAction');
        return $prop->isInitialized($this) ? $this->writeRolesByAction : [];
    }

    public function primaryKey(): ?FieldMeta
    {
        foreach ($this->fields as $field) {
            if ($field->isPrimary) {
                return $field;
            }
        }
        return null;
    }

    /** @return list<FieldMeta> */
    public function fkFields(): array
    {
        return array_values(array_filter($this->fields, fn(FieldMeta $f) => $f->isFk()));
    }

    /** @return list<string> */
    public function columnNames(): array
    {
        return array_map(fn(FieldMeta $f) => $f->name, $this->fields);
    }

    public function effectiveWritableMode(): string
    {
        $mode = trim((string)($this->writableMode ?? ''));
        if ($mode !== '') {
            return strtolower($mode);
        }

        return $this->writable ? 'full' : 'none';
    }

    /**
     * Detect a soft-delete marker column. Convention: a nullable timestamp
     * column literally named `deleted_at`. Returns the column name, or null
     * if the table is not soft-deletable.
     *
     * @param list<FieldMeta> $fields
     */
    public static function detectSoftDeleteColumn(array $fields): ?string
    {
        foreach ($fields as $field) {
            if ($field->name !== 'deleted_at') {
                continue;
            }
            if (!$field->nullable) {
                continue;
            }
            $type = strtolower($field->dbType);
            if (in_array($type, ['timestamp', 'datetime'], true)) {
                return 'deleted_at';
            }
        }
        return null;
    }

    /**
     * Detect audit columns by name + type convention.
     *
     *   `created_at`, `updated_at` (timestamp / datetime)
     *     → kind 'timestamp', stamped with the current UTC time on insert /
     *       (and `updated_at` also on update)
     *
     *   `created_by`, `updated_by` (int / bigint / varchar / char)
     *     → kind 'user_id', stamped with the authenticated caller's id on
     *       insert / (and `updated_by` also on update)
     *
     * Returns a map ['column_name' => 'timestamp'|'user_id']. Empty map when
     * no audit columns are present.
     *
     * @param list<FieldMeta> $fields
     * @return array<string, string>
     */
    public static function detectAuditColumns(array $fields): array
    {
        $audit = [];
        foreach ($fields as $field) {
            $name = $field->name;
            $type = strtolower($field->dbType);

            if (in_array($name, ['created_at', 'updated_at'], true)
                && in_array($type, ['timestamp', 'datetime'], true)
            ) {
                $audit[$name] = 'timestamp';
                continue;
            }

            if (in_array($name, ['created_by', 'updated_by'], true)
                && in_array($type, ['int', 'integer', 'bigint', 'smallint', 'varchar', 'char'], true)
            ) {
                $audit[$name] = 'user_id';
            }
        }
        return $audit;
    }
}
