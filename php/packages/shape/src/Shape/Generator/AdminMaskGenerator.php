<?php

declare(strict_types=1);

namespace Shape\Generator;

use Shape\Metadata\DatabaseIntrospector;
use ShapeRuntime\Core\SqlDialect;

/**
 * Built-in admin masks for "secret" tables (users, refresh_tokens, …).
 *
 * Secret tables are never exposed via auto-CRUD. When the dev opts in via
 * `'admin_masks' => true`, this generator builds read-only `<table>.adminList`
 * and `<table>.adminGet` masks gated to the `admin` role, with sensitive
 * columns (password*, *_hash, *_secret, salt*, mfa_*, totp_*, exact `token`/
 * `secret`) stripped from the SELECT.
 */
final class AdminMaskGenerator
{
    /**
     * @param list<string> $secretTables Tables to attempt — silently skipped if absent
     * @param array{column?:string,unscoped_roles?:list<string>}|null $tenancy
     *        Normalized tenancy config. When a secret table carries the tenant
     *        discriminator column, generated masks get the `<col> = :__tenant`
     *        predicate plus a `tenant_scope` carrier, and an unscoped variant so
     *        superadmins keep cross-tenant reach. Null when tenancy is disabled.
     * @return array<string, array<string, mixed>>
     */
    public static function generate(DatabaseIntrospector $introspector, array $secretTables, ?array $tenancy = null): array
    {
        $existing = $introspector->listTables();
        $driver = $introspector->driver();
        $masks = [];

        $tenantColumn = $tenancy['column'] ?? null;
        $unscopedRoles = array_values(array_filter(
            $tenancy['unscoped_roles'] ?? [],
            static fn(string $r): bool => $r !== ''
        ));
        // Unscoped roles must also reach admin masks (they see across tenants);
        // mirror the .NET generator, which folds them into the mask roles.
        $roles = array_values(array_unique(array_merge(['admin'], $unscopedRoles)));

        foreach ($secretTables as $table) {
            if (!in_array($table, $existing, true)) {
                continue;
            }

            $columns = $introspector->describeColumns($table);
            $columnNames = array_map(fn(array $c) => (string)$c['name'], $columns);
            $safeNames = array_values(array_filter($columnNames, self::isSafeColumn(...)));
            if ($safeNames === []) {
                continue;
            }

            $select = implode(', ', array_map(fn(string $c) => self::q($c, $driver), $safeNames));
            $qt = self::q($table, $driver);

            $tenantScoped = $tenantColumn !== null && in_array($tenantColumn, $columnNames, true);
            $hasUnscopedVariant = $tenantScoped && $unscopedRoles !== [];
            $qtc = $tenantScoped ? self::q($tenantColumn, $driver) : '';
            $scopedWhere = $tenantScoped ? " WHERE {$qtc} = :__tenant" : '';

            $listMask = [
                'sql' => "SELECT {$select} FROM {$qt}{$scopedWhere}",
                'params' => [],
                'roles' => $roles,
                'description' => "(admin only) List rows from {$table} with sensitive columns stripped.",
            ];
            if ($tenantScoped) {
                $listMask['tenant_scope'] = $tenantColumn;
                if ($hasUnscopedVariant) {
                    $listMask['sql_unscoped'] = "SELECT {$select} FROM {$qt}";
                }
            }
            $masks[$table . '.adminList'] = $listMask;

            if (in_array('id', $safeNames, true)) {
                $qid = self::q('id', $driver);
                $getWhere = $tenantScoped
                    ? " WHERE {$qid} = :id AND {$qtc} = :__tenant"
                    : " WHERE {$qid} = :id";
                $getMask = [
                    'sql' => "SELECT {$select} FROM {$qt}{$getWhere}",
                    'params' => ['id' => ['type' => 'int', 'required' => true]],
                    'roles' => $roles,
                    'description' => "(admin only) Get one row from {$table} by id with sensitive columns stripped.",
                ];
                if ($tenantScoped) {
                    $getMask['tenant_scope'] = $tenantColumn;
                    if ($hasUnscopedVariant) {
                        $getMask['sql_unscoped'] = "SELECT {$select} FROM {$qt} WHERE {$qid} = :id";
                    }
                }
                $masks[$table . '.adminGet'] = $getMask;
            }
        }

        return $masks;
    }

    private static function isSafeColumn(string $name): bool
    {
        $lower = strtolower($name);
        if (in_array($lower, ['token', 'secret', 'password', 'salt'], true)) {
            return false;
        }
        foreach (['password_', 'secret_', 'salt_', 'mfa_', 'totp_'] as $prefix) {
            if (str_starts_with($lower, $prefix)) {
                return false;
            }
        }
        foreach (['_hash', '_secret', '_token'] as $suffix) {
            if (str_ends_with($lower, $suffix)) {
                return false;
            }
        }
        return true;
    }

    private static function q(string $name, string $driver): string
    {
        return SqlDialect::quoteIdentifier($driver, $name);
    }
}
