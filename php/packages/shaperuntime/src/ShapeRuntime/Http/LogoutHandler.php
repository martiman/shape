<?php

declare(strict_types=1);

namespace ShapeRuntime\Http;

use ShapeRuntime\Auth\LoginService;
use ShapeRuntime\Core\ApiException;
use Throwable;

final class LogoutHandler
{
    public function __construct(private readonly LoginService $loginService)
    {
    }

    public function handle(): void
    {
        header('Content-Type: application/json');

        try {
            if (($_SERVER['REQUEST_METHOD'] ?? 'GET') !== 'POST') {
                throw new ApiException('BAD_REQUEST', 'Only POST is allowed.', 405);
            }

            $raw = file_get_contents('php://input');
            if (!is_string($raw) || trim($raw) === '') {
                throw new ApiException('INVALID_JSON', 'Request body is empty.', 400);
            }

            $payload = json_decode($raw, true);
            if (!is_array($payload)) {
                throw new ApiException('INVALID_JSON', 'Body must be valid JSON object.', 400);
            }

            $result = $this->loginService->logout($payload);
            http_response_code(200);
            echo json_encode($result, JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        } catch (ApiException $e) {
            http_response_code($e->status());
            echo json_encode([
                'error' => [
                    'code' => $e->apiCode(),
                    'message' => $e->getMessage(),
                ],
            ], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
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
}
