<?php

declare(strict_types=1);

namespace Shape\Mcp;

use ShapeRuntime\Auth\JwtGuard;
use ShapeRuntime\Core\Database;

/**
 * HTTP transport for the MCP server. Implements the streamable-HTTP variant:
 * a single endpoint that accepts JSON-RPC over POST and replies with a
 * JSON-RPC response (no SSE streaming for v1 — Shape's use cases don't need
 * server-initiated push, and hosted backends behind PHP-FPM can't keep
 * persistent SSE connections cheaply anyway).
 *
 * Wired into ShapeRouter at `/api.php?mcp`. Auth flows through the same
 * JwtGuard the rest of the API uses, so the MCP session inherits the
 * caller's role and `tools/list` is filtered accordingly.
 */
final class HttpTransport
{
    /**
     * @param array<string, mixed> $authConfig
     * @param ?Database $database  Required for OIDC user-id resolution
     *   (mapping the token's oid to local users.id). Without it, OIDC
     *   tokens still validate but the auth context's `id` stays as the
     *   raw IdP claim instead of users.id.
     */
    public function __construct(
        private readonly McpServer $server,
        private readonly array $authConfig = [],
        private readonly ?Database $database = null,
    ) {
    }

    public function serve(): void
    {
        $method = strtoupper($_SERVER['REQUEST_METHOD'] ?? 'GET');

        // Browsers preflighting a fetch() will OPTIONS first.
        if ($method === 'OPTIONS') {
            $this->writeCorsHeaders();
            http_response_code(204);
            return;
        }

        // GET on the MCP endpoint advertises the server (capability hint
        // for clients that expect a discovery handshake before POSTing).
        if ($method === 'GET') {
            $this->writeCorsHeaders();
            header('Content-Type: application/json');
            echo json_encode([
                'transport' => 'streamable-http',
                'protocolVersion' => '2024-11-05',
                'serverInfo' => ['name' => 'shape-mcp'],
                'usage' => "POST JSON-RPC messages to this URL. Authorization: Bearer <jwt> if auth is configured.",
            ], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
            return;
        }

        if ($method !== 'POST') {
            $this->writeCorsHeaders();
            $this->jsonRpcError(null, -32600, 'Only POST is supported on the MCP endpoint.', 405);
            return;
        }

        // Authorize once per request — JwtGuard reads the Authorization
        // header and returns claims, NullGuard returns []. The role flows
        // into McpServer so tools/list is filtered for this caller.
        $authContext = $this->resolveAuthContext();
        $this->server->setAuthContext($authContext);

        $raw = file_get_contents('php://input');
        if (!is_string($raw) || trim($raw) === '') {
            $this->writeCorsHeaders();
            $this->jsonRpcError(null, -32700, 'Request body is empty.');
            return;
        }

        $message = json_decode($raw, true);
        if (!is_array($message)) {
            $this->writeCorsHeaders();
            $this->jsonRpcError(null, -32700, 'Request body must be a JSON-RPC object.');
            return;
        }

        $response = $this->server->handle($message);

        $this->writeCorsHeaders();

        // JSON-RPC notifications expect no response body; 202 Accepted is the
        // streamable-HTTP convention.
        if ($response === null) {
            http_response_code(202);
            return;
        }

        http_response_code(200);
        header('Content-Type: application/json');
        echo json_encode($response, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    }

    /** @return array<string, mixed> */
    private function resolveAuthContext(): array
    {
        $driver = (string)($this->authConfig['driver'] ?? 'none');

        // No auth driver configured: treat the caller as admin (same default
        // as the stdio transport on a local machine). The intent of
        // 'driver: none' is "this backend doesn't gate access" — silently
        // 403'ing every MCP tool because the empty NullGuard role doesn't
        // match any mask's role list would be a worse UX than just letting
        // the agent through. Configure 'auth.driver' => 'jwt' to gate.
        if ($driver !== 'jwt') {
            return ['role' => 'admin'];
        }

        $guard = new JwtGuard($this->authConfig, $this->database);
        try {
            $claims = $guard->authorize(self::readHeaders());
        } catch (\Throwable) {
            // Bad / missing token: pass through with no role so McpServer's
            // per-tool role check returns AUTH_FORBIDDEN with the standard
            // self-documenting envelope. Don't 401 here.
            return [];
        }
        return is_array($claims) ? $claims : [];
    }

    private function writeCorsHeaders(): void
    {
        header('Access-Control-Allow-Origin: *');
        header('Access-Control-Allow-Methods: POST, GET, OPTIONS');
        header('Access-Control-Allow-Headers: Content-Type, Authorization');
        header('Access-Control-Max-Age: 600');
    }

    private function jsonRpcError(int|string|null $id, int $code, string $message, int $httpStatus = 400): void
    {
        http_response_code($httpStatus);
        header('Content-Type: application/json');
        echo json_encode([
            'jsonrpc' => '2.0',
            'id' => $id,
            'error' => ['code' => $code, 'message' => $message],
        ], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
    }

    /** @return array<string, string> */
    private static function readHeaders(): array
    {
        if (function_exists('getallheaders')) {
            $headers = getallheaders();
            $out = [];
            if (is_array($headers)) {
                foreach ($headers as $k => $v) {
                    $out[strtolower((string)$k)] = (string)$v;
                }
            }
            return $out;
        }
        $out = [];
        foreach ($_SERVER as $k => $v) {
            if (strncmp($k, 'HTTP_', 5) !== 0) {
                continue;
            }
            $name = strtolower(str_replace('_', '-', substr($k, 5)));
            $out[$name] = (string)$v;
        }
        return $out;
    }
}
