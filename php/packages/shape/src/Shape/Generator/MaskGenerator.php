<?php

declare(strict_types=1);

namespace Shape\Generator;

use Shape\Metadata\EntityMeta;
use Shape\Metadata\FieldMeta;
use Shape\Metadata\SensitiveColumns;
use ShapeRuntime\Core\SqlDialect;

final class MaskGenerator
{
    private string $driver;
    private int $defaultListLimit;
    /** @var array<string, string> table → 'manual'; resolved by ShapeApp from pk_assignment + schema */
    private array $pkStrategies;
    /** @var array<string, string> table → tenancy column; resolved by ShapeApp from tenancy config + schema */
    private array $tenantScopes;
    /** @var list<string> roles that see across tenants (drives whether `sql_unscoped` is emitted) */
    private array $unscopedRoles;
    /** @var array<string, array<string, array{multi: bool}>> table → column → {multi}; resolved by ShapeApp from scopes config + schema (ROW-SCOPES-PHP-PLAN §2.3) */
    private array $rowScopes;
    /** @var array<string, array<string, mixed>> normalized `scopes` config keyed by column (per-scope unscoped_roles drive `sql_unscoped`) */
    private array $scopesConfig;

    /**
     * @param array<string, string> $pkStrategies
     * @param array<string, string> $tenantScopes
     * @param list<string> $unscopedRoles
     * @param array<string, array<string, array{multi: bool}>> $rowScopes
     * @param array<string, array<string, mixed>> $scopesConfig
     */
    public function __construct(
        string $driver = 'mysql',
        int $defaultListLimit = 100,
        array $pkStrategies = [],
        array $tenantScopes = [],
        array $unscopedRoles = [],
        array $rowScopes = [],
        array $scopesConfig = [],
    ) {
        $this->driver = $driver;
        $this->defaultListLimit = $defaultListLimit > 0 ? $defaultListLimit : 0;
        $this->pkStrategies = $pkStrategies;
        $this->tenantScopes = $tenantScopes;
        $this->unscopedRoles = $unscopedRoles;
        $this->rowScopes = $rowScopes;
        $this->scopesConfig = $scopesConfig;
    }

    /**
     * Generate masks from entity metadata.
     *
     * @param array<string, EntityMeta> $entities keyed by class name
     * @return array<string, array<string, mixed>>
     */
    public function generate(array $entities): array
    {
        $masks = [];
        foreach ($entities as $meta) {
            foreach ($this->generateForEntity($meta, $entities) as $name => $mask) {
                $masks[$name] = $mask;
            }
        }
        return $masks;
    }

    /**
     * @param array<string, EntityMeta> $allEntities
     * @return array<string, array<string, mixed>>
     */
    private function generateForEntity(EntityMeta $meta, array $allEntities = []): array
    {
        $table = $meta->table;
        $columns = $this->quotedColumnList($meta);
        $qt = $this->q($table);
        $pk = $meta->primaryKey();
        $masks = [];

        // Soft-delete: when set, all generated reads filter out tombstoned rows
        // and the writable mask carries a hint so DELETE becomes UPDATE.
        $softCol = $meta->softDeleteColumn;
        $softWhere = $softCol !== null ? " WHERE {$this->q($softCol)} IS NULL" : '';
        $softAnd = $softCol !== null ? " AND {$this->q($softCol)} IS NULL" : '';
        $softNote = $softCol !== null ? ', excluding soft-deleted rows' : '';

        // Tenancy: when the table carries the discriminator, every generated
        // read mask filters `<col> = :__tenant` — combined with the soft-delete
        // guard, same "compiled at build time, bound at run time" mechanism
        // (MULTI-TENANCY-PLAN §2.4). A second `sql_unscoped` variant (predicate
        // dropped) is emitted only when unscoped_roles is configured, so a
        // superadmin executes it and a tenant user executes the scoped `sql` —
        // two compiled strings beat one index-defeating OR predicate.
        $tenantCol = $this->tenantScopes[$table] ?? null;
        $tenantAnd = $tenantCol !== null ? " AND {$this->q($tenantCol)} = :__tenant" : '';
        // Dimension row scopes (ROW-SCOPES-PHP-PLAN §2.3): every scope column
        // the table carries adds `AND <col> = :__scope_<col>` (IN when multi)
        // alongside the tenant predicate — fast path, fully compiled.
        $scopeCols = $this->rowScopes[$table] ?? [];
        $scopeAnd = '';
        foreach ($scopeCols as $col => $sMeta) {
            $scopeAnd .= ' AND ' . $this->scopePredicate($col, $sMeta['multi']);
        }
        // list WHERE combining tenancy + scopes + soft-delete for the scoped variant.
        $listConds = [];
        if ($tenantCol !== null) {
            $listConds[] = "{$this->q($tenantCol)} = :__tenant";
        }
        foreach ($scopeCols as $col => $sMeta) {
            $listConds[] = $this->scopePredicate($col, $sMeta['multi']);
        }
        if ($softCol !== null) {
            $listConds[] = "{$this->q($softCol)} IS NULL";
        }
        $listWhere = $listConds !== [] ? ' WHERE ' . implode(' AND ', $listConds) : '';

        // list
        $listMask = $this->withScopes([
            'sql' => "SELECT {$columns} FROM {$qt}{$listWhere}",
            'params' => [],
            'roles' => $meta->readRoles,
            'description' => "List rows from {$table}{$softNote}.",
        ], $tenantCol, $scopeCols, "SELECT {$columns} FROM {$qt}{$softWhere}");
        // BatchExecutor pushes LIMIT into the SQL when no orderby/groupby
        // is requested. Without a default cap, an auto .list against a
        // 75k-row table OOMs PHP. Explicit ?$top= overrides; orderby falls
        // back to in-memory because that path can't safely pre-truncate.
        if ($this->defaultListLimit > 0) {
            $listMask['default_limit'] = $this->defaultListLimit;
        }
        $masks[$table . '.list'] = $listMask;

        if ($pk !== null) {
            $qpk = $this->q($pk->name);

            // get
            $masks[$table . '.get'] = $this->withScopes([
                'sql' => "SELECT {$columns} FROM {$qt} WHERE {$qpk} = :{$pk->name}{$tenantAnd}{$scopeAnd}{$softAnd}",
                'params' => [$pk->name => ['type' => $pk->paramType(), 'required' => true]],
                'roles' => $meta->readRoles,
                'description' => "Get one row from {$table} by primary key ({$pk->name}){$softNote}.",
            ], $tenantCol, $scopeCols, "SELECT {$columns} FROM {$qt} WHERE {$qpk} = :{$pk->name}{$softAnd}");

            // getMany
            $arrayType = $pk->phpType === 'int' ? 'array_int' : 'array_string';
            $paramName = $pk->name . 's';
            $masks[$table . '.getMany'] = $this->withScopes([
                'sql' => "SELECT {$columns} FROM {$qt} WHERE {$qpk} IN (:{$paramName}){$tenantAnd}{$scopeAnd}{$softAnd}",
                'params' => [$paramName => ['type' => $arrayType, 'required' => true]],
                'roles' => $meta->readRoles,
                'description' => "Get multiple rows from {$table} by a list of primary keys ({$pk->name}){$softNote}.",
            ], $tenantCol, $scopeCols, "SELECT {$columns} FROM {$qt} WHERE {$qpk} IN (:{$paramName}){$softAnd}");
        }

        // byFK
        foreach ($meta->fkFields() as $field) {
            $maskName = $table . '.by' . self::pascalCase($field->name);
            $qf = $this->q($field->name);
            $byFkMask = $this->withScopes([
                'sql' => "SELECT {$columns} FROM {$qt} WHERE {$qf} = :{$field->name}{$tenantAnd}{$scopeAnd}{$softAnd}",
                'params' => [$field->name => ['type' => $field->paramType(), 'required' => true]],
                'roles' => $meta->readRoles,
                'description' => "List {$table} rows filtered by {$field->name}{$softNote}.",
            ], $tenantCol, $scopeCols, "SELECT {$columns} FROM {$qt} WHERE {$qf} = :{$field->name}{$softAnd}");
            // by<FK> is a list shape too — same rationale as .list.
            if ($this->defaultListLimit > 0) {
                $byFkMask['default_limit'] = $this->defaultListLimit;
            }
            $masks[$maskName] = $byFkMask;
        }

        // writable
        $mode = $meta->effectiveWritableMode();
        if ($mode !== 'none') {
            $firstCol = $meta->fields[0]->name ?? 'id';
            $writeNote = $softCol !== null
                ? " DELETE soft-deletes (sets {$softCol})."
                : '';
            $auditNote = $meta->auditColumns !== []
                ? ' Audit columns (' . implode(', ', array_keys($meta->auditColumns)) . ') are populated automatically — do not include them in `data`.'
                : '';

            // Inverse FK map: which child tables have FKs pointing at THIS
            // parent, and through which columns? Used by BatchExecutor's
            // bulk-nested-create path to walk `relations` blocks.
            $childRelations = self::collectChildRelations($meta, $allEntities);
            $relationsNote = $childRelations !== []
                ? ' Bulk-create children inline via `relations: { <child_table>: [...] }` — framework walks the FK chain in one transaction.'
                : '';
            $capabilities = match ($mode) {
                'append_only' => "Insert only — UPDATE and DELETE rejected (append-only contract).",
                'no_delete' => "Insert / update only — DELETE rejected.",
                default => "Insert / update / delete rows",
            };

            // Per-operation split (roles_by_action): the mask-level `roles`
            // gate must admit every per-action role — the union — while the
            // executor narrows per mutation action. Absent when no action
            // was configured explicitly (mask roles govern all actions).
            $rolesByAction = $meta->writeRolesByAction();
            $writableRoles = $meta->writeRoles;
            if ($rolesByAction !== []) {
                $writableRoles = array_values(array_unique(array_merge($writableRoles, ...array_values($rolesByAction))));
            }

            $writableMask = [
                'sql' => $this->driver === 'sqlsrv'
                    ? "SELECT TOP 1 {$this->q($firstCol)} FROM {$qt}"
                    : "SELECT {$this->q($firstCol)} FROM {$qt} LIMIT 1",
                'roles' => $writableRoles,
                'writable' => true,
                'description' => "{$capabilities} in {$table}.{$writeNote}{$auditNote}{$relationsNote}",
            ];
            if ($rolesByAction !== []) {
                $writableMask['roles_by_action'] = $rolesByAction;
            }
            if ($mode !== 'full') {
                $writableMask['writable_mode'] = $mode;
            }
            if ($softCol !== null) {
                $writableMask['soft_delete'] = $softCol;
            }
            if ($meta->auditColumns !== []) {
                $writableMask['audit'] = $meta->auditColumns;
            }
            if ($childRelations !== []) {
                $writableMask['relations'] = $childRelations;
            }
            // PK carrier keys (cross-language contract — .NET emits the same
            // `pk` / `pk_strategy` names): `pk` keeps INSERT's RETURNING/
            // OUTPUT correct for tables whose PK isn't named `id`;
            // `pk_strategy` 'manual' routes inserts through framework-side
            // MAX+1 allocation. Composite PKs are omitted — there is no
            // single column to return or allocate.
            $pkFields = array_values(array_filter(
                $meta->fields,
                static fn(FieldMeta $f): bool => $f->isPrimary
            ));
            if (count($pkFields) === 1) {
                $writableMask['pk'] = $pkFields[0]->name;
                if (($this->pkStrategies[$table] ?? null) === 'manual') {
                    $writableMask['pk_strategy'] = 'manual';
                }
            }
            // Tenancy / row-scope carriers on the writable mask drive the
            // executor's INSERT stamping and the UPDATE/DELETE WHERE guards
            // (§2.5, ROW-SCOPES §2.4). No `sql_unscoped` here — the writable
            // "mask" is only a probe SELECT.
            if ($tenantCol !== null) {
                $writableMask['tenant_scope'] = $tenantCol;
            }
            if ($scopeCols !== []) {
                $writableMask['row_scopes'] = $scopeCols;
            }
            // Cross-tenant FK escape hatch (§2.5): compile the FK targets that
            // are themselves tenant-scoped onto the writable mask so the
            // opt-in `tenancy.validate_fks` runtime check knows what to probe
            // — an `order` must not point at another tenant's `customer_id`.
            $fkChecks = $this->collectScopedFkChecks($meta, $allEntities);
            if ($fkChecks !== []) {
                $writableMask['fk_checks'] = $fkChecks;
            }

            // Field-level write gate (SECURITY-REVIEW H1): only these columns
            // may appear in a client `data` block. Excludes audit columns
            // (auto-stamped — listing them would let a client forge
            // created_by/updated_at), auto-increment PKs (DB-assigned), and
            // sensitive/privilege columns (password_hash, role, is_active, … —
            // the escalation vector). The tenant + row-scope columns stay
            // writable: their values are validated and stamped server-side, and
            // unscoped-role writes must supply them.
            $manualPk = ($this->pkStrategies[$table] ?? null) === 'manual';
            $auditCols = $meta->auditColumns;
            $writableColumns = [];
            foreach ($meta->fields as $f) {
                if (array_key_exists($f->name, $auditCols)) {
                    continue;
                }
                if ($f->isPrimary && ($f->isAutoIncrement || !$manualPk)) {
                    continue;
                }
                $isTenantOrScope = ($tenantCol !== null && $f->name === $tenantCol)
                    || array_key_exists($f->name, $scopeCols);
                if (SensitiveColumns::isWriteSensitive($f->name) && !$isTenantOrScope) {
                    continue;
                }
                $writableColumns[] = $f->name;
            }
            $writableMask['writable_columns'] = $writableColumns;

            $masks[$table] = $writableMask;
        }

        return $masks;
    }

    /**
     * Attach the scoping carrier keys to a generated read mask:
     * `tenant_scope` (the tenancy column), `row_scopes` (column => {multi}
     * per dimension the table carries), and `sql_unscoped` (the
     * predicate-free variant, emitted when tenancy or any of the table's
     * scopes has unscoped roles so an exempt caller has something to
     * execute — the wrap path re-appends the predicates it IS subject to).
     * The mask's own `sql` already carries every predicate.
     *
     * @param array<string, mixed> $mask
     * @param array<string, array{multi: bool}> $scopeCols
     * @return array<string, mixed>
     */
    private function withScopes(array $mask, ?string $tenantCol, array $scopeCols, string $unscopedSql): array
    {
        if ($tenantCol === null && $scopeCols === []) {
            return $mask;
        }
        if ($tenantCol !== null) {
            $mask['tenant_scope'] = $tenantCol;
        }
        if ($scopeCols !== []) {
            $mask['row_scopes'] = $scopeCols;
        }

        $hasUnscoped = $tenantCol !== null && $this->unscopedRoles !== [];
        foreach (array_keys($scopeCols) as $col) {
            $roles = $this->scopesConfig[$col]['unscoped_roles'] ?? [];
            $hasUnscoped = $hasUnscoped || (is_array($roles) && $roles !== []);
        }
        if ($hasUnscoped) {
            $mask['sql_unscoped'] = $unscopedSql;
        }
        return $mask;
    }

    /** The compiled read predicate for one dimension scope (§2.3). */
    private function scopePredicate(string $col, bool $multi): string
    {
        return $multi
            ? "{$this->q($col)} IN (:__scope_{$col})"
            : "{$this->q($col)} = :__scope_{$col}";
    }

    /**
     * Build the inverse-FK map for $parent: child tables whose FK columns
     * reference $parent's PK. The result keys child-table-name → list of
     * FK column names. When a child table has multiple FKs pointing to the
     * same parent (e.g. notes.author_id AND notes.editor_id both → users),
     * both columns appear and the dispatcher requires explicit
     * disambiguation at call time.
     *
     * @param array<string, EntityMeta> $allEntities
     * @return array<string, array{fk_columns: list<string>}>
     */
    private static function collectChildRelations(EntityMeta $parent, array $allEntities): array
    {
        $relations = [];
        $parentTable = $parent->table;
        $parentClass = $parent->class;

        foreach ($allEntities as $candidate) {
            if ($candidate->table === $parentTable && $candidate->class === $parentClass) {
                continue;
            }
            foreach ($candidate->fkFields() as $fk) {
                $target = $fk->fkEntity;
                if ($target === null) {
                    continue;
                }
                // DB-first: fkEntity is the table name directly.
                // Entity-first: fkEntity is a class name; match against $parent->class.
                $matches = ($target === $parentTable) || ($target === $parentClass);
                if (!$matches) {
                    continue;
                }
                $relations[$candidate->table]['fk_columns'][] = $fk->name;
            }
        }
        return $relations;
    }

    /**
     * FK columns of $meta whose target table is tenant-scoped, resolved to
     * `column => ['table' => <target>, 'pk' => <target pk>, 'tenant_column' => <col>]`.
     * Only scoped targets are carried — the check exists solely to stop a
     * cross-tenant reference (MULTI-TENANCY-PLAN §2.5); unscoped targets have
     * nothing to leak. Mirrors the tenant leg of .NET's mask.FkChecks.
     *
     * @param array<string, EntityMeta> $allEntities
     * @return array<string, array{table:string, pk:string, tenant_column:string}>
     */
    private function collectScopedFkChecks(EntityMeta $meta, array $allEntities): array
    {
        if ($this->tenantScopes === []) {
            return [];
        }
        $checks = [];
        foreach ($meta->fkFields() as $fk) {
            $target = $fk->fkEntity;
            if ($target === null) {
                continue;
            }
            // DB-first: fkEntity is the table name; entity-first: a class name.
            $targetMeta = null;
            foreach ($allEntities as $candidate) {
                if ($candidate->table === $target || $candidate->class === $target) {
                    $targetMeta = $candidate;
                    break;
                }
            }
            if ($targetMeta === null) {
                continue;
            }
            $tenantColumn = $this->tenantScopes[$targetMeta->table] ?? null;
            if ($tenantColumn === null) {
                continue;
            }
            $targetPk = $fk->fkColumn;
            if ($targetPk === null) {
                $pkField = $targetMeta->primaryKey();
                $targetPk = $pkField !== null ? $pkField->name : 'id';
            }
            $checks[$fk->name] = [
                'table' => $targetMeta->table,
                'pk' => $targetPk,
                'tenant_column' => $tenantColumn,
            ];
        }
        return $checks;
    }

    private function q(string $name): string
    {
        return SqlDialect::quoteIdentifier($this->driver, $name);
    }

    private function quotedColumnList(EntityMeta $meta): string
    {
        return implode(', ', array_map(
            fn(FieldMeta $f) => $this->q($f->name),
            $meta->fields
        ));
    }

    private static function pascalCase(string $snakeCase): string
    {
        return str_replace(' ', '', ucwords(str_replace('_', ' ', $snakeCase)));
    }
}
