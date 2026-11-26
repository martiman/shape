<?php

/**
 * Self scope: `scopes.<col>.self = true` — the scope value is the caller's own users.id,
 * so a user sees and writes only own rows while unscoped roles (admin) see all.
 * No users.<col> column or membership table is needed. SQLite in-memory/file.
 *
 *   vendor/bin/tester tests/SelfScope.phpt
 */

declare(strict_types=1);

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

$catch = static function (callable $fn): array {
    try {
        $fn();
    } catch (ApiException $e) {
        return ['code' => $e->apiCode(), 'status' => $e->status(), 'details' => $e->details()];
    }
    return ['code' => '(none)', 'status' => 0, 'details' => []];
};
$jwtPayload = static function (string $token): array {
    $seg = explode('.', $token)[1] ?? '';
    $seg .= str_repeat('=', (4 - strlen($seg) % 4) % 4);
    return json_decode((string)base64_decode(strtr($seg, '-_', '+/')), true) ?: [];
};

// ── Data + executor ───────────────────────────────────────────────────────
$db = new Database(['dsn' => 'sqlite::memory:']);
$db->exec('CREATE TABLE sw_logs (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INT NOT NULL, note VARCHAR(50) NOT NULL)', []);
$db->exec("INSERT INTO sw_logs (user_id, note) VALUES (1, 'a1'), (1, 'a2'), (2, 'b1')", []);
$access = ['sw_logs' => ['user', 'admin']];
$entities = (new DatabaseIntrospector($db))->introspect($access, [], []);
$scopes = ['user_id' => ['self' => true, 'unscoped_roles' => ['admin']]];
$config = $buildFromEntities->invoke(null, ['access' => $access, 'scopes' => $scopes], $entities, 'sqlite');
Assert::true($config['scopes']['user_id']['self']);
$executor = new BatchExecutor($db, new MaskRegistry($config['masks']), new ValidatorRegistry($config['validators'] ?? []));
$executor->setScoping($config['tenancy'] ?? [], $config['scopes']);

$list = static function (array $ctx) use ($executor): array {
    $r = $executor->execute([['ref' => 'q', 'sql' => 'sw_logs.list', 'params' => []]], $ctx)['results'][0];
    return array_map(static fn(array $row): array => array_combine($r['meta']['columns'], $row), $r['rows'] ?? []);
};
$ctxU1 = ['id' => 1, 'role' => 'user', 'user_id' => 1];
$ctxU2 = ['id' => 2, 'role' => 'user', 'user_id' => 2];
$ctxAdmin = ['id' => 9, 'role' => 'admin', 'user_id' => null];

Assert::count(2, $list($ctxU1));
Assert::count(1, $list($ctxU2));
Assert::count(3, $list($ctxAdmin));

$mut = static fn(array $ctx, array $data) => $executor->executeMutations([['ref' => 'm', 'table' => 'sw_logs', 'action' => 'insert', 'data' => $data]], $ctx)['results'][0];
$err = $catch(fn() => $mut($ctxU1, ['user_id' => 2, 'note' => 'forged']));   // write for someone else
Assert::same('VALIDATION_ERROR', $err['code']);
Assert::same('SCOPE_MISMATCH', $err['details']['detail'] ?? null);
Assert::same(1, $mut($ctxU1, ['user_id' => 1, 'note' => 'mine'])['affected']);
Assert::same(1, $mut($ctxU1, ['note' => 'stamped'])['affected']);            // omitted -> own id
Assert::contains('stamped', array_column($list($ctxU1), 'note'));
Assert::notContains('stamped', array_column($list($ctxU2), 'note'));
Assert::same(1, $mut($ctxAdmin, ['user_id' => 2, 'note' => 'by admin'])['affected']); // admin must name the user

// Cannot touch another user's row.
$upd = $executor->executeMutations([['ref' => 'u', 'table' => 'sw_logs', 'action' => 'update', 'filter' => ['id' => 3], 'data' => ['note' => 'hack']]], $ctxU1)['results'][0];
Assert::same(0, $upd['affected']);

// ── Config validation ─────────────────────────────────────────────────────
foreach ([['multi' => true], ['claim_table' => ['table' => 'x', 'user_column' => 'user_id'], 'multi' => true]] as $bad) {
    $err = $catch(fn() => $buildFromEntities->invoke(null, ['access' => $access, 'scopes' => ['user_id' => ['self' => true] + $bad]], $entities, 'sqlite'));
    Assert::same('CONFIG_INVALID', $err['code']);
}

// ── Auth: claim = users.id, no users.user_id column ──────────────────────
$pw = password_hash('secret123', PASSWORD_BCRYPT);
$tmp = tempnam(sys_get_temp_dir(), 'shape_self_') . '.sqlite';
$fdb = new Database(['dsn' => 'sqlite:' . $tmp]);
$fdb->exec('CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT, email VARCHAR(100) NOT NULL, password_hash VARCHAR(255) NOT NULL, display_name VARCHAR(100), role VARCHAR(20) NOT NULL, is_active INT NOT NULL DEFAULT 1)', []);
$fdb->exec('CREATE TABLE refresh_tokens (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INT NOT NULL, token_hash VARCHAR(64) NOT NULL, expires_at VARCHAR(32) NOT NULL, revoked_at VARCHAR(32), replaced_by_token_id INT, last_used_at VARCHAR(32))', []);
$fdb->exec("INSERT INTO users (email, password_hash, display_name, role) VALUES ('a', '{$pw}', 'A', 'admin'), ('u', '{$pw}', 'U', 'user')", []);
$authConfig = ['driver' => 'jwt', 'jwt_secret' => 'test-secret-please-ignore-0123456789', 'assignable_roles' => ['user', 'admin'],
    'scopes' => ['user_id' => $config['scopes']['user_id']]];
$login = new LoginService(['dsn' => 'sqlite:' . $tmp], $authConfig, new JwtIssuer($authConfig));

$tok = $login->login(['email' => 'u', 'password' => 'secret123']);
Assert::same(2, $jwtPayload($tok['access_token'])['user_id'] ?? null);
$tok2 = $login->refresh(['refresh_token' => $tok['refresh_token']]);
Assert::same(2, $jwtPayload($tok2['access_token'])['user_id'] ?? null);
$tokA = $login->login(['email' => 'a', 'password' => 'secret123']);
Assert::true(array_key_exists('user_id', $jwtPayload($tokA['access_token'])));
Assert::null($jwtPayload($tokA['access_token'])['user_id']);

@unlink($tmp);
echo "OK\n";
