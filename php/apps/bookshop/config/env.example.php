<?php

declare(strict_types=1);

/**
 * Copy to env.local.php (git-ignored) and set real values.
 * Shape auto-merges env.local.php / env.php into config/database.php.
 */
return [
    'db' => [
        'dsn' => 'sqlite:' . __DIR__ . '/../database/bookshop.sqlite',
        'user' => '',
        'pass' => '',
    ],
    'auth' => [
        'driver' => 'jwt',
        // Generate a long random value, e.g. `php -r 'echo bin2hex(random_bytes(32));'`
        'jwt_secret' => 'CHANGE-ME',
        'jwt_issuer' => 'bookshop.local',
        'jwt_audience' => 'bookshop.clients',
        'access_token_ttl' => 3600,
        'refresh_token_ttl' => 2592000,
        'users_table' => 'users',
        'refresh_tokens_table' => 'refresh_tokens',
        'password_min_length' => 8,
        'assignable_roles' => ['user', 'admin'],
    ],
];
