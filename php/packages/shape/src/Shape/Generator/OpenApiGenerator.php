<?php

declare(strict_types=1);

namespace Shape\Generator;

use Shape\Metadata\EntityMeta;
use Shape\Metadata\FieldMeta;

final class OpenApiGenerator
{
    /**
     * @param array<string, EntityMeta> $entities
     * @param array<string, array<string, mixed>> $masks
     * @param list<string> $commands
     * @param array<string, mixed> $authConfig
     * @param array<string, mixed> $openapiConfig OpenAPI-specific knobs:
     *   - `reads`: 'get' | 'post' | 'both' (default 'get'). Controls
     *     whether the generator emits GET paths, POST envelopes, or both
     *     for read masks. Mutations / auth / commands stay on POST
     *     regardless. Default is 'get' because the GET surface is now
     *     headlined and the duplicate POST envelopes are pure noise for
     *     spec consumers. Set to 'post' for back-compat with v1 spec
     *     consumers; 'both' keeps both shapes for transition periods.
     */
    public function generate(
        array $entities,
        array $masks,
        array $commands,
        array $authConfig = [],
        string $serviceName = 'shape',
        array $openapiConfig = [],
    ): array {
        $reads = $openapiConfig['reads'] ?? 'get';
        if (!in_array($reads, ['get', 'post', 'both'], true)) {
            $reads = 'get';
        }
        $emitReadsAsGet  = $reads === 'get'  || $reads === 'both';
        $emitReadsAsPost = $reads === 'post' || $reads === 'both';
        [$tags, $tagGroups] = $this->buildTagsAndGroups($entities, $masks, $commands);

        $spec = [
            'openapi' => '3.1.0',
            'info' => [
                'title' => ucfirst($serviceName) . ' API',
                'version' => '1.0.0',
                'description' => "Auto-generated OpenAPI spec for {$serviceName}. Three call shapes are documented: (1) GET /api.php/<entity>[/<id>] — REST/OData reads, ETag/Cache-Control friendly; (2) GET /api.php/_/<mask>?<params> — generic mask reads; (3) POST /api.php with JSON body — batch queries, mutations, commands.\n\n**Tip for Postman / Insomnia / Bruno users:** switch the collection's grouping from \"Folder\" to \"Tag\" so operations cluster by entity instead of by path. Stoplight Studio and Redoc honor `x-tagGroups` automatically.",
            ],
            'tags' => $tags,
            'x-tagGroups' => $tagGroups,
            'paths' => [],
            'components' => [
                'securitySchemes' => [
                    'bearerAuth' => [
                        'type' => 'http',
                        'scheme' => 'bearer',
                        'bearerFormat' => 'JWT',
                    ],
                ],
                'schemas' => $this->buildSchemas($entities),
            ],
        ];

        // Auth paths (no Bearer needed)
        $spec['paths']['/api.php?op=auth.token'] = [
            'post' => [
                'operationId' => 'auth.token',
                'summary' => 'Login and get JWT token pair',
                'tags' => ['Auth'],
                'requestBody' => $this->jsonBody([
                    'type' => 'object',
                    'required' => ['op', 'payload'],
                    'properties' => [
                        'op' => ['type' => 'string', 'enum' => ['auth.token']],
                        'payload' => [
                            'type' => 'object',
                            'required' => ['email', 'password'],
                            'properties' => [
                                'email' => ['type' => 'string', 'format' => 'email'],
                                'password' => ['type' => 'string'],
                            ],
                        ],
                    ],
                ]),
                'responses' => [
                    '200' => $this->jsonResponse('Token pair', [
                        'type' => 'object',
                        'properties' => [
                            'access_token' => ['type' => 'string'],
                            'token_type' => ['type' => 'string', 'example' => 'Bearer'],
                            'expires_in' => ['type' => 'integer'],
                            'refresh_token' => ['type' => 'string'],
                            'user' => [
                                'type' => 'object',
                                'properties' => [
                                    'id' => ['type' => 'integer'],
                                    'email' => ['type' => 'string'],
                                    'display_name' => ['type' => 'string'],
                                    'role' => ['type' => 'string'],
                                ],
                            ],
                        ],
                    ]),
                    '400' => $this->errorResponse('Invalid credentials'),
                ],
            ],
        ];

        $spec['paths']['/api.php?op=auth.refresh'] = [
            'post' => [
                'operationId' => 'auth.refresh',
                'summary' => 'Refresh token pair',
                'tags' => ['Auth'],
                'requestBody' => $this->jsonBody([
                    'type' => 'object',
                    'required' => ['op', 'payload'],
                    'properties' => [
                        'op' => ['type' => 'string', 'enum' => ['auth.refresh']],
                        'payload' => [
                            'type' => 'object',
                            'required' => ['refresh_token'],
                            'properties' => [
                                'refresh_token' => ['type' => 'string'],
                            ],
                        ],
                    ],
                ]),
                'responses' => [
                    '200' => $this->jsonResponse('New token pair', ['type' => 'object']),
                    '400' => $this->errorResponse('Invalid or expired refresh token'),
                ],
            ],
        ];

        // Query paths per entity
        foreach ($entities as $meta) {
            $table = $meta->table;
            $entityName = ucfirst($table);
            $rowSchema = ['$ref' => '#/components/schemas/' . $entityName];
            $pk = $meta->primaryKey();

            // ── REST/OData GET surface ────────────────────────────────────
            // Emit GET paths so importers (Postman, Swagger UI, openapi-generator)
            // see the cacheable read paths. Headlined in AGENTS.md but
            // previously absent from the spec — readers had only the heavyweight
            // POST envelopes to work with.
            if ($emitReadsAsGet) {
                $this->addRestGetPaths($spec, $meta, $rowSchema, $masks);
            }

            // ── POST read envelopes ──────────────────────────────────────
            // Suppressible via openapi.reads = 'get'. Mutations and commands
            // stay on POST regardless — they always need a body. The toggle
            // only governs the duplicate-of-GET read surface.
            if ($emitReadsAsPost) {

            // list
            $spec['paths']["/api.php?op=query&ref={$table}.list"] = [
                'post' => [
                    'operationId' => "{$table}.list",
                    'summary' => self::summary($masks, "{$table}.list", "List all {$table}"),
                    'tags' => [$entityName],
                    'security' => [['bearerAuth' => []]],
                    'requestBody' => $this->queryBody($table . '.list', []),
                    'responses' => [
                        '200' => $this->queryResponse("List of {$table}", $rowSchema),
                        '401' => $this->errorResponse('Authentication required'),
                        '403' => $this->errorResponse('Forbidden'),
                    ],
                ],
            ];

            // get
            if ($pk !== null) {
                $spec['paths']["/api.php?op=query&ref={$table}.get"] = [
                    'post' => [
                        'operationId' => "{$table}.get",
                        'summary' => self::summary($masks, "{$table}.get", "Get {$table} by {$pk->name}"),
                        'tags' => [$entityName],
                        'security' => [['bearerAuth' => []]],
                        'requestBody' => $this->queryBody($table . '.get', [
                            $pk->name => $this->fieldToJsonSchema($pk),
                        ]),
                        'responses' => [
                            '200' => $this->queryResponse("Single {$table}", $rowSchema),
                            '401' => $this->errorResponse('Authentication required'),
                        ],
                    ],
                ];
            }

            // byFK masks
            foreach ($meta->fkFields() as $fk) {
                $maskName = $table . '.by' . self::pascalCase($fk->name);
                $spec['paths']["/api.php?op=query&ref={$maskName}"] = [
                    'post' => [
                        'operationId' => $maskName,
                        'summary' => self::summary($masks, $maskName, "Filter {$table} by {$fk->name}"),
                        'tags' => [$entityName],
                        'security' => [['bearerAuth' => []]],
                        'requestBody' => $this->queryBody($maskName, [
                            $fk->name => $this->fieldToJsonSchema($fk),
                        ]),
                        'responses' => [
                            '200' => $this->queryResponse("{$entityName} filtered by {$fk->name}", $rowSchema),
                            '401' => $this->errorResponse('Authentication required'),
                        ],
                    ],
                ];
            }

            } // end if ($emitReadsAsPost)

            // Mutations (insert, update, delete)
            if ($meta->effectiveWritableMode() !== 'none') {
                // Manual-strategy PKs (framework-allocated MAX+1) render as
                // auto-assigned, same as identity columns — the writable
                // mask carries the resolved strategy.
                $writableMask = $masks[$table] ?? null;
                $manualPk = is_array($writableMask)
                    && ($writableMask['pk_strategy'] ?? null) === 'manual'
                    && is_string($writableMask['pk'] ?? null)
                    ? $writableMask['pk']
                    : null;

                // Row-scope columns (ROW-SCOPES §2.5): optional in insert
                // (server-stamped from the claim; required for multi —
                // noted), excluded from update (a scoped move is rejected).
                $rowScopes = is_array($writableMask['row_scopes'] ?? null) ? $writableMask['row_scopes'] : [];

                $insertProps = [];
                $insertRequired = [];
                $updateProps = [];
                foreach ($meta->fields as $field) {
                    if ($field->isAutoIncrement || $field->name === $manualPk) {
                        continue;
                    }
                    $schema = $this->fieldToJsonSchema($field);
                    if ($field->validators !== []) {
                        $schema['x-validators'] = $field->validators;
                    }
                    $scopeMeta = $rowScopes[$field->name] ?? null;
                    if ($scopeMeta !== null) {
                        $multi = (bool)(is_array($scopeMeta) ? ($scopeMeta['multi'] ?? false) : false);
                        $schema['description'] = trim((string)($schema['description'] ?? '') . ' ' . ($multi
                            ? "Row scope (required): must be one of the caller's {$field->name} memberships."
                            : "Row scope: stamped from the caller's {$field->name} claim when omitted."));
                        $insertProps[$field->name] = $schema;
                        continue; // never in update, never in insertRequired
                    }
                    $insertProps[$field->name] = $schema;
                    $updateProps[$field->name] = $schema;
                    if (!$field->nullable && !$field->hasDefault && !$field->isPrimary) {
                        $insertRequired[] = $field->name;
                    }
                }

                // insert
                $insertOp = [
                        'operationId' => "{$table}.insert",
                        'summary' => "Insert new {$table}",
                        'tags' => [$entityName],
                        'security' => [['bearerAuth' => []]],
                        'requestBody' => $this->mutationBody('insert', $table, $insertProps, $insertRequired),
                        'responses' => [
                            '200' => $this->mutationResponse('Row inserted'),
                            '400' => $this->errorResponse('Validation error'),
                            '401' => $this->errorResponse('Authentication required'),
                            '403' => $this->errorResponse('Not writable'),
                        ],
                ];
                $scopeSentences = self::rowScopeSentences($rowScopes);
                if ($scopeSentences !== '') {
                    $insertOp['description'] = $scopeSentences;
                }
                $spec['paths']["/api.php?op=mutation&action=insert&table={$table}"] = ['post' => $insertOp];

                // update
                if ($pk !== null) {
                    $spec['paths']["/api.php?op=mutation&action=update&table={$table}"] = [
                        'post' => [
                            'operationId' => "{$table}.update",
                            'summary' => "Update {$table}",
                            'tags' => [$entityName],
                            'security' => [['bearerAuth' => []]],
                            'requestBody' => $this->mutationBody('update', $table, $updateProps, [], [
                                $pk->name => $this->fieldToJsonSchema($pk),
                            ]),
                            'responses' => [
                                '200' => $this->mutationResponse('Row updated'),
                                '400' => $this->errorResponse('Validation error'),
                            ],
                        ],
                    ];

                    // delete
                    $spec['paths']["/api.php?op=mutation&action=delete&table={$table}"] = [
                        'post' => [
                            'operationId' => "{$table}.delete",
                            'summary' => "Delete {$table}",
                            'tags' => [$entityName],
                            'security' => [['bearerAuth' => []]],
                            'requestBody' => $this->mutationBody('delete', $table, [], [], [
                                $pk->name => $this->fieldToJsonSchema($pk),
                            ]),
                            'responses' => [
                                '200' => $this->mutationResponse('Row deleted'),
                            ],
                        ],
                    ];
                }
            }
        }

        // Commands
        foreach ($commands as $cmd) {
            $spec['paths']["/api.php?op=command&name={$cmd}"] = [
                'post' => [
                    'operationId' => "command.{$cmd}",
                    'summary' => "Execute command: {$cmd}",
                    'tags' => ['Commands'],
                    'security' => [['bearerAuth' => []]],
                    'requestBody' => $this->jsonBody([
                        'type' => 'object',
                        'required' => ['op', 'payload'],
                        'properties' => [
                            'op' => ['type' => 'string', 'enum' => ['command']],
                            'payload' => [
                                'type' => 'object',
                                'required' => ['name'],
                                'properties' => [
                                    'name' => ['type' => 'string', 'enum' => [$cmd]],
                                    'params' => ['type' => 'object'],
                                ],
                            ],
                        ],
                    ]),
                    'responses' => [
                        '200' => $this->jsonResponse('Command result', ['type' => 'object']),
                        '400' => $this->errorResponse('Command validation error'),
                        '404' => $this->errorResponse('Command not found'),
                    ],
                ],
            ];
        }

        // Standalone masks (not owned by entities)
        $claimedMasks = [];
        foreach ($entities as $meta) {
            $t = $meta->table;
            foreach ($masks as $name => $_) {
                if (str_starts_with($name, $t . '.') || $name === $t) {
                    $claimedMasks[$name] = true;
                }
            }
        }
        if ($emitReadsAsPost) {
            foreach ($masks as $name => $mask) {
                if (isset($claimedMasks[$name])) {
                    continue;
                }
                $params = [];
                foreach (($mask['params'] ?? []) as $pName => $pDef) {
                    $type = $pDef['type'] ?? 'string';
                    $params[$pName] = ['type' => $this->maskParamToJsonType($type)];
                }
                $spec['paths']["/api.php?op=query&ref={$name}"] = [
                    'post' => [
                        'operationId' => $name,
                        'summary' => self::summary($masks, $name, "Query: {$name}"),
                        'tags' => ['Queries'],
                        'security' => [['bearerAuth' => []]],
                        'requestBody' => $this->queryBody($name, $params),
                        'responses' => [
                            '200' => $this->queryResponse("Results for {$name}", ['type' => 'object']),
                        ],
                    ],
                ];
            }
        }

        // Generic-mask GET surface — one path per registered mask. Each entry
        // exposes the mask's declared params as query parameters so a Postman
        // import (or any code generator) renders a directly-runnable
        // GET /api.php/_/<mask>?<params> request without manual editing.
        // Mutation masks (writable === true) are skipped — GET is read-only.
        if ($emitReadsAsGet) {
            foreach ($masks as $name => $mask) {
                if (($mask['writable'] ?? false) === true) {
                    continue;
                }
                $this->addGenericMaskGetPath($spec, $name, $mask, $masks);
            }
        }

        return $spec;
    }

    /**
     * REST/OData GET surface for one entity:
     *   GET /api.php/<table>            — list, with $top/$skip/$orderby/$filter/$select/$count
     *   GET /api.php/<table>/<id>       — get-by-PK
     *
     * The OData parens form (/api.php/<table>(<id>)) is reachable at runtime
     * but skipped here — most OpenAPI consumers don't render parens-style
     * paths cleanly, and the slash form is the recommended one.
     *
     * @param array<string, mixed>                    $spec       (mutated)
     * @param array<string, mixed>                    $rowSchema  $ref to the entity schema
     * @param array<string, array<string, mixed>>    $masks
     */
    private function addRestGetPaths(array &$spec, EntityMeta $meta, array $rowSchema, array $masks): void
    {
        $table = $meta->table;
        $entityName = ucfirst($table);

        // Pick a column to use in the OData examples — first non-PK,
        // non-FK column makes for a useful $orderby/$filter sample.
        $exampleColumn = $this->exampleColumnFor($meta);

        // ── List (with OData query params + cache headers) ────────────
        if (isset($masks["{$table}.list"])) {
            $listOp = [
                    'operationId' => "{$table}.list.rest",
                    'summary' => self::summary($masks, "{$table}.list", "List {$table} (REST/OData; cacheable)"),
                    'tags' => [$entityName],
                    'security' => [['bearerAuth' => []]],
                    'parameters' => $this->odataQueryParameters($exampleColumn, $masks["{$table}.list"]['default_limit'] ?? null),
                    'responses' => [
                        '200' => $this->cacheableJsonResponse(
                            "List of {$table}",
                            ['type' => 'array', 'items' => $rowSchema]
                        ),
                        '304' => ['description' => 'Not Modified (matched If-None-Match)'],
                        '401' => $this->errorResponse('Authentication required'),
                        '403' => $this->errorResponse('Forbidden'),
                    ],
            ];
            $scopeSentences = self::rowScopeSentences(
                is_array($masks["{$table}.list"]['row_scopes'] ?? null) ? $masks["{$table}.list"]['row_scopes'] : []
            );
            if ($scopeSentences !== '') {
                $listOp['description'] = $scopeSentences;
            }
            $spec['paths']["/api.php/{$table}"] = ['get' => $listOp];
        }

        // ── Get by PK ────────────────────────────────────────────────
        $pk = $meta->primaryKey();
        if ($pk !== null && isset($masks["{$table}.get"])) {
            $spec['paths']["/api.php/{$table}/{{$pk->name}}"] = [
                'get' => [
                    'operationId' => "{$table}.get.rest",
                    'summary' => self::summary($masks, "{$table}.get", "Get {$table} by {$pk->name} (REST)"),
                    'tags' => [$entityName],
                    'security' => [['bearerAuth' => []]],
                    'parameters' => [
                        [
                            'name' => $pk->name,
                            'in' => 'path',
                            'required' => true,
                            'schema' => $this->fieldToJsonSchema($pk),
                        ],
                    ],
                    'responses' => [
                        '200' => $this->cacheableJsonResponse("Single {$table}", $rowSchema),
                        '304' => ['description' => 'Not Modified (matched If-None-Match)'],
                        '401' => $this->errorResponse('Authentication required'),
                        '404' => $this->errorResponse('Not found'),
                    ],
                ],
            ];
        }
    }

    /**
     * One generic-mask GET path: GET /api.php/_/<mask>?<params>
     *
     * Each declared mask param becomes a query parameter. Hand-written and
     * admin masks for secret tables flow through the same path — that's
     * what makes the generic mask GET the universal escape hatch.
     *
     * @param array<string, mixed>                    $spec  (mutated)
     * @param array<string, mixed>                    $mask
     * @param array<string, array<string, mixed>>    $masks
     */
    private function addGenericMaskGetPath(array &$spec, string $maskName, array $mask, array $masks): void
    {
        $parameters = [];
        foreach (($mask['params'] ?? []) as $pName => $pDef) {
            $type = (string)($pDef['type'] ?? 'string');
            $required = (bool)($pDef['required'] ?? false);
            $parameters[] = [
                'name' => $pName,
                'in' => 'query',
                'required' => $required,
                'schema' => ['type' => $this->maskParamToJsonType($type)],
            ];
        }

        $spec['paths']["/api.php/_/{$maskName}"] = [
            'get' => [
                'operationId' => "mask.{$maskName}.get",
                'summary' => self::summary($masks, $maskName, "GET via generic mask: {$maskName}"),
                'tags' => ['Generic mask GET'],
                'security' => [['bearerAuth' => []]],
                'parameters' => $parameters,
                'responses' => [
                    '200' => $this->cacheableJsonResponse(
                        "Result rows for mask {$maskName}",
                        ['type' => 'object']
                    ),
                    '304' => ['description' => 'Not Modified (matched If-None-Match)'],
                    '401' => $this->errorResponse('Authentication required'),
                    '403' => $this->errorResponse('Forbidden'),
                    '404' => $this->errorResponse('Mask not found'),
                ],
            ],
        ];
    }

    /**
     * The shared OData query-parameter set used by all REST list paths.
     * `example` keys are populated so a Postman import renders runnable
     * URLs without the user editing placeholder values.
     *
     * @return list<array<string, mixed>>
     */
    private function odataQueryParameters(?string $exampleColumn, ?int $defaultLimit): array
    {
        $orderbyExample = $exampleColumn !== null ? "{$exampleColumn} desc" : 'created_at desc';
        $filterExample  = $exampleColumn !== null ? "{$exampleColumn} eq 'value'" : "name eq 'Acme'";
        $selectExample  = $exampleColumn !== null ? "id,{$exampleColumn}" : 'id,name';

        $topSchema = ['type' => 'integer', 'maximum' => 200];
        if ($defaultLimit !== null && $defaultLimit > 0) {
            $topSchema['default'] = $defaultLimit;
        }

        return [
            [
                'name' => '$top',
                'in' => 'query',
                'description' => 'Maximum rows to return. Capped at 200 (MAX_TOP).',
                'schema' => $topSchema,
            ],
            [
                'name' => '$skip',
                'in' => 'query',
                'description' => 'Rows to skip before returning the page.',
                'schema' => ['type' => 'integer', 'minimum' => 0],
            ],
            [
                'name' => '$orderby',
                'in' => 'query',
                'description' => 'Column and direction, e.g. `created_at desc`.',
                'schema' => ['type' => 'string', 'example' => $orderbyExample],
            ],
            [
                'name' => '$filter',
                'in' => 'query',
                'description' => "OData-style filter, e.g. `{$filterExample}`.",
                'schema' => ['type' => 'string', 'example' => $filterExample],
            ],
            [
                'name' => '$select',
                'in' => 'query',
                'description' => 'Comma-separated subset of columns.',
                'schema' => ['type' => 'string', 'example' => $selectExample],
            ],
            [
                'name' => '$count',
                'in' => 'query',
                'description' => 'When true, response includes `total_count` (separate COUNT query).',
                'schema' => ['type' => 'boolean'],
            ],
        ];
    }

    /**
     * Pick the most-useful column to use in OData examples for an entity.
     * Heuristic: first non-PK, non-FK string column; fall back to first
     * non-PK column; fall back to null (the calling code substitutes a
     * generic placeholder).
     */
    private function exampleColumnFor(EntityMeta $meta): ?string
    {
        $firstNonPk = null;
        foreach ($meta->fields as $field) {
            if ($field->isPrimary) {
                continue;
            }
            if ($firstNonPk === null) {
                $firstNonPk = $field->name;
            }
            if ($field->isFk()) {
                continue;
            }
            if (in_array($field->phpType, ['string'], true)) {
                return $field->name;
            }
        }
        return $firstNonPk;
    }

    /**
     * 200 response shape for the cacheable GET surface — includes ETag and
     * Cache-Control headers in the response so consumers know the
     * conditional-request flow is supported.
     *
     * @param array<string, mixed> $schema
     * @return array<string, mixed>
     */
    private function cacheableJsonResponse(string $description, array $schema): array
    {
        return [
            'description' => $description,
            'headers' => [
                'ETag' => [
                    'description' => 'Strong validator. Send back as `If-None-Match` to get 304.',
                    'schema' => ['type' => 'string'],
                ],
                'Cache-Control' => [
                    'description' => 'Per-mask cache directive (set via mask config `cache_control`).',
                    'schema' => ['type' => 'string'],
                ],
            ],
            'content' => [
                'application/json' => ['schema' => $schema],
            ],
        ];
    }

    /**
     * Build the spec-level `tags` array (each tag with a description) and
     * the `x-tagGroups` extension (Redoc / Stoplight Studio / Postman pick
     * this up to render entity-grouped folders without manual user
     * intervention). Tag names match the per-operation tags emitted
     * elsewhere in the spec — Postman's "Group by Tag" view will collapse
     * the path-derived folders into these.
     *
     * @param array<string, EntityMeta> $entities
     * @param array<string, array<string, mixed>> $masks
     * @param list<string> $commands
     * @return array{0:list<array<string, mixed>>, 1:list<array<string, mixed>>}
     */
    private function buildTagsAndGroups(array $entities, array $masks, array $commands): array
    {
        $entityTags = [];
        $entityTagNames = [];
        foreach ($entities as $meta) {
            $tagName = ucfirst($meta->table);
            $entityTagNames[] = $tagName;

            $cols = count($meta->fields);
            $hints = [];
            $hints[] = "{$cols} column" . ($cols === 1 ? '' : 's');
            if ($meta->softDeleteColumn !== null) {
                $hints[] = 'soft-delete';
            }
            $mode = $meta->effectiveWritableMode();
            $hints[] = match ($mode) {
                'full' => 'writable',
                'append_only' => 'append-only',
                'no_delete' => 'no-delete',
                default => 'read-only',
            };
            if ($meta->auditColumns !== []) {
                $hints[] = 'audit columns';
            }
            if ($meta->uniqueConstraints !== []) {
                $hints[] = 'unique constraints';
            }

            $entityTags[] = [
                'name' => $tagName,
                'description' => (!empty($meta->description)
                    ? $meta->description . ' '
                    : "CRUD + masks for `{$meta->table}` ")
                    . '(' . implode(', ', $hints) . ').',
            ];
        }
        // Stable order so manifest diffs cleanly across deploys.
        sort($entityTagNames);
        usort($entityTags, fn(array $a, array $b): int => strcmp((string)$a['name'], (string)$b['name']));

        $systemTags = [
            ['name' => 'Auth', 'description' => 'Login, refresh, logout — local users-table JWT pathway.'],
        ];
        $systemTagNames = ['Auth'];

        // Standalone read masks live under "Queries"; the generic-mask GET
        // surface lives under "Generic mask GET". Only emit tag entries
        // when the corresponding spec entries exist.
        $hasStandaloneReads = false;
        foreach ($masks as $name => $mask) {
            if (($mask['writable'] ?? false) === true) {
                continue;
            }
            $owned = false;
            foreach ($entities as $m) {
                if (str_starts_with($name, $m->table . '.') || $name === $m->table) {
                    $owned = true;
                    break;
                }
            }
            if (!$owned) {
                $hasStandaloneReads = true;
                break;
            }
        }
        if ($hasStandaloneReads) {
            $systemTags[] = ['name' => 'Queries', 'description' => 'Hand-written read masks not owned by a specific entity.'];
            $systemTagNames[] = 'Queries';
        }

        // Generic mask GET emits a path per readable mask; if any such
        // mask is registered, tag the group.
        $hasReadableMasks = false;
        foreach ($masks as $mask) {
            if (($mask['writable'] ?? false) !== true) {
                $hasReadableMasks = true;
                break;
            }
        }
        if ($hasReadableMasks) {
            $systemTags[] = ['name' => 'Generic mask GET', 'description' => 'Universal cacheable read path: GET /api.php/_/<mask>?<params>. Reaches every read mask, including admin masks for secret tables.'];
            $systemTagNames[] = 'Generic mask GET';
        }

        if ($commands !== []) {
            $systemTags[] = ['name' => 'Commands', 'description' => 'Business-logic operations beyond CRUD (e.g. order.process, inventory.adjust).'];
            $systemTagNames[] = 'Commands';
        }

        $allTags = array_merge($entityTags, $systemTags);

        $tagGroups = [];
        if ($entityTagNames !== []) {
            $tagGroups[] = ['name' => 'Entities', 'tags' => $entityTagNames];
        }
        if ($systemTagNames !== []) {
            $tagGroups[] = ['name' => 'System', 'tags' => $systemTagNames];
        }

        return [$allTags, $tagGroups];
    }

    /** @return array<string, array<string, mixed>> */
    private function buildSchemas(array $entities): array
    {
        $schemas = [];
        foreach ($entities as $meta) {
            $name = ucfirst($meta->table);
            $props = [];
            $required = [];
            foreach ($meta->fields as $field) {
                $schema = $this->fieldToJsonSchema($field);
                if ($field->isPrimary) {
                    $schema['readOnly'] = true;
                }
                if ($field->validators !== []) {
                    $schema['x-validators'] = $field->validators;
                }
                if ($field->enumValues !== null && $field->enumValues !== []) {
                    $schema['enum'] = $field->enumValues;
                }
                if ($field->description !== null && $field->description !== '') {
                    $schema['description'] = $field->description;
                }
                $props[$field->name] = $schema;
                if (!$field->nullable && !$field->hasDefault) {
                    $required[] = $field->name;
                }
            }
            $s = ['type' => 'object', 'properties' => $props];
            if ($meta->description !== null && $meta->description !== '') {
                $s['description'] = $meta->description;
            }
            if ($required !== []) {
                $s['required'] = $required;
            }
            $schemas[$name] = $s;
        }

        $schemas['Error'] = [
            'type' => 'object',
            'properties' => [
                'error' => [
                    'type' => 'object',
                    'properties' => [
                        'code' => ['type' => 'string'],
                        'message' => ['type' => 'string'],
                    ],
                ],
            ],
        ];

        $schemas['MutationResult'] = [
            'type' => 'object',
            'properties' => [
                'results' => [
                    'type' => 'array',
                    'items' => [
                        'type' => 'object',
                        'properties' => [
                            'ref' => ['type' => 'string', 'nullable' => true],
                            'action' => ['type' => 'string', 'enum' => ['insert', 'update', 'delete']],
                            'table' => ['type' => 'string'],
                            'affected' => ['type' => 'integer'],
                        ],
                    ],
                ],
            ],
        ];

        return $schemas;
    }

    private function fieldToJsonSchema(FieldMeta $field): array
    {
        $schema = match ($field->phpType) {
            'int' => ['type' => 'integer'],
            'float' => ['type' => 'number'],
            'bool' => ['type' => 'boolean'],
            default => ['type' => 'string'],
        };

        if ($field->nullable) {
            $schema['nullable'] = true;
        }
        if ($field->hasDefault) {
            $schema['default'] = $field->default;
        }
        if ($field->length !== null && $field->phpType === 'string') {
            $schema['maxLength'] = $field->length;
        }
        if ($field->dbType === 'date') {
            $schema['format'] = 'date';
        }
        if ($field->dbType === 'text') {
            $schema['format'] = 'text';
        }
        if ($field->enumValues !== null && $field->enumValues !== []) {
            $schema['enum'] = $field->enumValues;
        }
        if ($field->description !== null && $field->description !== '') {
            $schema['description'] = $field->description;
        }

        return $schema;
    }

    private function queryBody(string $maskName, array $paramSchemas): array
    {
        $paramsSchema = $paramSchemas !== []
            ? ['type' => 'object', 'required' => array_keys($paramSchemas), 'properties' => $paramSchemas]
            : ['type' => 'object'];

        return $this->jsonBody([
            'type' => 'object',
            'required' => ['op', 'payload'],
            'properties' => [
                'op' => ['type' => 'string', 'enum' => ['query']],
                'payload' => [
                    'type' => 'object',
                    'required' => ['queries'],
                    'properties' => [
                        'queries' => [
                            'type' => 'array',
                            'items' => [
                                'type' => 'object',
                                'required' => ['ref', 'sql'],
                                'properties' => [
                                    'ref' => ['type' => 'string', 'example' => $maskName],
                                    'sql' => ['type' => 'string', 'enum' => [$maskName]],
                                    'params' => $paramsSchema,
                                    'options' => [
                                        'type' => 'object',
                                        'description' => 'Optional refinements applied after the mask runs.',
                                        'properties' => [
                                            'orderby' => [
                                                'type' => 'array',
                                                'description' => 'List of {field, dir} entries. Pushdown into SQL is best-effort; for ordered pagination on large tables, write a hand-written mask with ORDER BY in the SQL.',
                                                'items' => [
                                                    'type' => 'object',
                                                    'required' => ['field'],
                                                    'properties' => [
                                                        'field' => ['type' => 'string', 'example' => 'created_at'],
                                                        'dir'   => ['type' => 'string', 'enum' => ['asc', 'desc'], 'example' => 'desc', 'default' => 'asc'],
                                                    ],
                                                ],
                                                'example' => [['field' => 'created_at', 'dir' => 'desc']],
                                            ],
                                            'top' => ['type' => 'integer', 'maximum' => 200, 'example' => 50],
                                            'skip' => ['type' => 'integer', 'minimum' => 0, 'example' => 0],
                                            'count' => ['type' => 'boolean', 'description' => 'When true, response includes the unbounded `total_count`.', 'example' => false],
                                        ],
                                    ],
                                ],
                            ],
                        ],
                    ],
                ],
            ],
        ]);
    }

    private function queryResponse(string $description, array $rowSchema): array
    {
        return $this->jsonResponse($description, [
            'type' => 'object',
            'properties' => [
                'results' => [
                    'type' => 'array',
                    'items' => [
                        'type' => 'object',
                        'properties' => [
                            'ref' => ['type' => 'string'],
                            'meta' => [
                                'type' => 'object',
                                'properties' => [
                                    'columns' => ['type' => 'array', 'items' => ['type' => 'string']],
                                    'ms' => ['type' => 'number'],
                                    'rows' => ['type' => 'integer'],
                                ],
                            ],
                            'rows' => ['type' => 'array', 'items' => ['type' => 'array']],
                            'count' => ['type' => 'integer'],
                            'total_count' => ['type' => 'integer'],
                        ],
                    ],
                ],
            ],
        ]);
    }

    private function mutationBody(string $action, string $table, array $dataProps, array $dataRequired, array $filterProps = []): array
    {
        $mutation = [
            'type' => 'object',
            'required' => ['table', 'action'],
            'properties' => [
                'table' => ['type' => 'string', 'enum' => [$table]],
                'action' => ['type' => 'string', 'enum' => [$action]],
                'ref' => ['type' => 'string'],
            ],
        ];

        if ($dataProps !== []) {
            $dataSchema = ['type' => 'object', 'properties' => $dataProps];
            if ($dataRequired !== []) {
                $dataSchema['required'] = $dataRequired;
            }
            $mutation['properties']['data'] = $dataSchema;
        }
        if ($filterProps !== []) {
            $mutation['properties']['filter'] = [
                'type' => 'object',
                'properties' => $filterProps,
            ];
        }

        return $this->jsonBody([
            'type' => 'object',
            'required' => ['op', 'payload'],
            'properties' => [
                'op' => ['type' => 'string', 'enum' => ['mutation']],
                'payload' => [
                    'type' => 'object',
                    'required' => ['mutations'],
                    'properties' => [
                        'mutations' => ['type' => 'array', 'items' => $mutation],
                        'transaction' => ['type' => 'boolean', 'default' => false],
                    ],
                ],
            ],
        ]);
    }

    private function mutationResponse(string $description): array
    {
        return $this->jsonResponse($description, ['$ref' => '#/components/schemas/MutationResult']);
    }

    private function jsonBody(array $schema): array
    {
        return ['required' => true, 'content' => ['application/json' => ['schema' => $schema]]];
    }

    private function jsonResponse(string $description, array $schema): array
    {
        return ['description' => $description, 'content' => ['application/json' => ['schema' => $schema]]];
    }

    private function errorResponse(string $description): array
    {
        return $this->jsonResponse($description, ['$ref' => '#/components/schemas/Error']);
    }

    private function maskParamToJsonType(string $papiType): string
    {
        return match ($papiType) {
            'int', 'array_int' => 'integer',
            'float' => 'number',
            'bool' => 'boolean',
            default => 'string',
        };
    }

    private static function pascalCase(string $snakeCase): string
    {
        return str_replace(' ', '', ucwords(str_replace('_', ' ', $snakeCase)));
    }

    /**
     * One documentation sentence per row scope (ROW-SCOPES §2.5):
     * "Rows are automatically limited to the caller's <column>."
     *
     * @param array<mixed, mixed> $rowScopes column => {multi} (raw mask config)
     */
    private static function rowScopeSentences(array $rowScopes): string
    {
        $parts = [];
        foreach (array_keys($rowScopes) as $col) {
            if (is_string($col) && $col !== '') {
                $parts[] = "Rows are automatically limited to the caller's {$col}.";
            }
        }
        return implode(' ', $parts);
    }

    /**
     * Prefer an explicit `description` on the mask config; fall back to the
     * caller-supplied default summary if none is set.
     *
     * @param array<string, array<string, mixed>> $masks
     */
    private static function summary(array $masks, string $maskName, string $fallback): string
    {
        $desc = $masks[$maskName]['description'] ?? null;
        return is_string($desc) && $desc !== '' ? $desc : $fallback;
    }
}
