<?php

declare(strict_types=1);

namespace Shape\Metadata;

use ShapeRuntime\Core\ApiException;

/**
 * Classifies which tables a brownfield database should expose under tenancy
 * (BROWNFIELD-ADAPTER-GUIDE.md): a table is tenant-owned iff it carries the
 * tenancy column AND that column is a real foreign key to the registry
 * table's PK — column-name coincidence alone does not qualify. Feeds the
 * access map so only verified tenant-linked business tables become API
 * surface; the registry itself, the app's internal tables, and unlinked
 * tables stay unexposed. Mirrors .NET Metadata/TenantExposurePlanner.
 *
 * Row-level scoping of the exposed tables is unchanged — the standard
 * tenancy machinery handles it.
 */
final class TenantExposurePlanner
{
    /**
     * @param array<string, mixed> $tenancy Tenancy config — supplies the
     *   discriminator column ('column') and registry table ('table').
     * @param list<string> $excludeTables App-internal tables to skip
     *   regardless of their FKs (identity tables, the app's own token store,
     *   migration bookkeeping). Case-insensitive.
     * @return list<string> Sorted tenant-owned table names.
     */
    public static function planTables(DatabaseIntrospector $introspector, array $tenancy, array $excludeTables = []): array
    {
        $column = isset($tenancy['column']) && is_string($tenancy['column']) ? $tenancy['column'] : '';
        $registry = isset($tenancy['table']) && is_string($tenancy['table']) ? $tenancy['table'] : '';
        if ($column === '' || $registry === '') {
            throw new ApiException(
                'CONFIG_INVALID',
                'TenantExposurePlanner requires tenancy.column and tenancy.table.',
                500,
                null,
                ['hint' => "Set tenancy 'column' (the discriminator) and 'table' (the registry) before planning exposure."]
            );
        }

        $excluded = array_map('strtolower', $excludeTables);
        $tables = [];
        foreach (array_unique($introspector->listTables()) as $table) {
            // The registry is global by definition; internal tables are the
            // app's business, not API surface.
            if (strcasecmp($table, $registry) === 0 || in_array(strtolower($table), $excluded, true)) {
                continue;
            }

            $hasColumn = false;
            foreach ($introspector->describeColumns($table) as $col) {
                if (strcasecmp($col['name'], $column) === 0) {
                    $hasColumn = true;
                    break;
                }
            }
            if (!$hasColumn) {
                continue;
            }

            // The column must be a *verified* FK to the registry PK — a
            // column that merely shares the name proves nothing.
            $fk = null;
            foreach ($introspector->describeForeignKeys($table) as $localColumn => $target) {
                if (strcasecmp($localColumn, $column) === 0) {
                    $fk = $target;
                    break;
                }
            }
            if ($fk === null
                || strcasecmp($fk['table'], $registry) !== 0
                || strcasecmp($fk['column'], 'id') !== 0
            ) {
                continue;
            }

            $tables[] = $table;
        }

        sort($tables, SORT_STRING);
        return $tables;
    }
}
