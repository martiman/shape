<?php

declare(strict_types=1);

namespace ShapeRuntime\Http;

final class JsonResponse
{
    private function __construct()
    {
    }

    public static function errorAndExit(string $code, string $message, int $status = 500): never
    {
        http_response_code($status);
        header('Content-Type: application/json');
        echo json_encode([
            'error' => [
                'code' => $code,
                'message' => $message,
            ],
        ], JSON_UNESCAPED_UNICODE | JSON_UNESCAPED_SLASHES);
        exit;
    }
}
