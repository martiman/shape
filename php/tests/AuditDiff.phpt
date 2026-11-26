<?php

/**
 * Audit Tier 2: before/after VALUES in audit_log (opt-in `audit_diff`), one entry per affected
 * row, sensitive columns redacted, loud failure when the audit table lacks the value columns.
 *
 *   vendor/bin/tester tests/AuditDiff.phpt
 */

declare(strict_types=1);

use Shape\Audit\AuditLogger;
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

$make = static function (bool $valueColumns, ?array $tables, array $exclude) use ($buildFromEntities): array {
    $db = new Database(['dsn' => 'sqlite::memory:']);
    $db->exec('CREATE TABLE notes (id INTEGER PRIMARY KEY AUTOINCREMENT, grp INT NOT NULL, title VARCHAR(50) NOT NULL, body VARCHAR(200) NULL, password_hash VARCHAR(80) NULL)', []);
    $db->exec('CREATE TABLE other (id INTEGER PRIMARY KEY AUTOINCREMENT, name VARCHAR(50) NOT NULL)', []);
    $db->exec('CREATE TABLE audit_log (id INTEGER PRIMARY KEY AUTOINCREMENT, occurred_at TEXT NOT NULL, actor_id TEXT NULL, actor_email TEXT NULL, actor_role TEXT NULL, table_name TEXT NOT NULL, row_id TEXT NULL, action TEXT NOT NULL, changed TEXT NULL'
        . ($valueColumns ? ', old_values TEXT NULL, new_values TEXT NULL' : '') . ')', []);
    $access = ['notes' => ['user', 'admin'], 'other' => ['user', 'admin']];
    $entities = (new DatabaseIntrospector($db))->introspect($access, [], ['audit_log']);
    $config = $buildFromEntities->invoke(null, ['access' => $access], $entities, 'sqlite');
    $executor = new BatchExecutor($db, new MaskRegistry($config['masks']), new ValidatorRegistry($config['validators'] ?? []));
    $logger = new AuditLogger($db, 'audit_log', true);
    $executor->setAuditLogger(static fn($t, $a, $rid, $auth, $cols, $old = null, $new = null) => $logger->log($t, $a, $rid, $auth, $cols, $old, $new));
    $executor->setAuditDiff($tables, $exclude, static fn() => $logger->assertValueColumns());
    return [$db, $executor];
};

$ctx = ['id' => 7, 'role' => 'user', 'email' => 'u@x'];
$mut = static fn(BatchExecutor $e, array $m) => $e->executeMutations([['ref' => 'm'] + $m], $ctx)['results'][0];
$audit = static function (Database $db): array {
    $r = $db->run('SELECT table_name, row_id, action, changed, old_values, new_values FROM audit_log ORDER BY id', []);
    return array_map(static function (array $row) use ($r): array {
        $o = array_combine($r['columns'], $row);
        $o['old'] = $o['old_values'] === null ? null : json_decode((string)$o['old_values'], true);
        $o['new'] = $o['new_values'] === null ? null : json_decode((string)$o['new_values'], true);
        return $o;
    }, $r['rows']);
};

// ── insert: new values, secrets redacted, pk included ────────────────────
[$db, $ex] = $make(true, null, ['notes.body']);
$id = $mut($ex, ['table' => 'notes', 'action' => 'insert', 'data' => ['grp' => 1, 'title' => 'first', 'body' => 'private text', 'password_hash' => 'bcrypt$abc']])['id'];
$a = $audit($db);
Assert::count(1, $a);
Assert::same('insert', $a[0]['action']);
Assert::same((string)$id, $a[0]['row_id']);
Assert::null($a[0]['old']);
Assert::same('first', $a[0]['new']['title']);
Assert::same('[redacted]', $a[0]['new']['password_hash']);   // sensitive by name
Assert::same('[redacted]', $a[0]['new']['body']);            // excluded by config (table.column)

// ── update: only columns that really changed; no-op columns skipped ──────
$mut($ex, ['table' => 'notes', 'action' => 'update', 'filter' => ['id' => $id], 'data' => ['title' => 'renamed', 'grp' => 1]]);
$a = $audit($db);
Assert::count(2, $a);
Assert::same(['title' => 'first'], $a[1]['old']);
Assert::same(['title' => 'renamed'], $a[1]['new']);
Assert::same((string)$id, $a[1]['row_id']);

// ── multi-row update by a non-pk filter: one entry per row with its own id ─
$mut($ex, ['table' => 'notes', 'action' => 'insert', 'data' => ['grp' => 2, 'title' => 'g2a']]);
$mut($ex, ['table' => 'notes', 'action' => 'insert', 'data' => ['grp' => 2, 'title' => 'g2b']]);
$before = count($audit($db));
$mut($ex, ['table' => 'notes', 'action' => 'update', 'filter' => ['grp' => 2], 'data' => ['title' => 'same']]);
$a = array_slice($audit($db), $before);
Assert::count(2, $a);
Assert::notSame($a[0]['row_id'], $a[1]['row_id']);
Assert::same(['g2a', 'g2b'], [$a[0]['old']['title'], $a[1]['old']['title']]);

// ── delete: full old row (redacted) ──────────────────────────────────────
$before = count($audit($db));
$mut($ex, ['table' => 'notes', 'action' => 'delete', 'filter' => ['id' => $id]]);
$a = array_slice($audit($db), $before);
Assert::count(1, $a);
Assert::same('delete', $a[0]['action']);
Assert::same('renamed', $a[0]['old']['title']);
Assert::same('[redacted]', $a[0]['old']['password_hash']);
Assert::null($a[0]['new']);

// ── touching nothing still logs a Tier-1 entry, without values ───────────
$before = count($audit($db));
$mut($ex, ['table' => 'notes', 'action' => 'update', 'filter' => ['id' => 9999], 'data' => ['title' => 'x']]);
$a = array_slice($audit($db), $before);
Assert::count(1, $a);
Assert::null($a[0]['old']);
Assert::null($a[0]['new']);

// ── per-table opt-in: tables not listed keep Tier-1 behaviour ────────────
[$db2, $ex2] = $make(true, ['notes'], []);
$mut($ex2, ['table' => 'other', 'action' => 'insert', 'data' => ['name' => 'plain']]);
$mut($ex2, ['table' => 'notes', 'action' => 'insert', 'data' => ['grp' => 1, 'title' => 'listed']]);
$a = $audit($db2);
Assert::null($a[0]['new']);                 // other: names only
Assert::same('listed', $a[1]['new']['title']); // notes: values

// ── audit_diff on but audit_log lacks the columns: fails BEFORE the data changes ─
[$db3, $ex3] = $make(false, null, []);
try {
    $mut($ex3, ['table' => 'notes', 'action' => 'insert', 'data' => ['grp' => 1, 'title' => 'never']]);
    Assert::fail('expected AUDIT_CONFIG_ERROR');
} catch (ApiException $e) {
    Assert::same('AUDIT_CONFIG_ERROR', $e->apiCode());
    Assert::contains('old_values', (string)($e->details()['hint'] ?? ''));
}
Assert::same(0, (int)$db3->run('SELECT COUNT(*) FROM notes', [])['rows'][0][0]);

echo "OK\n";
