<?php

/**
 * Multi-tenancy SIMPLE tier completion (MULTI-TENANCY-SIMPLE-PLAN) — the PHP
 * security-parity and per-operation-roles work, SQLite in-memory:
 *
 *   S1  createUser tenancy guards (stamp, TENANT_MISMATCH, escalation guard)
 *   S2  changePassword confined to the caller's tenant
 *   S3  manual-mask declaration contract (build-time CONFIG_INVALID)
 *   S4  opt-in validate_fks → TENANT_FK_MISMATCH on cross-tenant reference
 *   S5  JwtGuard claim normalization + per-request suspension gate
 *   S6  OIDC auto-provision without a tenant source fails the boot
 *   S7  built-in tenant.provision command
 *   S8  `public` cache_control on a scoped mask fails the boot
 *   S10-S12  per-operation write roles (roles_by_action)
 *
 *   vendor/bin/tester tests/TenancySimpleTier.phpt
 */

declare(strict_types=1);

use Shape\Metadata\DatabaseIntrospector;
use Shape\ShapeApp;
use ShapeRuntime\Auth\JwtGuard;
use ShapeRuntime\Auth\JwtIssuer;
use ShapeRuntime\Auth\LoginService;
use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\BatchExecutor;
use ShapeRuntime\Core\CommandExecutor;
use ShapeRuntime\Core\Database;
use ShapeRuntime\Core\MaskRegistry;
use ShapeRuntime\Core\ValidatorRegistry;
use Tester\Assert;

require __DIR__ . '/../vendor/autoload.php';

Tester\Environment::setup();

$buildFromEntities = new ReflectionMethod(ShapeApp::class, 'buildFromEntities');
$buildFromEntities->setAccessible(true);

/** @return array{code:string, status:int, details:array} */
$catch = static function (callable $fn): array {
    try {
        $fn();
    } catch (ApiException $e) {
        return ['code' => $e->apiCode(), 'status' => $e->status(), 'details' => $e->details()];
    }
    return ['code' => '(none)', 'status' => 0, 'details' => []];
};

// ── Shared schema ──────────────────────────────────────────────────────────
$dsn = 'sqlite:file:tenancy-simple?mode=memory&cache=shared';
$pdoKeepAlive = new PDO($dsn); // hold the shared in-memory db open
$db = new Database(['dsn' => $dsn]);
$db->exec('CREATE TABLE tenants (id INTEGER PRIMARY KEY AUTOINCREMENT, name VARCHAR(50) NOT NULL, is_active INT NOT NULL DEFAULT 1)', []);
$db->exec('CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id INT NOT NULL, email VARCHAR(100) NOT NULL UNIQUE, password_hash VARCHAR(100) NOT NULL, display_name VARCHAR(100), role VARCHAR(20) NOT NULL, is_active INT NOT NULL DEFAULT 1, updated_at DATETIME)', []);
$db->exec('CREATE TABLE refresh_tokens (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INT NOT NULL, token_hash VARCHAR(64) NOT NULL, expires_at DATETIME NOT NULL, revoked_at DATETIME, replaced_by_token_id INT, last_used_at DATETIME)', []);
$db->exec('CREATE TABLE authors (id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id INT NOT NULL, name VARCHAR(50) NOT NULL)', []);
$db->exec('CREATE TABLE books (id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id INT NOT NULL, title VARCHAR(100) NOT NULL, author_id INT NOT NULL REFERENCES authors(id))', []);
$db->exec("INSERT INTO tenants (name, is_active) VALUES ('Acme', 1), ('Globex', 1)", []);
$db->exec("INSERT INTO authors (tenant_id, name) VALUES (1, 'A-author'), (2, 'B-author')", []);

$tenancy = [
    'table' => 'tenants',
    'column' => 'tenant_id',
    'unscoped_roles' => ['superadmin'],
    'enforce' => 'strict',
    'global_tables' => [],
    'check_active_per_request' => false,
    'validate_fks' => true,
    'oidc_tenant_claim' => null,
    'oidc_auto_provision_tenant' => null,
];
$dbConfig = ['dsn' => $dsn];
$authConfig = [
    'driver' => 'jwt',
    'jwt_secret' => str_repeat('s', 40),
    'assignable_roles' => ['user', 'admin', 'manager', 'superadmin'],
    'tenancy' => $tenancy,
];
$issuer = new JwtIssuer($authConfig);
$login = new LoginService($dbConfig, $authConfig, $issuer);

$scopedAdmin1 = ['sub' => '1', 'role' => 'admin', 'tenant_id' => 1];
$superadmin = ['sub' => '9', 'role' => 'superadmin', 'tenant_id' => null];

// ── S1: createUser tenancy guards ─────────────────────────────────────────
// Scoped admin: differing payload tenant is a hard error, never a silent stamp.
$err = $catch(fn() => $login->createUser(['email' => 'x1@t.io', 'password' => 'secret123', 'tenant_id' => 2], $scopedAdmin1));
Assert::same('VALIDATION_ERROR', $err['code']);
Assert::same('TENANT_MISMATCH', $err['details']['detail'] ?? null);

// Scoped admin cannot mint a superadmin (privilege-escalation guard §2.6).
$err = $catch(fn() => $login->createUser(['email' => 'x2@t.io', 'password' => 'secret123', 'role' => 'superadmin'], $scopedAdmin1));
Assert::same('AUTH_FORBIDDEN', $err['code']);
Assert::same(403, $err['status']);

// Scoped admin creating normally → row stamped with the caller's tenant.
$created = $login->createUser(['email' => 'ok1@t.io', 'password' => 'secret123'], $scopedAdmin1);
Assert::same(1, (int)$db->run('SELECT tenant_id FROM users WHERE email = :e', ['e' => 'ok1@t.io'])['rows'][0][0]);
Assert::same(1, (int)$created['tenant_id']);

// Unscoped caller must name the target tenant explicitly.
$err = $catch(fn() => $login->createUser(['email' => 'x3@t.io', 'password' => 'secret123'], $superadmin));
Assert::same('VALIDATION_ERROR', $err['code']);
Assert::same('TENANT_MISSING', $err['details']['detail'] ?? null);

// Unscoped caller with an explicit tenant seeds another tenant's users.
$login->createUser(['email' => 'ok2@t.io', 'password' => 'secret123', 'tenant_id' => 2], $superadmin);
Assert::same(2, (int)$db->run('SELECT tenant_id FROM users WHERE email = :e', ['e' => 'ok2@t.io'])['rows'][0][0]);

// Unscoped-role user rows still get a home tenant (NOT NULL under strict tenancy).
$login->createUser(['email' => 'root@t.io', 'password' => 'secret123', 'role' => 'superadmin', 'tenant_id' => 1], $superadmin);
Assert::same(1, (int)$db->run('SELECT tenant_id FROM users WHERE email = :e', ['e' => 'root@t.io'])['rows'][0][0]);

// ── S2: changePassword confined to the caller's tenant ────────────────────
// ok2 lives in tenant 2 — a tenant-1 admin cannot reset its password.
$err = $catch(fn() => $login->changePassword(['email' => 'ok2@t.io', 'new_password' => 'newsecret1'], $scopedAdmin1));
Assert::same('AUTH_USER_NOT_FOUND', $err['code']);
// The unscoped caller can.
$out = $login->changePassword(['email' => 'ok2@t.io', 'new_password' => 'newsecret1'], $superadmin);
Assert::true($out['updated']);
// And a tenant-1 admin still reaches tenant-1 users.
$out = $login->changePassword(['email' => 'ok1@t.io', 'new_password' => 'newsecret2'], $scopedAdmin1);
Assert::true($out['updated']);

// ── Build the app config (also exercises S3/S8 happy path) ────────────────
$introspector = new DatabaseIntrospector($db);
$entities = $introspector->introspect([
    'authors' => ['user', 'admin', 'manager', 'superadmin'],
    // S10-S12: per-operation split — everyone writes, only manager deletes.
    'books' => [
        'read' => ['user', 'admin', 'manager', 'superadmin'],
        'write' => ['user', 'admin'],
        'delete' => ['manager'],
    ],
], [], []);

$appConfig = [
    'access' => [
        'authors' => ['user', 'admin', 'manager', 'superadmin'],
        'books' => ['read' => ['user', 'admin', 'manager', 'superadmin'], 'write' => ['user', 'admin'], 'delete' => ['manager']],
    ],
    'tenancy' => ['unscoped_roles' => ['superadmin'], 'validate_fks' => true],
    'auth' => $authConfig,
];
$config = $buildFromEntities->invoke(null, $appConfig, $entities, 'sqlite');

// ── S10-S12: per-operation write roles ─────────────────────────────────────
// Generator: union gate + roles_by_action carrier (cascade fills create/update).
Assert::same(['user', 'admin', 'manager'], $config['masks']['books']['roles']);
Assert::same(
    ['create' => ['user', 'admin'], 'update' => ['user', 'admin'], 'delete' => ['manager']],
    $config['masks']['books']['roles_by_action'] ?? null
);
// No action key on authors → no roles_by_action (flat semantics unchanged).
Assert::false(isset($config['masks']['authors']['roles_by_action']));

$registry = new MaskRegistry($config['masks']);
$executor = new BatchExecutor($db, $registry, new ValidatorRegistry($config['validators'] ?? []));
$executor->setTenancy($config['tenancy']);

$user1 = ['sub' => '2', 'role' => 'user', 'tenant_id' => 1];
$manager1 = ['sub' => '3', 'role' => 'manager', 'tenant_id' => 1];

// user may create…
$res = $executor->executeMutations([['ref' => 'c1', 'table' => 'books', 'action' => 'insert', 'data' => ['title' => 'Mine', 'author_id' => 1]]], $user1);
$bookId = (int)$res['results'][0]['id'];
// …and update…
$executor->executeMutations([['ref' => 'u1', 'table' => 'books', 'action' => 'update', 'filter' => ['id' => $bookId], 'data' => ['title' => 'Mine v2']]], $user1);
// …but not delete.
$err = $catch(fn() => $executor->executeMutations([['ref' => 'd1', 'table' => 'books', 'action' => 'delete', 'filter' => ['id' => $bookId]]], $user1));
Assert::same('AUTH_FORBIDDEN', $err['code']);
Assert::same(403, $err['status']);
// manager deletes (and passes the union mask gate).
$res = $executor->executeMutations([['ref' => 'd2', 'table' => 'books', 'action' => 'delete', 'filter' => ['id' => $bookId]]], $manager1);
Assert::same(1, $res['results'][0]['affected']);
// manager cannot create (create resolved to write roles, manager not in them).
$err = $catch(fn() => $executor->executeMutations([['ref' => 'c2', 'table' => 'books', 'action' => 'insert', 'data' => ['title' => 'Nope', 'author_id' => 1]]], $manager1));
Assert::same('AUTH_FORBIDDEN', $err['code']);

// ── S4: opt-in cross-tenant FK check ───────────────────────────────────────
// fk_checks compiled onto the writable mask (author target is scoped).
Assert::same('authors', $config['masks']['books']['fk_checks']['author_id']['table'] ?? null);
Assert::same('tenant_id', $config['masks']['books']['fk_checks']['author_id']['tenant_column'] ?? null);

// Tenant-1 user pointing a book at tenant-2's author → TENANT_FK_MISMATCH.
$err = $catch(fn() => $executor->executeMutations([['ref' => 'f1', 'table' => 'books', 'action' => 'insert', 'data' => ['title' => 'Sneaky', 'author_id' => 2]]], $user1));
Assert::same('VALIDATION_ERROR', $err['code']);
Assert::same('TENANT_FK_MISMATCH', $err['details']['detail'] ?? null);
// Same-tenant reference passes.
$res = $executor->executeMutations([['ref' => 'f2', 'table' => 'books', 'action' => 'insert', 'data' => ['title' => 'Legit', 'author_id' => 1]]], $user1);
Assert::same(1, $res['results'][0]['affected']);

// ── S3 + S8: manual-mask declaration contract at build time ───────────────
$contractCase = static function (array $mask) use ($buildFromEntities, $entities, $appConfig): string {
    $cfg = $appConfig;
    $cfg['masks'] = ['books.custom' => $mask];
    try {
        $buildFromEntities->invoke(null, $cfg, $entities, 'sqlite');
    } catch (ApiException $e) {
        return $e->apiCode() . ':' . $e->getMessage();
    }
    return 'OK';
};

// Undeclared manual mask referencing a scoped table (strict) → boot failure.
Assert::contains('CONFIG_INVALID', $contractCase(['sql' => 'SELECT COUNT(*) FROM books', 'roles' => ['user']]));
// Declared scope but no :__tenant placeholder in a read mask → boot failure.
Assert::contains('does not reference `__tenant`', $contractCase(['sql' => 'SELECT COUNT(*) FROM books', 'tenant_scope' => 'tenant_id', 'roles' => ['user']]));
// Correct declaration passes.
Assert::same('OK', $contractCase(['sql' => 'SELECT COUNT(*) FROM books WHERE tenant_id = :__tenant', 'tenant_scope' => 'tenant_id', 'roles' => ['user']]));
// Writable masks are exempt (probe SQL only; write path enforces the tenant).
Assert::same('OK', $contractCase(['sql' => 'SELECT id FROM books LIMIT 1', 'tenant_scope' => 'tenant_id', 'writable' => true, 'roles' => ['user']]));
// Opt-out reachable by a scoped role is a leak by construction → boot failure.
Assert::contains('opts out', $contractCase(['sql' => 'SELECT COUNT(*) FROM books', 'tenant_scope' => false, 'roles' => ['user']]));
// Opt-out confined to unscoped roles is the sanctioned cross-tenant report.
Assert::same('OK', $contractCase(['sql' => 'SELECT COUNT(*) FROM books', 'tenant_scope' => false, 'roles' => ['superadmin']]));
// S8: `public` cache_control on a scoped mask → boot failure.
Assert::contains('public cache-control', $contractCase(['sql' => 'SELECT COUNT(*) FROM books WHERE tenant_id = :__tenant', 'tenant_scope' => 'tenant_id', 'cache_control' => 'public, max-age=60', 'roles' => ['user']]));
// `private` stays fine.
Assert::same('OK', $contractCase(['sql' => 'SELECT COUNT(*) FROM books WHERE tenant_id = :__tenant', 'tenant_scope' => 'tenant_id', 'cache_control' => 'private, max-age=60', 'roles' => ['user']]));

// ── S6: OIDC auto-provision without a tenant source fails the boot ────────
$cfgOidc = $appConfig;
// audience is mandatory (SECURITY-REVIEW M2); set it so this case exercises
// the tenant-source check it actually targets.
$cfgOidc['auth']['oidc'] = ['jwks_uri' => 'https://idp/.well-known/jwks.json', 'audience' => 'shape-app', 'auto_provision' => true];
$err = $catch(fn() => $buildFromEntities->invoke(null, $cfgOidc, $entities, 'sqlite'));
Assert::same('CONFIG_INVALID', $err['code']);
// A tenant source satisfies the check.
$cfgOidc['tenancy'] = ['unscoped_roles' => ['superadmin'], 'oidc_auto_provision_tenant' => 1];
Assert::type('array', $buildFromEntities->invoke(null, $cfgOidc, $entities, 'sqlite'));

// ── S7: built-in tenant.provision command ──────────────────────────────────
Assert::true(isset($config['commands']['tenant.provision']));
Assert::contains('tenant.provision', $config['_shape']['commands']);

$commandExecutor = new CommandExecutor($db, $executor, $config['commands']);

// Gated to unscoped roles.
$err = $catch(fn() => $commandExecutor->execute('tenant.provision', ['tenant' => ['name' => 'Initech'], 'user' => ['email' => 'boss@initech.io', 'password' => 'secret123']], $scopedAdmin1));
Assert::same('AUTH_FORBIDDEN', $err['code']);

// The first admin must be tenant-scoped.
$err = $catch(fn() => $commandExecutor->execute('tenant.provision', ['tenant' => ['name' => 'Initech'], 'user' => ['email' => 'boss@initech.io', 'password' => 'secret123', 'role' => 'superadmin']], $superadmin));
Assert::same('AUTH_FORBIDDEN', $err['code']);

// Happy path: registry row + first admin, atomic, {tenant_id, user_id}.
$provisioned = $commandExecutor->execute('tenant.provision', ['tenant' => ['name' => 'Initech'], 'user' => ['email' => 'boss@initech.io', 'password' => 'secret123']], $superadmin);
Assert::true($provisioned['tenant_id'] > 0);
Assert::true($provisioned['user_id'] > 0);
$row = $db->run('SELECT tenant_id, role FROM users WHERE email = :e', ['e' => 'boss@initech.io'])['rows'][0];
Assert::same((int)$provisioned['tenant_id'], (int)$row[0]);
Assert::same('admin', (string)$row[1]);

// ── S5: JwtGuard claim normalization + per-request suspension gate ─────────
$guardConfig = $authConfig;
$guardConfig['tenancy']['check_active_per_request'] = true;
$guard = new JwtGuard($guardConfig, $db);

$headersFor = static fn(string $token): array => ['authorization' => 'Bearer ' . $token];

// Pre-tenancy token (no claim) → AUTH_INVALID with the re-login hint.
$legacy = $issuer->issue(['sub' => '1', 'role' => 'user'], 600);
$err = $catch(fn() => $guard->authorize($headersFor($legacy)));
Assert::same('AUTH_INVALID', $err['code']);
Assert::contains('Re-authenticate', $err['details']['hint'] ?? '');

// Unscoped role normalizes to tenant_id = null.
$superToken = $issuer->issue(['sub' => '9', 'role' => 'superadmin', 'tenant_id' => 1], 600);
$ctx = $guard->authorize($headersFor($superToken));
Assert::null($ctx['tenant_id']);

// Active tenant passes; suspended tenant is cut off immediately per request.
$scopedToken = $issuer->issue(['sub' => '2', 'role' => 'user', 'tenant_id' => 2], 600);
$ctx = $guard->authorize($headersFor($scopedToken));
Assert::same(2, (int)$ctx['tenant_id']);
$db->exec('UPDATE tenants SET is_active = 0 WHERE id = 2', []);
$err = $catch(fn() => $guard->authorize($headersFor($scopedToken)));
Assert::same('TENANT_SUSPENDED', $err['code']);
$db->exec('UPDATE tenants SET is_active = 1 WHERE id = 2', []);

// ── Manifest carries roles_by_action (cross-runtime snake_case key) ───────
$manifest = (new Shape\Generator\ManifestGenerator())->generate($config['_shape']['entities'], $config['masks'], $config['_shape']['commands']);
$bookEntity = null;
foreach ($manifest['entities'] as $entity) {
    if (($entity['table'] ?? null) === 'books') {
        $bookEntity = $entity;
    }
}
Assert::type('array', $bookEntity);
Assert::same(['manager'], $bookEntity['masks']['books']['roles_by_action']['delete'] ?? null);
