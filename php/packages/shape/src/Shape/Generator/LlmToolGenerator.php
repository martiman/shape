<?php

declare(strict_types=1);

namespace Shape\Generator;

use Shape\Metadata\EntityMeta;

/**
 * Emits the manifest as a ready-to-paste tool-definitions array for an LLM,
 * in either Anthropic or OpenAI tool-call shape. Removes the translation
 * step every agent integration would otherwise repeat.
 *
 * Read masks become tools that take the mask's params as input. Writable
 * masks become a single `<table>_mutate` tool with an action enum. Commands
 * are emitted with an empty input schema (their PHP signatures don't carry
 * per-parameter typing yet — see roadmap item 8 for descriptions).
 */
final class LlmToolGenerator
{
    /**
     * @param array<string, EntityMeta> $entities
     * @param array<string, array<string, mixed>> $masks
     * @param array<string, mixed> $commands
     * @return list<array<string, mixed>>
     */
    public function generateAnthropic(array $entities, array $masks, array $commands): array
    {
        $tools = [];
        foreach ($this->buildTools($entities, $masks, $commands) as $tool) {
            $tools[] = [
                'name' => $tool['name'],
                'description' => $tool['description'],
                'input_schema' => $tool['input_schema'],
            ];
        }
        return $tools;
    }

    /**
     * @param array<string, EntityMeta> $entities
     * @param array<string, array<string, mixed>> $masks
     * @param array<string, mixed> $commands
     * @return list<array<string, mixed>>
     */
    public function generateOpenAi(array $entities, array $masks, array $commands): array
    {
        $tools = [];
        foreach ($this->buildTools($entities, $masks, $commands) as $tool) {
            $tools[] = [
                'type' => 'function',
                'function' => [
                    'name' => $tool['name'],
                    'description' => $tool['description'],
                    'parameters' => $tool['input_schema'],
                ],
            ];
        }
        return $tools;
    }

    /**
     * Internal canonical tool form. Both wrappers above re-shape from this.
     *
     * @param array<string, EntityMeta> $entities
     * @param array<string, array<string, mixed>> $masks
     * @param array<string, mixed> $commands
     * @return list<array{name:string, description:string, input_schema:array<string, mixed>}>
     */
    private function buildTools(array $entities, array $masks, array $commands): array
    {
        $entityByTable = [];
        foreach ($entities as $meta) {
            $entityByTable[$meta->table] = $meta;
        }

        $tools = [];

        foreach ($masks as $name => $mask) {
            $writable = (bool)($mask['writable'] ?? false);

            if ($writable) {
                // Single mutate-tool per table covers insert / update / delete.
                $tools[] = $this->mutateTool($name, $entityByTable[$name] ?? null, $mask);
                continue;
            }

            $tools[] = [
                'name' => $this->toolName($name),
                'description' => $this->resolveDescription($name, $mask, $entityByTable),
                'input_schema' => $this->paramsToSchema($mask['params'] ?? []),
            ];
        }

        foreach (array_keys($commands) as $cmdName) {
            $tools[] = [
                'name' => $this->toolName((string)$cmdName),
                'description' => "Custom command '{$cmdName}'. Parameters depend on the implementation — see manifest commands list for details.",
                'input_schema' => [
                    'type' => 'object',
                    'properties' => (object)[],
                    'additionalProperties' => true,
                ],
            ];
        }

        return $tools;
    }

    /**
     * @param array<string, array<string, mixed>> $params
     * @return array<string, mixed>
     */
    private function paramsToSchema(array $params): array
    {
        $properties = [];
        $required = [];

        foreach ($params as $name => $spec) {
            $type = (string)($spec['type'] ?? 'string');
            $properties[$name] = $this->mapParamType($type);
            if (($spec['required'] ?? false) === true) {
                $required[] = $name;
            }
        }

        $schema = [
            'type' => 'object',
            'properties' => $properties === [] ? (object)[] : $properties,
        ];
        if ($required !== []) {
            $schema['required'] = $required;
        }
        return $schema;
    }

    /** @return array<string, mixed> */
    private function mapParamType(string $type): array
    {
        return match ($type) {
            'int', 'integer' => ['type' => 'integer'],
            'float', 'double', 'decimal', 'number' => ['type' => 'number'],
            'bool', 'boolean' => ['type' => 'boolean'],
            'array_int' => ['type' => 'array', 'items' => ['type' => 'integer']],
            'array_string' => ['type' => 'array', 'items' => ['type' => 'string']],
            default => ['type' => 'string'],
        };
    }

    /**
     * Tool names: replace dots with underscores, strip anything outside
     * [A-Za-z0-9_-]. Anthropic and OpenAI both accept this character set.
     */
    private function toolName(string $maskName): string
    {
        $name = str_replace('.', '_', $maskName);
        $name = preg_replace('/[^A-Za-z0-9_-]/', '_', $name) ?? $name;
        return substr($name, 0, 64);
    }

    /**
     * Prefer an explicit `description` on the mask config (set by generators
     * or by the dev's hand-written mask). Fall back to verb-based inference
     * for masks that have no description set.
     *
     * @param array<string, mixed> $mask
     * @param array<string, EntityMeta> $entityByTable
     */
    private function resolveDescription(string $name, array $mask, array $entityByTable): string
    {
        $entityHint = $this->entityHint($entityByTable[$this->tableFromMaskName($name)] ?? null);
        if (isset($mask['description']) && is_string($mask['description']) && $mask['description'] !== '') {
            $cols = $this->columnsHint($name, $entityByTable);
            return $cols !== ''
                ? rtrim($mask['description'], '. ') . ". Returns rows with columns: {$cols}." . $entityHint
                : $mask['description'] . $entityHint;
        }
        return $this->describeReadMask($name, $mask, $entityByTable) . $entityHint;
    }

    /**
     * @param array<string, EntityMeta> $entityByTable
     */
    private function columnsHint(string $maskName, array $entityByTable): string
    {
        $dot = strpos($maskName, '.');
        $table = $dot === false ? $maskName : substr($maskName, 0, $dot);
        $entity = $entityByTable[$table] ?? null;
        if ($entity === null) {
            return '';
        }
        return implode(', ', array_map(fn($f) => $f->name, $entity->fields));
    }

    /**
     * @param array<string, mixed> $mask
     * @param array<string, EntityMeta> $entityByTable
     */
    private function describeReadMask(string $name, array $mask, array $entityByTable): string
    {
        $dot = strpos($name, '.');
        if ($dot === false) {
            return "Run mask '{$name}'.";
        }

        $table = substr($name, 0, $dot);
        $verb = substr($name, $dot + 1);
        $entity = $entityByTable[$table] ?? null;
        $cols = $entity !== null
            ? implode(', ', array_map(fn($f) => $f->name, $entity->fields))
            : '';

        $base = match (true) {
            $verb === 'list'      => "List rows from {$table}",
            $verb === 'get'       => "Get one row from {$table} by primary key",
            $verb === 'getMany'   => "Get multiple rows from {$table} by primary keys",
            str_starts_with($verb, 'by') => "Filter {$table} by " . lcfirst(substr($verb, 2)),
            $verb === 'adminList' => "(admin only) List rows from {$table} with sensitive columns stripped",
            $verb === 'adminGet'  => "(admin only) Get one row from {$table} by primary key with sensitive columns stripped",
            default               => "Run mask '{$name}' on {$table}",
        };

        if ($cols !== '') {
            $base .= ". Returns rows with columns: {$cols}.";
        } else {
            $base .= '.';
        }
        return $base;
    }

    /**
     * @param array<string, mixed> $mask
     * @return array{name:string, description:string, input_schema:array<string, mixed>}
     */
    private function mutateTool(string $table, ?EntityMeta $entity, array $mask = []): array
    {
        $cols = $entity !== null
            ? implode(', ', array_map(fn($f) => $f->name, $entity->fields))
            : '';
        $mode = $entity?->effectiveWritableMode() ?? 'full';
        $entityHint = $this->entityHint($entity);

        // Prefer an explicit description on the writable mask. Otherwise
        // synthesize from the entity shape.
        if (isset($mask['description']) && is_string($mask['description']) && $mask['description'] !== '') {
            $description = $mask['description'];
            if ($cols !== '' && !str_contains($description, $cols)) {
                $description .= " Columns: {$cols}.";
            }
        } else {
            $description = match ($mode) {
                'append_only' => "Insert rows in {$table} only.",
                'no_delete' => "Insert / update rows in {$table}; DELETE is rejected.",
                default => "Insert / update / delete rows in {$table}.",
            };
            if ($cols !== '') {
                $description .= " Columns: {$cols}.";
            }
            if ($entity?->softDeleteColumn !== null) {
                $description .= " DELETE on this table is a soft-delete (sets {$entity->softDeleteColumn} = CURRENT_TIMESTAMP); reads automatically filter out soft-deleted rows.";
            }
        }
        $description .= $entityHint;

        return [
            'name' => $this->toolName($table) . '_mutate',
            'description' => $description,
            'input_schema' => [
                'type' => 'object',
                'properties' => [
                    'action' => [
                        'type' => 'string',
                        'enum' => ['insert', 'update', 'delete'],
                        'description' => 'Mutation action to perform.',
                    ],
                    'data' => [
                        'type' => 'object',
                        'description' => 'Column-value pairs (insert/update only).',
                        'additionalProperties' => true,
                    ],
                    'filter' => [
                        'type' => 'object',
                        'description' => 'Row filter (update/delete only). At least one column required.',
                        'additionalProperties' => true,
                    ],
                ],
                'required' => ['action'],
            ],
        ];
    }

    private function entityHint(?EntityMeta $entity): string
    {
        if ($entity === null || $entity->description === null || $entity->description === '') {
            return '';
        }

        return ' (' . $entity->description . ')';
    }

    private function tableFromMaskName(string $name): string
    {
        $dot = strpos($name, '.');
        return $dot === false ? $name : substr($name, 0, $dot);
    }
}
