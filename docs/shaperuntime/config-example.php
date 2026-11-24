<?php

declare(strict_types=1);

// Example configuration fragments for the current layout.
// app.php lives in: config/{app}/app.php
// env.php lives in: config/{app}/env.php

$appConfig = [
    'masks' => [
        'orderById' => [
            'sql' => 'SELECT id, created_at, total FROM orders WHERE id = :id LIMIT 1',
            'roles' => ['admin', 'user'],
            'params' => [
                'id' => ['type' => 'int', 'required' => true],
            ],
        ],
        'orders' => [
            'sql' => 'SELECT id FROM orders LIMIT 1',
            'roles' => ['admin'],
            'writable' => true,
        ],
    ],
    'validators' => [
        'orders.insert' => [
            'validators' => [
                'total' => static fn($value) => is_numeric($value) && (float)$value > 0
                    ? null
                    : 'Order total must be > 0',
            ],
            'hooks' => [
                'after' => static function (array $context): void {
                    error_log('orders.insert affected=' . (string)($context['affected'] ?? 0));
                },
            ],
        ],
    ],
    'commands' => [
        'order.process' => static function (array $params, ShapeRuntime\Core\CommandContext $context): array {
            $orderId = isset($params['order_id']) ? (int)$params['order_id'] : 0;
            if ($orderId <= 0) {
                throw new ShapeRuntime\Core\ApiException('COMMAND_VALIDATION_ERROR', 'order_id is required.', 400);
            }

            $result = $context->executeMutations([
                [
                    'table' => 'orders',
                    'action' => 'update',
                    'filter' => ['id' => $orderId],
                    'data' => ['status' => 'processing'],
                ],
            ], true);

            return [
                'status' => 200,
                'order_id' => $orderId,
                'new_status' => 'processing',
                'mutations_applied' => count($result['results'] ?? []),
            ];
        },
    ],
];

$envConfig = [
    'db' => [
        'dsn' => 'mysql:host=127.0.0.1;dbname=app;charset=utf8mb4',
        'user' => 'app',
        'pass' => 'secret',
        'options' => [],
    ],
    'auth' => [
        'driver' => 'jwt',
        'jwt_secret' => 'your-secret-key-change-me',
        'jwt_issuer' => 'api.example.com',
        'jwt_audience' => 'app.example.com',
        'access_token_ttl' => 3600,
        'refresh_token_ttl' => 2592000,
        'users_table' => 'users',
        'refresh_tokens_table' => 'refresh_tokens',
        'password_min_length' => 8,
        'assignable_roles' => ['user', 'admin'],
    ],
];

return [
    'app.php' => $appConfig,
    'env.php' => $envConfig,
];
