<?php

/**
 * MSSQL support regression tests.
 *
 *   vendor/bin/tester tests/MssqlSupport.phpt
 */

declare(strict_types=1);

use Shape\Metadata\DatabaseIntrospector;
use ShapeRuntime\Core\Database;
use Shape\ShapeApp;
use Tester\Assert;

require __DIR__ . '/../vendor/autoload.php';

Tester\Environment::setup();

// DB_SCHEMA should flow from env into the generated db config so SQL Server
// deployments can introspect a non-default schema.
$buildConfig = new ReflectionMethod(ShapeApp::class, 'buildConfigFromEnv');
$buildConfig->setAccessible(true);
$config = $buildConfig->invoke(null, [
    'DB_DSN' => 'sqlsrv:Server=localhost;Database=shape',
    'DB_SCHEMA' => 'sales',
], '/tmp/shape/.env');
Assert::same('sales', $config['db']['schema'] ?? null);

// SQL Server `timestamp` is rowversion, not a user-writable datetime field.
$database = new Database(['dsn' => 'sqlite::memory:']);
$introspector = new DatabaseIntrospector($database);
$schemaFilter = new ReflectionMethod(DatabaseIntrospector::class, 'mssqlSchemaFilter');
$schemaFilter->setAccessible(true);
Assert::same('COALESCE(:schema, SCHEMA_NAME())', $schemaFilter->invoke($introspector));

$mapColumn = new ReflectionMethod(DatabaseIntrospector::class, 'mapMssqlColumn');
$mapColumn->setAccessible(true);
$column = $mapColumn->invoke($introspector, ['rv', 'timestamp', 'NO', null, 8, null, null, 0]);
Assert::same('rowversion', $column['type']);
Assert::true($column['auto_increment']);
Assert::same(null, $column['default']);
