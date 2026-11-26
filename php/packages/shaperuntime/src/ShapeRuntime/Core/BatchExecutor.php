<?php

declare(strict_types=1);

namespace ShapeRuntime\Core;

use Throwable;

/**
 * @phpstan-type ScopeSpec array{kind:string,column:string,claim:string,param:string,multi:bool,unscoped_roles:list<string>}
 * @phpstan-type ScopeGuard array{kind:string,column:string,param:string,multi:bool,value:int|string|list<int|string|null>}
 */
final class BatchExecutor
{
    private const MAX_TOP = 200;

    /**
     * Cap on queries/mutations per batch (SECURITY-REVIEW M5). `top` bounds
     * rows per query; this bounds ops per request against a DoS.
     */
    private const MAX_BATCH_ITEMS = 100;

    private ParamResolver $resolver;
    private ValidatorRegistry $validators;

    /** @var (callable(string,string,?string,array<string, mixed>,list<string>,?array<string, mixed>,?array<string, mixed>):void)|null */
    private $auditLogger = null;

    /** @var (callable(list<array{table:string,action:string,affected:int}>):void)|null */
    private $changeListener = null;

    /** @var array{tables:?list<string>, exclude:list<string>}|null Audit Tier 2 (before/after values). */
    private ?array $auditDiff = null;

    /** @var (callable():void)|null */
    private $auditProbe = null;
    private bool $auditProbed = false;

    /** @var list<string> Roles that see across tenants (MULTI-TENANCY-PLAN §2.3). */
    private array $unscopedRoles = [];

    /** @var array<string, mixed> Normalized tenancy config; [] when tenancy is off. */
    private array $tenancy = [];

    /** @var array<string, array<mixed, mixed>> Normalized dimension scopes keyed by column (ROW-SCOPES-PHP-PLAN §2.1); [] when off. */
    private array $scopes = [];

    public function __construct(
        private readonly Database $database,
        private readonly MaskRegistry $registry,
        ?ValidatorRegistry $validators = null
    ) {
        $this->resolver = new ParamResolver();
        $this->validators = $validators ?? new ValidatorRegistry();
    }

    /**
     * Wire an audit logger callback. Called after each successful mutation
     * with (targetTable, action, rowId, authContext, changedColumns). When
     * the caller wraps the batch in a transaction, the audit write is part
     * of that same transaction (atomic). Outside a transaction, best-effort.
     *
     * Decoupled via callable so BatchExecutor (in shaperuntime) doesn't have
     * to import the higher-level Shape\Audit\AuditLogger class.
     *
     * @param callable(string,string,?string,array<string,mixed>,list<string>):void $cb
     */
    public function setAuditLogger(callable $cb): void
    {
        $this->auditLogger = $cb;
    }

    /**
     * Called once per mutation batch AFTER it succeeded (after the commit when transactional) with the list of
     * `{table, action, affected}` that actually changed rows. Used for event notifications; an exception thrown by
     * the listener never fails the mutation.
     *
     * @param callable(list<array{table:string,action:string,affected:int}>):void $cb
     */
    public function setChangeListener(callable $cb): void
    {
        $this->changeListener = $cb;
    }

    /** @param list<array<string, mixed>> $results */
    private function fireChanges(array $results): void
    {
        if ($this->changeListener === null) {
            return;
        }
        $changes = [];
        foreach ($results as $r) {
            if ((int)($r['affected'] ?? 0) > 0 && isset($r['table'], $r['action'])) {
                $changes[] = ['table' => (string)$r['table'], 'action' => (string)$r['action'], 'affected' => (int)$r['affected']];
            }
        }
        if ($changes === []) {
            return;
        }
        try {
            ($this->changeListener)($changes);
        } catch (Throwable) {
            // a notification problem must never turn a committed mutation into an error
        }
    }

    /**
     * Audit Tier 2: capture before/after VALUES (not just column names) for the
     * given tables. Requires an audit logger. Update/delete snapshot the matched
     * rows (same WHERE + scope guards as the mutation) before it runs and log one
     * audit entry per affected row; sensitive columns are recorded as '[redacted]'.
     *
     * @param list<string>|null $tables  null = every table
     * @param list<string>      $exclude 'column' or 'table.column' entries whose values are redacted
     * @param (callable():void)|null $probe called once before the first mutation to fail loudly when
     *                                      the audit table lacks the old_values/new_values columns
     */
    public function setAuditDiff(?array $tables, array $exclude = [], ?callable $probe = null): void
    {
        $this->auditDiff = ['tables' => $tables, 'exclude' => $exclude];
        $this->auditProbe = $probe;
        $this->auditProbed = false;
    }

    private function diffActiveFor(string $table): bool
    {
        if ($this->auditDiff === null || $this->auditLogger === null || $table === 'audit_log') {
            return false;
        }
        return $this->auditDiff['tables'] === null || in_array($table, $this->auditDiff['tables'], true);
    }

    /**
     * Supply the tenancy runtime config (MULTI-TENANCY-PLAN §2.3). Consumed
     * here: `unscoped_roles` (variant selection / guard exemption) and
     * `validate_fks` (opt-in cross-tenant FK check §2.5) — the per-mask
     * scoping column travels on the mask itself (`tenant_scope`) and the
     * caller's tenant on the auth context (`tenant_id`). Called by
     * ShapeRouter wherever an executor is built; a no-op when tenancy is
     * disabled.
     *
     * @param array<string, mixed> $tenancy
     */
    public function setTenancy(array $tenancy): void
    {
        $this->setScoping($tenancy, $this->scopes);
    }

    /**
     * Supply tenancy + dimension row-scope runtime config in one call
     * (ROW-SCOPES-PHP-PLAN §2.6 / P2). `$scopes` is the normalized
     * `scopes` map keyed by column ({claim, unscoped_roles, multi, ...});
     * per-mask participation travels on the mask itself (`row_scopes`).
     *
     * @param array<string, mixed> $tenancy
     * @param array<mixed, mixed> $scopes Raw or normalized config — entries
     *   are re-validated here before the runtime consumes them.
     */
    public function setScoping(array $tenancy, array $scopes = []): void
    {
        $this->tenancy = $tenancy;
        $roles = $tenancy['unscoped_roles'] ?? [];
        $this->unscopedRoles = is_array($roles)
            ? array_values(array_filter($roles, static fn($r): bool => is_string($r) && $r !== ''))
            : [];
        $clean = [];
        foreach ($scopes as $col => $spec) {
            if (is_string($col) && $col !== '' && is_array($spec)) {
                $clean[$col] = $spec;
            }
        }
        $this->scopes = $clean;
    }

    /**
     * True when the caller's role is exempt from one scope's predicate.
     *
     * @param array{unscoped_roles: list<string>} $spec
     * @param array<string, mixed> $authContext
     */
    private function isUnscopedFor(array $spec, array $authContext): bool
    {
        $role = isset($authContext['role']) ? (string)$authContext['role'] : '';
        return $role !== '' && in_array($role, $spec['unscoped_roles'], true);
    }

    /**
     * @param list<array<string, mixed>> $mutations
     * @param array<string, mixed> $authContext
     * @param bool $useTransaction Whether to wrap mutations in a transaction
     * @return array{results:list<array{ref:?string,action:string,table:string,affected:int}>}
     */
    public function executeMutations(array $mutations, array $authContext = [], bool $useTransaction = false): array
    {
        if ($mutations === []) {
            throw new ApiException('MUTATION_VALIDATION_ERROR', 'Request must include at least one mutation.', 400);
        }
        // SECURITY-REVIEW M5: cap batch size — `top` bounds rows per query, but
        // an unbounded op count is a DoS vector.
        if (count($mutations) > self::MAX_BATCH_ITEMS) {
            throw new ApiException(
                'MUTATION_VALIDATION_ERROR',
                'Batch exceeds the maximum of ' . self::MAX_BATCH_ITEMS . ' mutations.',
                400,
                null,
                ['expected' => 'at most ' . self::MAX_BATCH_ITEMS . ' mutations per request', 'hint' => 'Split the work across multiple requests.']
            );
        }

        // Force a transaction whenever any mutation declares `relations` —
        // a partial nested-create that left orphan children would be worse
        // than a clean rollback. The dev's explicit `transaction: false` is
        // honored only when no nested creates are involved.
        if (!$useTransaction) {
            foreach ($mutations as $m) {
                if (is_array($m) && !empty($m['relations'])) {
                    $useTransaction = true;
                    break;
                }
            }
        }

        if ($useTransaction) {
            return $this->executeMutationsInTransaction($mutations, $authContext);
        }

        return $this->executeMutationsWithoutTransaction($mutations, $authContext);
    }

    /**
     * @param list<array<string, mixed>> $mutations
     * @param array<string, mixed> $authContext
     * @return array{results:list<array{ref:?string,action:string,table:string,affected:int}>}
     */
    private function executeMutationsInTransaction(array $mutations, array $authContext): array
    {
        $this->database->beginTransaction();
        try {
            $results = [];
            $seenRefs = [];

            foreach ($mutations as $mutation) {
                $result = $this->executeSingleMutation($mutation, $authContext, $seenRefs);
                $results[] = $result;
            }

            $this->database->commit();
            $this->fireChanges($results);
            return ['results' => $results];
        } catch (Throwable $e) {
            if ($this->database->isInTransaction()) {
                $this->database->rollback();
            }
            throw $e;
        }
    }

    /**
     * @param list<array<string, mixed>> $mutations
     * @param array<string, mixed> $authContext
     * @return array{results:list<array{ref:?string,action:string,table:string,affected:int}>}
     */
    private function executeMutationsWithoutTransaction(array $mutations, array $authContext): array
    {
        $results = [];
        $seenRefs = [];

        foreach ($mutations as $mutation) {
            $result = $this->executeSingleMutation($mutation, $authContext, $seenRefs);
            $results[] = $result;
        }

        $this->fireChanges($results);
        return ['results' => $results];
    }

    /**
     * @param array<string, mixed> $mutation
     * @param array<string, mixed> $authContext
     * @param array<string, bool> $seenRefs
     * @return array{ref:?string,action:string,table:string,affected:int}
     */
    private function executeSingleMutation(array $mutation, array $authContext, array &$seenRefs): array
    {
        $table = isset($mutation['table']) ? trim((string)$mutation['table']) : '';
        $action = isset($mutation['action']) ? trim((string)$mutation['action']) : '';
        $ref = $mutation['ref'] ?? null;
        $filter = $mutation['filter'] ?? null;
        $data = $mutation['data'] ?? null;

        if ($table === '') {
            throw new ApiException(
                'MUTATION_VALIDATION_ERROR',
                'Field table is required.',
                400,
                $ref,
                [
                    'expected' => ['table' => 'string (required)', 'action' => 'string (required)', 'data' => 'object', 'filter' => 'object'],
                    'example' => ['ref' => 'm1', 'table' => 'books', 'action' => 'insert', 'data' => ['title' => 'example']],
                ]
            );
        }
        if ($action === '' || !in_array($action, ['insert', 'update', 'delete'], true)) {
            throw new ApiException(
                'MUTATION_VALIDATION_ERROR',
                'Field action must be insert, update, or delete.',
                400,
                $ref,
                [
                    'expected' => ['action' => "one of: 'insert', 'update', 'delete'"],
                    'example' => ['ref' => 'm1', 'table' => $table, 'action' => 'insert', 'data' => ['column' => 'value']],
                ]
            );
        }

        if ($ref !== null && isset($seenRefs[$ref])) {
            throw new ApiException('DUPLICATE_REF', "Duplicate ref '{$ref}'.", 400, (string)$ref);
        }
        if ($ref !== null) {
            $seenRefs[$ref] = true;
        }

        // Verify table is in user's writable masks
        $maskName = $table;
        $mask = $this->registry->resolve($maskName, $authContext, $ref ?? $table);
        if (($mask['writable'] ?? false) !== true) {
            throw new ApiException('AUTHORIZATION_ERROR', "Table '{$table}' is not writable for your role.", 403);
        }

        $writableMode = isset($mask['writable_mode']) && is_string($mask['writable_mode']) ? $mask['writable_mode'] : 'full';
        if ($writableMode === 'append_only' && $action !== 'insert') {
            throw new ApiException(
                'WRITABLE_MODE_FORBIDDEN',
                "Table '{$table}' is append-only — '{$action}' is not permitted.",
                403,
                $ref,
                [
                    'expected' => ['action' => "'insert' (append-only contract)"],
                    'hint' => 'Append-only tables accept INSERT only. Use a sibling table or mark the entity writable if mutation is intended.',
                ]
            );
        }
        if ($writableMode === 'no_delete' && $action === 'delete') {
            throw new ApiException(
                'WRITABLE_MODE_FORBIDDEN',
                "Table '{$table}' does not allow DELETE — entity is marked no_delete.",
                403,
                $ref,
                [
                    'expected' => ['action' => "one of: 'insert', 'update'"],
                    'hint' => 'no_delete tables accept INSERT and UPDATE only. Consider a soft-delete column instead, or mark the entity writable=full.',
                ]
            );
        }

        // Per-operation write roles: the mask-level `roles` gate (resolve()
        // above) admits the union of all action roles; narrow to the caller's
        // action here. Config keys are create/update/delete — the mutation
        // action 'insert' maps to 'create'.
        $rolesByAction = is_array($mask['roles_by_action'] ?? null) ? $mask['roles_by_action'] : [];
        if ($rolesByAction !== []) {
            $actionKey = $action === 'insert' ? 'create' : $action;
            $allowed = is_array($rolesByAction[$actionKey] ?? null) ? $rolesByAction[$actionKey] : [];
            $role = isset($authContext['role']) ? (string)$authContext['role'] : '';
            if ($role === '' || !in_array($role, $allowed, true)) {
                throw new ApiException(
                    'AUTH_FORBIDDEN',
                    "Your role cannot {$action} on '{$table}'.",
                    403,
                    $ref,
                    [
                        'expected' => ['role' => $allowed !== [] ? 'one of: ' . implode(', ', $allowed) : 'no role is permitted for this action'],
                        'hint' => "The '{$actionKey}' operation on '{$table}' is restricted separately from the other write operations.",
                    ]
                );
            }
        }

        // Validate action-specific requirements
        if ($action !== 'insert' && !is_array($filter)) {
            throw new ApiException(
                'MUTATION_VALIDATION_ERROR',
                "Field filter is required for {$action} actions.",
                400,
                $ref,
                [
                    'expected' => ['filter' => 'object (required for ' . $action . ')'],
                    'example' => ['ref' => $ref ?? 'm1', 'table' => $table, 'action' => $action, 'filter' => ['id' => 1]] + ($action === 'update' ? ['data' => ['column' => 'newValue']] : []),
                ]
            );
        }
        if (($action === 'insert' || $action === 'update') && !is_array($data)) {
            throw new ApiException(
                'MUTATION_VALIDATION_ERROR',
                "Field data is required for {$action} actions.",
                400,
                $ref,
                [
                    'expected' => ['data' => 'object (required for ' . $action . ')'],
                    'example' => $action === 'insert'
                        ? ['ref' => $ref ?? 'm1', 'table' => $table, 'action' => 'insert', 'data' => ['column' => 'value']]
                        : ['ref' => $ref ?? 'm1', 'table' => $table, 'action' => 'update', 'filter' => ['id' => 1], 'data' => ['column' => 'newValue']],
                ]
            );
        }

        // Run before hooks
        $this->runBeforeHook($table, $action, $data ?? [], $filter ?? []);

        // Run validators
        if ($data !== null) {
            $this->validateFields($table, $action, $data);
        }

        // Field-level write gate (SECURITY-REVIEW H1). Enforce the mask's
        // writable-column allowlist against the CLIENT-supplied data, before
        // any server-side augmentation (audit columns, scope stamping) adds its
        // own keys. Blocks mass-assignment / privilege-escalation (e.g. a client
        // setting `role`/`password_hash` on a writable users entity) and
        // audit-column forgery. Absent allowlist = legacy unrestricted mask.
        if ($data !== null && ($action === 'insert' || $action === 'update')) {
            self::enforceWritableColumns($data, $mask, $table, is_string($ref) ? $ref : null);
        }

        // Audit columns: stamp created_at/by + updated_at/by from convention.
        // Runs AFTER validators so auto-injected values don't fail per-field
        // rules the dev didn't write for them. Dev's explicit value wins.
        if ($data !== null) {
            $data = self::applyAuditColumns($data, $action, $mask, $authContext);
        }

        // Scope stamping / guarding (MULTI-TENANCY-PLAN §2.5, ROW-SCOPES §2.4).
        // INSERT stamps each discriminator from the caller's claims;
        // UPDATE/DELETE constrain the WHERE per scope so a cross-scope id
        // yields affected-rows 0 (no existence oracle across scopes).
        if ($action === 'insert') {
            $data = $this->stampScopesOnInsert($data ?? [], $mask, $authContext);
        }
        $scopeGuards = $this->resolveScopeGuards($mask, $authContext);
        if ($action === 'update' && $scopeGuards !== [] && $data !== null) {
            self::assertNoScopeMove($data, $scopeGuards);
        }

        // Opt-in cross-tenant FK check (§2.5): each FK column whose target
        // table is tenant-scoped must reference a row owned by the effective
        // tenant. Runs after stamping so the row's tenant is authoritative.
        if ($data !== null && $action !== 'delete') {
            $this->checkTenantForeignKeys($data, $mask, $authContext, is_string($ref) ? $ref : null);
        }

        $diff = $this->diffActiveFor($table);
        $snapshot = null;
        if ($diff) {
            if (!$this->auditProbed && $this->auditProbe !== null) {
                ($this->auditProbe)();  // fail before the data changes, not after
                $this->auditProbed = true;
            }
            if ($action !== 'insert') {
                $snapshot = $this->snapshotRows($table, $filter, $mask, $scopeGuards, $action);
            }
        }

        $affected = 0;
        $rowId = null;
        $newId = null;
        switch ($action) {
            case 'insert':
                $insertResult = $this->performInsert($table, $data ?? [], $mask);
                $affected = $insertResult['affected'];
                $newId = $insertResult['new_id'];
                $rowId = $newId !== null ? (string)$newId : null;
                break;
            case 'update':
                $affected = $this->performUpdate($table, $filter ?? [], $data ?? [], $mask, $scopeGuards);
                $rowId = self::extractRowId($filter ?? []);
                break;
            case 'delete':
                $affected = $this->performDelete($table, $filter ?? [], $mask, $scopeGuards);
                $rowId = self::extractRowId($filter ?? []);
                break;
        }

        // Audit trail: append a row to the audit_log table when the framework
        // detected one at boot. Audit_log itself is in the secret-table
        // baseline so it can never trigger this path recursively.
        if ($this->auditLogger !== null && $table !== 'audit_log') {
            $changedColumns = $action === 'delete' ? [] : array_keys($data ?? []);
            if ($diff && $affected > 0) {
                $this->logWithDiff($table, $action, $rowId, $authContext, $changedColumns, $data ?? [], $snapshot, $mask);
            } else {
                ($this->auditLogger)($table, $action, $rowId, $authContext, $changedColumns, null, null);
            }
        }

        // Run after hooks
        $this->runAfterHook($table, $action, $affected);

        $result = [
            'ref' => $ref,
            'action' => $action,
            'table' => $table,
            'affected' => $affected,
        ];
        if ($newId !== null) {
            $result['id'] = $newId;
        }

        // Bulk nested create: walk the relations tree. Child mutations recurse
        // back through executeSingleMutation, so audit log + audit columns +
        // role checks + further nested relations all work uniformly.
        if ($action === 'insert'
            && $newId !== null
            && is_array($mutation['relations'] ?? null)
            && $mutation['relations'] !== []
        ) {
            $result['relations'] = $this->walkRelations(
                $mutation['relations'],
                $mask,
                $newId,
                $authContext,
                $seenRefs,
                $table,
            );
        }

        return $result;
    }

    /**
     * Walk a `relations` tree under a parent insert. For each entry:
     *   1. Resolve the FK column from the parent mask's relations map.
     *   2. Inject the parent's new PK into each child's data.
     *   3. Recurse via executeSingleMutation (audit log, validators, soft-
     *      delete, deeper relations all behave uniformly).
     *   4. Strip 'relations' from each child's data block before insert
     *      and pass it to the recursive call as a sibling key — keeps
     *      arbitrary nesting depth working without ambiguous schema.
     *
     * @param array<string, mixed> $relations
     * @param array<string, mixed> $parentMask
     * @param array<string, mixed> $authContext
     * @param array<string, bool>  $seenRefs
     * @return array<string, list<array<string, mixed>>>
     */
    private function walkRelations(array $relations, array $parentMask, int|string $parentId, array $authContext, array &$seenRefs, string $parentTable): array
    {
        $declared = is_array($parentMask['relations'] ?? null) ? $parentMask['relations'] : [];
        $output = [];

        foreach ($relations as $key => $rows) {
            if (!is_string($key)) {
                continue;
            }
            // Allow `child_table` (auto FK) or `child_table.fk_column` (explicit
            // disambiguation when the child has multiple FKs to this parent).
            $explicitFk = null;
            $childTable = $key;
            if (str_contains($key, '.')) {
                [$childTable, $explicitFk] = explode('.', $key, 2);
            }

            $relMeta = $declared[$childTable] ?? null;
            if (!is_array($relMeta)) {
                throw new ApiException(
                    'MUTATION_VALIDATION_ERROR',
                    "Unknown relation '{$childTable}' from '{$parentTable}'.",
                    400,
                    null,
                    [
                        'expected' => $declared === [] ? "(no auto-detected FK relations from {$parentTable})" : 'one of: ' . implode(', ', array_keys($declared)),
                        'hint' => 'Auto-detected relations come from FK columns in other tables that point at this one.',
                    ]
                );
            }

            $fkColumns = is_array($relMeta['fk_columns'] ?? null) ? $relMeta['fk_columns'] : [];
            if ($fkColumns === []) {
                throw new ApiException('MUTATION_VALIDATION_ERROR', "Relation '{$childTable}' has no FK columns.", 500);
            }

            if ($explicitFk !== null) {
                if (!in_array($explicitFk, $fkColumns, true)) {
                    throw new ApiException(
                        'MUTATION_VALIDATION_ERROR',
                        "FK column '{$explicitFk}' is not a valid relation from '{$childTable}' to '{$parentTable}'.",
                        400,
                        null,
                        ['expected' => array_map(fn(string $c): string => "{$childTable}.{$c}", $fkColumns)]
                    );
                }
                $fkColumn = $explicitFk;
            } elseif (count($fkColumns) === 1) {
                $fkColumn = $fkColumns[0];
            } else {
                throw new ApiException(
                    'MUTATION_VALIDATION_ERROR',
                    "'{$childTable}' has multiple FKs to '{$parentTable}'; disambiguate the relation key.",
                    400,
                    null,
                    [
                        'expected' => array_map(fn(string $c): string => "{$childTable}.{$c}", $fkColumns),
                        'hint' => "Use 'relations: { \"{$childTable}.<fk_column>\": [...] }' to pick which FK the children attach through.",
                    ]
                );
            }

            if (!is_array($rows)) {
                throw new ApiException(
                    'MUTATION_VALIDATION_ERROR',
                    "Relation '{$key}' must be an array of child rows.",
                    400,
                    null,
                    [
                        'expected' => "list<object> for relation '{$childTable}'",
                        'example' => [$key => [['<col>' => '<value>']]],
                    ]
                );
            }

            $childResults = [];
            foreach ($rows as $childRow) {
                if (!is_array($childRow)) {
                    throw new ApiException(
                        'MUTATION_VALIDATION_ERROR',
                        "Each entry in relation '{$key}' must be an object.",
                        400,
                    );
                }

                // Pull out any further nesting before inserting the child.
                $deeperRelations = is_array($childRow['relations'] ?? null) ? $childRow['relations'] : null;
                unset($childRow['relations']);

                // Inject the parent's PK into the child's FK column. Dev's
                // explicit value wins (rare — usually nonsensical here, but
                // we don't second-guess).
                if (!array_key_exists($fkColumn, $childRow)) {
                    $childRow[$fkColumn] = $parentId;
                }

                $childMutation = [
                    'table' => $childTable,
                    'action' => 'insert',
                    'data' => $childRow,
                ];
                if ($deeperRelations !== null && $deeperRelations !== []) {
                    $childMutation['relations'] = $deeperRelations;
                }

                $childResults[] = $this->executeSingleMutation($childMutation, $authContext, $seenRefs);
            }
            $output[$childTable] = $childResults;
        }

        return $output;
    }

    /** Max rows whose values are captured for one multi-row update/delete. */
    private const AUDIT_DIFF_MAX_ROWS = 200;

    /**
     * Rows an update/delete is about to touch (same WHERE + scope guards the
     * mutation uses), as assoc arrays. One extra row signals "more than the cap".
     *
     * @param array<string, mixed> $filter
     * @param array<string, mixed> $mask
     * @param list<ScopeGuard> $scopeGuards
     * @return list<array<string, mixed>>
     */
    private function snapshotRows(string $table, array $filter, array $mask, array $scopeGuards, string $action): array
    {
        if ($filter === []) {
            return [];  // the mutation itself rejects an empty filter
        }
        [$whereSql, $params] = $this->buildWhereClause($filter);
        $whereSql .= $this->buildScopeGuardSql($scopeGuards, $params);
        $softCol = $mask['soft_delete'] ?? null;
        if ($action === 'delete' && is_string($softCol) && $softCol !== '') {
            $whereSql .= ' AND ' . $this->quoteName($softCol) . ' IS NULL';
        }
        $inner = 'SELECT * FROM ' . $this->quoteName($table) . ' WHERE ' . $whereSql;
        $sql = SqlDialect::paginate($this->database->getDriver(), $inner, self::AUDIT_DIFF_MAX_ROWS + 1, 0);
        $res = $this->database->run($sql, $params);
        $rows = [];
        foreach ($res['rows'] as $row) {
            $rows[] = array_combine($res['columns'], $row);
        }
        return $rows;
    }

    /**
     * Audit entries with values: insert → new values; update → old/new of the
     * columns that really changed, per affected row; delete → full old row.
     *
     * @param array<string, mixed> $authContext
     * @param list<string> $changedColumns
     * @param array<string, mixed> $data
     * @param list<array<string, mixed>>|null $snapshot
     * @param array<string, mixed> $mask
     */
    private function logWithDiff(string $table, string $action, ?string $rowId, array $authContext, array $changedColumns, array $data, ?array $snapshot, array $mask): void
    {
        $log = $this->auditLogger;
        if ($log === null) {
            return;
        }
        $pk = isset($mask['pk']) && is_string($mask['pk']) && $mask['pk'] !== '' ? $mask['pk'] : 'id';

        if ($action === 'insert') {
            $new = $data;
            if ($rowId !== null && !array_key_exists($pk, $new)) {
                $new[$pk] = $rowId;
            }
            $log($table, 'insert', $rowId, $authContext, $changedColumns, null, $this->redact($table, $new));
            return;
        }

        $rows = $snapshot ?? [];
        if ($rows === [] || count($rows) > self::AUDIT_DIFF_MAX_ROWS) {
            // Nothing to compare (or too many rows to snapshot): Tier-1 style entry, flagged.
            $note = count($rows) > self::AUDIT_DIFF_MAX_ROWS
                ? ['_note' => 'more than ' . self::AUDIT_DIFF_MAX_ROWS . ' rows matched; values not captured']
                : null;
            $log($table, $action, $rowId, $authContext, $changedColumns, null, $note);
            return;
        }

        foreach ($rows as $row) {
            $rid = isset($row[$pk]) && is_scalar($row[$pk]) ? (string)$row[$pk] : $rowId;
            if ($action === 'delete') {
                $log($table, 'delete', $rid, $authContext, [], $this->redact($table, $row), null);
                continue;
            }
            $old = [];
            $new = [];
            foreach ($data as $col => $value) {
                $col = (string)$col;
                $before = $row[$col] ?? null;
                if (self::sameValue($before, $value)) {
                    continue;
                }
                $old[$col] = $before;
                $new[$col] = $value;
            }
            $log($table, 'update', $rid, $authContext, $changedColumns, $this->redact($table, $old), $this->redact($table, $new));
        }
    }

    private static function sameValue(mixed $a, mixed $b): bool
    {
        if ($a === null || $b === null) {
            return $a === $b;
        }
        if (is_scalar($a) && is_scalar($b)) {
            return (string)$a === (string)$b;
        }
        return $a === $b;
    }

    /**
     * Replace values of sensitive columns (passwords, tokens, secrets, API keys,
     * `*_hash`, plus the configured exclude list) with a marker; the column NAME
     * stays visible so the audit still shows that it changed.
     *
     * @param array<string, mixed> $values
     * @return array<string, mixed>
     */
    private function redact(string $table, array $values): array
    {
        $exclude = $this->auditDiff['exclude'] ?? [];
        foreach ($values as $col => $_) {
            $c = strtolower((string)$col);
            if (
                preg_match('/(password|passwd|secret|token|api_?key|credential)|_hash$/', $c) === 1
                || in_array((string)$col, $exclude, true)
                || in_array($table . '.' . $col, $exclude, true)
            ) {
                $values[$col] = '[redacted]';
            }
        }
        return $values;
    }

    /**
     * Best-effort row-id extraction for audit logging on update / delete.
     * Picks the conventional `id` column if present in the filter; otherwise
     * the first scalar value (single-column PKs by other names like `uuid`,
     * `slug`, etc.). Returns null when the filter is empty or non-scalar
     * — the audit row's `row_id` is documented as best-effort, not strict.
     *
     * @param array<string, mixed> $filter
     */
    private static function extractRowId(array $filter): ?string
    {
        if (isset($filter['id']) && is_scalar($filter['id'])) {
            return (string)$filter['id'];
        }
        foreach ($filter as $value) {
            if (is_scalar($value)) {
                return (string)$value;
            }
        }
        return null;
    }

    /**
     * @param array<string, mixed> $data
     * @param array<string, mixed> $filter
     */
    private function runBeforeHook(string $table, string $action, array $data, array $filter): void
    {
        $hook = $this->validators->getBeforeHook($table, $action);
        if ($hook === null) {
            return;
        }

        try {
            $hook(['table' => $table, 'action' => $action, 'data' => $data, 'filter' => $filter]);
        } catch (Throwable $e) {
            throw new ApiException('HOOK_ERROR', 'Before hook failed: ' . $e->getMessage(), 400);
        }
    }

    private function runAfterHook(string $table, string $action, int $affected): void
    {
        $hook = $this->validators->getAfterHook($table, $action);
        if ($hook === null) {
            return;
        }

        try {
            $hook(['table' => $table, 'action' => $action, 'affected' => $affected]);
        } catch (Throwable $e) {
            throw new ApiException('HOOK_ERROR', 'After hook failed: ' . $e->getMessage(), 400);
        }
    }

    /**
     * @param array<string, mixed> $data
     */
    private function validateFields(string $table, string $action, array $data): void
    {
        foreach ($data as $field => $value) {
            $error = $this->validators->validate($table, $action, (string)$field, $value);
            if ($error !== null) {
                throw new ApiException('VALIDATION_ERROR', $error, 400);
            }
        }
    }

    /**
     * Field-level write authorization (SECURITY-REVIEW H1). Rejects any
     * client-supplied column not on the mask's `writable_columns` allowlist.
     * Absent allowlist = legacy unrestricted mask (all columns writable). Runs
     * on raw client data before server-side augmentation.
     *
     * @param array<string, mixed> $data
     * @param array<string, mixed> $mask
     */
    private static function enforceWritableColumns(array $data, array $mask, string $table, ?string $ref): void
    {
        if (!isset($mask['writable_columns']) || !is_array($mask['writable_columns'])) {
            return;
        }
        $allow = $mask['writable_columns'];
        $allowed = array_fill_keys(array_map('strval', $allow), true);
        foreach (array_keys($data) as $col) {
            $name = trim((string)$col);
            if (isset($allowed[$name])) {
                continue;
            }
            throw new ApiException(
                'WRITE_COLUMN_FORBIDDEN',
                "Column '{$name}' is not writable on '{$table}'.",
                403,
                $ref,
                [
                    'expected' => $allow !== []
                        ? 'one of: ' . implode(', ', array_map('strval', $allow))
                        : '(no columns are client-writable on this entity)',
                    'hint' => 'Audit, primary-key, tenant-managed, and sensitive columns '
                        . '(password_hash, role, is_active, …) are not settable through the generic '
                        . 'mutation op. Use a dedicated command or admin endpoint for those.',
                ]
            );
        }
    }

    /**
     * @param array<string, mixed> $data
     * @param array<string, mixed> $mask
     * @return array{affected:int,new_id:?int} new_id is the PK value of the
     *   inserted row when one is known (DB-assigned, framework-allocated, or
     *   client-supplied on a manual-PK table); null otherwise.
     */
    private function performInsert(string $table, array $data, array $mask): array
    {
        if ($data === []) {
            throw new ApiException('MUTATION_VALIDATION_ERROR', 'Insert requires at least one column.', 400);
        }

        $pk = isset($mask['pk']) && is_string($mask['pk']) && $mask['pk'] !== '' ? $mask['pk'] : 'id';
        $manualPk = ($mask['pk_strategy'] ?? null) === 'manual';

        // Manual-PK tables (legacy int PK without identity/default): when the
        // client didn't supply the PK, the framework allocates MAX+1
        // atomically. A client-supplied PK always wins and falls through to
        // the normal path — supports data migration/import flows.
        if ($manualPk && !array_key_exists($pk, $data)) {
            $newId = $this->database->insertWithManualPk($table, $pk, $data);
            return ['affected' => 1, 'new_id' => $newId > 0 ? $newId : null];
        }

        $columns = [];
        $placeholders = [];
        $params = [];

        foreach ($data as $col => $value) {
            $colName = trim((string)$col);
            if ($colName === '') {
                throw new ApiException('MUTATION_VALIDATION_ERROR', 'Column name cannot be empty.', 400);
            }
            $columns[] = $colName;
            $placeholders[] = ':' . $colName;
            $params[$colName] = $value;
        }

        $sql = 'INSERT INTO ' . $this->quoteName($table) . ' (' .
               implode(', ', array_map($this->quoteName(...), $columns)) . ') VALUES (' .
               implode(', ', $placeholders) . ')';

        // Use insertReturningId for portable lastInsertId — gives us the new
        // row's PK on every supported driver (RETURNING on PG, lastInsertId
        // on MySQL/SQLite). The audit logger needs this to record row_id.
        // The mask's `pk` keeps tables whose PK isn't named `id` working on
        // the RETURNING/OUTPUT drivers.
        $newId = $this->database->insertReturningId($sql, $params, $pk);

        // Explicit id on a manual-PK table: lastInsertId can't see
        // non-identity PKs (MySQL reports 0, SQLite the internal rowid) —
        // the client-supplied value is authoritative.
        if ($manualPk && array_key_exists($pk, $data) && is_numeric($data[$pk])) {
            $newId = (int)$data[$pk];
        }

        $affected = $newId > 0 ? 1 : 0;
        return ['affected' => $affected, 'new_id' => $newId > 0 ? $newId : null];
    }

    /**
     * @param array<string, mixed> $filter
     * @param array<string, mixed> $data
     * @param array<string, mixed> $mask
     * @param list<ScopeGuard> $scopeGuards Appends `AND <col> = :<param>`
     *   (or `IN`) per scope so a scoped caller can only touch own-scope rows.
     */
    private function performUpdate(string $table, array $filter, array $data, array $mask, array $scopeGuards = []): int
    {
        if ($data === []) {
            throw new ApiException('MUTATION_VALIDATION_ERROR', 'Update requires at least one column to modify.', 400);
        }

        $setClauses = [];
        $params = [];
        $paramIndex = 0;

        foreach ($data as $col => $value) {
            $colName = trim((string)$col);
            if ($colName === '') {
                throw new ApiException('MUTATION_VALIDATION_ERROR', 'Column name cannot be empty.', 400);
            }
            $paramName = 'set_' . $paramIndex;
            $setClauses[] = $this->quoteName($colName) . ' = :' . $paramName;
            $params[$paramName] = $value;
            $paramIndex++;
        }

        [$whereSql, $whereParams] = $this->buildWhereClause($filter);
        $params = array_merge($params, $whereParams);
        $whereSql .= $this->buildScopeGuardSql($scopeGuards, $params);

        $sql = 'UPDATE ' . $this->quoteName($table) . ' SET ' .
               implode(', ', $setClauses) . ' WHERE ' . $whereSql;

        return $this->database->exec($sql, $params);
    }

    /**
     * @param array<string, mixed> $filter
     * @param array<string, mixed> $mask
     * @param list<ScopeGuard> $scopeGuards
     */
    private function performDelete(string $table, array $filter, array $mask, array $scopeGuards = []): int
    {
        if ($filter === []) {
            throw new ApiException('MUTATION_VALIDATION_ERROR', 'Delete requires a filter (no unfiltered deletes allowed).', 400);
        }

        [$whereSql, $params] = $this->buildWhereClause($filter);

        // Scope guards on the WHERE (scoped callers) — same mechanism as the
        // soft-delete `IS NULL` guard below.
        $guardSql = $this->buildScopeGuardSql($scopeGuards, $params);

        // Soft-delete: tables marked with a soft_delete column on the writable
        // mask get DELETE translated to UPDATE … SET <col> = CURRENT_TIMESTAMP.
        // The extra `<col> IS NULL` guard prevents re-stamping already-deleted
        // rows, so a second delete returns affected = 0 (correct semantics).
        $softCol = $mask['soft_delete'] ?? null;
        if (is_string($softCol) && $softCol !== '') {
            $qCol = $this->quoteName($softCol);
            $sql = 'UPDATE ' . $this->quoteName($table)
                . ' SET ' . $qCol . ' = CURRENT_TIMESTAMP'
                . ' WHERE ' . $whereSql . $guardSql . ' AND ' . $qCol . ' IS NULL';
            return $this->database->exec($sql, $params);
        }

        $sql = 'DELETE FROM ' . $this->quoteName($table) . ' WHERE ' . $whereSql . $guardSql;
        return $this->database->exec($sql, $params);
    }

    /**
     * @param array<string, mixed> $filter
     * @return array{0:string, 1:array<string, mixed>}
     */
    private function buildWhereClause(array $filter): array
    {
        if ($filter === []) {
            throw new ApiException('MUTATION_VALIDATION_ERROR', 'Filter cannot be empty.', 400);
        }

        $clauses = [];
        $params = [];
        $paramIndex = 0;

        foreach ($filter as $col => $value) {
            $colName = trim((string)$col);
            if ($colName === '') {
                throw new ApiException('MUTATION_VALIDATION_ERROR', 'Filter column name cannot be empty.', 400);
            }

            $paramName = 'where_' . $paramIndex;
            if ($value === null) {
                $clauses[] = $this->quoteName($colName) . ' IS NULL';
            } else {
                $clauses[] = $this->quoteName($colName) . ' = :' . $paramName;
                $params[$paramName] = $value;
                $paramIndex++;
            }
        }

        return [implode(' AND ', $clauses), $params];
    }

    private function quoteName(string $name): string
    {
        return $this->database->quoteIdentifier($name);
    }

    /**
     * Inject convention-driven audit values into the mutation payload.
     *
     * The mask carries an `audit` map (column → kind) detected from the
     * schema by EntityMeta::detectAuditColumns(). For each column:
     *
     *   - If the dev's data already includes the column, do nothing —
     *     explicit override wins (essential for migrations / data imports).
     *   - On INSERT, stamp every audit column.
     *   - On UPDATE, stamp only `updated_*` (never overwrite created_*).
     *   - 'timestamp' kind → current UTC time as 'Y-m-d H:i:s'.
     *   - 'user_id' kind → the authenticated caller's id (id / oid / sub).
     *     If no authenticated user is in scope, the column is left alone.
     *
     * @param array<string, mixed> $data
     * @param array<string, mixed> $mask
     * @param array<string, mixed> $authContext
     * @return array<string, mixed>
     */
    private static function applyAuditColumns(array $data, string $action, array $mask, array $authContext): array
    {
        $audit = $mask['audit'] ?? null;
        if (!is_array($audit) || $audit === []) {
            return $data;
        }

        $now = gmdate('Y-m-d H:i:s');
        $userId = self::authUserId($authContext);

        foreach ($audit as $col => $kind) {
            if (!is_string($col) || !is_string($kind)) {
                continue;
            }

            // UPDATE never re-stamps created_* columns.
            if ($action === 'update' && str_starts_with($col, 'created_')) {
                continue;
            }

            // Dev override wins.
            if (array_key_exists($col, $data)) {
                continue;
            }

            if ($kind === 'timestamp') {
                $data[$col] = $now;
            } elseif ($kind === 'user_id' && $userId !== null) {
                $data[$col] = $userId;
            }
        }

        return $data;
    }

    /**
     * Extract a stable user identifier from the auth context.
     * Tries id (OIDC verifier sets this), oid (Entra), then sub
     * (Shape's local HS256). Returns null when no authenticated user
     * is in scope (NullGuard or anonymous request).
     *
     * @param array<string, mixed> $authContext
     */
    private static function authUserId(array $authContext): int|string|null
    {
        foreach (['id', 'oid', 'sub'] as $key) {
            $val = $authContext[$key] ?? null;
            if ($val === null) {
                continue;
            }
            if (is_int($val)) {
                if ($val > 0) {
                    return $val;
                }
                continue;
            }
            if (is_string($val) && $val !== '' && $val !== '0') {
                return ctype_digit($val) ? (int)$val : $val;
            }
        }
        return null;
    }

    // ── Row scoping: tenancy + dimensions (MULTI-TENANCY-PLAN §2.4/§2.5,
    //    ROW-SCOPES-PHP-PLAN §2.3/§2.4/§2.6) ─────────────────────────────

    /**
     * The ScopeSpecs governing one mask (§2.6): the tenant spec from
     * `tenant_scope` plus one dimension spec per `row_scopes` entry.
     * Detection/compilation happened at build time; here each spec carries
     * what the runtime needs — claim key, bind param, cardinality, and the
     * roles exempt from the predicate.
     *
     * @param array<string, mixed> $mask
     * @return list<ScopeSpec>
     */
    private function maskScopeSpecs(array $mask): array
    {
        $specs = [];
        $tenantCol = $mask['tenant_scope'] ?? null;
        if (is_string($tenantCol) && $tenantCol !== '') {
            $specs[] = ['kind' => 'tenant', 'column' => $tenantCol, 'claim' => 'tenant_id', 'param' => '__tenant', 'multi' => false, 'unscoped_roles' => $this->unscopedRoles];
        }
        $rowScopes = is_array($mask['row_scopes'] ?? null) ? $mask['row_scopes'] : [];
        foreach ($rowScopes as $col => $meta) {
            if (!is_string($col) || $col === '') {
                continue;
            }
            $cfg = is_array($this->scopes[$col] ?? null) ? $this->scopes[$col] : [];
            $claim = isset($cfg['claim']) && is_string($cfg['claim']) && $cfg['claim'] !== '' ? $cfg['claim'] : $col;
            $multi = is_array($meta) && array_key_exists('multi', $meta)
                ? (bool)$meta['multi']
                : (bool)($cfg['multi'] ?? false);
            $roles = is_array($cfg['unscoped_roles'] ?? null)
                ? array_values(array_filter($cfg['unscoped_roles'], static fn($r): bool => is_string($r) && $r !== ''))
                : [];
            $specs[] = ['kind' => 'dimension', 'column' => $col, 'claim' => $claim, 'param' => '__scope_' . $col, 'multi' => $multi, 'unscoped_roles' => $roles];
        }
        return $specs;
    }

    /**
     * Read path (§2.3): variant selection + per-scope binding.
     *
     * Fast path — caller subject to every scope: run the compiled `sql`
     * (all predicates baked in) and bind each scope param. Caller unscoped
     * for every scope: run `sql_unscoped` plain. Exception path — unscoped
     * for ≥1 scope but not all: start from `sql_unscoped` and wrap it in a
     * subselect appending only the predicates the caller IS subject to
     * (the COUNT-wrap pattern; optimizers flatten it).
     *
     * @param array<string, mixed> $mask
     * @param array<string, mixed> $params
     * @param array<string, mixed> $authContext
     * @return array{0:array<string, mixed>, 1:array<string, mixed>}
     */
    private function applyRowScopes(array $mask, array $params, array $authContext, ?string $ref): array
    {
        $specs = $this->maskScopeSpecs($mask);
        if ($specs === []) {
            return [$mask, $params];
        }

        $subject = [];
        $exempt = false;
        foreach ($specs as $spec) {
            if ($this->isUnscopedFor($spec, $authContext)) {
                $exempt = true;
            } else {
                $subject[] = $spec;
            }
        }

        if (!$exempt) {
            foreach ($subject as $spec) {
                $params[$spec['param']] = $this->callerScopeValue($spec, $authContext, $ref);
            }
            return [$mask, $params];
        }

        if (isset($mask['sql_unscoped']) && is_string($mask['sql_unscoped']) && $mask['sql_unscoped'] !== '') {
            $mask['sql'] = $mask['sql_unscoped'];
        }
        if ($subject !== []) {
            $conds = [];
            foreach ($subject as $spec) {
                $q = $this->quoteName($spec['column']);
                $conds[] = $spec['multi'] ? "{$q} IN (:{$spec['param']})" : "{$q} = :{$spec['param']}";
                $params[$spec['param']] = $this->callerScopeValue($spec, $authContext, $ref);
            }
            $mask['sql'] = 'SELECT * FROM (' . $mask['sql'] . ') AS __rs WHERE ' . implode(' AND ', $conds);
        }
        return [$mask, $params];
    }

    /**
     * The caller's claim value for one scope. Tenant kind keeps the exact
     * tenancy contract; dimension kind reads the configured claim — missing
     * claim on a scoped operation is rejected (pre-feature tokens, §2.2),
     * never falling through to unscoped. Multi claims normalize to a list
     * (Database::run expands array params); an empty membership set binds
     * [null] so the IN matches nothing instead of erroring.
     *
     * @param ScopeSpec $spec
     * @param array<string, mixed> $authContext
     * @return int|string|list<int|string|null>
     */
    private function callerScopeValue(array $spec, array $authContext, ?string $ref): int|string|array
    {
        if ($spec['kind'] === 'tenant') {
            return $this->callerTenantId($authContext, $ref);
        }
        $value = $authContext[$spec['claim']] ?? null;
        if ($value === null || $value === '') {
            throw new ApiException(
                'AUTH_INVALID',
                "Missing {$spec['claim']} claim. Re-login required after row scopes were enabled.",
                401,
                $ref,
                ['hint' => 'Re-authenticate — access tokens issued before row scopes were enabled lack the scope claim.']
            );
        }
        if ($spec['multi']) {
            $list = is_array($value) ? array_values($value) : [$value];
            $list = array_map(static fn($v): int|string|null => is_numeric($v) ? (int)$v : (is_scalar($v) ? (string)$v : null), $list);
            return $list === [] ? [null] : $list;
        }
        return is_numeric($value) ? (int)$value : (string)$value;
    }

    /**
     * The caller's tenant from the auth context. A scoped operation with no
     * tenant in scope is rejected rather than falling through to unscoped —
     * pre-tenancy tokens lack the claim (§2.3).
     *
     * @param array<string, mixed> $authContext
     */
    private function callerTenantId(array $authContext, ?string $ref): int|string
    {
        $tid = $authContext['tenant_id'] ?? null;
        if ($tid === null || $tid === '') {
            // 401 + AUTH_INVALID (not 403) so clients treat it as a stale-token
            // signal and re-authenticate — a pre-tenancy access token simply
            // lacks the claim. Matches the .NET runtime's error contract.
            throw new ApiException(
                'AUTH_INVALID',
                'Missing tenant_id claim. Re-login required after tenancy was enabled.',
                401,
                $ref,
                ['hint' => 'Re-authenticate — access tokens issued before tenancy was enabled lack the tenant claim.']
            );
        }
        if (is_int($tid)) {
            return $tid;
        }
        return is_numeric($tid) ? (int)$tid : (string)$tid;
    }

    /**
     * Write guards for UPDATE / DELETE — one per scope the caller is subject
     * to. Empty when the mask is unscoped or the caller is exempt everywhere.
     *
     * @param array<string, mixed> $mask
     * @param array<string, mixed> $authContext
     * @return list<ScopeGuard>
     */
    private function resolveScopeGuards(array $mask, array $authContext): array
    {
        $guards = [];
        foreach ($this->maskScopeSpecs($mask) as $spec) {
            if ($this->isUnscopedFor($spec, $authContext)) {
                continue;
            }
            $guards[] = [
                'kind' => $spec['kind'],
                'column' => $spec['column'],
                'param' => $spec['param'],
                'multi' => $spec['multi'],
                'value' => $this->callerScopeValue($spec, $authContext, null),
            ];
        }
        return $guards;
    }

    /**
     * Render the guards as WHERE fragments and bind their params (shared by
     * performUpdate / performDelete — Database::exec expands array params).
     *
     * @param list<ScopeGuard> $scopeGuards
     * @param array<string, mixed> $params (by ref — guard bindings added)
     */
    private function buildScopeGuardSql(array $scopeGuards, array &$params): string
    {
        $sql = '';
        foreach ($scopeGuards as $g) {
            $q = $this->quoteName($g['column']);
            $sql .= ' AND ' . ($g['multi'] ? "{$q} IN (:{$g['param']})" : "{$q} = :{$g['param']}");
            $params[$g['param']] = $g['value'];
        }
        return $sql;
    }

    /**
     * INSERT stamping (§2.4). Per spec: a caller scoped to a single value
     * gets the discriminator stamped from the claim (differing client value
     * → TENANT_MISMATCH / SCOPE_MISMATCH); a multi-valued caller must supply
     * the column and the value must lie within the membership set; an
     * exempt caller must name the value explicitly — never guess.
     *
     * @param array<string, mixed> $data
     * @param array<string, mixed> $mask
     * @param array<string, mixed> $authContext
     * @return array<string, mixed>
     */
    private function stampScopesOnInsert(array $data, array $mask, array $authContext): array
    {
        foreach ($this->maskScopeSpecs($mask) as $spec) {
            $col = $spec['column'];
            $supplied = array_key_exists($col, $data) && $data[$col] !== null && $data[$col] !== '';

            if ($this->isUnscopedFor($spec, $authContext)) {
                if (!$supplied) {
                    throw $spec['kind'] === 'tenant'
                        ? new ApiException(
                            'VALIDATION_ERROR',
                            "Column '{$col}' is required for unscoped writes.",
                            400,
                            null,
                            [
                                'detail' => 'TENANT_REQUIRED',
                                'hint' => 'An unscoped-role caller must name the target tenant explicitly when inserting.',
                            ]
                        )
                        : self::scopeRequired($col, false);
                }
                continue;
            }

            if ($spec['kind'] === 'tenant') {
                $tid = $this->callerTenantId($authContext, null);
                if ($supplied && (string)$data[$col] !== (string)$tid) {
                    throw self::tenantMismatch($col);
                }
                $data[$col] = $tid;
                continue;
            }

            $value = $this->callerScopeValue($spec, $authContext, null);
            if ($spec['multi']) {
                if (!$supplied) {
                    throw self::scopeRequired($col, true);
                }
                if (!self::inScopeSet($data[$col], $value)) {
                    throw self::scopeMismatch($col);
                }
                continue; // client value kept — it names one of N memberships
            }
            if ($supplied && (string)$data[$col] !== (string)$value) {
                throw self::scopeMismatch($col);
            }
            $data[$col] = $value;
        }
        return $data;
    }

    /**
     * A scoped caller must not move a row across a tenant or a dimension
     * via UPDATE (§2.4).
     *
     * @param array<string, mixed> $data
     * @param list<ScopeGuard> $scopeGuards
     */
    private static function assertNoScopeMove(array $data, array $scopeGuards): void
    {
        foreach ($scopeGuards as $g) {
            $col = $g['column'];
            if (!array_key_exists($col, $data) || $data[$col] === null) {
                continue;
            }
            $ok = $g['multi']
                ? self::inScopeSet($data[$col], is_array($g['value']) ? $g['value'] : [$g['value']])
                : (string)$data[$col] === (string)$g['value'];
            if (!$ok) {
                throw $g['kind'] === 'tenant' ? self::tenantMismatch($col) : self::scopeMismatch($col);
            }
        }
    }

    /**
     * Membership test for multi-valued scopes (string-compared, null-safe).
     *
     * @param list<int|string|null> $set
     */
    private static function inScopeSet(mixed $value, array $set): bool
    {
        if (!is_scalar($value)) {
            return false;
        }
        foreach ($set as $member) {
            if ($member !== null && (string)$value === (string)$member) {
                return true;
            }
        }
        return false;
    }

    private static function scopeMismatch(string $col): ApiException
    {
        return new ApiException(
            'VALIDATION_ERROR',
            "Cross-scope write rejected: '{$col}' is outside your {$col} scope.",
            400,
            null,
            [
                'detail' => 'SCOPE_MISMATCH',
                'scope' => $col,
                'hint' => "The '{$col}' value must match your claim (one of your memberships for multi-valued scopes).",
            ]
        );
    }

    private static function scopeRequired(string $col, bool $multi): ApiException
    {
        return new ApiException(
            'VALIDATION_ERROR',
            "Column '{$col}' is required.",
            400,
            null,
            [
                'detail' => 'SCOPE_REQUIRED',
                'scope' => $col,
                'hint' => $multi
                    ? "Supply '{$col}' explicitly — you belong to multiple {$col} values and the framework never guesses among memberships."
                    : "A caller unscoped for '{$col}' must name the target value explicitly when inserting.",
            ]
        );
    }

    /**
     * Opt-in `tenancy.validate_fks` (§2.5): for each FK column in $data whose
     * target table is tenant-scoped, one indexed SELECT verifies the
     * referenced row belongs to the effective tenant — otherwise an insert
     * could point an `order` at another tenant's `customer_id`. The
     * `fk_checks` map (column => {table, pk, tenant_column}) is compiled onto
     * the writable mask by the generator; only scoped targets are carried.
     * Mirrors the tenant leg of .NET BatchExecutor.CheckForeignKeys.
     *
     * @param array<string, mixed> $data
     * @param array<string, mixed> $mask
     * @param array<string, mixed> $authContext
     */
    private function checkTenantForeignKeys(array $data, array $mask, array $authContext, ?string $ref): void
    {
        if (($this->tenancy['validate_fks'] ?? false) !== true) {
            return;
        }
        $fkChecks = is_array($mask['fk_checks'] ?? null) ? $mask['fk_checks'] : [];
        if ($fkChecks === []) {
            return;
        }

        // The row's stamped tenant is authoritative (stampTenantOnInsert has
        // already run — the column is configurable, so read it by name from
        // the mask); a scoped update without the column falls back to the
        // caller's claim. Unscoped callers without an explicit tenant skip
        // the check — there is no tenant to anchor it to.
        $tenantCol = isset($mask['tenant_scope']) && is_string($mask['tenant_scope']) ? $mask['tenant_scope'] : null;
        $currentTenant = $tenantCol !== null && isset($data[$tenantCol]) && $data[$tenantCol] !== ''
            ? $data[$tenantCol]
            : ($authContext['tenant_id'] ?? null);
        if ($currentTenant === null || $currentTenant === '') {
            return;
        }

        $driver = $this->database->getDriver();
        foreach ($fkChecks as $column => $fk) {
            if (!is_array($fk) || !is_string($column)) {
                continue;
            }
            if (!array_key_exists($column, $data) || $data[$column] === null) {
                continue;
            }
            $targetTable = isset($fk['table']) && is_string($fk['table']) ? $fk['table'] : null;
            $targetPk = isset($fk['pk']) && is_string($fk['pk']) && $fk['pk'] !== '' ? $fk['pk'] : 'id';
            $targetTenantCol = isset($fk['tenant_column']) && is_string($fk['tenant_column']) ? $fk['tenant_column'] : null;
            if ($targetTable === null || $targetTenantCol === null) {
                continue;
            }

            $qt = $this->quoteName($targetTable);
            $where = "WHERE {$this->quoteName($targetPk)} = :v AND {$this->quoteName($targetTenantCol)} = :__tenant";
            $sql = $driver === 'sqlsrv'
                ? "SELECT TOP 1 1 FROM {$qt} {$where}"
                : "SELECT 1 FROM {$qt} {$where} LIMIT 1";
            $result = $this->database->run($sql, ['v' => $data[$column], '__tenant' => $currentTenant]);
            if ($result['count'] === 0) {
                throw new ApiException(
                    'VALIDATION_ERROR',
                    "Foreign key check failed: {$column} = " . (is_scalar($data[$column]) ? (string)$data[$column] : '?') . " belongs to a different tenant.",
                    400,
                    $ref,
                    [
                        'detail' => 'TENANT_FK_MISMATCH',
                        'expected' => "{$column} must reference a row in {$targetTable} owned by the caller's tenant",
                        'hint' => "Insert a row in {$targetTable} for the caller's tenant, then use the returned id here.",
                    ]
                );
            }
        }
    }

    private static function tenantMismatch(string $col): ApiException
    {
        return new ApiException(
            'VALIDATION_ERROR',
            "Cross-tenant write rejected: '{$col}' does not match your tenant.",
            400,
            null,
            [
                'detail' => 'TENANT_MISMATCH',
                'hint' => 'The tenant discriminator is server-assigned; omit it or send your own tenant value.',
            ]
        );
    }

    /**
     * @param list<array<string, mixed>> $queries
     * @param array<string, mixed> $authContext
     * @param array<string, string> $odataFilters Trusted, router-supplied `$filter` expressions keyed by query ref.
     *   Compiled to SQL and applied before pagination and COUNT (never read from client `options`).
     * @return array{results:list<array<string, mixed>>}
     */
    public function execute(array $queries, array $authContext = [], array $odataFilters = []): array
    {
        if ($queries === []) {
            throw new ApiException('QUERY_VALIDATION_ERROR', 'Request must include at least one query.', 400);
        }
        // SECURITY-REVIEW M5: cap batch size against an unbounded op-count DoS.
        if (count($queries) > self::MAX_BATCH_ITEMS) {
            throw new ApiException(
                'QUERY_VALIDATION_ERROR',
                'Batch exceeds the maximum of ' . self::MAX_BATCH_ITEMS . ' queries.',
                400,
                null,
                ['expected' => 'at most ' . self::MAX_BATCH_ITEMS . ' queries per request', 'hint' => 'Split the work across multiple requests.']
            );
        }

        $results = [];
        $refMap = [];
        $seenRefs = [];

        foreach ($queries as $query) {
            $ref = $this->requiredString($query, 'ref');
            if (isset($seenRefs[$ref])) {
                throw new ApiException('DUPLICATE_REF', "Duplicate ref '{$ref}'.", 400, $ref);
            }
            $seenRefs[$ref] = true;

            $sqlOrMask = $this->requiredString($query, 'sql');
            $mask = $this->registry->resolve($sqlOrMask, $authContext, $ref);
            $rawParams = $query['params'] ?? [];
            if (!is_array($rawParams) || ($rawParams !== [] && array_is_list($rawParams))) {
                throw new ApiException('QUERY_VALIDATION_ERROR', 'Query params must be an object.', 400, $ref);
            }

            $resolvedParams = $this->resolver->resolve(
                $rawParams,
                $mask['params'],
                $refMap,
                $ref
            );

            // Row scoping (MULTI-TENANCY-PLAN §2.4, ROW-SCOPES §2.3): swap in
            // the right compiled variant (wrapping when partially unscoped)
            // and bind :__tenant / :__scope_* from the auth context. Done
            // before pagination/COUNT so both wrap the chosen variant — a
            // wrong $count is also a leak.
            [$mask, $resolvedParams] = $this->applyRowScopes($mask, $resolvedParams, $authContext, $ref);

            // OData $filter → SQL condition over the (already scoped) mask result. Applied before
            // pagination and COUNT so both see only matching rows.
            $filterCols = [];
            $baseSql = (string)$mask['sql'];
            $baseParams = $resolvedParams;
            $filterExpr = $odataFilters[$ref] ?? null;
            if (is_string($filterExpr) && trim($filterExpr) !== '') {
                $compiled = ODataFilter::compile($filterExpr, fn(string $c): string => $this->database->quoteIdentifier($c), $ref);
                $mask['sql'] = "SELECT * FROM ({$baseSql}) AS __filter WHERE {$compiled['sql']}";
                $resolvedParams = array_merge($resolvedParams, $compiled['params']);
                $filterCols = $compiled['columns'];
            }

            $options = $this->readOptions($query, $ref);

            // Push LIMIT/OFFSET into the SQL when it's safe to do so.
            // The fetch-then-array_slice path silently OOMs on large tables;
            $page = $this->planPagination($mask, $options, $ref);

            $totalCount = null;
            try {
                if ($page['pushed'] && ($options['count'] ?? false) === true) {
                    // Separate COUNT query when paging is pushed down — without
                    // it we'd have to fetch everything just to count.
                    $countSql = "SELECT COUNT(*) AS __cnt FROM ({$mask['sql']}) AS __c";
                    $countResult = $this->database->run($countSql, $resolvedParams);
                    $totalCount = isset($countResult['rows'][0][0]) ? (int)$countResult['rows'][0][0] : 0;
                }

                $dbResult = $this->database->run($page['sql'], $resolvedParams);
            } catch (ApiException $e) {
                if ($filterCols !== [] && $e->apiCode() === 'DB_ERROR') {
                    $this->explainFilterFailure($baseSql, $baseParams, $filterCols, $ref);
                }
                throw $e;
            }
            $processed = $this->applyOptions($dbResult, $options, $ref, $page['pushed']);
            if ($totalCount !== null) {
                $processed['total_count'] = $totalCount;
            }
            $result = [
                'ref' => $ref,
                'meta' => [
                    'columns' => $processed['columns'],
                    'ms' => $dbResult['ms'],
                    'rows' => $processed['count'],
                ],
                'rows' => $processed['rows'],
                'count' => $processed['count'],
            ];

            if (isset($processed['total_count'])) {
                $result['total_count'] = $processed['total_count'];
                $result['meta']['total_rows'] = $processed['total_count'];
            }

            $results[] = $result;
            $refMap[$ref] = [
                'meta' => $result['meta'],
                'rows' => $result['rows'],
            ];
        }

        return ['results' => $results];
    }

    /**
     * A query with an OData $filter failed in the database. The usual cause is a column that the mask does not return;
     * report that as a client error listing the available columns. Otherwise the caller rethrows the original error.
     *
     * @param array<string, mixed> $baseParams
     * @param list<string> $filterCols
     */
    private function explainFilterFailure(string $baseSql, array $baseParams, array $filterCols, string $ref): void
    {
        try {
            $probe = $this->database->run("SELECT * FROM ({$baseSql}) AS __probe WHERE 1 = 0", $baseParams);
        } catch (ApiException) {
            return;
        }
        $available = $probe['columns'];
        if ($available === []) {
            return;
        }
        $lower = array_map('strtolower', $available);
        $unknown = array_values(array_filter($filterCols, static fn(string $c): bool => !in_array(strtolower($c), $lower, true)));
        if ($unknown !== []) {
            throw new ApiException(
                'QUERY_VALIDATION_ERROR',
                'Unknown column in $filter: ' . implode(', ', $unknown) . '.',
                400,
                $ref,
                ['expected' => 'one of the returned columns', 'available' => $available, 'hint' => 'Filter only on columns this mask returns: ' . implode(', ', $available) . '.']
            );
        }
    }

    /** @param array<string, mixed> $query */
    private function requiredString(array $query, string $key): string
    {
        $value = $query[$key] ?? null;
        if (!is_string($value) || trim($value) === '') {
            throw new ApiException('QUERY_VALIDATION_ERROR', "Query field '{$key}' must be a non-empty string.", 400);
        }
        return $value;
    }

    /** @param array<string, mixed> $query */
    private function readOptions(array $query, string $ref): array
    {
        $options = $query['options'] ?? [];
        if ($options === null) {
            return [];
        }
        if (!is_array($options) || ($options !== [] && array_is_list($options))) {
            throw new ApiException('QUERY_VALIDATION_ERROR', 'Query options must be an object.', 400, $ref);
        }

        $allowed = ['orderby', 'skip', 'top', 'count', 'groupby', 'aggregate'];
        foreach ($options as $key => $_) {
            if (!in_array((string)$key, $allowed, true)) {
                throw new ApiException('QUERY_VALIDATION_ERROR', "Unsupported query option '{$key}'.", 400, $ref);
            }
        }

        return $options;
    }

    /**
     * Decide whether top/skip can be pushed into the SQL. Pushing down means
     * the DB returns at most `top` rows — bounded memory regardless of table
     * size. We don't push when the request also wants groupby/aggregate
     * (those operate on the full result set; pre-truncating changes the
     * answer) or orderby (pushing it requires column-validating the orderby
     * fields against the inner SQL's output, which can be a hand-written
     * mask returning unknown columns — a separate enhancement).
     *
     * @param array<string, mixed> $mask
     * @param array<string, mixed> $options
     * @return array{pushed:bool, sql:string}
     */
    private function planPagination(array $mask, array $options, ?string $ref): array
    {
        $maskSql = (string)$mask['sql'];

        $hasOrderby   = !empty($options['orderby']);
        $hasGroupby   = !empty($options['groupby']);
        $hasAggregate = !empty($options['aggregate']);
        if ($hasOrderby || $hasGroupby || $hasAggregate) {
            return ['pushed' => false, 'sql' => $maskSql];
        }

        $skip = $this->readNonNegativeIntOption($options, 'skip', $ref, 0);

        $explicitTop = array_key_exists('top', $options)
            ? $this->readNonNegativeIntOption($options, 'top', $ref, self::MAX_TOP)
            : null;
        $defaultLimit = isset($mask['default_limit']) && is_numeric($mask['default_limit'])
            ? (int)$mask['default_limit']
            : 0;
        $effectiveTop = $explicitTop ?? $defaultLimit;

        if ($effectiveTop > self::MAX_TOP) {
            throw new ApiException(
                'QUERY_VALIDATION_ERROR',
                'Option top exceeds allowed maximum (' . self::MAX_TOP . ').',
                400,
                $ref
            );
        }

        if ($effectiveTop <= 0 && $skip <= 0) {
            return ['pushed' => false, 'sql' => $maskSql];
        }

        // Driver-correct wrapper. SQL Server uses OFFSET/FETCH (+ mandatory
        // ORDER BY shim) instead of LIMIT/OFFSET; SqlDialect owns that split.
        // Values are validated non-negative integers — safe to interpolate.
        $wrapped = SqlDialect::paginate($this->database->getDriver(), $maskSql, $effectiveTop, $skip);
        return ['pushed' => true, 'sql' => $wrapped];
    }

    /**
     * @param array{columns:list<string>, rows:list<list<mixed>>, count:int, ms:float} $dbResult
     * @param array<string, mixed> $options
     * @return array{columns:list<string>, rows:list<list<mixed>>, count:int, total_count?:int}
     */
    private function applyOptions(array $dbResult, array $options, string $ref, bool $paginatedInSql = false): array
    {
        if ($options === []) {
            return [
                'columns' => $dbResult['columns'],
                'rows' => $dbResult['rows'],
                'count' => $dbResult['count'],
            ];
        }

        $columns = $dbResult['columns'];
        $rowsAssoc = $this->rowsToAssoc($columns, $dbResult['rows']);

        $groupby = $this->readStringListOption($options, 'groupby', $ref);
        $aggregate = $this->readAggregateOption($options, $ref);
        if (($groupby !== [] || $aggregate !== []) && $groupby === [] && $aggregate !== []) {
            throw new ApiException('QUERY_VALIDATION_ERROR', 'Option aggregate requires non-empty groupby.', 400, $ref);
        }

        if ($groupby !== [] || $aggregate !== []) {
            $this->assertFieldsExist($columns, $groupby, $ref, 'groupby');
            foreach ($aggregate as $agg) {
                if ($agg['fn'] !== 'count') {
                    $this->assertFieldsExist($columns, [$agg['field']], $ref, 'aggregate');
                }
            }
            [$rowsAssoc, $columns] = $this->applyGrouping($rowsAssoc, $groupby, $aggregate);
        }

        $orderby = $this->readOrderByOption($options, $ref);
        if ($orderby !== []) {
            $sortFields = [];
            foreach ($orderby as $item) {
                $sortFields[] = $item['field'];
            }
            $this->assertFieldsExist($columns, $sortFields, $ref, 'orderby');
            $this->applyOrderBy($rowsAssoc, $orderby);
        }

        // When push-down already applied LIMIT/OFFSET in SQL, the DB result
        // is already trimmed — array_slice would either be a no-op or would
        // re-trim a window that's already correct. Skip both. The total_count
        // path runs as a separate COUNT query upstream in execute().
        if (!$paginatedInSql) {
            $skip = $this->readNonNegativeIntOption($options, 'skip', $ref, 0);
            $top = $this->readNonNegativeIntOption($options, 'top', $ref, self::MAX_TOP);
            if ($top > self::MAX_TOP) {
                throw new ApiException('QUERY_VALIDATION_ERROR', 'Option top exceeds allowed maximum.', 400, $ref);
            }

            $totalCount = count($rowsAssoc);
            if ($skip > 0) {
                $rowsAssoc = array_slice($rowsAssoc, $skip);
            }
            if ($top > 0) {
                $rowsAssoc = array_slice($rowsAssoc, 0, $top);
            }
        } else {
            $totalCount = count($rowsAssoc);
        }

        $rows = $this->assocToRows($columns, $rowsAssoc);
        $out = [
            'columns' => $columns,
            'rows' => $rows,
            'count' => count($rows),
        ];

        if (!$paginatedInSql && ($options['count'] ?? false) === true) {
            // SQL-pushed path computes total_count via the separate COUNT
            // query in execute(); only the in-PHP path sets it here.
            $out['total_count'] = $totalCount;
        }

        return $out;
    }

    /** @param list<string> $columns @param list<list<mixed>> $rows @return list<array<string, mixed>> */
    private function rowsToAssoc(array $columns, array $rows): array
    {
        $out = [];
        foreach ($rows as $row) {
            $assoc = [];
            foreach ($columns as $i => $col) {
                $assoc[$col] = $row[$i] ?? null;
            }
            $out[] = $assoc;
        }
        return $out;
    }

    /** @param list<string> $columns @param list<array<string, mixed>> $rows @return list<list<mixed>> */
    private function assocToRows(array $columns, array $rows): array
    {
        $out = [];
        foreach ($rows as $row) {
            $line = [];
            foreach ($columns as $col) {
                $line[] = $row[$col] ?? null;
            }
            $out[] = $line;
        }
        return $out;
    }

    /** @param array<string, mixed> $options @return list<string> */
    private function readStringListOption(array $options, string $key, string $ref): array
    {
        $value = $options[$key] ?? [];
        if ($value === null) {
            return [];
        }
        if (!is_array($value) || !array_is_list($value)) {
            throw new ApiException('QUERY_VALIDATION_ERROR', "Option {$key} must be an array.", 400, $ref);
        }

        $out = [];
        foreach ($value as $item) {
            if (!is_string($item) || trim($item) === '') {
                throw new ApiException('QUERY_VALIDATION_ERROR', "Option {$key} values must be non-empty strings.", 400, $ref);
            }
            $out[] = $item;
        }
        return $out;
    }

    /**
     * @param array<string, mixed> $options
     * @return list<array{field:string,dir:string}>
     */
    private function readOrderByOption(array $options, string $ref): array
    {
        $value = $options['orderby'] ?? [];
        if ($value === null) {
            return [];
        }
        if (!is_array($value) || !array_is_list($value)) {
            throw new ApiException('QUERY_VALIDATION_ERROR', 'Option orderby must be an array.', 400, $ref);
        }

        $out = [];
        foreach ($value as $item) {
            if (!is_array($item) || array_is_list($item)) {
                throw new ApiException('QUERY_VALIDATION_ERROR', 'Each orderby item must be an object.', 400, $ref);
            }
            $field = isset($item['field']) ? trim((string)$item['field']) : '';
            $dir = strtolower(trim((string)($item['dir'] ?? 'asc')));
            if ($field === '' || ($dir !== 'asc' && $dir !== 'desc')) {
                throw new ApiException('QUERY_VALIDATION_ERROR', 'Invalid orderby item.', 400, $ref);
            }
            $out[] = ['field' => $field, 'dir' => $dir];
        }
        return $out;
    }

    /**
     * @param array<string, mixed> $options
     * @return list<array{field:string,fn:string,as:string}>
     */
    private function readAggregateOption(array $options, string $ref): array
    {
        $value = $options['aggregate'] ?? [];
        if ($value === null) {
            return [];
        }
        if (!is_array($value) || !array_is_list($value)) {
            throw new ApiException('QUERY_VALIDATION_ERROR', 'Option aggregate must be an array.', 400, $ref);
        }

        $out = [];
        $seenAliases = [];
        foreach ($value as $item) {
            if (!is_array($item) || array_is_list($item)) {
                throw new ApiException('QUERY_VALIDATION_ERROR', 'Each aggregate item must be an object.', 400, $ref);
            }

            $field = isset($item['field']) ? trim((string)$item['field']) : '';
            $fn = strtolower(trim((string)($item['fn'] ?? '')));
            $as = isset($item['as']) ? trim((string)$item['as']) : '';
            if (!in_array($fn, ['count', 'sum', 'avg', 'min', 'max'], true) || $as === '') {
                throw new ApiException('QUERY_VALIDATION_ERROR', 'Invalid aggregate item.', 400, $ref);
            }
            if ($fn !== 'count' && $field === '') {
                throw new ApiException('QUERY_VALIDATION_ERROR', 'Aggregate field is required.', 400, $ref);
            }
            if (isset($seenAliases[$as])) {
                throw new ApiException('QUERY_VALIDATION_ERROR', 'Aggregate alias must be unique.', 400, $ref);
            }
            $seenAliases[$as] = true;
            $out[] = ['field' => $field, 'fn' => $fn, 'as' => $as];
        }
        return $out;
    }

    /** @param array<string, mixed> $options */
    private function readNonNegativeIntOption(array $options, string $key, string $ref, int $default): int
    {
        $value = $options[$key] ?? $default;
        if (!is_int($value)) {
            throw new ApiException('QUERY_VALIDATION_ERROR', "Option {$key} must be an integer.", 400, $ref);
        }
        if ($value < 0) {
            throw new ApiException('QUERY_VALIDATION_ERROR', "Option {$key} must be >= 0.", 400, $ref);
        }
        return $value;
    }

    /** @param list<string> $available @param list<string> $requested */
    private function assertFieldsExist(array $available, array $requested, string $ref, string $option): void
    {
        $map = [];
        foreach ($available as $name) {
            $map[$name] = true;
        }
        foreach ($requested as $field) {
            if (!isset($map[$field])) {
                throw new ApiException('QUERY_VALIDATION_ERROR', "Option {$option} uses unknown field '{$field}'.", 400, $ref);
            }
        }
    }

    /**
     * @param list<array<string, mixed>> $rows
     * @param list<string> $groupby
     * @param list<array{field:string,fn:string,as:string}> $aggregate
     * @return array{0:list<array<string, mixed>>,1:list<string>}
     */
    private function applyGrouping(array $rows, array $groupby, array $aggregate): array
    {
        $groups = [];
        foreach ($rows as $row) {
            $keyValues = [];
            foreach ($groupby as $field) {
                $keyValues[] = $row[$field] ?? null;
            }
            $groupKey = json_encode($keyValues);
            if (!is_string($groupKey)) {
                $groupKey = '[]';
            }
            $groups[$groupKey][] = $row;
        }

        $outRows = [];
        foreach ($groups as $groupRows) {
            $first = $groupRows[0] ?? [];
            $line = [];
            foreach ($groupby as $field) {
                $line[$field] = $first[$field] ?? null;
            }
            foreach ($aggregate as $agg) {
                $line[$agg['as']] = $this->computeAggregate($groupRows, $agg['fn'], $agg['field']);
            }
            $outRows[] = $line;
        }

        $newColumns = $groupby;
        foreach ($aggregate as $agg) {
            $newColumns[] = $agg['as'];
        }

        return [$outRows, $newColumns];
    }

    /** @param list<array<string, mixed>> $rows */
    private function computeAggregate(array $rows, string $fn, string $field): mixed
    {
        if ($fn === 'count') {
            return count($rows);
        }

        $values = [];
        foreach ($rows as $row) {
            $values[] = $row[$field] ?? null;
        }

        switch ($fn) {
            case 'sum':
                $sum = 0.0;
                foreach ($values as $v) {
                    if (is_numeric($v)) {
                        $sum += (float)$v;
                    }
                }
                return $sum;
            case 'avg':
                $sum = 0.0;
                $n = 0;
                foreach ($values as $v) {
                    if (is_numeric($v)) {
                        $sum += (float)$v;
                        $n++;
                    }
                }
                return $n > 0 ? $sum / $n : null;
            case 'min':
                $filtered = array_values(array_filter($values, static fn($v): bool => $v !== null));
                if ($filtered === []) {
                    return null;
                }
                sort($filtered);
                return $filtered[0];
            case 'max':
                $filtered = array_values(array_filter($values, static fn($v): bool => $v !== null));
                if ($filtered === []) {
                    return null;
                }
                rsort($filtered);
                return $filtered[0];
            default:
                return null;
        }
    }

    /** @param list<array<string, mixed>> $rows @param list<array{field:string,dir:string}> $orderby */
    private function applyOrderBy(array &$rows, array $orderby): void
    {
        usort($rows, static function (array $a, array $b) use ($orderby): int {
            foreach ($orderby as $order) {
                $field = $order['field'];
                $dir = $order['dir'] === 'desc' ? -1 : 1;
                $av = $a[$field] ?? null;
                $bv = $b[$field] ?? null;
                if ($av === $bv) {
                    continue;
                }
                if ($av === null) {
                    return -1 * $dir;
                }
                if ($bv === null) {
                    return 1 * $dir;
                }
                if ($av < $bv) {
                    return -1 * $dir;
                }
                if ($av > $bv) {
                    return 1 * $dir;
                }
            }
            return 0;
        });
    }
}
