<?php

declare(strict_types=1);

namespace ShapeRuntime\Http;

use ShapeRuntime\Auth\AuthGuard;
use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\BatchExecutor;
use Throwable;

final class QueryHandler
{
    public function __construct(
        private readonly BatchExecutor $executor,
        private readonly AuthGuard $authGuard
    ) {
    }

    public function handle(): void
    {
        header('Content-Type: application/json');

        try {
            if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'POST') {
                throw new ApiException('BAD_REQUEST', 'Only POST is allowed.', 405);
            }

            $headers = $this->normalizeHeaders($this->readHeaders());
            $authContext = $this->authGuard->authorize($headers);

            $raw = file_get_contents('php://input');
            if (!is_string($raw) || trim($raw) === '') {
                throw new ApiException('INVALID_JSON', 'Request body is empty.', 400);
            }

            $payload = json_decode($raw, true);
            if (!is_array($payload)) {
                throw new ApiException('INVALID_JSON', 'Body must be valid JSON object.', 400);
            }

            $queries = $payload['queries'] ?? null;
            if (!is_array($queries) || !array_is_list($queries)) {
                throw new ApiException('QUERY_VALIDATION_ERROR', 'Field queries must be an array.', 400);
            }

            $response = $this->executor->execute($queries, $authContext);
            http_response_code(200);
            echo json_encode($response, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        } catch (ApiException $e) {
            http_response_code($e->status());
            $error = [
                'code' => $e->apiCode(),
                'message' => $e->getMessage(),
            ];
            if ($e->ref() !== null) {
                $error['ref'] = $e->ref();
            }
            echo json_encode(['error' => $error], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        } catch (Throwable $e) {
            http_response_code(500);
            echo json_encode([
                'error' => [
                    'code' => 'INTERNAL_ERROR',
                    'message' => 'Unexpected server error.',
                ],
            ], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        }
    }

    /** @return array<string, string> */
    private function normalizeHeaders(array $headers): array
    {
        $out = [];
        foreach ($headers as $k => $v) {
            $out[strtolower((string)$k)] = (string)$v;
        }
        return $out;
    }

    /** @return array<string, string> */
    private function readHeaders(): array
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
}
