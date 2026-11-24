<?php

/**
 * Regression tests for config-backed mask SQL files.
 *
 *   vendor/bin/tester tests/ConfigSqlFile.phpt
 */

declare(strict_types=1);

use Shape\ShapeApp;
use ShapeRuntime\Core\ApiException;
use Tester\Assert;

require __DIR__ . '/../vendor/autoload.php';

Tester\Environment::setup();

$base = sys_get_temp_dir() . '/shape-sqlfile-' . bin2hex(random_bytes(4));
@mkdir($base . '/masks', 0777, true);

$cleanup = static function (string $path) use (&$cleanup): void {
    if (!is_dir($path)) {
        return;
    }
    $items = glob($path . '/*');
    if (is_array($items)) {
        foreach ($items as $item) {
            if (is_dir($item)) {
                $cleanup($item);
                continue;
            }
            @unlink($item);
        }
    }
    @rmdir($path);
};

try {
    file_put_contents($base . '/masks/recent.sql', "\xEF\xBB\xBFSELECT 1 AS answer");
    file_put_contents($base . '/config.php', <<<'PHP'
<?php
return [
    'masks' => [
        'recent' => [
            'sqlFile' => 'masks/recent.sql',
        ],
    ],
];
PHP);

    $loadConfig = new ReflectionMethod(ShapeApp::class, 'loadConfig');
    $loadConfig->setAccessible(true);

    $config = $loadConfig->invoke(null, $base . '/config.php');
    Assert::same('SELECT 1 AS answer', $config['masks']['recent']['sql'] ?? null);
    Assert::false(isset($config['masks']['recent']['sqlFile']));

    file_put_contents($base . '/bad-both.php', <<<'PHP'
<?php
return [
    'masks' => [
        'bad' => [
            'sql' => 'SELECT 1',
            'sqlFile' => 'masks/recent.sql',
        ],
    ],
];
PHP);

    try {
        $loadConfig->invoke(null, $base . '/bad-both.php');
        Assert::fail('Expected CONFIG_INVALID when sql and sqlFile are both set.');
    } catch (ApiException $e) {
        Assert::same('CONFIG_INVALID', $e->apiCode());
        Assert::true(str_contains($e->getMessage(), 'both `sql` and `sqlFile`'));
    }

    file_put_contents($base . '/bad-abs.php', <<<'PHP'
<?php
return [
    'masks' => [
        'bad' => [
            'sqlFile' => '/tmp/forbidden.sql',
        ],
    ],
];
PHP);

    try {
        $loadConfig->invoke(null, $base . '/bad-abs.php');
        Assert::fail('Expected CONFIG_INVALID for an absolute sqlFile path.');
    } catch (ApiException $e) {
        Assert::same('CONFIG_INVALID', $e->apiCode());
        Assert::true(str_contains($e->getMessage(), 'absolute or home-relative'));
    }
} finally {
    $cleanup($base);
}
