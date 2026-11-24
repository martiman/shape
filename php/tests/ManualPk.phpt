<?php

/**
 * Manual primary-key assignment (MANUAL-PK-PLAN) — SQLite in-memory
 * integration test covering the whole pipeline: config normalization →
 * strategy resolution → mask emission → runtime allocation → response ids →
 * validators/manifest contract.
 *
 *   vendor/bin/tester tests/ManualPk.phpt
 */

declare(strict_types=1);

use Shape\Generator\ManifestGenerator;
use Shape\Metadata\AutoValidators;
use Shape\Metadata\DatabaseIntrospector;
use Shape\ShapeApp;
use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\BatchExecutor;
use ShapeRuntime\Core\Database;
use ShapeRuntime\Core\MaskRegistry;
use ShapeRuntime\Core\ValidatorRegistry;
use Tester\Assert;

require __DIR__ . '/../vendor/autoload.php';

Tester\Environment::setup();

$buildFromEntities = new ReflectionMethod(ShapeApp::class, 'buildFromEntities');
$buildFromEntities->setAccessible(true);

// ── Schema: one legacy manual-PK table, one non-'id' PK name, one identity ──
// `INT PRIMARY KEY` deliberately avoids SQLite's `INTEGER PRIMARY KEY`
// rowid alias, mirroring a legacy table where nothing assigns the PK.
$db = new Database(['dsn' => 'sqlite::memory:']);
$db->exec('CREATE TABLE invoices (id INT NOT NULL PRIMARY KEY, title VARCHAR(50) NOT NULL)', []);
$db->exec('CREATE TABLE ledger (entry_id INT NOT NULL PRIMARY KEY, memo VARCHAR(100) NOT NULL)', []);
$db->exec('CREATE TABLE notes (id INTEGER PRIMARY KEY AUTOINCREMENT, body TEXT)', []);

$introspector = new DatabaseIntrospector($db);
$entities = $introspector->introspect(['*' => ['admin']], [], []);

// ── Strategy resolution + mask emission (auto rule) ──────────────────────
$config = $buildFromEntities->invoke(null, ['pk_assignment' => 'auto'], $entities, 'sqlite');

Assert::same('id', $config['masks']['invoices']['pk'] ?? null);
Assert::same('manual', $config['masks']['invoices']['pk_strategy'] ?? null);
Assert::same('entry_id', $config['masks']['ledger']['pk'] ?? null);
Assert::same('manual', $config['masks']['ledger']['pk_strategy'] ?? null);
// Identity/autoincrement PK stays db-assigned; `pk` is still emitted.
Assert::same('id', $config['masks']['notes']['pk'] ?? null);
Assert::false(isset($config['masks']['notes']['pk_strategy']));

// Per-table 'db' override wins over auto detection.
$dbOverride = $buildFromEntities->invoke(null, ['pk_assignment' => ['*' => 'auto', 'invoices' => 'db']], $entities, 'sqlite');
Assert::false(isset($dbOverride['masks']['invoices']['pk_strategy']));
Assert::same('manual', $dbOverride['masks']['ledger']['pk_strategy'] ?? null);

// ── Runtime: sequential allocation 1, 2, 3 ───────────────────────────────
$registry = new MaskRegistry($config['masks']);
$executor = new BatchExecutor($db, $registry, new ValidatorRegistry($config['validators'] ?? []));
$auth = ['role' => 'admin'];

// First insert outside a batch transaction (allocator wraps its own).
$r1 = $executor->executeMutations([
    ['ref' => 'm1', 'table' => 'invoices', 'action' => 'insert', 'data' => ['title' => 'first']],
], $auth);
Assert::same(1, $r1['results'][0]['id']);
Assert::same(1, $r1['results'][0]['affected']);

// Next two inside one transaction (allocator joins the batch transaction).
$r23 = $executor->executeMutations([
    ['ref' => 'm2', 'table' => 'invoices', 'action' => 'insert', 'data' => ['title' => 'second']],
    ['ref' => 'm3', 'table' => 'invoices', 'action' => 'insert', 'data' => ['title' => 'third']],
], $auth, true);
Assert::same(2, $r23['results'][0]['id']);
Assert::same(3, $r23['results'][1]['id']);

// Non-'id' PK name: allocation threads the mask's `pk` (§2.5 fix).
$rl = $executor->executeMutations([
    ['ref' => 'l1', 'table' => 'ledger', 'action' => 'insert', 'data' => ['memo' => 'opening']],
], $auth);
Assert::same(1, $rl['results'][0]['id']);

// ── Explicit client-supplied PK wins (pass-through) ──────────────────────
$r42 = $executor->executeMutations([
    ['ref' => 'm4', 'table' => 'invoices', 'action' => 'insert', 'data' => ['id' => 42, 'title' => 'explicit']],
], $auth);
Assert::same(42, $r42['results'][0]['id']);
$row42 = $db->run('SELECT id, title FROM invoices WHERE id = :id', ['id' => 42]);
Assert::same(1, $row42['count']);

// Allocation continues after the explicit id (MAX+1 = 43).
$r43 = $executor->executeMutations([
    ['ref' => 'm5', 'table' => 'invoices', 'action' => 'insert', 'data' => ['title' => 'after explicit']],
], $auth);
Assert::same(43, $r43['results'][0]['id']);

// Identity table untouched by the manual path.
$rn = $executor->executeMutations([
    ['ref' => 'n1', 'table' => 'notes', 'action' => 'insert', 'data' => ['body' => 'hello']],
], $auth);
Assert::same(1, $rn['results'][0]['id']);

// ── Validators: a manual-strategy int PK is never required on insert ─────
// (AutoValidators derives notEmpty only for string columns; the inserts
// above already prove a missing id passes validation end-to-end.)
Assert::same([], AutoValidators::fromColumn([
    'type' => 'int', 'nullable' => false, 'auto_increment' => false, 'has_default' => false, 'length' => null,
]));
Assert::same([], AutoValidators::fromColumn([
    'type' => 'decimal', 'nullable' => false, 'auto_increment' => false, 'has_default' => false, 'length' => null,
]));

// ── Manifest contract: pk / pk_strategy keys + generated PK rendering ────
$manifest = (new ManifestGenerator())->generate($entities, $config['masks'], []);
$invoicesEntity = null;
foreach ($manifest['entities'] as $entity) {
    if ($entity['table'] === 'invoices') {
        $invoicesEntity = $entity;
    }
}
Assert::notNull($invoicesEntity);
Assert::same('id', $invoicesEntity['masks']['invoices']['pk'] ?? null);
Assert::same('manual', $invoicesEntity['masks']['invoices']['pk_strategy'] ?? null);
$idField = null;
foreach ($invoicesEntity['fields'] as $field) {
    if ($field['name'] === 'id') {
        $idField = $field;
    }
}
Assert::true($idField['autoIncrement'] ?? false); // manual PK presents as generated

// ── Config validation ─────────────────────────────────────────────────────
Assert::exception(
    fn() => $buildFromEntities->invoke(null, ['pk_assignment' => ['*' => 'bogus']], $entities, 'sqlite'),
    ApiException::class,
    "Invalid pk_assignment entry '*' => 'bogus'."
);

// Forcing manual onto a composite PK is a build-time error.
$db2 = new Database(['dsn' => 'sqlite::memory:']);
$db2->exec('CREATE TABLE pairs (a INT NOT NULL, b INT NOT NULL, PRIMARY KEY (a, b))', []);
$db2->exec('CREATE TABLE codes (code VARCHAR(10) NOT NULL PRIMARY KEY, label VARCHAR(20))', []);
$entities2 = (new DatabaseIntrospector($db2))->introspect(['*' => ['admin']], [], []);

Assert::exception(
    fn() => $buildFromEntities->invoke(null, ['pk_assignment' => ['pairs' => 'manual']], $entities2, 'sqlite'),
    ApiException::class,
    "pk_assignment forces 'manual' on table 'pairs', which has a composite primary key."
);
Assert::exception(
    fn() => $buildFromEntities->invoke(null, ['pk_assignment' => ['codes' => 'manual']], $entities2, 'sqlite'),
    ApiException::class,
    "pk_assignment forces 'manual' on table 'codes', which has a non-integer primary key (code: varchar)."
);
// Under 'auto' the same tables resolve to db-assigned without error.
$config2 = $buildFromEntities->invoke(null, [], $entities2, 'sqlite');
Assert::false(isset($config2['masks']['pairs']['pk_strategy']));
Assert::false(isset($config2['masks']['codes']['pk_strategy']));

// ── Auth-table runtime lookup (LoginService / OidcVerifier path) ──────────
Assert::same('manual', Database::resolvePkAssignmentMode(['*' => 'auto', 'users' => 'manual'], 'users'));
Assert::same('auto', Database::resolvePkAssignmentMode(['users' => 'manual'], 'refresh_tokens'));
Assert::same('manual', Database::resolvePkAssignmentMode('manual', 'users'));
Assert::same('auto', Database::resolvePkAssignmentMode(null, 'users'));

// Direct shared-helper call against a legacy users-like table.
$db3 = new Database(['dsn' => 'sqlite::memory:']);
$db3->exec('CREATE TABLE users (id INT NOT NULL PRIMARY KEY, email VARCHAR(100) NOT NULL, role VARCHAR(20) NOT NULL)', []);
Assert::same(1, $db3->insertWithManualPk('users', 'id', ['email' => 'a@example.com', 'role' => 'user']));
Assert::same(2, $db3->insertWithManualPk('users', 'id', ['email' => 'b@example.com', 'role' => 'admin']));
