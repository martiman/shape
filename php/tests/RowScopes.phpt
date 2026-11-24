<?php

/**
 * Row-level security by dimension (ROW-SCOPES-PHP-PLAN §4) — SQLite in-memory.
 * The `department_id` dimension composes with tenancy: predicates AND together,
 * dimension-unscoped roles take the wrap path (list / get / COUNT), multi-valued
 * claims bind IN, writes stamp/guard with SCOPE_MISMATCH / SCOPE_REQUIRED
 * (nested relations and soft delete included), clients cannot smuggle
 * `__scope_*` params, config and mask-contract violations fail the boot,
 * auth claims round-trip login → refresh (users column and claim_table
 * variants), pre-feature tokens are rejected with AUTH_INVALID, and the
 * manifest carries the byte-exact row-scope keys. Mirror of the .NET
 * RowScopesTests.
 *
 *   vendor/bin/tester tests/RowScopes.phpt
 */

declare(strict_types=1);

use Shape\Generator\ManifestGenerator;
use Shape\Mcp\McpServer;
use Shape\Metadata\DatabaseIntrospector;
use Shape\ShapeApp;
use ShapeRuntime\Auth\JwtGuard;
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

/** @return array{code:string, status:int, details:array} */
$catch = static function (callable $fn): array {
    try {
        $fn();
    } catch (ApiException $e) {
        return ['code' => $e->apiCode(), 'status' => $e->status(), 'details' => $e->details()];
    }
    return ['code' => '(none)', 'status' => 0, 'details' => []];
};

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

// ── Schema: registry + parent/child tables scoped by tenant AND department ─
$db = new Database(['dsn' => 'sqlite::memory:']);
$db->exec('CREATE TABLE tenants (id INTEGER PRIMARY KEY AUTOINCREMENT, name VARCHAR(50) NOT NULL, is_active INT NOT NULL DEFAULT 1)', []);
$db->exec('CREATE TABLE rs_authors (id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id INT NOT NULL, department_id INT NOT NULL, name VARCHAR(50) NOT NULL)', []);
$db->exec('CREATE TABLE rs_books (id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id INT NOT NULL, department_id INT NOT NULL, title VARCHAR(100) NOT NULL, author_id INT NOT NULL REFERENCES rs_authors(id), deleted_at TIMESTAMP)', []);
$db->exec("INSERT INTO tenants (name) VALUES ('Acme'), ('Globex')", []);

$introspector = new DatabaseIntrospector($db);
$access = [
    'rs_authors' => ['user', 'controller', 'superadmin'],
    'rs_books' => ['user', 'controller', 'superadmin'],
];
$entities = $introspector->introspect($access, [], []);

/** Build config for a given dimension scope shape and return [config, executor]. */
$build = static function (array $deptScope, array $masks = []) use ($buildFromEntities, $entities, $db, $access): array {
    $appConfig = [
        'access' => $access,
        'tenancy' => ['unscoped_roles' => ['superadmin']],
        'scopes' => ['department_id' => $deptScope],
    ];
    if ($masks !== []) {
        $appConfig['masks'] = $masks;
    }
    $config = $buildFromEntities->invoke(null, $appConfig, $entities, 'sqlite');
    $executor = new BatchExecutor($db, new MaskRegistry($config['masks']), new ValidatorRegistry($config['validators'] ?? []));
    $executor->setScoping($config['tenancy'], $config['scopes']);
    return [$config, $executor];
};

$mutate = static fn(BatchExecutor $ex, array $ctx, string $table, string $action, array $extra): array =>
    $ex->executeMutations([array_merge(['ref' => 'm', 'table' => $table, 'action' => $action], $extra)], $ctx)['results'][0];
$insert = static fn(BatchExecutor $ex, array $ctx, string $table, array $data): array =>
    $mutate($ex, $ctx, $table, 'insert', ['data' => $data]);
$query = static fn(BatchExecutor $ex, array $ctx, string $mask, array $params = [], array $options = []): array =>
    $ex->execute([['ref' => 'q', 'sql' => $mask, 'params' => $params] + ($options !== [] ? ['options' => $options] : [])], $ctx)['results'][0];

// ── Mask compilation: predicates compose, carriers + unscoped variant ─────
[$config, $executor] = $build(['unscoped_roles' => ['controller', 'superadmin']]);
Assert::contains(':__scope_department_id', $config['masks']['rs_books.list']['sql']);
Assert::contains(':__tenant', $config['masks']['rs_books.list']['sql']);                 // predicates AND together
Assert::same(['multi' => false], $config['masks']['rs_books.list']['row_scopes']['department_id'] ?? null);
Assert::true(isset($config['masks']['rs_books.list']['sql_unscoped']));
Assert::notContains(':__scope_department_id', $config['masks']['rs_books.list']['sql_unscoped']);
Assert::notContains(':__tenant', $config['masks']['rs_books.list']['sql_unscoped']);
Assert::contains(':__scope_department_id', $config['masks']['rs_books.get']['sql']);
Assert::contains(':__scope_department_id', $config['masks']['rs_books.byAuthorId']['sql']);
// Writable mask carries the row-scope contract (drives stamping + guards).
Assert::same(['multi' => false], $config['masks']['rs_books']['row_scopes']['department_id'] ?? null);
// Registry table is never dimension-scoped (no department_id column).
Assert::false(isset($config['masks']['tenants.list']['row_scopes']));

// ── 4-quadrant matrix: 2 tenants × 2 departments ──────────────────────────
$t1d1 = ['role' => 'user', 'tenant_id' => 1, 'department_id' => 10];
$t1d2 = ['role' => 'user', 'tenant_id' => 1, 'department_id' => 20];
$t2d1 = ['role' => 'user', 'tenant_id' => 2, 'department_id' => 10];
$controllerT1 = ['role' => 'controller', 'tenant_id' => 1, 'department_id' => null]; // dept-unscoped, tenant-scoped
$super = ['role' => 'superadmin']; // unscoped for both dimensions

$a11 = (int)$insert($executor, $t1d1, 'rs_authors', ['name' => 'A-t1d1'])['id'];
$a12 = (int)$insert($executor, $t1d2, 'rs_authors', ['name' => 'A-t1d2'])['id'];
$a21 = (int)$insert($executor, $t2d1, 'rs_authors', ['name' => 'A-t2d1'])['id'];
$a22 = (int)$insert($executor, $super, 'rs_authors', ['tenant_id' => 2, 'department_id' => 20, 'name' => 'A-t2d2'])['id'];

$insert($executor, $t1d1, 'rs_books', ['title' => 't1d1', 'author_id' => $a11]);
$insert($executor, $t1d2, 'rs_books', ['title' => 't1d2', 'author_id' => $a12]);
$insert($executor, $t2d1, 'rs_books', ['title' => 't2d1', 'author_id' => $a21]);
$insert($executor, $super, 'rs_books', ['title' => 't2d2', 'tenant_id' => 2, 'department_id' => 20, 'author_id' => $a22]);

// Stamping landed each row in the intended quadrant.
Assert::same(4, (int)$db->run('SELECT COUNT(*) AS c FROM rs_books', [])['rows'][0][0]);
Assert::same([1, 10], array_map('intval', $db->run("SELECT tenant_id, department_id FROM rs_books WHERE title = 't1d1'", [])['rows'][0]));

// list — fast path (both predicates), wrap path (tenant only), unscoped (none).
Assert::same(1, $query($executor, $t1d1, 'rs_books.list')['count']);          // own quadrant only
Assert::same(2, $query($executor, $controllerT1, 'rs_books.list')['count']);  // both depts of tenant 1, never tenant 2 (wrap path)
Assert::same(4, $query($executor, $super, 'rs_books.list')['count']);         // everything

// COUNT wraps whatever SQL executes — a wrong total_count is also a leak.
Assert::same(1, $query($executor, $t1d1, 'rs_books.list', [], ['count' => true, 'top' => 1])['total_count']);
Assert::same(2, $query($executor, $controllerT1, 'rs_books.list', [], ['count' => true, 'top' => 1])['total_count']);
Assert::same(4, $query($executor, $super, 'rs_books.list', [], ['count' => true, 'top' => 1])['total_count']);

// In-memory options (the OData post-processing path) compose with — never
// replace — the compiled predicate: ordering/paging happens INSIDE the scope.
Assert::same(2, $query($executor, $controllerT1, 'rs_books.list', [], ['orderby' => [['field' => 'title', 'dir' => 'desc']]])['count']);

// get / byFK — cross-dimension rows are invisible, no existence oracle.
$d2Id = (int)$db->run("SELECT id FROM rs_books WHERE title = 't1d2'", [])['rows'][0][0];
$t2Id = (int)$db->run("SELECT id FROM rs_books WHERE title = 't2d1'", [])['rows'][0][0];
Assert::same(0, $query($executor, $t1d1, 'rs_books.get', ['id' => $d2Id])['count']);   // same tenant, other dept
Assert::same(1, $query($executor, $t1d2, 'rs_books.get', ['id' => $d2Id])['count']);
Assert::same(1, $query($executor, $controllerT1, 'rs_books.get', ['id' => $d2Id])['count']); // wrap path reaches both depts
Assert::same(0, $query($executor, $controllerT1, 'rs_books.get', ['id' => $t2Id])['count']); // …but never tenant 2
Assert::same(0, $query($executor, $t1d1, 'rs_books.byAuthorId', ['author_id' => $a12])['count']);
Assert::same(1, $query($executor, $t1d1, 'rs_books.byAuthorId', ['author_id' => $a11])['count']);

// ── Write guards ───────────────────────────────────────────────────────────
// Cross-department UPDATE / DELETE → affected 0.
Assert::same(0, $mutate($executor, $t1d1, 'rs_books', 'update', ['filter' => ['id' => $d2Id], 'data' => ['title' => 'hijacked']])['affected']);
Assert::same('t1d2', $db->run('SELECT title FROM rs_books WHERE id = :id', ['id' => $d2Id])['rows'][0][0]);
Assert::same(0, $mutate($executor, $t1d1, 'rs_books', 'delete', ['filter' => ['id' => $d2Id]])['affected']);
Assert::null($db->run('SELECT deleted_at FROM rs_books WHERE id = :id', ['id' => $d2Id])['rows'][0][0]); // not tombstoned

// Soft delete honors the guard: own quadrant tombstones, second delete = 0.
$tmpId = (int)$insert($executor, $t1d1, 'rs_books', ['title' => 'doomed', 'author_id' => $a11])['id'];
Assert::same(1, $mutate($executor, $t1d1, 'rs_books', 'delete', ['filter' => ['id' => $tmpId]])['affected']);
Assert::notNull($db->run('SELECT deleted_at FROM rs_books WHERE id = :id', ['id' => $tmpId])['rows'][0][0]);
Assert::same(0, $mutate($executor, $t1d1, 'rs_books', 'delete', ['filter' => ['id' => $tmpId]])['affected']);

// Foreign department on insert → SCOPE_MISMATCH (+ scope in details).
$err = $catch(fn() => $insert($executor, $t1d1, 'rs_books', ['title' => 'x', 'department_id' => 99, 'author_id' => $a11]));
Assert::same('VALIDATION_ERROR', $err['code']);
Assert::same(400, $err['status']);
Assert::same('SCOPE_MISMATCH', $err['details']['detail'] ?? null);
Assert::same('department_id', $err['details']['scope'] ?? null);
// Matching value is accepted (idempotent re-send).
Assert::same(1, $insert($executor, $t1d1, 'rs_books', ['title' => 'echo', 'department_id' => 10, 'author_id' => $a11])['affected']);

// UPDATE moving a row across the dimension → SCOPE_MISMATCH.
$ownId = (int)$db->run("SELECT id FROM rs_books WHERE title = 't1d1'", [])['rows'][0][0];
$err = $catch(fn() => $mutate($executor, $t1d1, 'rs_books', 'update', ['filter' => ['id' => $ownId], 'data' => ['department_id' => 20]]));
Assert::same('SCOPE_MISMATCH', $err['details']['detail'] ?? null);

// Dept-unscoped caller must name the department explicitly → SCOPE_REQUIRED.
$err = $catch(fn() => $insert($executor, $controllerT1, 'rs_books', ['title' => 'no-dept', 'author_id' => $a11]));
Assert::same('VALIDATION_ERROR', $err['code']);
Assert::same('SCOPE_REQUIRED', $err['details']['detail'] ?? null);
Assert::same('department_id', $err['details']['scope'] ?? null);
// …and may write any department inside its tenant.
Assert::same(1, $insert($executor, $controllerT1, 'rs_books', ['title' => 'seeded', 'department_id' => 20, 'author_id' => $a12])['affected']);
Assert::same(20, (int)$db->run("SELECT department_id FROM rs_books WHERE title = 'seeded'", [])['rows'][0][0]);

// Nested relations children inherit tenant + department stamping.
$nested = $executor->executeMutations([[
    'ref' => 'n',
    'table' => 'rs_authors',
    'action' => 'insert',
    'data' => ['name' => 'Nested'],
    'relations' => ['rs_books' => [['title' => 'nested-child']]],
]], $t1d1);
$childRow = $db->run("SELECT tenant_id, department_id, author_id FROM rs_books WHERE title = 'nested-child'", [])['rows'][0];
Assert::same([1, 10], [(int)$childRow[0], (int)$childRow[1]]);
Assert::same((int)$nested['results'][0]['id'], (int)$childRow[2]);

// Missing scope claim on a scoped operation (pre-feature token) → AUTH_INVALID.
$stale = ['role' => 'user', 'tenant_id' => 1]; // no department_id claim
$err = $catch(fn() => $query($executor, $stale, 'rs_books.list'));
Assert::same('AUTH_INVALID', $err['code']);
Assert::same(401, $err['status']);

// ── Bypass suite: reserved param hygiene ───────────────────────────────────
$err = $catch(fn() => $query($executor, $t1d1, 'rs_books.list', ['__scope_department_id' => 99]));
Assert::same('PARAM_VALIDATION_ERROR', $err['code']);
$err = $catch(fn() => $query($executor, $t1d1, 'rs_books.list', ['__tenant' => 2]));
Assert::same('PARAM_VALIDATION_ERROR', $err['code']);

// ── Multi-valued membership ────────────────────────────────────────────────
$db->exec('DELETE FROM rs_books', []);
[$configM, $executorM] = $build(['multi' => true]);
Assert::contains('IN (:__scope_department_id)', $configM['masks']['rs_books.list']['sql']);
Assert::same(['multi' => true], $configM['masks']['rs_books.list']['row_scopes']['department_id'] ?? null);

$member = ['role' => 'user', 'tenant_id' => 1, 'department_id' => [10, 20]];
$insert($executorM, $member, 'rs_books', ['title' => 'in-10', 'department_id' => 10, 'author_id' => $a11]);
$insert($executorM, $member, 'rs_books', ['title' => 'in-20', 'department_id' => 20, 'author_id' => $a12]);
Assert::same(2, $query($executorM, $member, 'rs_books.list')['count']);       // reads both memberships (IN)
Assert::same(1, $query($executorM, $t1d1, 'rs_books.list')['count']);         // single-membership caller sees dept 10 only

$err = $catch(fn() => $insert($executorM, $member, 'rs_books', ['title' => 'bad', 'department_id' => 30, 'author_id' => $a11]));
Assert::same('SCOPE_MISMATCH', $err['details']['detail'] ?? null);            // outside the membership set
$err = $catch(fn() => $insert($executorM, $member, 'rs_books', ['title' => 'no dept', 'author_id' => $a11]));
Assert::same('SCOPE_REQUIRED', $err['details']['detail'] ?? null);            // framework never guesses among N memberships

// UPDATE within the membership set passes; outside it → SCOPE_MISMATCH.
$in10 = (int)$db->run("SELECT id FROM rs_books WHERE title = 'in-10'", [])['rows'][0][0];
Assert::same(1, $mutate($executorM, $member, 'rs_books', 'update', ['filter' => ['id' => $in10], 'data' => ['department_id' => 20]])['affected']);
$err = $catch(fn() => $mutate($executorM, $member, 'rs_books', 'update', ['filter' => ['id' => $in10], 'data' => ['department_id' => 30]]));
Assert::same('SCOPE_MISMATCH', $err['details']['detail'] ?? null);

// ── Manifest: byte-exact cross-runtime keys (rowScopes + row_scopes) ──────
$manifest = (new ManifestGenerator())->generate($config['_shape']['entities'], $config['masks'], []);
$bookEntity = null;
foreach ($manifest['entities'] as $e) {
    if (($e['table'] ?? null) === 'rs_books') {
        $bookEntity = $e;
    }
}
Assert::type('array', $bookEntity);
Assert::same('["department_id"]', json_encode($bookEntity['rowScopes']));
Assert::same('{"department_id":{"multi":false}}', json_encode($bookEntity['masks']['rs_books.list']['row_scopes']));
Assert::same('{"department_id":{"multi":false}}', json_encode($bookEntity['masks']['rs_books']['row_scopes']));
Assert::true($bookEntity['tenantScoped'] ?? false);

$manifestM = (new ManifestGenerator())->generate($configM['_shape']['entities'], $configM['masks'], []);
foreach ($manifestM['entities'] as $e) {
    if (($e['table'] ?? null) === 'rs_books') {
        Assert::same('{"department_id":{"multi":true}}', json_encode($e['masks']['rs_books.list']['row_scopes']));
    }
}

// ── MCP parity: role-filtering is unchanged by row scopes (mirror of the .NET
//    Mcp_tools_list_is_unchanged_by_row_scopes) — scoping happens inside
//    execution, never in the advertised tool surface. ─────────────────────────
$toolNames = static function (array $cfg): array {
    $server = new McpServer($cfg, 'shape', ['role' => 'user']);
    $res = $server->handle(['jsonrpc' => '2.0', 'id' => 1, 'method' => 'tools/list']);
    $names = array_map(static fn(array $t): string => (string)($t['name'] ?? ''), $res['result']['tools'] ?? []);
    sort($names);
    return $names;
};
$unscopedCfg = $buildFromEntities->invoke(null, [
    'access' => $access,
    'tenancy' => ['unscoped_roles' => ['superadmin']],
], $entities, 'sqlite');
Assert::same($toolNames($unscopedCfg), $toolNames($config)); // identical tool lists, scoped vs unscoped

// ── Build-time guards: scopes config validation ────────────────────────────
// Non-map scopes config.
$err = $catch(function () use ($buildFromEntities, $entities, $access) {
    $buildFromEntities->invoke(null, ['access' => $access, 'scopes' => 'department_id'], $entities, 'sqlite');
});
Assert::same('CONFIG_INVALID', $err['code']);
// Invalid column identifier as key.
$err = $catch(function () use ($buildFromEntities, $entities, $access) {
    $buildFromEntities->invoke(null, ['access' => $access, 'scopes' => ['bad column!' => []]], $entities, 'sqlite');
});
Assert::same('CONFIG_INVALID', $err['code']);
// claim_table without multi.
$err = $catch(fn() => $build(['claim_table' => ['table' => 'user_departments', 'user_column' => 'user_id']]));
Assert::same('CONFIG_INVALID', $err['code']);
// Bad nullable value.
$err = $catch(fn() => $build(['nullable' => 'strict']));
Assert::same('CONFIG_INVALID', $err['code']);
// Scope column duplicating the tenancy column.
$err = $catch(function () use ($buildFromEntities, $entities, $access) {
    $buildFromEntities->invoke(null, [
        'access' => $access,
        'tenancy' => ['unscoped_roles' => ['superadmin']],
        'scopes' => ['tenant_id' => []],
    ], $entities, 'sqlite');
});
Assert::same('CONFIG_INVALID', $err['code']);

// Nullable dimension column: warn by default, error on opt-in.
$dbNull = new Database(['dsn' => 'sqlite::memory:']);
$dbNull->exec('CREATE TABLE rs_docs (id INTEGER PRIMARY KEY AUTOINCREMENT, department_id INT, title VARCHAR(50) NOT NULL)', []);
$entNull = (new DatabaseIntrospector($dbNull))->introspect(['rs_docs' => ['user']], [], []);
$okWarn = $buildFromEntities->invoke(null, ['access' => ['rs_docs' => ['user']], 'scopes' => ['department_id' => []]], $entNull, 'sqlite');
Assert::same(['multi' => false], $okWarn['masks']['rs_docs.list']['row_scopes']['department_id'] ?? null); // scoped despite warning
$err = $catch(function () use ($buildFromEntities, $entNull) {
    $buildFromEntities->invoke(null, ['access' => ['rs_docs' => ['user']], 'scopes' => ['department_id' => ['nullable' => 'error']]], $entNull, 'sqlite');
});
Assert::same('CONFIG_INVALID', $err['code']);

// Dimensions have no strict sweep: a table without the column simply stays
// unscoped (rs_authors keeps department_id; tenants doesn't — build passed above).
Assert::false(isset($config['masks']['tenants']['row_scopes']));

// ── Mask declaration contract (manual masks) ───────────────────────────────
$scopedSql = 'SELECT COUNT(*) FROM rs_books WHERE tenant_id = :__tenant AND department_id = :__scope_department_id';
$deptScope = ['unscoped_roles' => ['controller', 'superadmin']];

// Declared row_scopes with the placeholder present → OK.
[$okCfg] = $build($deptScope, ['rs.custom' => ['sql' => $scopedSql, 'tenant_scope' => 'tenant_id', 'row_scopes' => ['department_id'], 'roles' => ['user']]]);
Assert::true(isset($okCfg['masks']['rs.custom']));
// Declared row_scopes but no :__scope_<col> placeholder → boot failure.
$err = $catch(fn() => $build($deptScope, ['rs.custom' => ['sql' => 'SELECT COUNT(*) FROM rs_books WHERE tenant_id = :__tenant', 'tenant_scope' => 'tenant_id', 'row_scopes' => ['department_id'], 'roles' => ['user']]]));
Assert::same('CONFIG_INVALID', $err['code']);
Assert::contains('__scope_department_id', $err['details']['hint'] ?? '');
// Writable masks are exempt (probe SQL only; the write path enforces scopes).
[$okW] = $build($deptScope, ['rs.custom' => ['sql' => 'SELECT id FROM rs_books LIMIT 1', 'tenant_scope' => 'tenant_id', 'row_scopes' => ['department_id'], 'writable' => true, 'roles' => ['user']]]);
Assert::true(isset($okW['masks']['rs.custom']));
// Opt-out reachable by a scoped role → boot failure.
$err = $catch(fn() => $build($deptScope, ['rs.custom' => ['sql' => 'SELECT COUNT(*) FROM rs_books WHERE tenant_id = :__tenant', 'tenant_scope' => 'tenant_id', 'row_scopes' => false, 'roles' => ['user']]]));
Assert::same('CONFIG_INVALID', $err['code']);
// Opt-out confined to the dimension's unscoped roles is sanctioned.
[$okOpt] = $build($deptScope, ['rs.custom' => ['sql' => 'SELECT COUNT(*) FROM rs_books WHERE tenant_id = :__tenant', 'tenant_scope' => 'tenant_id', 'row_scopes' => false, 'roles' => ['controller']]]);
Assert::true(isset($okOpt['masks']['rs.custom']));
// `public` cache_control on a row-scoped mask → boot failure.
$err = $catch(fn() => $build($deptScope, ['rs.custom' => ['sql' => $scopedSql, 'tenant_scope' => 'tenant_id', 'row_scopes' => ['department_id'], 'cache_control' => 'public, max-age=60', 'roles' => ['user']]]));
Assert::same('CONFIG_INVALID', $err['code']);
// `private` stays fine.
[$okPriv] = $build($deptScope, ['rs.custom' => ['sql' => $scopedSql, 'tenant_scope' => 'tenant_id', 'row_scopes' => ['department_id'], 'cache_control' => 'private, max-age=60', 'roles' => ['user']]]);
Assert::true(isset($okPriv['masks']['rs.custom']));

// ── Auth: claims round-trip login → refresh; guard normalization ──────────
$pwHash = password_hash('secret123', PASSWORD_BCRYPT);
$tmpFile = tempnam(sys_get_temp_dir(), 'shape_rowscopes_') . '.sqlite';
$fileDb = new Database(['dsn' => 'sqlite:' . $tmpFile]);
$fileDb->exec('CREATE TABLE tenants (id INTEGER PRIMARY KEY AUTOINCREMENT, name VARCHAR(50) NOT NULL, is_active INT NOT NULL DEFAULT 1)', []);
$fileDb->exec('CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT, email VARCHAR(100) NOT NULL, password_hash VARCHAR(255) NOT NULL, display_name VARCHAR(100), role VARCHAR(20) NOT NULL, is_active INT NOT NULL DEFAULT 1, tenant_id INT NOT NULL, department_id INT)', []);
$fileDb->exec('CREATE TABLE refresh_tokens (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INT NOT NULL, token_hash VARCHAR(64) NOT NULL, expires_at VARCHAR(32) NOT NULL, revoked_at VARCHAR(32), replaced_by_token_id INT, last_used_at VARCHAR(32))', []);
$fileDb->exec('CREATE TABLE user_departments (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INT NOT NULL, department_id INT NOT NULL)', []);
$fileDb->exec("INSERT INTO tenants (name, is_active) VALUES ('Acme', 1)", []);
$fileDb->exec("INSERT INTO users (email, password_hash, display_name, role, tenant_id, department_id) VALUES ('u@acme.test', '{$pwHash}', 'U', 'user', 1, 10)", []);
$fileDb->exec("INSERT INTO users (email, password_hash, display_name, role, tenant_id, department_id) VALUES ('c@acme.test', '{$pwHash}', 'C', 'controller', 1, NULL)", []);
$fileDb->exec("INSERT INTO users (email, password_hash, display_name, role, tenant_id, department_id) VALUES ('m@acme.test', '{$pwHash}', 'M', 'user', 1, NULL)", []);
$fileDb->exec('INSERT INTO user_departments (user_id, department_id) VALUES (3, 10), (3, 20)', []);

$tenancyCfg = ['table' => 'tenants', 'column' => 'tenant_id', 'unscoped_roles' => ['superadmin'], 'enforce' => 'strict', 'global_tables' => [], 'check_active_per_request' => false, 'validate_fks' => false];
$deptCfg = ['column' => 'department_id', 'claim' => 'department_id', 'unscoped_roles' => ['controller', 'superadmin'], 'multi' => false, 'claim_table' => null, 'nullable' => 'warn'];
$authConfig = [
    'driver' => 'jwt',
    'jwt_secret' => 'test-secret-please-ignore-0123456789',
    'assignable_roles' => ['user', 'controller', 'superadmin'],
    'tenancy' => $tenancyCfg,
    'scopes' => ['department_id' => $deptCfg],
];
$issuer = new JwtIssuer($authConfig);
$login = new LoginService(['dsn' => 'sqlite:' . $tmpFile], $authConfig, $issuer);

// Login stamps the dimension claim from users.<column>.
$tok = $login->login(['email' => 'u@acme.test', 'password' => 'secret123']);
$claims = $jwtPayload($tok['access_token']);
Assert::same(1, $claims['tenant_id'] ?? null);
Assert::same(10, $claims['department_id'] ?? null);

// Refresh re-reads the row — the rotated token still carries the claim.
$tok2 = $login->refresh(['refresh_token' => $tok['refresh_token']]);
Assert::same(10, $jwtPayload($tok2['access_token'])['department_id'] ?? null);

// A role unscoped for the dimension gets an explicit null claim.
$tokC = $login->login(['email' => 'c@acme.test', 'password' => 'secret123']);
Assert::null($jwtPayload($tokC['access_token'])['department_id']);
Assert::true(array_key_exists('department_id', $jwtPayload($tokC['access_token'])));

// claim_table variant: multi-membership list rides in the claim.
$authConfigM = $authConfig;
$authConfigM['scopes']['department_id'] = ['column' => 'department_id', 'claim' => 'department_id', 'unscoped_roles' => ['controller'], 'multi' => true, 'claim_table' => ['table' => 'user_departments', 'user_column' => 'user_id'], 'nullable' => 'warn'];
$loginM = new LoginService(['dsn' => 'sqlite:' . $tmpFile], $authConfigM, new JwtIssuer($authConfigM));
$tokM = $loginM->login(['email' => 'm@acme.test', 'password' => 'secret123']);
Assert::same([10, 20], $jwtPayload($tokM['access_token'])['department_id'] ?? null);

// JwtGuard normalization: pre-feature token → AUTH_INVALID; unscoped → null.
$guard = new JwtGuard($authConfig, $fileDb);
$headersFor = static fn(string $token): array => ['authorization' => 'Bearer ' . $token];

$legacy = $issuer->issue(['sub' => '1', 'role' => 'user', 'tenant_id' => 1], 600); // no department claim
$err = $catch(fn() => $guard->authorize($headersFor($legacy)));
Assert::same('AUTH_INVALID', $err['code']);
Assert::same(401, $err['status']);
Assert::contains('Re-authenticate', $err['details']['hint'] ?? '');

$ctx = $guard->authorize($headersFor($tok['access_token'])); // scoped token flows through
Assert::same(10, (int)$ctx['department_id']);

$ctrlTok = $issuer->issue(['sub' => '2', 'role' => 'controller', 'tenant_id' => 1, 'department_id' => 99], 600);
$ctx = $guard->authorize($headersFor($ctrlTok));
Assert::null($ctx['department_id']); // unscoped role's claim is forced null

@unlink($tmpFile);

echo "OK\n";
