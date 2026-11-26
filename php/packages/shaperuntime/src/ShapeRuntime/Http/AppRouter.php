<?php

declare(strict_types=1);

namespace ShapeRuntime\Http;

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
use Throwable;

final class AppRouter
{
    public static function run(string $configPath, string $serviceName = 'shaperuntime'): void
    {
        // Convert PHP fatals (OOM, parse errors, type errors, segfaults)
        // into the standard JSON error envelope. Without this, those leak
        // raw HTML and break the self-documenting-errors promise at the
        // exact failure modes that matter most for operators and LLM clients.
        FatalHandler::register();

        if (!is_file($configPath)) {
            self::jsonError(500, 'INTERNAL_ERROR', 'Missing config file config/{app}/app.php');
            return;
        }

        $config = require $configPath;
        if (!is_array($config)) {
            self::jsonError(500, 'INTERNAL_ERROR', 'Invalid config file format.');
            return;
        }

        $envPath = dirname($configPath) . '/env.php';
        if (is_file($envPath)) {
            $env = require $envPath;
            if (is_array($env)) {
                $config = array_replace_recursive($env, $config);
            }
        }

        $dbConfig = $config['db'] ?? [];
        $masks = $config['masks'] ?? [];
        $authConfig = $config['auth'] ?? [];
        $validators = $config['validators'] ?? [];
        $commands = $config['commands'] ?? [];

        // Deployed configs carry tenancy at the top level; auth consumers
        // (JwtGuard claim normalization, AdminAuth unscoped-role gate,
        // LoginService suspension checks) read it from auth config (§2.3).
        if (is_array($config['tenancy'] ?? null) && !isset($authConfig['tenancy'])) {
            $authConfig['tenancy'] = $config['tenancy'];
        }

        $issuer = new JwtIssuer($authConfig);
        $loginService = new LoginService($dbConfig, $authConfig, $issuer);

        $database = new Database($dbConfig);

        $guard = (($authConfig['driver'] ?? 'none') === 'jwt')
            ? new JwtGuard($authConfig, $database)
            : new NullGuard();
        $registry = new MaskRegistry($masks);
        $validatorRegistry = new ValidatorRegistry($validators);
        $executor = new BatchExecutor($database, $registry, $validatorRegistry);
        if (is_array($config['tenancy'] ?? null) || is_array($config['scopes'] ?? null)) {
            $executor->setScoping(
                is_array($config['tenancy'] ?? null) ? $config['tenancy'] : [],
                is_array($config['scopes'] ?? null) ? $config['scopes'] : [],
            );
        }
        $commandExecutor = new CommandExecutor($database, $executor, $commands, $config['command_roles'] ?? []);
        $adminAuth = new AdminAuth($authConfig);

        $method = strtoupper($_SERVER['REQUEST_METHOD'] ?? 'GET');
        if ($method === 'GET') {
            header('Content-Type: application/json');
            echo json_encode([
                'service' => $serviceName,
                'status' => 'ok',
                'endpoint' => '/api.php',
                'message' => 'ShapeRuntime backend is running. Send POST JSON with op and payload to /api.php.',
            ], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
            return;
        }

        if ($method !== 'POST') {
            self::jsonError(405, 'BAD_REQUEST', 'Only GET and POST are allowed.');
            return;
        }

        try {
            $request = self::readJsonBody();
            $op = isset($request['op']) ? trim((string)$request['op']) : '';
            $payload = $request['payload'] ?? null;

            if ($op === '') {
                throw new ApiException('REQUEST_VALIDATION_ERROR', 'Field op is required.', 400);
            }
            if (!is_array($payload)) {
                throw new ApiException('REQUEST_VALIDATION_ERROR', 'Field payload must be an object.', 400);
            }

            $status = 200;
            $result = null;

            switch ($op) {
                case 'query':
                    $headers = self::normalizeHeaders(self::readHeaders());
                    $authContext = $guard->authorize($headers);
                    $queries = $payload['queries'] ?? null;
                    if (!is_array($queries) || !array_is_list($queries)) {
                        throw new ApiException('QUERY_VALIDATION_ERROR', 'Field queries must be an array.', 400);
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
                    // Brownfield bridge (TOKEN-EXCHANGE.md) — anonymous like
                    // auth.token; the external token IS the credential.
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
                        throw new ApiException('MUTATION_VALIDATION_ERROR', 'Field mutations must be an array.', 400);
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
                        throw new ApiException('COMMAND_VALIDATION_ERROR', 'Field name is required.', 400);
                    }
                    if (!is_array($commandParams)) {
                        throw new ApiException('COMMAND_VALIDATION_ERROR', 'Field params must be an object.', 400);
                    }
                    $result = $commandExecutor->execute($commandName, $commandParams, $authContext);
                    break;

                default:
                    throw new ApiException('NOT_FOUND', 'Unknown operation.', 404);
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
}
