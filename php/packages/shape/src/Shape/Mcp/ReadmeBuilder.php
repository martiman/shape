<?php

declare(strict_types=1);

namespace Shape\Mcp;

use Shape\Metadata\EntityMeta;

/**
 * Builds the `shape://README.md` resource — a narrated, human-readable
 * overview of the entire backend that an agent can attach as context with
 * one click in Claude Desktop / Cursor / Cline.
 *
 * Most MCP servers expose raw tools and let the LLM figure things out from
 * tool names. Shape exposes a *guided tour* of the backend: which entities
 * exist, what they relate to, what's writable, what's soft-deleted, what
 * commands exist, and how auth works. Attach this resource and the agent
 * has the entire surface area as context before its first tool call.
 */
final class ReadmeBuilder
{
    /**
     * @param array<string, EntityMeta> $entities
     * @param array<string, array<string, mixed>> $masks
     * @param array<string, mixed> $commands
     * @param array<string, mixed> $authConfig
     */
    public static function build(
        array $entities,
        array $masks,
        array $commands,
        array $authConfig,
        string $serviceName = 'shape',
    ): string {
        $writableTables = [];
        $softDeleteTables = [];
        $entityRows = [];

        foreach ($entities as $meta) {
            if ($meta->effectiveWritableMode() !== 'none') {
                $writableTables[] = $meta->table;
            }
            if ($meta->softDeleteColumn !== null) {
                $softDeleteTables[] = $meta->table;
            }
            $entityRows[] = self::renderEntity($meta, $masks);
        }

        $standalone = self::collectStandaloneMasks($entities, $masks);
        $authDriver = (string)($authConfig['driver'] ?? 'none');

        $md = "# {$serviceName} Backend\n\n";
        $md .= self::introBlurb(count($entities), count($writableTables), count($softDeleteTables), count($standalone), count($commands));
        $md .= "\n\n";

        $md .= "## How to call this backend\n\n";
        $md .= self::callingGuide($authDriver);
        $md .= "\n";

        if ($entities !== []) {
            $md .= "## Entities (" . count($entities) . ")\n\n";
            $md .= implode("\n", $entityRows);
            $md .= "\n";
        }

        if ($standalone !== []) {
            $md .= "## Custom masks (" . count($standalone) . ")\n\n";
            foreach ($standalone as $name => $info) {
                $desc = $info['description'] ?? 'Hand-written mask.';
                $mode = is_string($info['writable_mode'] ?? null) ? $info['writable_mode'] : '';
                if ($mode === 'append_only') {
                    $desc .= ' *(append-only)*';
                } elseif ($mode === 'no_delete') {
                    $desc .= ' *(no-delete)*';
                }
                $md .= "- **`{$name}`** — {$desc}\n";
                $params = is_array($info['params'] ?? null) ? $info['params'] : [];
                // Mask configs use param names as keys (`['id' => ['type' => 'int', ...]]`).
                $paramNames = $params === [] ? [] : array_keys($params);
                if ($paramNames !== []) {
                    $md .= "  - Params: " . implode(', ', $paramNames) . "\n";
                }
            }
            $md .= "\n";
        }

        if ($commands !== []) {
            $md .= "## Commands (" . count($commands) . ")\n\n";
            foreach (array_keys($commands) as $cmdName) {
                $md .= "- `{$cmdName}` — custom business-logic operation. Call via the `" . self::commandToolName((string)$cmdName) . "` tool.\n";
            }
            $md .= "\n";
        }

        $md .= "## Notes for agents\n\n";
        $md .= self::agentNotes($softDeleteTables);

        return $md;
    }

    /**
     * @param array<string, array<string, mixed>> $masks
     */
    private static function renderEntity(EntityMeta $meta, array $masks): string
    {
        $table = $meta->table;
        $mode = $meta->effectiveWritableMode();
        $writable = match ($mode) {
            'full' => ' *(writable)*',
            'append_only' => ' *(append-only)*',
            'no_delete' => ' *(no-delete)*',
            default => '',
        };
        $soft = $meta->softDeleteColumn !== null ? ' *(soft-delete via `deleted_at`)*' : '';

        $lines = ["### `{$table}`{$writable}{$soft}"];
        if ($meta->description !== null && $meta->description !== '') {
            $lines[] = '- Description: ' . $meta->description;
        }

        $cols = [];
        foreach ($meta->fields as $f) {
            $marker = '';
            if ($f->isPrimary) $marker .= ' (pk)';
            if ($f->isFk()) $marker .= ' (fk → ' . $f->fkEntity . ')';
            if ($f->enumValues !== null && $f->enumValues !== []) $marker .= ' (enum)';
            $cols[] = "`{$f->name}`{$marker}";
        }
        if ($cols !== []) {
            $lines[] = '- Columns: ' . implode(', ', $cols);
        }

        if ($meta->readRoles !== [] || $meta->writeRoles !== []) {
            $roles = [];
            if ($meta->readRoles !== []) $roles[] = 'read = ' . implode('/', $meta->readRoles);
            if ($meta->writeRoles !== []) $roles[] = 'write = ' . implode('/', $meta->writeRoles);
            $lines[] = '- Roles: ' . implode(', ', $roles);
        }

        if ($meta->uniqueConstraints !== []) {
            $unique = array_map(
                static fn(array $cols): string => '(' . implode(', ', $cols) . ')',
                $meta->uniqueConstraints
            );
            $lines[] = '- Unique: ' . implode(', ', $unique);
        }

        $entityMasks = [];
        foreach ($masks as $name => $mask) {
            if (str_starts_with($name, $table . '.') || $name === $table) {
                $entityMasks[$name] = $mask;
            }
        }
        if ($entityMasks !== []) {
            $lines[] = '- Endpoints:';
            foreach ($entityMasks as $name => $mask) {
                $desc = is_string($mask['description'] ?? null) ? ' — ' . $mask['description'] : '';
                $mode = is_string($mask['writable_mode'] ?? null) ? $mask['writable_mode'] : '';
                if ($mode === 'append_only') {
                    $desc .= ' *(append-only)*';
                } elseif ($mode === 'no_delete') {
                    $desc .= ' *(no-delete)*';
                }
                $lines[] = "  - `{$name}`{$desc}";
            }
        }

        return implode("\n", $lines) . "\n";
    }

    /**
     * @param array<string, EntityMeta> $entities
     * @param array<string, array<string, mixed>> $masks
     * @return array<string, array<string, mixed>>
     */
    private static function collectStandaloneMasks(array $entities, array $masks): array
    {
        $entityTables = [];
        foreach ($entities as $meta) {
            $entityTables[$meta->table] = true;
        }
        $standalone = [];
        foreach ($masks as $name => $mask) {
            $dot = strpos($name, '.');
            $table = $dot === false ? $name : substr($name, 0, $dot);
            if (!isset($entityTables[$table])) {
                $standalone[$name] = $mask;
            }
        }
        return $standalone;
    }

    private static function introBlurb(int $entityCount, int $writableCount, int $softCount, int $standaloneCount, int $commandCount): string
    {
        $bits = ["{$entityCount} introspected entities ({$writableCount} writable, {$softCount} soft-deletable)"];
        if ($standaloneCount > 0) $bits[] = "{$standaloneCount} hand-written masks";
        if ($commandCount > 0) $bits[] = "{$commandCount} custom commands";
        return "Live overview of this Shape backend. Surface area: " . implode(', ', $bits) . ".";
    }

    private static function callingGuide(string $authDriver): string
    {
        $auth = $authDriver === 'jwt'
            ? "**Auth:** JWT. Call the `auth_token` tool (or POST `op:'auth.token'`) to obtain an access token; pass it as `Authorization: Bearer <token>` for subsequent calls."
            : "**Auth:** none configured. Calls are unauthenticated.";

        return <<<MD
- **Read** an entity: call the `<table>_list`, `<table>_get`, `<table>_getMany`, or `<table>_by<FK>` tool.
- **Write** an entity: call the `<table>_mutate` tool with `action: 'insert' | 'update' | 'delete'` and either `data` or `filter`.
- **Errors** carry `expected` + `example` + `hint` — when a call fails, the response tells you exactly what it should have looked like; correct and retry once, do not re-fetch the manifest.
- **Soft-delete tables** automatically filter `deleted_at IS NULL` on reads. DELETE on those tables sets `deleted_at` instead of removing the row. To restore: `update` with `data: { deleted_at: null }`.

{$auth}
MD;
    }

    /** @param list<string> $softDeleteTables */
    private static function agentNotes(array $softDeleteTables): string
    {
        $lines = [
            '- All tools enforce role-based authorization at runtime — a tool you can list is one your role can call.',
            '- Mask names are stable across versions; rely on them for orchestration. Tool names mirror mask names with `.` replaced by `_`.',
            '- Reads return rows in column-major (`columns` + `rows`) format unless the tool description says otherwise.',
            '- Writes return `{ ref, action, table, affected }` per mutation.',
            '- Errors carry `expected` / `example` / `hint` — read them and retry once. **Do not** re-fetch this README or the manifest after a parameter error.',
        ];
        if ($softDeleteTables !== []) {
            $lines[] = '- Soft-deletable tables: ' . implode(', ', $softDeleteTables) . '.';
        }
        $lines[] = '';
        $lines[] = '## Framework reference';
        $lines[] = '';
        $lines[] = 'This README describes *this specific backend*. For framework-level documentation — Shape conventions, error envelope shape, MCP protocol, soft-delete semantics, common mistakes — read **`AGENTS.md`** in the Shape repository. It is the single canonical entry point for agents working with Shape and stays under 5k tokens by design.';
        return implode("\n", $lines) . "\n";
    }

    private static function commandToolName(string $command): string
    {
        $name = str_replace('.', '_', $command);
        return preg_replace('/[^A-Za-z0-9_-]/', '_', $name) ?? $name;
    }
}
