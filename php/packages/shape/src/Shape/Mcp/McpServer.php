<?php

declare(strict_types=1);

namespace Shape\Mcp;

use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\BatchExecutor;
use ShapeRuntime\Core\CommandExecutor;
use ShapeRuntime\Core\Database;
use ShapeRuntime\Core\MaskRegistry;
use ShapeRuntime\Core\ValidatorRegistry;
use Shape\Generator\LlmToolGenerator;
use Shape\Generator\ManifestGenerator;
use Throwable;

/**
 * MCP (Model Context Protocol) server for a Shape backend.
 *
 * Transport-agnostic: takes a JSON-RPC message, returns a JSON-RPC response.
 * Wrap with StdioTransport (Claude Desktop / local) or HttpTransport (SSE
 * for hosted backends).
 *
 * Protocol surface:
 *   - initialize / initialized      ← handshake
 *   - tools/list                    ← derived from LlmToolGenerator
 *   - tools/call                    ← dispatched into BatchExecutor / CommandExecutor
 *   - resources/list                ← shape://manifest, shape://README.md, shape://entities/{table}
 *   - resources/templates/list      ← URI templates for parameterized resources
 *   - resources/read                ← serves the resource content
 *   - ping                          ← keepalive
 *
 * The differentiator is the resource set: every other MCP server today exposes
 * tools and lets the LLM guess. Shape exposes a narrated README plus the full
 * machine-readable manifest, so an agent gets the entire backend's surface
 * area as context with one click — before its first tool call.
 */
final class McpServer
{
    private const PROTOCOL_VERSION = '2024-11-05';

    /** @var array<string, array{mask:string, kind:string}> tool name → mask metadata */
    private array $toolIndex = [];

    /**
     * @param array<string, mixed> $config
     * @param array<string, mixed> $authContext  Default auth claims for tool calls (stdio defaults to admin)
     */
    public function __construct(
        private readonly array $config,
        private readonly string $serviceName = 'shape',
        private array $authContext = ['role' => 'admin'],
    ) {
        $this->indexTools();
    }

    /**
     * Override the auth context (used by HttpTransport after JwtGuard authorization).
     *
     * @param array<string, mixed> $authContext
     */
    public function setAuthContext(array $authContext): void
    {
        $this->authContext = $authContext;
    }

    /**
     * Handle a single JSON-RPC message. Returns the response object, or null
     * for notifications (no response expected by the client).
     *
     * @param array<string, mixed> $message
     * @return array<string, mixed>|null
     */
    public function handle(array $message): ?array
    {
        $id = $message['id'] ?? null;
        $method = (string)($message['method'] ?? '');
        $params = is_array($message['params'] ?? null) ? $message['params'] : [];

        // Notifications have no `id` and expect no response.
        $isNotification = !array_key_exists('id', $message);

        try {
            $result = match ($method) {
                'initialize'                  => $this->handleInitialize($params),
                'initialized', 'notifications/initialized' => null,
                'ping'                        => (object)[],
                'tools/list'                  => $this->handleToolsList(),
                'tools/call'                  => $this->handleToolsCall($params),
                'resources/list'              => $this->handleResourcesList(),
                'resources/templates/list'    => $this->handleResourceTemplatesList(),
                'resources/read'              => $this->handleResourcesRead($params),
                default                       => throw new McpException(-32601, "Method not found: {$method}"),
            };

            if ($isNotification) {
                return null;
            }

            return [
                'jsonrpc' => '2.0',
                'id' => $id,
                'result' => $result,
            ];
        } catch (McpException $e) {
            if ($isNotification) {
                return null;
            }
            return [
                'jsonrpc' => '2.0',
                'id' => $id,
                'error' => [
                    'code' => $e->mcpCode(),
                    'message' => $e->getMessage(),
                ],
            ];
        } catch (Throwable $e) {
            if ($isNotification) {
                return null;
            }
            return [
                'jsonrpc' => '2.0',
                'id' => $id,
                'error' => [
                    'code' => -32603,
                    'message' => 'Internal error: ' . $e->getMessage(),
                ],
            ];
        }
    }

    // ── Handshake ─────────────────────────────────────────────────────

    /** @param array<string, mixed> $params */
    private function handleInitialize(array $params): array
    {
        return [
            'protocolVersion' => self::PROTOCOL_VERSION,
            'capabilities' => [
                'tools' => (object)[],
                'resources' => ['subscribe' => false, 'listChanged' => false],
            ],
            'serverInfo' => [
                'name' => 'shape-mcp',
                'title' => 'Shape Backend (' . $this->serviceName . ')',
                'version' => '1.0.0',
            ],
        ];
    }

    // ── Tools ─────────────────────────────────────────────────────────

    private function handleToolsList(): array
    {
        $entities = $this->config['_shape']['entities'] ?? [];
        $masks = $this->config['masks'] ?? [];
        $commands = $this->config['commands'] ?? [];

        $tools = (new LlmToolGenerator())->generateAnthropic($entities, $masks, $commands);

        // Filter to tools the current role can actually call. The agent never
        // sees forbidden tools, so it never wastes tokens trying them.
        // Also rename Anthropic-style `input_schema` → MCP-spec `inputSchema`
        // (https://spec.modelcontextprotocol.io/specification/server/tools/).
        // Without this, Claude Desktop / claude.ai web / ChatGPT all show
        // "no tools available" because they look for inputSchema specifically.
        $role = isset($this->authContext['role']) ? (string)$this->authContext['role'] : '';
        $visible = [];
        foreach ($tools as $tool) {
            if (! $this->isToolVisible((string)$tool['name'], $role)) {
                continue;
            }
            if (isset($tool['input_schema']) && ! isset($tool['inputSchema'])) {
                $tool['inputSchema'] = $tool['input_schema'];
                unset($tool['input_schema']);
            }
            $visible[] = $tool;
        }

        return ['tools' => $visible];
    }

    /** @param array<string, mixed> $params */
    private function handleToolsCall(array $params): array
    {
        $name = (string)($params['name'] ?? '');
        $arguments = is_array($params['arguments'] ?? null) ? $params['arguments'] : [];

        if (!isset($this->toolIndex[$name])) {
            return $this->errorResult("Unknown tool '{$name}'. Use tools/list to see available tools.");
        }

        $entry = $this->toolIndex[$name];
        try {
            $payload = match ($entry['kind']) {
                'read'    => $this->callReadMask($entry['mask'], $arguments),
                'mutate'  => $this->callMutate($entry['mask'], $arguments),
                'command' => $this->callCommand($entry['mask'], $arguments),
                default   => throw new McpException(-32602, 'Unknown tool kind: ' . $entry['kind']),
            };
            return $this->successResult($payload);
        } catch (ApiException $e) {
            // Pass Shape's self-documenting error envelope through as the
            // tool's error content, so the agent gets `expected` / `example` /
            // `hint` in the same response — no manifest re-fetch required.
            $envelope = ['code' => $e->apiCode(), 'message' => $e->getMessage()] + $e->details();
            return $this->errorResult(json_encode($envelope, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT));
        }
    }

    /** @param array<string, mixed> $arguments */
    private function callReadMask(string $maskName, array $arguments): array
    {
        $executor = $this->buildBatchExecutor();
        $result = $executor->execute(
            [['ref' => 'mcp', 'sql' => $maskName, 'params' => (object)$arguments === (object)[] ? new \stdClass() : $arguments]],
            $this->authContext,
        );
        $first = $result['results'][0] ?? null;
        if ($first === null) {
            return ['rows' => [], 'count' => 0];
        }
        return [
            'columns' => $first['meta']['columns'] ?? [],
            'rows' => $first['rows'] ?? [],
            'count' => $first['count'] ?? 0,
        ];
    }

    /** @param array<string, mixed> $arguments */
    private function callMutate(string $table, array $arguments): array
    {
        $action = (string)($arguments['action'] ?? '');
        $mutation = ['ref' => 'mcp', 'table' => $table, 'action' => $action];
        if (isset($arguments['data']) && is_array($arguments['data'])) {
            $mutation['data'] = $arguments['data'];
        }
        if (isset($arguments['filter']) && is_array($arguments['filter'])) {
            $mutation['filter'] = $arguments['filter'];
        }

        $executor = $this->buildBatchExecutor();
        $result = $executor->executeMutations([$mutation], $this->authContext, false);
        return $result['results'][0] ?? ['action' => $action, 'table' => $table, 'affected' => 0];
    }

    /** @param array<string, mixed> $arguments */
    private function callCommand(string $commandName, array $arguments): array
    {
        $database = new Database($this->config['db'] ?? []);
        $registry = new MaskRegistry($this->config['masks'] ?? []);
        $validators = new ValidatorRegistry($this->config['validators'] ?? []);
        $batch = new BatchExecutor($database, $registry, $validators);
        if (is_array($this->config['tenancy'] ?? null) || is_array($this->config['scopes'] ?? null)) {
            $batch->setScoping(
                is_array($this->config['tenancy'] ?? null) ? $this->config['tenancy'] : [],
                is_array($this->config['scopes'] ?? null) ? $this->config['scopes'] : [],
            );
        }
        $executor = new CommandExecutor($database, $batch, $this->config['commands'] ?? [], $this->config['command_roles'] ?? []);
        return $executor->execute($commandName, $arguments, $this->authContext);
    }

    // ── Resources ─────────────────────────────────────────────────────

    private function handleResourcesList(): array
    {
        $entities = $this->config['_shape']['entities'] ?? [];
        $resources = [
            [
                'uri' => 'shape://README.md',
                'name' => 'Backend overview',
                'description' => 'Narrated, human-readable guide to every entity, mask, command, and auth flow in this backend. Attach as context once per session — it replaces a manifest fetch and a half-dozen exploratory tool calls.',
                'mimeType' => 'text/markdown',
            ],
            [
                'uri' => 'shape://manifest',
                'name' => 'Machine-readable manifest',
                'description' => 'Full Shape manifest: entities, fields, masks, roles, soft-delete columns, descriptions. Same shape as POST {op: "manifest"}.',
                'mimeType' => 'application/json',
            ],
            [
                'uri' => 'shape://tools',
                'name' => 'Tool list as JSON',
                'description' => 'The same tools as tools/list, in Anthropic-tool-call format. Useful if the agent needs to inspect tool schemas as data rather than as MCP tools.',
                'mimeType' => 'application/json',
            ],
        ];

        // One resource per entity for drill-down browsing.
        foreach ($entities as $meta) {
            $resources[] = [
                'uri' => 'shape://entities/' . $meta->table,
                'name' => "Entity: {$meta->table}",
                'description' => "Field-level detail, FKs, validators, and roles for `{$meta->table}`.",
                'mimeType' => 'application/json',
            ];
        }

        return ['resources' => $resources];
    }

    private function handleResourceTemplatesList(): array
    {
        return [
            'resourceTemplates' => [
                [
                    'uriTemplate' => 'shape://entities/{table}',
                    'name' => 'Entity drill-down',
                    'description' => 'Per-entity field, FK, validator, and role detail.',
                    'mimeType' => 'application/json',
                ],
            ],
        ];
    }

    /** @param array<string, mixed> $params */
    private function handleResourcesRead(array $params): array
    {
        $uri = (string)($params['uri'] ?? '');
        $allEntities = $this->config['_shape']['entities'] ?? [];
        $allMasks = $this->config['masks'] ?? [];
        $allCommands = $this->config['commands'] ?? [];

        // SECURITY-REVIEW M8: the resource surface must be role-filtered the same
        // way tools/list is. Otherwise a low-priv (or, on driver:none, anonymous)
        // caller reads the full admin/secret-table map from shape://manifest /
        // shape://tools / shape://entities and defeats the mask role gate.
        $role = isset($this->authContext['role']) ? (string)$this->authContext['role'] : '';
        $masks = $this->visibleMasks($allMasks, $role);
        $commands = [];
        foreach ($allCommands as $cmdName => $handler) {
            if ($this->isCommandAllowed((string)$cmdName, $role)) {
                $commands[$cmdName] = $handler;
            }
        }
        $entities = array_filter($allEntities, function ($meta) use ($masks): bool {
            foreach (array_keys($masks) as $m) {
                if ($m === $meta->table || str_starts_with((string)$m, $meta->table . '.')) {
                    return true;
                }
            }
            return false;
        });

        if ($uri === 'shape://README.md') {
            $text = ReadmeBuilder::build($entities, $masks, $commands, $this->config['auth'] ?? [], $this->serviceName);
            return $this->resourceText($uri, 'text/markdown', $text);
        }

        if ($uri === 'shape://manifest') {
            $uploads = is_array($this->config['uploads'] ?? null) ? $this->config['uploads'] : [];
            $manifest = (new ManifestGenerator())->generate($entities, $masks, $commands, $uploads);
            return $this->resourceText($uri, 'application/json', json_encode($manifest, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT));
        }

        if ($uri === 'shape://tools') {
            $tools = (new LlmToolGenerator())->generateAnthropic($entities, $masks, $commands);
            return $this->resourceText($uri, 'application/json', json_encode(['tools' => $tools], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT));
        }

        if (str_starts_with($uri, 'shape://entities/')) {
            $tableName = substr($uri, strlen('shape://entities/'));
            foreach ($entities as $meta) {
                if ($meta->table === $tableName) {
                    $payload = $this->describeEntity($meta, $masks);
                    return $this->resourceText($uri, 'application/json', json_encode($payload, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT));
                }
            }
            throw new McpException(-32602, "Unknown entity: {$tableName}");
        }

        throw new McpException(-32602, "Unknown resource URI: {$uri}");
    }

    /**
     * @param array<string, array<string, mixed>> $masks
     * @return array<string, mixed>
     */
    private function describeEntity(\Shape\Metadata\EntityMeta $meta, array $masks): array
    {
        // Manual-strategy PKs (framework-allocated MAX+1) present as
        // auto-assigned — clients must not send them.
        $writableMask = $masks[$meta->table] ?? null;
        $manualPk = is_array($writableMask)
            && ($writableMask['pk_strategy'] ?? null) === 'manual'
            && is_string($writableMask['pk'] ?? null)
            ? $writableMask['pk']
            : null;

        $fields = [];
        foreach ($meta->fields as $f) {
            $field = [
                'name' => $f->name,
                'type' => $f->dbType,
                'nullable' => $f->nullable,
                'primary' => $f->isPrimary,
                'autoIncrement' => $f->isAutoIncrement || $f->name === $manualPk,
                'fk' => $f->fkEntity,
                'fkColumn' => $f->fkColumn,
                'validators' => $f->validators,
                'hasDefault' => $f->hasDefault,
                'default' => $f->default,
            ];
            if ($f->enumValues !== null && $f->enumValues !== []) {
                $field['enum'] = $f->enumValues;
            }
            if ($f->description !== null && $f->description !== '') {
                $field['description'] = $f->description;
            }
            $fields[] = $field;
        }
        $entityMasks = [];
        foreach ($masks as $name => $mask) {
            if (str_starts_with($name, $meta->table . '.') || $name === $meta->table) {
                $entityMasks[$name] = [
                    'params' => array_keys($mask['params'] ?? []),
                    'writable' => (bool)($mask['writable'] ?? false),
                    'description' => $mask['description'] ?? null,
                ];
                if (isset($mask['writable_mode']) && is_string($mask['writable_mode']) && $mask['writable_mode'] !== '') {
                    $entityMasks[$name]['writableMode'] = $mask['writable_mode'];
                }
            }
        }
        $writableMode = $meta->effectiveWritableMode();

        $entity = [
            'table' => $meta->table,
            'class' => $meta->class,
            'writable' => $writableMode !== 'none',
            'description' => $meta->description,
            'softDelete' => $meta->softDeleteColumn,
            'roles' => ['read' => $meta->readRoles, 'write' => $meta->writeRoles],
            'uniqueConstraints' => $meta->uniqueConstraints,
            'fields' => $fields,
            'masks' => $entityMasks,
        ];
        if ($writableMode !== 'full' && $writableMode !== 'none') {
            $entity['writableMode'] = $writableMode;
        }

        return $entity;
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private function indexTools(): void
    {
        $masks = $this->config['masks'] ?? [];
        $commands = $this->config['commands'] ?? [];

        foreach ($masks as $name => $mask) {
            if ((bool)($mask['writable'] ?? false)) {
                $tool = $this->toolName($name) . '_mutate';
                $this->toolIndex[$tool] = ['mask' => $name, 'kind' => 'mutate'];
            } else {
                $tool = $this->toolName($name);
                $this->toolIndex[$tool] = ['mask' => $name, 'kind' => 'read'];
            }
        }

        foreach (array_keys($commands) as $cmdName) {
            $tool = $this->toolName((string)$cmdName);
            $this->toolIndex[$tool] = ['mask' => (string)$cmdName, 'kind' => 'command'];
        }
    }

    private function toolName(string $maskName): string
    {
        $name = str_replace('.', '_', $maskName);
        return preg_replace('/[^A-Za-z0-9_-]/', '_', $name) ?? $name;
    }

    private function isToolVisible(string $toolName, string $role): bool
    {
        $entry = $this->toolIndex[$toolName] ?? null;
        if ($entry === null) {
            return false;
        }
        if ($entry['kind'] === 'command') {
            return $this->isCommandAllowed((string)$entry['mask'], $role);
        }
        $masks = $this->config['masks'] ?? [];
        $mask = $masks[$entry['mask']] ?? null;
        if ($mask === null) {
            return true;
        }
        $allowed = $mask['roles'] ?? [];
        if ($allowed === []) {
            return true;
        }
        return $role !== '' && in_array($role, $allowed, true);
    }

    /**
     * SECURITY-REVIEW M9: a command is visible/invocable only when the caller's
     * role satisfies its `command_roles` entry. No entry = unrestricted (mirrors
     * the mask-role default).
     */
    private function isCommandAllowed(string $commandName, string $role): bool
    {
        $roles = $this->config['command_roles'][$commandName] ?? null;
        if (!is_array($roles) || $roles === []) {
            return true;
        }
        return $role !== '' && in_array($role, $roles, true);
    }

    /**
     * The subset of masks the given role may see — same gate as
     * MaskRegistry::resolve (empty/absent roles = open). Role-filters the MCP
     * resource surface (SECURITY-REVIEW M8).
     *
     * @param array<string, array<string, mixed>> $masks
     * @return array<string, array<string, mixed>>
     */
    private function visibleMasks(array $masks, string $role): array
    {
        $out = [];
        foreach ($masks as $name => $mask) {
            $allowed = is_array($mask['roles'] ?? null) ? $mask['roles'] : [];
            if ($allowed === [] || ($role !== '' && in_array($role, $allowed, true))) {
                $out[$name] = $mask;
            }
        }
        return $out;
    }

    private function buildBatchExecutor(): BatchExecutor
    {
        $database = new Database($this->config['db'] ?? []);
        $registry = new MaskRegistry($this->config['masks'] ?? []);
        $validators = new ValidatorRegistry($this->config['validators'] ?? []);
        $executor = new BatchExecutor($database, $registry, $validators);
        if (is_array($this->config['tenancy'] ?? null) || is_array($this->config['scopes'] ?? null)) {
            $executor->setScoping(
                is_array($this->config['tenancy'] ?? null) ? $this->config['tenancy'] : [],
                is_array($this->config['scopes'] ?? null) ? $this->config['scopes'] : [],
            );
        }
        return $executor;
    }

    /** @param array<string, mixed>|string $payload */
    private function successResult(array|string $payload): array
    {
        $text = is_string($payload)
            ? $payload
            : json_encode($payload, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT);
        return [
            'content' => [['type' => 'text', 'text' => $text ?: '']],
            'isError' => false,
        ];
    }

    private function errorResult(string $text): array
    {
        return [
            'content' => [['type' => 'text', 'text' => $text]],
            'isError' => true,
        ];
    }

    private function resourceText(string $uri, string $mime, string|false $text): array
    {
        return [
            'contents' => [[
                'uri' => $uri,
                'mimeType' => $mime,
                'text' => is_string($text) ? $text : '',
            ]],
        ];
    }
}
