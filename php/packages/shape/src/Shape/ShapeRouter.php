<?php

declare(strict_types=1);

namespace Shape;

use ShapeRuntime\Auth\JwtGuard;
use ShapeRuntime\Auth\JwtIssuer;
use ShapeRuntime\Auth\LoginService;
use ShapeRuntime\Auth\NullGuard;
use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\BatchExecutor;
use ShapeRuntime\Core\CommandExecutor;
use ShapeRuntime\Core\Database;
use ShapeRuntime\Core\MaskRegistry;
use ShapeRuntime\Core\ValidatorRegistry;
use ShapeRuntime\Http\AdminAuth;
use ShapeRuntime\Http\FatalHandler;
use Shape\Generator\LlmToolGenerator;
use Shape\Generator\ManifestGenerator;
use Shape\Generator\OpenApiGenerator;
use Shape\Http\CacheHeaders;
use Shape\Mcp\HttpTransport as McpHttpTransport;
use Shape\Mcp\McpServer;
use Shape\Pdf\DocumentService;
use Shape\Upload\AttachmentService;
use Shape\Upload\UploadManager;
use Throwable;

final class ShapeRouter
{
    /**
     * @param array<string, mixed> $config Complete config from ShapeApp
     */
    public static function run(array $config, string $serviceName = 'shape'): void
    {
        // Convert PHP fatals (OOM, parse errors, type errors, segfaults)
        // into the standard JSON error envelope. Lives in shaperuntime so
        // ShapeRuntime\Http\AppRouter can share the same hook.
        FatalHandler::register();

        $masks = $config['masks'] ?? [];
        $authConfig = $config['auth'] ?? [];
        $shapeData = $config['_shape'] ?? [];
        $manifestRoles = $config['manifest'] ?? null;
        $uploads = is_array($config['uploads'] ?? null) ? $config['uploads'] : [];

        $method = strtoupper($_SERVER['REQUEST_METHOD'] ?? 'GET');
        $pathInfo = trim($_SERVER['PATH_INFO'] ?? '', '/');
        $queryString = $_SERVER['QUERY_STRING'] ?? '';

        // ── Private attachments: GET|DELETE /api.php/files/<upload>/<id>.
        // Authorised through the attachment table's scoped .get mask, never served statically.
        if (in_array($method, ['GET', 'DELETE'], true) && str_starts_with($pathInfo, 'files/')) {
            self::wrapErrors(fn() => self::serveFile($method, substr($pathInfo, strlen('files/')), $config, $authConfig, $masks, $shapeData));
            return;
        }

        // ── PDF documents: GET /api.php/pdf/<name>?<params>. Data comes from masks run in the caller's context.
        // POST /api.php/pdf/<name>?<params> generates the PDF AND stores it as a private attachment (`archive`).
        if (in_array($method, ['GET', 'POST'], true) && str_starts_with($pathInfo, 'pdf/')) {
            self::wrapErrors(fn() => self::servePdf(substr($pathInfo, strlen('pdf/')), $config, $authConfig, $masks, $shapeData, $method === 'POST'));
            return;
        }

        // ── GET requests ──────────────────────────────────────────────
        if ($method === 'GET') {

            $qs = [];
            parse_str($queryString, $qs);

            // GET /api.php?docs → interactive API reference (DocsUiPage). The
            // page carries no schema data — the ?openapi fetch it makes is what
            // the manifest roles gate; the page prompts for a token on 401.
            if ($pathInfo === '' && (isset($qs['docs']) || $queryString === 'docs')) {
                if (($config['docs_ui'] ?? false) !== true) {
                    self::jsonError(404, 'NOT_FOUND', 'The docs UI is not enabled.', [
                        'hint' => "Set 'docs_ui' => true to serve the interactive API reference.",
                    ]);
                    return;
                }
                $assetUrl = isset($config['docs_ui_asset_url']) && is_string($config['docs_ui_asset_url'])
                    ? $config['docs_ui_asset_url']
                    : null;
                header('Content-Type: text/html; charset=utf-8');
                // SECURITY-REVIEW L8: baseline hardening headers on the HTML page
                // (no strict CSP — the viewer loads a CDN bundle).
                header('X-Content-Type-Options: nosniff');
                header('X-Frame-Options: SAMEORIGIN');
                header('Referrer-Policy: no-referrer');
                echo DocsUiPage::render($serviceName, '/api.php?openapi', $assetUrl);
                return;
            }

            // GET /api.php?manifest                         → JSON manifest
            // GET /api.php?manifest&format=anthropic-tools  → Anthropic tools array
            // GET /api.php?manifest&format=openai-tools     → OpenAI tools array
            if ($pathInfo === '' && (isset($qs['manifest']) || $queryString === 'manifest')) {
                $format = isset($qs['format']) ? (string)$qs['format'] : '';
                self::wrapErrors(fn() => self::serveManifest($shapeData, $masks, $authConfig, $manifestRoles, $format, $uploads));
                return;
            }

            // GET /api.php?openapi
            if ($pathInfo === '' && (isset($qs['openapi']) || $queryString === 'openapi')) {
                self::wrapErrors(fn() => self::serveOpenApi($shapeData, $masks, $authConfig, $manifestRoles, $serviceName));
                return;
            }

            // GET /api.php?mcp → MCP discovery handshake (streamable-HTTP info).
            // Real MCP traffic comes in via POST below; this lets clients that
            // do a probing GET first see they've got the right URL.
            if ($pathInfo === '' && (isset($qs['mcp']) || $queryString === 'mcp')) {
                self::wrapErrors(fn() => self::serveMcp($config, $serviceName, $authConfig));
                return;
            }

            // GET /api.php/{entity}?$filter=...&$orderby=...&$top=...&$skip=...&$select=...&$count=true
            // GET /api.php/{entity}({id})
            // GET /api.php/{entity}({id})/{related}
            if ($pathInfo !== '') {
                self::wrapErrors(fn() => self::handleODataGet(
                    $pathInfo, $queryString, $config, $masks, $authConfig, $shapeData
                ));
                return;
            }

            // GET /api.php → health check
            $health = [
                'service' => $serviceName,
                'status' => 'ok',
                'engine' => 'shape',
                'endpoint' => '/api.php',
                'manifest' => '/api.php?manifest',
                'openapi' => '/api.php?openapi',
                'message' => 'Shape backend is running. Supports OData GET and POST JSON.',
            ];
            if (($config['docs_ui'] ?? false) === true) {
                $health['docs'] = '/api.php?docs';
            }
            header('Content-Type: application/json');
            echo json_encode($health, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
            return;
        }

        // ── MCP endpoint (?mcp): JSON-RPC body, not the standard {op, payload}.
        // Short-circuit before the op parser. OPTIONS handled here too for CORS.
        $isMcp = false;
        if (isset($_SERVER['QUERY_STRING'])) {
            $mcpQs = [];
            parse_str((string)$_SERVER['QUERY_STRING'], $mcpQs);
            $isMcp = isset($mcpQs['mcp']) || $queryString === 'mcp';
        }
        if ($isMcp && in_array($method, ['POST', 'OPTIONS'], true)) {
            self::wrapErrors(fn() => self::serveMcp($config, $serviceName, $authConfig));
            return;
        }

        // ── Upload endpoint: POST /api.php/upload/<name> with multipart body.
        // Short-circuit before the op parser; uploads have a different content
        // type and response shape from JSON-envelope mutations.
        if ($method === 'POST' && str_starts_with($pathInfo, 'upload/')) {
            $uploadName = substr($pathInfo, strlen('upload/'));
            self::wrapErrors(fn() => self::serveUpload($uploadName, $config, $authConfig, $masks, $shapeData));
            return;
        }

        // ── POST requests ─────────────────────────────────────────────
        if ($method !== 'POST') {
            self::jsonError(405, 'BAD_REQUEST', 'Only GET and POST are allowed.');
            return;
        }

        try {
            $request = self::readJsonBody();
            $op = isset($request['op']) ? trim((string)$request['op']) : '';
            $payload = $request['payload'] ?? null;

            if ($op === '') {
                throw new ApiException(
                    'REQUEST_VALIDATION_ERROR',
                    'Field op is required.',
                    400,
                    null,
                    [
                        'expected' => ['op' => 'string (required)', 'payload' => 'object'],
                        'example' => ['op' => 'manifest'],
                        'hint' => "Set op to one of: 'query', 'mutation', 'command', 'manifest', 'openapi', 'auth.token', 'auth.refresh', 'auth.logout', 'auth.change-password'.",
                    ]
                );
            }

            // Manifest and OpenAPI don't require payload or DB
            if ($op === 'manifest' || $op === 'openapi') {
                self::authorizeManifest($authConfig, $manifestRoles);
                if ($op === 'openapi') {
                    $result = self::buildOpenApi($shapeData, $masks, $authConfig, $serviceName);
                } else {
                    $format = is_array($payload) && isset($payload['format'])
                        ? (string)$payload['format']
                        : '';
                    $result = self::buildManifestForFormat($shapeData, $masks, $format, $uploads);
                }

                http_response_code(200);
                header('Content-Type: application/json');
                echo json_encode($result, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
                return;
            }

            if (!is_array($payload)) {
                throw new ApiException(
                    'REQUEST_VALIDATION_ERROR',
                    'Field payload must be an object.',
                    400,
                    null,
                    [
                        'expected' => ['op' => 'string (required)', 'payload' => 'object (required for non-manifest ops)'],
                        'example' => match ($op) {
                            'query' => ['op' => 'query', 'payload' => ['queries' => [['ref' => 'r1', 'sql' => 'books.list', 'params' => new \stdClass()]]]],
                            'mutation' => ['op' => 'mutation', 'payload' => ['mutations' => [['ref' => 'r1', 'table' => 'books', 'action' => 'insert', 'data' => ['title' => 'example']]]]],
                            'command' => ['op' => 'command', 'payload' => ['name' => 'commandName', 'params' => new \stdClass()]],
                            default => ['op' => $op, 'payload' => new \stdClass()],
                        },
                    ]
                );
            }

            // Lazy-create DB-dependent components
            $dbConfig = $config['db'] ?? [];
            $validators = $config['validators'] ?? [];
            $commands = $config['commands'] ?? [];

            $database = new Database($dbConfig);
            $registry = new MaskRegistry($masks);
            $validatorRegistry = new ValidatorRegistry($validators);
            $executor = self::buildExecutor($config, $shapeData, $database, $registry, $validatorRegistry);

            $commandExecutor = new CommandExecutor($database, $executor, $commands, $config['command_roles'] ?? []);
            $adminAuth = new AdminAuth($authConfig);
            $issuer = new JwtIssuer($authConfig);
            $loginService = new LoginService($dbConfig, $authConfig, $issuer);

            $guard = (($authConfig['driver'] ?? 'none') === 'jwt')
                ? new JwtGuard($authConfig, $database)
                : new NullGuard();

            $status = 200;
            $result = null;

            switch ($op) {
                case 'query':
                    $headers = self::normalizeHeaders(self::readHeaders());
                    $authContext = $guard->authorize($headers);
                    $queries = $payload['queries'] ?? null;
                    if (!is_array($queries) || !array_is_list($queries)) {
                        throw new ApiException(
                            'QUERY_VALIDATION_ERROR',
                            'Field queries must be an array.',
                            400,
                            null,
                            [
                                'expected' => ['payload.queries' => 'array<object> (required)'],
                                'example' => ['queries' => [['ref' => 'r1', 'sql' => 'books.list', 'params' => new \stdClass()]]],
                                'hint' => "Each query needs at minimum a 'ref' (unique label), 'sql' (mask name), and 'params' object.",
                            ]
                        );
                    }
                    $result = $executor->execute($queries, $authContext);
                    break;

                case 'auth.token':
                case 'auth.login':
                    $result = $loginService->login($payload);
                    break;

                case 'auth.refresh':
                    $result = $loginService->refresh($payload);
                    break;

                case 'auth.exchange':
                    // Brownfield bridge (TOKEN-EXCHANGE.md): swap an
                    // app-validated external token for a Shape JWT. Anonymous
                    // like auth.token — the external token IS the credential.
                    $result = $loginService->exchange($payload);
                    break;

                case 'auth.logout':
                    $result = $loginService->logout($payload);
                    break;

                case 'auth.admin.create-user':
                    $adminClaims = $adminAuth->authorizeAdmin();
                    $result = $loginService->createUser($payload, $adminClaims);
                    $status = 201;
                    break;

                case 'auth.admin.change-password':
                    $adminClaims = $adminAuth->authorizeAdmin();
                    $result = $loginService->changePassword($payload, $adminClaims);
                    break;

                case 'auth.change-password':
                    $ownClaims = $guard->authorize(self::normalizeHeaders(self::readHeaders()));
                    $result = $loginService->changeOwnPassword($payload, $ownClaims);
                    break;

                case 'mutation':
                    $headers = self::normalizeHeaders(self::readHeaders());
                    $authContext = $guard->authorize($headers);
                    $mutationList = $payload['mutations'] ?? null;
                    if (!is_array($mutationList) || !array_is_list($mutationList)) {
                        throw new ApiException(
                            'MUTATION_VALIDATION_ERROR',
                            'Field mutations must be an array.',
                            400,
                            null,
                            [
                                'expected' => ['payload.mutations' => 'array<object> (required)'],
                                'example' => ['mutations' => [['ref' => 'm1', 'table' => 'books', 'action' => 'insert', 'data' => ['title' => 'example']]]],
                                'hint' => "Each mutation needs 'ref', 'table', 'action' (insert/update/delete), and 'data' or 'filter' depending on action.",
                            ]
                        );
                    }
                    $useTransaction = (bool)($payload['transaction'] ?? false);
                    $result = $executor->executeMutations($mutationList, $authContext, $useTransaction);
                    break;

                case 'command':
                    $headers = self::normalizeHeaders(self::readHeaders());
                    $authContext = $guard->authorize($headers);
                    $commandName = isset($payload['name']) ? trim((string)$payload['name']) : '';
                    $commandParams = $payload['params'] ?? [];
                    if ($commandName === '') {
                        $available = array_keys($commands);
                        throw new ApiException(
                            'COMMAND_VALIDATION_ERROR',
                            'Field name is required.',
                            400,
                            null,
                            [
                                'expected' => ['payload.name' => 'string (required)', 'payload.params' => 'object'],
                                'example' => $available !== []
                                    ? ['name' => $available[0], 'params' => new \stdClass()]
                                    : ['name' => 'commandName', 'params' => new \stdClass()],
                                'hint' => $available !== []
                                    ? 'Available commands: ' . implode(', ', $available) . '.'
                                    : 'No commands are configured for this app.',
                            ]
                        );
                    }
                    if (!is_array($commandParams)) {
                        throw new ApiException(
                            'COMMAND_VALIDATION_ERROR',
                            'Field params must be an object.',
                            400,
                            null,
                            [
                                'expected' => ['payload.params' => 'object'],
                                'example' => ['name' => $commandName, 'params' => new \stdClass()],
                            ]
                        );
                    }
                    $result = $commandExecutor->execute($commandName, $commandParams, $authContext);
                    break;

                default:
                    throw new ApiException(
                        'NOT_FOUND',
                        "Unknown operation '{$op}'.",
                        404,
                        null,
                        [
                            'expected' => ['op' => "one of: query, mutation, command, manifest, openapi, auth.token, auth.refresh, auth.logout, auth.admin.create-user, auth.admin.change-password"],
                            'example' => ['op' => 'query', 'payload' => ['queries' => [['ref' => 'r1', 'sql' => 'books.list', 'params' => new \stdClass()]]]],
                        ]
                    );
            }

            http_response_code($status);
            header('Content-Type: application/json');
            echo json_encode($result, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        } catch (ApiException $e) {
            self::jsonError($e->status(), $e->apiCode(), $e->getMessage(), $e->details());
        } catch (Throwable $e) {
            self::jsonError(500, 'INTERNAL_ERROR', 'Unexpected server error.');
        }
    }

    // ── OData GET handler ─────────────────────────────────────────────

    /**
     * Handle OData-style GET requests:
     *   /api.php/books                           → books.list + options
     *   /api.php/books(42)                       → books.get by PK
     *   /api.php/books(42)/reviews               → reviews.byBookId
     *   ?$filter=price gt 10 and author_id eq 1
     *   ?$select=id,title,price
     *   ?$orderby=price desc,title asc
     *   ?$top=10&$skip=20&$count=true
     */
    private static function handleODataGet(
        string $pathInfo,
        string $queryString,
        array $config,
        array $masks,
        array $authConfig,
        array $shapeData,
    ): void {
        // Generic mask path: /_/<mask.name>?<params>
        // Reaches any mask (hand-written or auto-generated) — including ones
        // that don't fit the entity/{id}/{related} convention. Same ETag and
        // cache_control treatment as the entity-level routes.
        if (str_starts_with($pathInfo, '_/')) {
            self::handleGenericMaskGet(substr($pathInfo, 2), $queryString, $config, $masks, $authConfig);
            return;
        }

        // Parse path: entity, optional (id), optional /related
        $parsed = self::parseODataPath($pathInfo);
        $entity = $parsed['entity'];
        $id = $parsed['id'];
        $related = $parsed['related'];

        // Parse query string into OData options
        $odata = self::parseODataQuery($queryString);

        // DB first (the auth guard needs it for OIDC user-id resolution).
        $database = new Database($config['db'] ?? []);
        $registry = new MaskRegistry($masks);
        $validatorRegistry = new ValidatorRegistry($config['validators'] ?? []);
        $executor = new BatchExecutor($database, $registry, $validatorRegistry);
        if (is_array($config['tenancy'] ?? null) || is_array($config['scopes'] ?? null)) {
            $executor->setScoping(
                is_array($config['tenancy'] ?? null) ? $config['tenancy'] : [],
                is_array($config['scopes'] ?? null) ? $config['scopes'] : [],
            );
        }

        // Auth
        $guard = (($authConfig['driver'] ?? 'none') === 'jwt')
            ? new JwtGuard($authConfig, $database)
            : new NullGuard();
        $headers = self::normalizeHeaders(self::readHeaders());
        $authContext = $guard->authorize($headers);

        // Determine mask and params
        $maskName = '';
        $params = [];

        if ($id !== null && $related !== null) {
            // /books(42)/reviews → reviews.byBookId
            $fkField = self::findFkField($shapeData, $related, $entity);
            if ($fkField === null) {
                throw new ApiException('NOT_FOUND', "No relationship from '{$related}' to '{$entity}'.", 404);
            }
            $maskName = $related . '.by' . self::pascalCase($fkField);
            $params[$fkField] = $id;
        } elseif ($id !== null) {
            // /books(42) → books.get
            $maskName = $entity . '.get';
            $pk = self::findPkName($shapeData, $entity);
            $params[$pk] = $id;
        } else {
            // /books → books.list
            $maskName = $entity . '.list';
        }

        // Build query item
        $queryItem = [
            'ref' => $entity,
            'sql' => $maskName,
            'params' => $params,
        ];

        // Build options from OData params
        $options = [];
        if ($odata['orderby'] !== []) {
            $options['orderby'] = $odata['orderby'];
        }
        if ($odata['top'] !== null) {
            $options['top'] = $odata['top'];
        }
        if ($odata['skip'] !== null) {
            $options['skip'] = $odata['skip'];
        }
        if ($odata['count']) {
            $options['count'] = true;
        }
        if ($options !== []) {
            $queryItem['options'] = $options;
        }

        // Execute. $filter is compiled to SQL by the executor, so it applies before paging and $count.
        $result = $executor->execute([$queryItem], $authContext, $odata['filter'] !== null ? [$entity => $odata['filter']] : []);
        $queryResult = $result['results'][0] ?? [];

        // Format response as OData-style JSON
        $columns = $queryResult['meta']['columns'] ?? [];
        $rows = $queryResult['rows'] ?? [];
        $selectFields = $odata['select'];

        // Convert columnar rows to objects
        $items = [];
        foreach ($rows as $row) {
            $obj = [];
            foreach ($columns as $i => $col) {
                if ($selectFields === [] || in_array($col, $selectFields, true)) {
                    $obj[$col] = $row[$i] ?? null;
                }
            }
            $items[] = $obj;
        }

        $response = ['value' => $items];
        if ($odata['count']) {
            $response['@odata.count'] = $queryResult['total_count'] ?? count($items);
        }

        // Single entity by ID → return object, not array
        if ($id !== null && $related === null) {
            $response = $items[0] ?? null;
            if ($response === null) {
                throw new ApiException('NOT_FOUND', "Entity '{$entity}' with id '{$id}' not found.", 404);
            }
        }

        $body = json_encode($response, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        if ($body === false) {
            $body = '{}';
        }

        // Cache-Control comes from the resolved mask config (if the dev set
        // 'cache_control' on it). ETag is always emitted; If-None-Match
        // collapses to 304.
        $cacheControl = isset($masks[$maskName]['cache_control']) && is_string($masks[$maskName]['cache_control'])
            ? $masks[$maskName]['cache_control']
            : null;

        // Same URL, different tenants/scopes, one shared cache = leak — scoped
        // GET responses always vary on the credential (MULTI-TENANCY-PLAN
        // §2.9, ROW-SCOPES §2.5). Set before the 304 short-circuit so
        // conditional responses carry it.
        if (isset($masks[$maskName]['tenant_scope']) || !empty($masks[$maskName]['row_scopes'])) {
            header('Vary: Authorization');
        }

        if (CacheHeaders::shouldRespond304($body, $cacheControl)) {
            return;
        }

        header('Content-Type: application/json');
        echo $body;
    }

    /**
     * Generic-mask GET handler.
     *
     *   GET /api.php/_/reports.topAuthors
     *   GET /api.php/_/books.byAuthorId?author_id=42
     *   GET /api.php/_/users.adminGet?id=7
     *
     * Reaches any mask in the config — including hand-written ones that
     * don't fit the entity/{id}/{related} convention. URL query parameters
     * are mapped to mask params; comma-separated values become arrays for
     * `array_int` / `array_string` typed params.
     *
     * Behavior matches the entity-level GET path:
     *   - Auth via JwtGuard / NullGuard
     *   - Self-documenting errors flow through (mask not found, missing
     *     required param, type mismatch, AUTH_FORBIDDEN)
     *   - ETag computed from response body, If-None-Match honored
     *   - Cache-Control emitted from the mask's `cache_control` field
     *
     * @param array<string, mixed> $config
     * @param array<string, array<string, mixed>> $masks
     * @param array<string, mixed> $authConfig
     */
    private static function handleGenericMaskGet(
        string $maskName,
        string $queryString,
        array $config,
        array $masks,
        array $authConfig,
    ): void {
        if ($maskName === '' || !isset($masks[$maskName])) {
            throw new ApiException(
                'NOT_FOUND',
                "Mask '{$maskName}' is not registered.",
                404,
                null,
                [
                    'hint' => "Fetch GET /api.php?manifest to see available masks. The generic GET path is /api.php/_/<mask.name>.",
                ]
            );
        }

        $mask = $masks[$maskName];

        // Parse the URL query into mask params, with type-aware coercion of
        // array_* params (comma-separated or repeated `?ids[]=1&ids[]=2`).
        $qs = [];
        parse_str($queryString, $qs);
        $params = [];
        $paramSchema = is_array($mask['params'] ?? null) ? $mask['params'] : [];
        foreach ($paramSchema as $name => $spec) {
            if (!array_key_exists($name, $qs)) {
                continue;
            }
            $raw = $qs[$name];
            $type = is_array($spec) ? (string)($spec['type'] ?? 'string') : 'string';
            if (str_starts_with($type, 'array_')) {
                if (!is_array($raw)) {
                    $raw = array_values(array_filter(
                        array_map('trim', explode(',', (string)$raw)),
                        static fn(string $v): bool => $v !== ''
                    ));
                }
            }
            $params[$name] = $raw;
        }

        // DB first (the auth guard needs it for OIDC user-id resolution).
        $database = new Database($config['db'] ?? []);
        $registry = new MaskRegistry($masks);
        $validatorRegistry = new ValidatorRegistry($config['validators'] ?? []);
        $executor = new BatchExecutor($database, $registry, $validatorRegistry);
        if (is_array($config['tenancy'] ?? null) || is_array($config['scopes'] ?? null)) {
            $executor->setScoping(
                is_array($config['tenancy'] ?? null) ? $config['tenancy'] : [],
                is_array($config['scopes'] ?? null) ? $config['scopes'] : [],
            );
        }

        // Auth — same pipeline as the entity-level GET path
        $guard = (($authConfig['driver'] ?? 'none') === 'jwt')
            ? new JwtGuard($authConfig, $database)
            : new NullGuard();
        $authContext = $guard->authorize(self::normalizeHeaders(self::readHeaders()));

        $result = $executor->execute(
            [['ref' => 'mcp_get', 'sql' => $maskName, 'params' => $params]],
            $authContext,
        );
        $queryResult = $result['results'][0] ?? [];
        $columns = $queryResult['meta']['columns'] ?? [];
        $rows = $queryResult['rows'] ?? [];

        // Convert column-major rows to objects for friendlier consumption.
        $items = [];
        foreach ($rows as $row) {
            $obj = [];
            foreach ($columns as $i => $col) {
                $obj[$col] = $row[$i] ?? null;
            }
            $items[] = $obj;
        }

        $response = ['value' => $items];
        $body = json_encode($response, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        if ($body === false) {
            $body = '{}';
        }

        $cacheControl = isset($mask['cache_control']) && is_string($mask['cache_control'])
            ? $mask['cache_control']
            : null;

        // Scoped GETs vary on the credential (§2.9) — see the entity-GET path.
        if (isset($mask['tenant_scope']) || !empty($mask['row_scopes'])) {
            header('Vary: Authorization');
        }

        if (CacheHeaders::shouldRespond304($body, $cacheControl)) {
            return;
        }

        header('Content-Type: application/json');
        echo $body;
    }

    /**
     * Parse path: supports both REST and OData style.
     *
     *   books              → list
     *   books/1            → get by ID (REST)
     *   books(1)           → get by ID (OData)
     *   books/1/reviews    → related (REST)
     *   books(1)/reviews   → related (OData)
     *
     * @return array{entity:string, id:int|string|null, related:string|null}
     */
    private static function parseODataPath(string $pathInfo): array
    {
        $entity = $pathInfo;
        $id = null;
        $related = null;

        // REST: entity/id/related
        if (preg_match('/^(\w+)\/([^\/]+)\/(\w+)$/', $pathInfo, $m)) {
            $entity = $m[1];
            $id = is_numeric($m[2]) ? (int)$m[2] : $m[2];
            $related = $m[3];
        }
        // OData: entity(id)/related
        elseif (preg_match('/^(\w+)\(([^)]+)\)\/(\w+)$/', $pathInfo, $m)) {
            $entity = $m[1];
            $id = is_numeric($m[2]) ? (int)$m[2] : $m[2];
            $related = $m[3];
        }
        // REST: entity/id
        elseif (preg_match('/^(\w+)\/([^\/]+)$/', $pathInfo, $m)) {
            $entity = $m[1];
            $id = is_numeric($m[2]) ? (int)$m[2] : $m[2];
        }
        // OData: entity(id)
        elseif (preg_match('/^(\w+)\(([^)]+)\)$/', $pathInfo, $m)) {
            $entity = $m[1];
            $id = is_numeric($m[2]) ? (int)$m[2] : $m[2];
        }
        // entity only
        elseif (preg_match('/^(\w+)$/', $pathInfo, $m)) {
            $entity = $m[1];
        }

        return ['entity' => $entity, 'id' => $id, 'related' => $related];
    }

    /**
     * Parse OData query string parameters.
     * Supports: $filter, $select, $orderby, $top, $skip, $count
     * @return array{select:list<string>, orderby:list<array{field:string,dir:string}>, top:?int, skip:?int, count:bool, filter:?string}
     */
    private static function parseODataQuery(string $queryString): array
    {
        parse_str($queryString, $qs);

        $select = [];
        if (isset($qs['$select']) && $qs['$select'] !== '') {
            $select = array_map('trim', explode(',', (string)$qs['$select']));
        }

        $orderby = [];
        if (isset($qs['$orderby']) && $qs['$orderby'] !== '') {
            foreach (explode(',', (string)$qs['$orderby']) as $part) {
                $parts = preg_split('/\s+/', trim($part));
                $field = $parts[0] ?? '';
                $dir = strtolower($parts[1] ?? 'asc');
                if ($field !== '' && in_array($dir, ['asc', 'desc'], true)) {
                    $orderby[] = ['field' => $field, 'dir' => $dir];
                }
            }
        }

        $top = isset($qs['$top']) ? (int)$qs['$top'] : null;
        $skip = isset($qs['$skip']) ? (int)$qs['$skip'] : null;
        $count = isset($qs['$count']) && $qs['$count'] === 'true';

        // $filter stays a raw string: BatchExecutor compiles it to a parameterised SQL condition.
        $filter = isset($qs['$filter']) && trim((string)$qs['$filter']) !== '' ? (string)$qs['$filter'] : null;

        return [
            'select' => $select,
            'orderby' => $orderby,
            'top' => $top,
            'skip' => $skip,
            'count' => $count,
            'filter' => $filter,
        ];
    }

    // ── Helpers ───────────────────────────────────────────────────────

    /** Find FK field name from related entity back to parent entity */
    private static function findFkField(array $shapeData, string $relatedTable, string $parentTable): ?string
    {
        $entities = $shapeData['entities'] ?? [];
        foreach ($entities as $meta) {
            if ($meta->table !== $relatedTable) {
                continue;
            }
            foreach ($meta->fields as $field) {
                $fkTarget = $field->fkEntity;
                if ($fkTarget === null) {
                    continue;
                }
                // DB-first: fkEntity is the table name directly
                if ($fkTarget === $parentTable) {
                    return $field->name;
                }
                // Entity-first: fkEntity is a class name, check if its table matches
                if (class_exists($fkTarget)) {
                    foreach ($entities as $candidate) {
                        if ($candidate->class === $fkTarget && $candidate->table === $parentTable) {
                            return $field->name;
                        }
                    }
                }
            }
        }
        return null;
    }

    /** Find primary key column name for an entity */
    private static function findPkName(array $shapeData, string $table): string
    {
        $entities = $shapeData['entities'] ?? [];
        foreach ($entities as $meta) {
            if ($meta->table === $table) {
                $pk = $meta->primaryKey();
                if ($pk !== null) {
                    return $pk->name;
                }
            }
        }
        return 'id';
    }

    private static function pascalCase(string $snakeCase): string
    {
        return str_replace(' ', '', ucwords(str_replace('_', ' ', $snakeCase)));
    }

    private static function wrapErrors(callable $fn): void
    {
        try {
            $fn();
        } catch (ApiException $e) {
            self::jsonError($e->status(), $e->apiCode(), $e->getMessage(), $e->details());
        } catch (Throwable $e) {
            self::jsonError(500, 'INTERNAL_ERROR', 'Unexpected server error.');
        }
    }

    private static function serveManifest(array $shapeData, array $masks, array $authConfig, ?array $manifestRoles, string $format = '', array $uploads = []): void
    {
        self::authorizeManifest($authConfig, $manifestRoles);
        $result = self::buildManifestForFormat($shapeData, $masks, $format, $uploads);
        header('Content-Type: application/json');
        echo json_encode($result, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT);
    }

    /**
     * Dispatch on the optional `format` parameter:
     *   ''                  → standard manifest (entities, masks, commands)
     *   'anthropic-tools'   → list of Anthropic tool definitions
     *   'openai-tools'      → list of OpenAI tool definitions
     */
    private static function buildManifestForFormat(array $shapeData, array $masks, string $format, array $uploads = []): array|object
    {
        $entities = $shapeData['entities'] ?? [];
        $commands = $shapeData['commands'] ?? [];

        $normalized = strtolower(str_replace('_', '-', $format));

        if ($normalized === 'anthropic-tools' || $normalized === 'anthropic') {
            $tools = (new LlmToolGenerator())->generateAnthropic($entities, $masks, $commands);
            return ['tools' => $tools];
        }

        if ($normalized === 'openai-tools' || $normalized === 'openai') {
            $tools = (new LlmToolGenerator())->generateOpenAi($entities, $masks, $commands);
            return ['tools' => $tools];
        }

        return self::buildManifest($shapeData, $masks, $uploads);
    }

    /**
     * One place that wires a BatchExecutor (scopes, tenancy, audit log) so the
     * JSON API, uploads and file downloads all enforce the same rules.
     *
     * @param array<string, mixed> $config
     * @param array<string, mixed> $shapeData
     */
    public static function buildExecutor(array $config, array $shapeData, Database $database, MaskRegistry $registry, ValidatorRegistry $validators): BatchExecutor
    {
        $executor = new BatchExecutor($database, $registry, $validators);
        if (is_array($config['tenancy'] ?? null) || is_array($config['scopes'] ?? null)) {
            $executor->setScoping(
                is_array($config['tenancy'] ?? null) ? $config['tenancy'] : [],
                is_array($config['scopes'] ?? null) ? $config['scopes'] : [],
            );
        }

        // Audit log: when ShapeApp detected the audit_log table at boot
        // and stashed the name in _shape, wire an AuditLogger so every
        // successful mutation appends one row.
        $auditTable = $shapeData['audit_log_table'] ?? null;
        if (is_string($auditTable) && $auditTable !== '') {
            // Tier 2: 'audit_diff' => true | ['table', ...] records before/after values (see DB-FIRST.md).
            $diffCfg = $config['audit_diff'] ?? false;
            $withValues = $diffCfg === true || (is_array($diffCfg) && $diffCfg !== []);
            $auditLogger = new \Shape\Audit\AuditLogger($database, $auditTable, $withValues);
            $executor->setAuditLogger(
                static fn(string $t, string $a, ?string $rid, array $auth, array $cols, ?array $old = null, ?array $new = null) =>
                    $auditLogger->log($t, $a, $rid, $auth, $cols, $old, $new)
            );
            if ($withValues) {
                $executor->setAuditDiff(
                    is_array($diffCfg) ? array_values(array_map('strval', $diffCfg)) : null,
                    is_array($config['audit_diff_exclude'] ?? null) ? $config['audit_diff_exclude'] : [],
                    static fn() => $auditLogger->assertValueColumns(),
                );
            }
        }

        // Event notifications: rules with an `on` block run right after a committed mutation (see DB-FIRST.md).
        $notify = is_array($config['notifications'] ?? null) ? $config['notifications'] : null;
        if ($notify !== null && \Shape\Notify\Notifier::hasEventRules($notify)) {
            $executor->setChangeListener(static function (array $changes) use ($config, $database, $executor, $notify): void {
                $auth = is_array($config['auth'] ?? null) ? $config['auth'] : [];
                $mailer = \Shape\Notify\NotifyCommand::mailerFromConfig(is_array($notify['transport'] ?? null) ? $notify['transport'] : ['driver' => 'log']);
                (new \Shape\Notify\Notifier(
                    $database,
                    $executor,
                    new \ShapeRuntime\Auth\LoginService($config['db'] ?? [], $auth, new \ShapeRuntime\Auth\JwtIssuer($auth)),
                    $mailer,
                    $notify,
                    ['table' => $auth['users_table'] ?? 'users']
                ))->onChanges($changes);
            });
        }
        return $executor;
    }

    /**
     * @param array<string, mixed> $config
     * @param array<string, mixed> $authConfig
     * @param array<string, mixed> $masks
     * @param array<string, mixed> $shapeData
     */
    private static function servePdf(string $name, array $config, array $authConfig, array $masks, array $shapeData, bool $archive = false): void
    {
        $documents = is_array($config['documents'] ?? null) ? $config['documents'] : [];
        if ($documents === [] || $name === '') {
            self::jsonError(404, 'NOT_FOUND', $documents === [] ? 'No documents are configured.' : 'Use /api.php/pdf/<name>?<params>.');
            return;
        }
        $database = new Database($config['db'] ?? []);
        $guard = (($authConfig['driver'] ?? 'none') === 'jwt')
            ? new JwtGuard($authConfig, $database)
            : new NullGuard();
        $auth = $guard->authorize(self::normalizeHeaders(self::readHeaders()));
        $executor = self::buildExecutor($config, $shapeData, $database, new MaskRegistry($masks), new ValidatorRegistry($config['validators'] ?? []));
        $service = new DocumentService($executor, $documents);
        $qs = [];
        parse_str((string)($_SERVER['QUERY_STRING'] ?? ''), $qs);
        if ($archive) {
            $stored = $service->archive($name, $qs, $auth, new AttachmentService(new UploadManager(is_array($config['uploads'] ?? null) ? $config['uploads'] : []), $executor));
            http_response_code(200);
            header('Content-Type: application/json');
            echo json_encode($stored, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
            return;
        }
        $doc = $service->render($name, $qs, $auth);

        header('Content-Type: application/pdf');
        header('Content-Disposition: inline; filename="' . $doc['filename'] . '"');
        header('X-Content-Type-Options: nosniff');
        header('Cache-Control: private, no-store');
        header('Content-Length: ' . strlen($doc['pdf']));
        http_response_code(200);
        echo $doc['pdf'];
    }

    /**
     * GET streams an attachment, DELETE removes it. A row the caller may not
     * see answers 404 exactly like a missing one.
     *
     * @param array<string, mixed> $config
     * @param array<string, mixed> $authConfig
     * @param array<string, mixed> $masks
     * @param array<string, mixed> $shapeData
     */
    private static function serveFile(string $method, string $rest, array $config, array $authConfig, array $masks, array $shapeData): void
    {
        $uploads = is_array($config['uploads'] ?? null) ? $config['uploads'] : [];
        $parts = explode('/', $rest);
        if (count($parts) !== 2 || $parts[0] === '' || $parts[1] === '' || !isset($uploads[$parts[0]])) {
            self::jsonError(404, 'NOT_FOUND', 'Use /api.php/files/<upload>/<id>.');
            return;
        }
        [$name, $id] = $parts;
        $database = new Database($config['db'] ?? []);
        $guard = (($authConfig['driver'] ?? 'none') === 'jwt')
            ? new JwtGuard($authConfig, $database)
            : new NullGuard();
        $auth = $guard->authorize(self::normalizeHeaders(self::readHeaders()));
        $service = new AttachmentService(
            new UploadManager($uploads),
            self::buildExecutor($config, $shapeData, $database, new MaskRegistry($masks), new ValidatorRegistry($config['validators'] ?? []))
        );
        $key = ctype_digit($id) ? (int)$id : $id;

        if ($method === 'DELETE') {
            $service->delete($name, $key, $auth);
            http_response_code(204);
            return;
        }

        $file = $service->open($name, $key, $auth);
        // RFC 5987 filename*, plus an ASCII fallback. attachment + nosniff: never rendered in the app origin.
        $ascii = preg_replace('/[^A-Za-z0-9._-]+/', '_', $file['filename']) ?: 'file';
        header('Content-Type: ' . ($file['mime_type'] !== '' ? $file['mime_type'] : 'application/octet-stream'));
        header("Content-Disposition: attachment; filename=\"{$ascii}\"; filename*=UTF-8''" . rawurlencode($file['filename']));
        header('X-Content-Type-Options: nosniff');
        header('Cache-Control: private, no-store');
        header('Content-Length: ' . $file['size']);
        header('ETag: "' . $file['sha256'] . '"');
        http_response_code(200);
        fpassthru($file['stream']);
        fclose($file['stream']);
    }

    /**
     * Dispatch a POST /api.php/upload/<name> request to the configured
     * upload endpoint. Auth flows through the same JwtGuard the rest of
     * the API uses; the per-endpoint role check happens inside UploadManager.
     *
     * @param array<string, mixed> $masks
     * @param array<string, mixed> $shapeData
     */
    private static function serveUpload(string $name, array $config, array $authConfig, array $masks = [], array $shapeData = []): void
    {
        $uploads = is_array($config['uploads'] ?? null) ? $config['uploads'] : [];
        if ($uploads === []) {
            self::jsonError(404, 'NOT_FOUND', "No upload endpoints are configured.");
            return;
        }
        if ($name === '') {
            self::jsonError(404, 'NOT_FOUND', "Upload endpoint name is required: POST /api.php/upload/<name>.");
            return;
        }

        // Resolve auth (same DB-backed JwtGuard pipeline as the rest of the
        // API — OIDC users get their oid resolved to users.id, audit-log
        // and audit-columns conventions all carry through).
        $database = new Database($config['db'] ?? []);
        $guard = (($authConfig['driver'] ?? 'none') === 'jwt')
            ? new JwtGuard($authConfig, $database)
            : new NullGuard();
        $authContext = $guard->authorize(self::normalizeHeaders(self::readHeaders()));

        $manager = new UploadManager($uploads);
        $service = new AttachmentService($manager, self::buildExecutor($config, $shapeData, $database, new MaskRegistry($masks), new ValidatorRegistry($config['validators'] ?? [])));
        $result = $service->isAttachmentEntry($name)
            ? $service->upload($name, $_FILES, $_POST, $authContext)
            : $manager->handle($name, $_FILES, $authContext);

        http_response_code(200);
        header('Content-Type: application/json');
        echo json_encode($result, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    }

    /**
     * Dispatch the request to the MCP HTTP transport.
     *
     * The McpServer is built from the same $config the rest of the router
     * uses, so masks / soft-delete / admin-masks / role checks all behave
     * identically to the JSON HTTP API. Auth context comes from JwtGuard
     * via HttpTransport — an unauthenticated caller sees an empty role and
     * tools/list filters to nothing reachable.
     */
    private static function serveMcp(array $config, string $serviceName, array $authConfig): void
    {
        $server = new McpServer($config, $serviceName, []);
        $database = new Database($config['db'] ?? []);
        (new McpHttpTransport($server, $authConfig, $database))->serve();
    }

    private static function serveOpenApi(array $shapeData, array $masks, array $authConfig, ?array $manifestRoles, string $serviceName): void
    {
        self::authorizeManifest($authConfig, $manifestRoles);
        $result = self::buildOpenApi($shapeData, $masks, $authConfig, $serviceName);
        header('Content-Type: application/json');
        echo json_encode($result, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES | JSON_PRETTY_PRINT);
    }

    /** @return array<string, mixed> */
    private static function readJsonBody(): array
    {
        $raw = file_get_contents('php://input');
        if (!is_string($raw) || trim($raw) === '') {
            throw new ApiException('INVALID_JSON', 'Request body is empty.', 400);
        }
        $decoded = json_decode($raw, true);
        if (!is_array($decoded)) {
            throw new ApiException('INVALID_JSON', 'Body must be valid JSON object.', 400);
        }
        return $decoded;
    }

    /** @return array<string, string> */
    private static function normalizeHeaders(array $headers): array
    {
        $out = [];
        foreach ($headers as $k => $v) {
            $out[strtolower((string)$k)] = (string)$v;
        }
        return $out;
    }

    /** @return array<string, string> */
    private static function readHeaders(): array
    {
        if (function_exists('getallheaders')) {
            $headers = getallheaders();
            return is_array($headers) ? $headers : [];
        }
        $headers = [];
        foreach ($_SERVER as $k => $v) {
            if (strncmp($k, 'HTTP_', 5) !== 0) {
                continue;
            }
            $name = strtolower(str_replace('_', '-', substr($k, 5)));
            $headers[$name] = (string)$v;
        }
        return $headers;
    }

    /** @param array<string, mixed> $details */
    private static function jsonError(int $status, string $code, string $message, array $details = []): void
    {
        http_response_code($status);
        header('Content-Type: application/json');
        $error = ['code' => $code, 'message' => $message];
        foreach (['expected', 'example', 'hint', 'ref', 'detail', 'scope'] as $key) {
            if (array_key_exists($key, $details)) {
                $error[$key] = $details[$key];
            }
        }
        echo json_encode(['error' => $error], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    }

    private static function authorizeManifest(array $authConfig, ?array $manifestRoles): void
    {
        if ($manifestRoles === null) {
            return;
        }
        $guard = (($authConfig['driver'] ?? 'none') === 'jwt')
            ? new JwtGuard($authConfig)
            : new NullGuard();
        $headers = self::normalizeHeaders(self::readHeaders());
        $authContext = $guard->authorize($headers);
        if ($manifestRoles !== []) {
            $role = isset($authContext['role']) ? (string)$authContext['role'] : '';
            if ($role === '' || !in_array($role, $manifestRoles, true)) {
                throw new ApiException('AUTH_FORBIDDEN', 'Your role cannot access the manifest.', 403);
            }
        }
    }

    private static function buildManifest(array $shapeData, array $masks, array $uploads = []): array
    {
        $generator = new ManifestGenerator();
        return $generator->generate($shapeData['entities'] ?? [], $masks, $shapeData['commands'] ?? [], $uploads);
    }

    private static function buildOpenApi(array $shapeData, array $masks, array $authConfig, string $serviceName): array
    {
        $generator = new OpenApiGenerator();
        $openapiConfig = is_array($shapeData['openapi'] ?? null) ? $shapeData['openapi'] : [];
        return $generator->generate(
            $shapeData['entities'] ?? [],
            $masks,
            $shapeData['commands'] ?? [],
            $authConfig,
            $serviceName,
            $openapiConfig
        );
    }
}
