<?php

declare(strict_types=1);

namespace ShapeRuntime\Core;

final class MaskRegistry
{
    /** @var array<string, array<string, mixed>> */
    private array $byName = [];

    /** @var array<string, array<string, mixed>> */
    private array $bySql = [];

    /**
     * Masks come straight from user/generated config — heterogeneous maps,
     * hence the deliberately loose value type (every key is re-validated
     * before it lands in the normalized form).
     *
     * @param array<mixed, mixed> $masks
     */
    public function __construct(array $masks)
    {
        foreach ($masks as $name => $mask) {
            if (!is_string($name) || $name === '' || !is_array($mask)) {
                continue;
            }
            if (!isset($mask['sql']) || !is_string($mask['sql']) || trim($mask['sql']) === '') {
                continue;
            }
            $roles = [];
            if (is_array($mask['roles'] ?? null)) {
                foreach ($mask['roles'] as $role) {
                    if (is_string($role) && $role !== '') {
                        $roles[] = $role;
                    }
                }
            }
            $normalized = [
                'sql' => $mask['sql'],
                'params' => is_array($mask['params'] ?? null) ? $mask['params'] : [],
                'roles' => $roles,
                'writable' => (bool)($mask['writable'] ?? false),
            ];
            $writableMode = $mask['writable_mode'] ?? ($mask['writableMode'] ?? null);
            if (is_string($writableMode)) {
                $mode = strtolower(trim($writableMode));
                if (in_array($mode, ['full', 'append_only', 'no_delete', 'none'], true)) {
                    if ($mode === 'none') {
                        $normalized['writable'] = false;
                        $normalized['writable_mode'] = 'none';
                    } elseif ($mode !== 'full') {
                        $normalized['writable_mode'] = $mode;
                    }
                }
            }
            if (isset($mask['soft_delete']) && is_string($mask['soft_delete']) && $mask['soft_delete'] !== '') {
                $normalized['soft_delete'] = $mask['soft_delete'];
            }
            if (isset($mask['description']) && is_string($mask['description']) && $mask['description'] !== '') {
                $normalized['description'] = $mask['description'];
            }
            if (isset($mask['cache_control']) && is_string($mask['cache_control']) && $mask['cache_control'] !== '') {
                $normalized['cache_control'] = $mask['cache_control'];
            }
            if (isset($mask['audit']) && is_array($mask['audit']) && $mask['audit'] !== []) {
                $normalized['audit'] = $mask['audit'];
            }
            if (isset($mask['relations']) && is_array($mask['relations']) && $mask['relations'] !== []) {
                $normalized['relations'] = $mask['relations'];
            }
            if (isset($mask['default_limit']) && is_numeric($mask['default_limit']) && (int)$mask['default_limit'] > 0) {
                $normalized['default_limit'] = (int)$mask['default_limit'];
            }
            // PK carrier keys (MANUAL-PK-PLAN §2.3): `pk` names the PK column
            // for the RETURNING/OUTPUT path; `pk_strategy` 'manual' routes
            // inserts through framework-side MAX+1 allocation.
            if (isset($mask['pk']) && is_string($mask['pk']) && $mask['pk'] !== '') {
                $normalized['pk'] = $mask['pk'];
                if (($mask['pk_strategy'] ?? null) === 'manual') {
                    $normalized['pk_strategy'] = 'manual';
                }
            }
            // Tenancy carrier keys (MULTI-TENANCY-PLAN §2.4): `tenant_scope`
            // names the discriminator column; `sql_unscoped` is the
            // predicate-free variant an unscoped-role caller executes.
            if (isset($mask['tenant_scope']) && is_string($mask['tenant_scope']) && $mask['tenant_scope'] !== '') {
                $normalized['tenant_scope'] = $mask['tenant_scope'];
            }
            // Dimension row-scope carrier (ROW-SCOPES-PHP-PLAN §2.3):
            // `row_scopes` maps column => {multi}. Manual masks may declare
            // a plain column list — normalize to the map form (`multi`
            // resolved from the scopes config at runtime).
            if (isset($mask['row_scopes']) && is_array($mask['row_scopes']) && $mask['row_scopes'] !== []) {
                $rowScopes = [];
                foreach ($mask['row_scopes'] as $col => $meta) {
                    if (is_int($col) && is_string($meta) && $meta !== '') {
                        $rowScopes[$meta] = [];
                    } elseif (is_string($col) && $col !== '') {
                        $rowScopes[$col] = is_array($meta) && array_key_exists('multi', $meta) ? ['multi' => (bool)$meta['multi']] : [];
                    }
                }
                if ($rowScopes !== []) {
                    $normalized['row_scopes'] = $rowScopes;
                }
            }
            if ((isset($normalized['tenant_scope']) || isset($normalized['row_scopes']))
                && isset($mask['sql_unscoped']) && is_string($mask['sql_unscoped']) && $mask['sql_unscoped'] !== ''
            ) {
                $normalized['sql_unscoped'] = $mask['sql_unscoped'];
            }
            // FK targets that are tenant-scoped (§2.5) — consumed by the
            // opt-in `tenancy.validate_fks` cross-tenant reference check.
            if (isset($mask['fk_checks']) && is_array($mask['fk_checks']) && $mask['fk_checks'] !== []) {
                $normalized['fk_checks'] = $mask['fk_checks'];
            }
            // Per-operation write roles: `roles` stays the union gate; the
            // executor narrows by mutation action via this map.
            if (isset($mask['roles_by_action']) && is_array($mask['roles_by_action']) && $mask['roles_by_action'] !== []) {
                $normalized['roles_by_action'] = $mask['roles_by_action'];
            }
            $this->byName[$name] = $normalized;
            $this->bySql[$mask['sql']] = $normalized;
        }
    }

    /**
     * @param array<string, mixed> $authContext
     * @return array<string, mixed>
     */
    public function resolve(string $sqlOrMask, array $authContext = [], ?string $ref = null): array
    {
        $mask = $this->byName[$sqlOrMask] ?? $this->bySql[$sqlOrMask] ?? null;
        if ($mask === null) {
            throw new ApiException(
                'MASK_NOT_ALLOWED',
                "Query '{$sqlOrMask}' is not allowed by masks.",
                400,
                $ref,
                [
                    'hint' => "Fetch GET /api.php?manifest (or POST {op:'manifest'}) to see available masks. Use the manifest's mask names verbatim.",
                ]
            );
        }

        $allowedRoles = is_array($mask['roles'] ?? null) ? $mask['roles'] : [];
        if ($allowedRoles !== []) {
            $role = isset($authContext['role']) ? (string)$authContext['role'] : '';
            if ($role === '' || !in_array($role, $allowedRoles, true)) {
                throw new ApiException(
                    'AUTH_FORBIDDEN',
                    'Current role is not allowed for this query.',
                    403,
                    $ref,
                    [
                        'expected' => ['role' => 'one of: ' . implode(', ', $allowedRoles)],
                        'hint' => $role === ''
                            ? "Authenticate first via POST {op:'auth.token'} and pass the access token as Authorization: Bearer <token>."
                            : "Your role '{$role}' cannot access this mask. Need one of: " . implode(', ', $allowedRoles) . '.',
                    ]
                );
            }
        }

        $resolved = [
            'sql' => $mask['sql'],
            'params' => $mask['params'] ?? [],
            'writable' => (bool)($mask['writable'] ?? false),
        ];
        if (isset($mask['writable_mode']) && is_string($mask['writable_mode']) && $mask['writable_mode'] !== '') {
            $resolved['writable_mode'] = $mask['writable_mode'];
        }
        if (isset($mask['soft_delete']) && is_string($mask['soft_delete']) && $mask['soft_delete'] !== '') {
            $resolved['soft_delete'] = $mask['soft_delete'];
        }
        if (isset($mask['audit']) && is_array($mask['audit']) && $mask['audit'] !== []) {
            $resolved['audit'] = $mask['audit'];
        }
        if (isset($mask['relations']) && is_array($mask['relations']) && $mask['relations'] !== []) {
            $resolved['relations'] = $mask['relations'];
        }
        if (isset($mask['default_limit'])) {
            $resolved['default_limit'] = $mask['default_limit'];
        }
        if (isset($mask['pk']) && is_string($mask['pk']) && $mask['pk'] !== '') {
            $resolved['pk'] = $mask['pk'];
            if (($mask['pk_strategy'] ?? null) === 'manual') {
                $resolved['pk_strategy'] = 'manual';
            }
        }
        if (isset($mask['tenant_scope']) && is_string($mask['tenant_scope']) && $mask['tenant_scope'] !== '') {
            $resolved['tenant_scope'] = $mask['tenant_scope'];
        }
        if (isset($mask['row_scopes']) && is_array($mask['row_scopes']) && $mask['row_scopes'] !== []) {
            $resolved['row_scopes'] = $mask['row_scopes'];
        }
        if ((isset($resolved['tenant_scope']) || isset($resolved['row_scopes']))
            && isset($mask['sql_unscoped']) && is_string($mask['sql_unscoped']) && $mask['sql_unscoped'] !== ''
        ) {
            $resolved['sql_unscoped'] = $mask['sql_unscoped'];
        }
        if (isset($mask['fk_checks']) && is_array($mask['fk_checks']) && $mask['fk_checks'] !== []) {
            $resolved['fk_checks'] = $mask['fk_checks'];
        }
        if (isset($mask['roles_by_action']) && is_array($mask['roles_by_action']) && $mask['roles_by_action'] !== []) {
            $resolved['roles_by_action'] = $mask['roles_by_action'];
        }
        return $resolved;
    }
}
