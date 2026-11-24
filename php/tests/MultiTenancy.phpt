<?php

/**
 * Multi-tenancy (MULTI-TENANCY-PLAN) — SQLite in-memory integration test.
 * Covers the whole PHP pipeline: config normalization → column detection →
 * mask compilation (scoped + unscoped variants) → runtime read scoping →
 * write stamping/guarding → reserved-param hygiene → build-time guards →
 * login claim + suspension → manifest rendering.
 *
 *   vendor/bin/tester tests/MultiTenancy.phpt
 */

declare(strict_types=1);

use Shape\Generator\ManifestGenerator;
use Shape\Metadata\DatabaseIntrospector;
use Shape\ShapeApp;
use ShapeRuntime\Auth\JwtIssuer;
use ShapeRuntime\Auth\LoginService;
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

/** Decode a JWT payload segment. */
$jwtPayload = static function (string $token): array {
    $seg = explode('.', $token)[1] ?? '';
    $pad = strlen($seg) % 4;
    if ($pad > 0) {
        $seg .= str_repeat('=', 4 - $pad);
    }
    $decoded = json_decode((string)base64_decode(strtr($seg, '-_', '+/')), true);
    return is_array($decoded) ? $decoded : [];
};

// ── Schema: registry + one scoped business table ──────────────────────────
$db = new Database(['dsn' => 'sqlite::memory:']);
$db->exec('CREATE TABLE tenants (id INTEGER PRIMARY KEY AUTOINCREMENT, name VARCHAR(50) NOT NULL, is_active INT NOT NULL DEFAULT 1)', []);
$db->exec('CREATE TABLE authors (id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id INT NOT NULL, name VARCHAR(50) NOT NULL)', []);
$db->exec('CREATE TABLE books (id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id INT NOT NULL, title VARCHAR(100) NOT NULL, author_id INT NOT NULL REFERENCES authors(id))', []);
$db->exec("INSERT INTO tenants (name, is_active) VALUES ('Acme', 1), ('Globex', 1)", []);

$introspector = new DatabaseIntrospector($db);
$entities = $introspector->introspect(['*' => ['user', 'superadmin']], [], []);

$appConfig = [
    'access' => ['*' => ['user', 'superadmin']],
    'tenancy' => ['unscoped_roles' => ['superadmin']],
];
$config = $buildFromEntities->invoke(null, $appConfig, $entities, 'sqlite');

// ── Mask compilation: scoped predicate + unscoped variant + carrier keys ──
Assert::contains(':__tenant', $config['masks']['books.list']['sql']);
Assert::same('tenant_id', $config['masks']['books.list']['tenant_scope'] ?? null);
Assert::same('tenant_id', $config['masks']['books']['tenant_scope'] ?? null); // writable carrier
Assert::true(isset($config['masks']['books.list']['sql_unscoped']));
Assert::notContains(':__tenant', $config['masks']['books.list']['sql_unscoped']);
Assert::contains(':__tenant', $config['masks']['books.get']['sql']);
Assert::contains(':__tenant', $config['masks']['books.byAuthorId']['sql']);
// Registry is global — never scoped — and auto-gated to unscoped roles.
Assert::false(isset($config['masks']['tenants.list']['tenant_scope']));
Assert::same(['superadmin'], $config['masks']['tenants.list']['roles'] ?? null);
Assert::same(['superadmin'], $config['masks']['tenants']['roles'] ?? null);

// ── Runtime read scoping ──────────────────────────────────────────────────
$registry = new MaskRegistry($config['masks']);
$executor = new BatchExecutor($db, $registry, new ValidatorRegistry($config['validators'] ?? []));
$executor->setTenancy($config['tenancy']);

$user1 = ['role' => 'user', 'tenant_id' => 1];
$user2 = ['role' => 'user', 'tenant_id' => 2];
$super = ['role' => 'superadmin']; // no tenant_id — sees across tenants

// Scoped inserts auto-stamp tenant_id from the token.
$executor->executeMutations([['ref' => 'a1', 'table' => 'books', 'action' => 'insert', 'data' => ['title' => 'A-one', 'author_id' => 10]]], $user1);
$executor->executeMutations([['ref' => 'a2', 'table' => 'books', 'action' => 'insert', 'data' => ['title' => 'A-two', 'author_id' => 10]]], $user1);
$executor->executeMutations([['ref' => 'b1', 'table' => 'books', 'action' => 'insert', 'data' => ['title' => 'B-one', 'author_id' => 20]]], $user2);

$rows1 = $db->run('SELECT tenant_id FROM books WHERE title = :t', ['t' => 'A-one']);
Assert::same(1, (int)$rows1['rows'][0][0]);          // stamped tenant 1
$rowsB = $db->run('SELECT tenant_id FROM books WHERE title = :t', ['t' => 'B-one']);
Assert::same(2, (int)$rowsB['rows'][0][0]);          // stamped tenant 2

// list: tenant 1 sees only its 2 rows; superadmin sees all 3.
$list1 = $executor->execute([['ref' => 'l', 'sql' => 'books.list', 'params' => []]], $user1);
Assert::same(2, $list1['results'][0]['count']);
$listSuper = $executor->execute([['ref' => 'l', 'sql' => 'books.list', 'params' => []]], $super);
Assert::same(3, $listSuper['results'][0]['count']);

// get: tenant 2's row is invisible to tenant 1 (no existence oracle).
$b1Id = (int)$db->run('SELECT id FROM books WHERE title = :t', ['t' => 'B-one'])['rows'][0][0];
$getCross = $executor->execute([['ref' => 'g', 'sql' => 'books.get', 'params' => ['id' => $b1Id]]], $user1);
Assert::same(0, $getCross['results'][0]['count']);
$getSuper = $executor->execute([['ref' => 'g', 'sql' => 'books.get', 'params' => ['id' => $b1Id]]], $super);
Assert::same(1, $getSuper['results'][0]['count']);

// byFK scoped: tenant 1 author 10 → 2 rows; tenant 2 author 20 seen by t1 → 0.
$byFk1 = $executor->execute([['ref' => 'f', 'sql' => 'books.byAuthorId', 'params' => ['author_id' => 10]]], $user1);
Assert::same(2, $byFk1['results'][0]['count']);
$byFk2 = $executor->execute([['ref' => 'f', 'sql' => 'books.byAuthorId', 'params' => ['author_id' => 20]]], $user1);
Assert::same(0, $byFk2['results'][0]['count']);

// ── Write guards ──────────────────────────────────────────────────────────
// Explicit foreign tenant_id on insert → TENANT_MISMATCH.
Assert::exception(
    fn() => $executor->executeMutations([['ref' => 'x', 'table' => 'books', 'action' => 'insert', 'data' => ['tenant_id' => 2, 'title' => 'sneaky', 'author_id' => 1]]], $user1),
    ApiException::class,
    "Cross-tenant write rejected: 'tenant_id' does not match your tenant."
);
// Matching tenant_id is accepted (idempotent re-send).
$okMatch = $executor->executeMutations([['ref' => 'ok', 'table' => 'books', 'action' => 'insert', 'data' => ['tenant_id' => 1, 'title' => 'echo', 'author_id' => 1]]], $user1);
Assert::same(1, $okMatch['results'][0]['affected']);
// Unscoped caller must supply the tenant explicitly.
Assert::exception(
    fn() => $executor->executeMutations([['ref' => 'nr', 'table' => 'books', 'action' => 'insert', 'data' => ['title' => 'no-tenant', 'author_id' => 1]]], $super),
    ApiException::class,
    "Column 'tenant_id' is required for unscoped writes."
);
// Unscoped caller may seed any tenant.
$seed = $executor->executeMutations([['ref' => 'sd', 'table' => 'books', 'action' => 'insert', 'data' => ['tenant_id' => 2, 'title' => 'seeded', 'author_id' => 1]]], $super);
Assert::same(1, $seed['results'][0]['affected']);

// Cross-tenant UPDATE / DELETE → affected 0; own-tenant → affected 1.
$crossUpd = $executor->executeMutations([['ref' => 'u', 'table' => 'books', 'action' => 'update', 'filter' => ['id' => $b1Id], 'data' => ['title' => 'hijacked']]], $user1);
Assert::same(0, $crossUpd['results'][0]['affected']);
$aId = (int)$db->run('SELECT id FROM books WHERE title = :t', ['t' => 'A-one'])['rows'][0][0];
$ownUpd = $executor->executeMutations([['ref' => 'u2', 'table' => 'books', 'action' => 'update', 'filter' => ['id' => $aId], 'data' => ['title' => 'A-one-edited']]], $user1);
Assert::same(1, $ownUpd['results'][0]['affected']);
$crossDel = $executor->executeMutations([['ref' => 'd', 'table' => 'books', 'action' => 'delete', 'filter' => ['id' => $b1Id]]], $user1);
Assert::same(0, $crossDel['results'][0]['affected']);
// A scoped UPDATE may not move a row to another tenant.
Assert::exception(
    fn() => $executor->executeMutations([['ref' => 'mv', 'table' => 'books', 'action' => 'update', 'filter' => ['id' => $aId], 'data' => ['tenant_id' => 2]]], $user1),
    ApiException::class,
    "Cross-tenant write rejected: 'tenant_id' does not match your tenant."
);

// ── Reserved-param hygiene: a client can't inject :__tenant ────────────────
Assert::exception(
    fn() => $executor->execute([['ref' => 'p', 'sql' => 'books.list', 'params' => ['__tenant' => 2]]], $user1),
    ApiException::class,
    "Parameter '__tenant' uses the reserved '__' prefix."
);

// ── Manifest rendering ─────────────────────────────────────────────────────
$manifest = (new ManifestGenerator())->generate($entities, $config['masks'], []);
$booksEntity = null;
$tenantsEntity = null;
foreach ($manifest['entities'] as $entity) {
    if ($entity['table'] === 'books') {
        $booksEntity = $entity;
    }
    if ($entity['table'] === 'tenants') {
        $tenantsEntity = $entity;
    }
}
Assert::true($booksEntity['tenantScoped'] ?? false);
Assert::same('tenant_id', $booksEntity['masks']['books.list']['tenant_scope'] ?? null);
Assert::false($tenantsEntity['tenantScoped'] ?? false);

// ── Build-time guards ──────────────────────────────────────────────────────
// Registry table must not carry the tenancy column.
$dbBadReg = new Database(['dsn' => 'sqlite::memory:']);
$dbBadReg->exec('CREATE TABLE tenants (id INTEGER PRIMARY KEY, name VARCHAR(50), tenant_id INT)', []);
$dbBadReg->exec('CREATE TABLE books (id INTEGER PRIMARY KEY, tenant_id INT NOT NULL, title VARCHAR(50) NOT NULL)', []);
$entBadReg = (new DatabaseIntrospector($dbBadReg))->introspect(['*' => ['user']], [], []);
Assert::exception(
    fn() => $buildFromEntities->invoke(null, ['tenancy' => true], $entBadReg, 'sqlite'),
    ApiException::class,
    '%A%must not carry the tenancy column%A%'
);

// Nullable tenancy column is a strict-mode error.
$dbNull = new Database(['dsn' => 'sqlite::memory:']);
$dbNull->exec('CREATE TABLE tenants (id INTEGER PRIMARY KEY, name VARCHAR(50))', []);
$dbNull->exec('CREATE TABLE books (id INTEGER PRIMARY KEY, tenant_id INT, title VARCHAR(50) NOT NULL)', []);
$entNull = (new DatabaseIntrospector($dbNull))->introspect(['*' => ['user']], [], []);
Assert::exception(
    fn() => $buildFromEntities->invoke(null, ['tenancy' => true], $entNull, 'sqlite'),
    ApiException::class,
    '%A%is nullable%A%'
);

// Strict sweep: an exposed table with neither the column nor a global entry fails.
$dbSweep = new Database(['dsn' => 'sqlite::memory:']);
$dbSweep->exec('CREATE TABLE tenants (id INTEGER PRIMARY KEY, name VARCHAR(50))', []);
$dbSweep->exec('CREATE TABLE books (id INTEGER PRIMARY KEY, tenant_id INT NOT NULL, title VARCHAR(50) NOT NULL)', []);
$dbSweep->exec('CREATE TABLE currencies (code VARCHAR(3) PRIMARY KEY, label VARCHAR(30) NOT NULL)', []);
$entSweep = (new DatabaseIntrospector($dbSweep))->introspect(['*' => ['user']], [], []);
Assert::exception(
    fn() => $buildFromEntities->invoke(null, ['tenancy' => true], $entSweep, 'sqlite'),
    ApiException::class,
    '%A%currencies%A%'
);
// global_tables clears the sweep; relaxed downgrades it.
$okGlobal = $buildFromEntities->invoke(null, ['tenancy' => ['global_tables' => ['currencies']]], $entSweep, 'sqlite');
Assert::false(isset($okGlobal['masks']['currencies']['tenant_scope']));
$okRelaxed = $buildFromEntities->invoke(null, ['tenancy' => ['enforce' => 'relaxed']], $entSweep, 'sqlite');
Assert::false(isset($okRelaxed['masks']['currencies']['tenant_scope']));

// Bad enforce value is rejected.
Assert::exception(
    fn() => $buildFromEntities->invoke(null, ['tenancy' => ['enforce' => 'loose']], $entities, 'sqlite'),
    ApiException::class,
    "Invalid tenancy.enforce value 'loose'."
);

// ── Login claim + suspension (LoginService) ────────────────────────────────
$pwHash = password_hash('secret123', PASSWORD_BCRYPT);
$authConfig = [
    'driver' => 'jwt',
    'jwt_secret' => 'test-secret-please-ignore-0123456789',
    'tenancy' => ['table' => 'tenants', 'column' => 'tenant_id', 'unscoped_roles' => ['superadmin'], 'enforce' => 'strict', 'global_tables' => [], 'check_active_per_request' => false, 'validate_fks' => false],
];
// LoginService opens its own PDO from dbConfig — point it at a temp file so
// its several short-lived connections all see the same data.
$tmpFile = tempnam(sys_get_temp_dir(), 'shape_tenancy_') . '.sqlite';
$fileDb = new Database(['dsn' => 'sqlite:' . $tmpFile]);
$fileDb->exec('CREATE TABLE tenants (id INTEGER PRIMARY KEY AUTOINCREMENT, name VARCHAR(50) NOT NULL, is_active INT NOT NULL DEFAULT 1)', []);
$fileDb->exec('CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT, email VARCHAR(100) NOT NULL, password_hash VARCHAR(255) NOT NULL, display_name VARCHAR(100), role VARCHAR(20) NOT NULL, is_active INT NOT NULL DEFAULT 1, tenant_id INT NOT NULL)', []);
$fileDb->exec('CREATE TABLE refresh_tokens (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INT NOT NULL, token_hash VARCHAR(64) NOT NULL, expires_at VARCHAR(32) NOT NULL, revoked_at VARCHAR(32), replaced_by_token_id INT, last_used_at VARCHAR(32))', []);
$fileDb->exec("INSERT INTO tenants (name, is_active) VALUES ('Acme', 1), ('Globex', 0)", []);
$fileDb->exec("INSERT INTO users (email, password_hash, display_name, role, is_active, tenant_id) VALUES ('a@acme.test', '{$pwHash}', 'A', 'admin', 1, 1)", []);
$fileDb->exec("INSERT INTO users (email, password_hash, display_name, role, is_active, tenant_id) VALUES ('g@globex.test', '{$pwHash}', 'G', 'admin', 1, 2)", []);

$login = new LoginService(['dsn' => 'sqlite:' . $tmpFile], $authConfig, new JwtIssuer($authConfig));

// Active tenant → token carries tenant_id claim.
$tok = $login->login(['email' => 'a@acme.test', 'password' => 'secret123']);
$claims = $jwtPayload($tok['access_token']);
Assert::same(1, $claims['tenant_id'] ?? null);

// Suspended tenant (Globex is_active = 0) → TENANT_SUSPENDED.
Assert::exception(
    fn() => $login->login(['email' => 'g@globex.test', 'password' => 'secret123']),
    ApiException::class,
    'Your tenant has been suspended.'
);

@unlink($tmpFile);

echo "OK\n";
