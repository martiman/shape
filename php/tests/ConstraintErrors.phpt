<?php

/**
 * Constraint violations map to a self-documenting CONSTRAINT_VIOLATION error,
 * and the manifest type string drops driver noise (int widths, text limits).
 *
 *   vendor/bin/tester tests/ConstraintErrors.phpt
 */

declare(strict_types=1);

use Shape\Generator\ManifestGenerator;
use Shape\Metadata\FieldMeta;
use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\Database;
use Tester\Assert;

require __DIR__ . '/../vendor/autoload.php';

Tester\Environment::setup();

$db = new Database(['dsn' => 'sqlite::memory:']);
$db->exec('PRAGMA foreign_keys = ON', []);
$db->exec('CREATE TABLE parents (id INTEGER PRIMARY KEY AUTOINCREMENT, code VARCHAR(10) NOT NULL UNIQUE)', []);
$db->exec('CREATE TABLE kids (id INTEGER PRIMARY KEY AUTOINCREMENT, parent_id INT NOT NULL REFERENCES parents(id), name VARCHAR(20) NOT NULL)', []);
$db->exec("INSERT INTO parents (code) VALUES ('a')", []);

$catch = static function (callable $fn): ?ApiException {
    try {
        $fn();
    } catch (ApiException $e) {
        return $e;
    }
    return null;
};

$e = $catch(fn() => $db->exec("INSERT INTO parents (code) VALUES ('a')", []));
Assert::same('CONSTRAINT_VIOLATION', $e?->apiCode());
Assert::same(409, $e?->status());
Assert::same('UNIQUE_VIOLATION', $e?->details()['detail'] ?? null);
Assert::true(isset($e?->details()['hint']));

$e = $catch(fn() => $db->exec("INSERT INTO kids (parent_id, name) VALUES (999, 'x')", []));
Assert::same('FK_VIOLATION', $e?->details()['detail'] ?? null);

$e = $catch(fn() => $db->exec('INSERT INTO kids (parent_id, name) VALUES (1, NULL)', []));
Assert::same('NOT_NULL_VIOLATION', $e?->details()['detail'] ?? null);

// insertReturningId goes through the same mapping.
$e = $catch(fn() => $db->insertReturningId("INSERT INTO parents (code) VALUES ('a')", []));
Assert::same('UNIQUE_VIOLATION', $e?->details()['detail'] ?? null);

// Unrelated failures stay opaque.
$e = $catch(fn() => $db->exec('INSERT INTO missing_table (x) VALUES (1)', []));
Assert::same('DB_ERROR', $e?->apiCode());
Assert::same(500, $e?->status());

// Manifest type display.
$display = new ReflectionMethod(ManifestGenerator::class, 'displayType');
$display->setAccessible(true);
$gen = (new ReflectionClass(ManifestGenerator::class))->newInstanceWithoutConstructor();
$type = static fn(FieldMeta $f): string => $display->invoke($gen, $f);
Assert::same('int', $type(new FieldMeta('a', 'int', 'int', precision: 10, scale: 0)));
Assert::same('text', $type(new FieldMeta('b', 'string', 'text', length: 65535)));
Assert::same('varchar(200)', $type(new FieldMeta('c', 'string', 'varchar', length: 200)));
Assert::same('decimal(10,2)', $type(new FieldMeta('d', 'float', 'decimal', precision: 10, scale: 2)));
