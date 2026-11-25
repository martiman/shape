<?php

declare(strict_types=1);

namespace Shape\Generator;

use ReflectionClass;
use Shape\Metadata\EntityMeta;
use Shape\Metadata\FieldMeta;

final class ManifestGenerator
{
    /**
     * @param array<string, EntityMeta> $entities
     * @param array<string, array<string, mixed>> $masks
     * @param list<string> $commands
     * @param array<string, array<string, mixed>> $uploads Per-name upload-endpoint config (from `config['uploads']`)
     */
    public function generate(array $entities, array $masks, array $commands, array $uploads = []): array
    {
        $entityList = [];
        foreach ($entities as $meta) {
            // A manual-strategy PK (framework-allocated MAX+1) is reported as
            // auto-assigned: externally it behaves exactly like an identity
            // column — clients never send it, the response returns the id.
            $writableMask = $masks[$meta->table] ?? null;
            $manualPk = is_array($writableMask)
                && ($writableMask['pk_strategy'] ?? null) === 'manual'
                && is_string($writableMask['pk'] ?? null)
                ? $writableMask['pk']
                : null;

            // Tenant-scoped when any generated mask on the table carries the
            // scope column (MULTI-TENANCY-PLAN §2.8). Row scopes report the
            // dimension columns the same way (ROW-SCOPES §2.5).
            $tenantScoped = false;
            $rowScopeCols = [];
            foreach ([$meta->table, $meta->table . '.list'] as $mn) {
                if (isset($masks[$mn]['tenant_scope']) && is_string($masks[$mn]['tenant_scope']) && $masks[$mn]['tenant_scope'] !== '') {
                    $tenantScoped = true;
                }
                if ($rowScopeCols === [] && is_array($masks[$mn]['row_scopes'] ?? null)) {
                    $rowScopeCols = array_values(array_filter(array_keys($masks[$mn]['row_scopes']), 'is_string'));
                }
            }

            $fields = [];
            foreach ($meta->fields as $field) {
                $f = [
                    'name' => $field->name,
                    'type' => $this->displayType($field),
                    'nullable' => $field->nullable,
                ];
                if ($field->isPrimary) {
                    $f['primary'] = true;
                }
                if ($field->isAutoIncrement || $field->name === $manualPk) {
                    $f['autoIncrement'] = true;
                }
                if ($field->isFk()) {
                    $f['fk'] = $field->fkEntity;
                }
                if ($field->validators !== []) {
                    $f['validators'] = $field->validators;
                }
                if ($field->enumValues !== null && $field->enumValues !== []) {
                    $f['enum'] = $field->enumValues;
                }
                if ($field->hasDefault) {
                    $f['default'] = $field->default;
                }
                if ($field->description !== null && $field->description !== '') {
                    $f['description'] = $field->description;
                }
                $fields[] = $f;
            }

            // Collect masks belonging to this entity
            $entityMasks = [];
            foreach ($masks as $name => $mask) {
                if (str_starts_with($name, $meta->table . '.') || $name === $meta->table) {
                    $writable = (bool)($mask['writable'] ?? false);
                    $writableMode = isset($mask['writable_mode']) && is_string($mask['writable_mode']) && $mask['writable_mode'] !== ''
                        ? $mask['writable_mode']
                        : null;
                    if ($writableMode === 'none') {
                        $writable = false;
                    } elseif ($writableMode !== null) {
                        $writable = true;
                    }
                    $entry = [
                        'params' => array_keys($mask['params'] ?? []),
                        'writable' => $writable,
                    ];
                    if (isset($mask['description']) && is_string($mask['description']) && $mask['description'] !== '') {
                        $entry['description'] = $mask['description'];
                    }
                    if ($writableMode !== null) {
                        $entry['writableMode'] = $writableMode;
                    }
                    if (isset($mask['default_limit']) && is_numeric($mask['default_limit']) && (int)$mask['default_limit'] > 0) {
                        $entry['defaultLimit'] = (int)$mask['default_limit'];
                    }
                    // Snake_case on purpose — `pk` / `pk_strategy` are the
                    // cross-runtime manifest contract shared with .NET.
                    if (isset($mask['pk']) && is_string($mask['pk']) && $mask['pk'] !== '') {
                        $entry['pk'] = $mask['pk'];
                        if (($mask['pk_strategy'] ?? null) === 'manual') {
                            $entry['pk_strategy'] = 'manual';
                        }
                    }
                    // Cross-runtime snake_case contract, shared with .NET.
                    if (isset($mask['tenant_scope']) && is_string($mask['tenant_scope']) && $mask['tenant_scope'] !== '') {
                        $entry['tenant_scope'] = $mask['tenant_scope'];
                    }
                    // Row scopes: column => {multi} — cross-runtime
                    // snake_case contract (ROW-SCOPES §2.3), shared with .NET.
                    if (is_array($mask['row_scopes'] ?? null) && $mask['row_scopes'] !== []) {
                        $rs = [];
                        foreach ($mask['row_scopes'] as $col => $m) {
                            if (is_string($col) && $col !== '') {
                                $rs[$col] = ['multi' => (bool)(is_array($m) ? ($m['multi'] ?? false) : false)];
                            }
                        }
                        if ($rs !== []) {
                            $entry['row_scopes'] = $rs;
                        }
                    }
                    // Per-operation write roles (cross-runtime snake_case).
                    if (isset($mask['roles_by_action']) && is_array($mask['roles_by_action']) && $mask['roles_by_action'] !== []) {
                        $entry['roles_by_action'] = $mask['roles_by_action'];
                    }
                    $entityMasks[$name] = $entry;
                }
            }

            // In entity mode, class is a real PHP class; in DB mode, it's the table name
            $name = class_exists($meta->class)
                ? (new ReflectionClass($meta->class))->getShortName()
                : ucfirst($meta->table);

            $writableMode = $meta->effectiveWritableMode();
            $entry = [
                'name' => $name,
                'table' => $meta->table,
                'writable' => $writableMode !== 'none',
                'fields' => $fields,
                'masks' => $entityMasks,
                'roles' => [
                    'read' => $meta->readRoles,
                    'write' => $meta->writeRoles,
                ],
            ];
            if ($meta->softDeleteColumn !== null) {
                $entry['softDelete'] = $meta->softDeleteColumn;
            }
            if ($meta->auditColumns !== []) {
                $entry['audit'] = $meta->auditColumns;
            }
            if ($meta->description !== null && $meta->description !== '') {
                $entry['description'] = $meta->description;
            }
            if ($meta->uniqueConstraints !== []) {
                $entry['uniqueConstraints'] = $meta->uniqueConstraints;
            }
            if ($writableMode !== 'full' && $writableMode !== 'none') {
                $entry['writableMode'] = $writableMode;
            }
            if ($tenantScoped) {
                $entry['tenantScoped'] = true;
            }
            if ($rowScopeCols !== []) {
                $entry['rowScopes'] = $rowScopeCols;
            }
            $entityList[] = $entry;
        }

        // Collect masks not owned by any entity (manual/orphan masks)
        $claimedMasks = [];
        foreach ($entityList as $entity) {
            foreach (array_keys($entity['masks']) as $maskName) {
                $claimedMasks[$maskName] = true;
            }
        }

        $standaloneMasks = [];
        foreach ($masks as $name => $mask) {
            if (!isset($claimedMasks[$name])) {
            $entry = [
                'params' => array_keys($mask['params'] ?? []),
                'writable' => false,
                'roles' => $mask['roles'] ?? [],
            ];
            $writable = (bool)($mask['writable'] ?? false);
            $writableMode = isset($mask['writable_mode']) && is_string($mask['writable_mode']) && $mask['writable_mode'] !== ''
                ? $mask['writable_mode']
                : null;
            if ($writableMode === 'none') {
                $writable = false;
            } elseif ($writableMode !== null) {
                $writable = true;
            }
            $entry['writable'] = $writable;
            if (isset($mask['description']) && is_string($mask['description']) && $mask['description'] !== '') {
                $entry['description'] = $mask['description'];
            }
            if ($writableMode !== null) {
                $entry['writableMode'] = $writableMode;
            }
            if (isset($mask['default_limit']) && is_numeric($mask['default_limit']) && (int)$mask['default_limit'] > 0) {
                $entry['defaultLimit'] = (int)$mask['default_limit'];
            }
            if (isset($mask['pk']) && is_string($mask['pk']) && $mask['pk'] !== '') {
                $entry['pk'] = $mask['pk'];
                if (($mask['pk_strategy'] ?? null) === 'manual') {
                    $entry['pk_strategy'] = 'manual';
                }
            }
            if (is_array($mask['row_scopes'] ?? null) && $mask['row_scopes'] !== []) {
                $rs = [];
                foreach ($mask['row_scopes'] as $col => $m) {
                    if (is_string($col) && $col !== '') {
                        $rs[$col] = ['multi' => (bool)(is_array($m) ? ($m['multi'] ?? false) : false)];
                    } elseif (is_int($col) && is_string($m) && $m !== '') {
                        $rs[$m] = ['multi' => false];
                    }
                }
                if ($rs !== []) {
                    $entry['row_scopes'] = $rs;
                }
            }
                $standaloneMasks[$name] = $entry;
            }
        }

        $result = [
            'entities' => $entityList,
            'commands' => $commands,
        ];

        if ($standaloneMasks !== []) {
            $result['masks'] = $standaloneMasks;
        }

        if ($uploads !== []) {
            $uploadList = [];
            foreach ($uploads as $name => $entry) {
                if (!is_array($entry)) {
                    continue;
                }
                $info = [];
                if (isset($entry['description']) && is_string($entry['description']) && $entry['description'] !== '') {
                    $info['description'] = $entry['description'];
                }
                if (isset($entry['roles']) && is_array($entry['roles'])) {
                    $info['roles'] = array_values(array_filter($entry['roles'], 'is_string'));
                }
                if (isset($entry['max_size']) && is_numeric($entry['max_size'])) {
                    $info['maxSize'] = (int)$entry['max_size'];
                }
                if (isset($entry['mime_types']) && is_array($entry['mime_types'])) {
                    $info['mimeTypes'] = array_values(array_filter($entry['mime_types'], 'is_string'));
                }
                $uploadList[(string)$name] = $info;
            }
            if ($uploadList !== []) {
                $result['uploads'] = $uploadList;
            }
        }

        return $result;
    }

    private function displayType(FieldMeta $field): string
    {
        $type = $field->dbType;
        $base = strtolower($type);
        // Show precision/scale only for exact-decimal types and length only for
        // bounded character types. Integer display widths (int(10,0)) and text
        // byte limits (text(65535)) are driver noise that differ per database.
        if (in_array($base, ['decimal', 'numeric', 'money', 'smallmoney'], true)
            && $field->precision !== null && $field->scale !== null) {
            $type .= "({$field->precision},{$field->scale})";
        } elseif ($field->length !== null && in_array($base, ['varchar', 'char', 'nvarchar', 'nchar', 'varbinary', 'binary'], true)) {
            $type .= "({$field->length})";
        }
        if ($field->unsigned) {
            $type .= ' unsigned';
        }
        return $type;
    }
}
