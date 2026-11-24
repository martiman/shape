<?php

/**
 * Token exchange + secure preset + exposure planner (TOKEN-EXCHANGE-PHP-PLAN)
 * — PHP port of dotnet TokenExchangeTests.cs, contract byte-identical:
 *
 *   auth.exchange   stateless swap, claim/role mapping, Guid `sub`,
 *                   AUTH_EXCHANGE_INVALID, tenancy suspension gate,
 *                   extra-key pass-through (never secrets)
 *   security:strict three CONFIG_INVALID boot failures + passing config
 *   planner         FK-verified exposure classification
 *
 *   vendor/bin/tester tests/TokenExchange.phpt
 */

declare(strict_types=1);

use Shape\Metadata\DatabaseIntrospector;
use Shape\Metadata\TenantExposurePlanner;
use Shape\ShapeApp;
use ShapeRuntime\Auth\JwtIssuer;
use ShapeRuntime\Auth\LoginService;
use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\Database;
use Tester\Assert;

require __DIR__ . '/../vendor/autoload.php';

Tester\Environment::setup();

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

// ── Schema: a registry with one live + one suspended org ──────────────────
$dsn = 'sqlite:file:token-exchange?mode=memory&cache=shared';
$pdoKeepAlive = new PDO($dsn);
$db = new Database(['dsn' => $dsn]);
$db->exec('CREATE TABLE orgs (id INTEGER PRIMARY KEY AUTOINCREMENT, name VARCHAR(50) NOT NULL, is_active INT NOT NULL DEFAULT 1)', []);
$db->exec("INSERT INTO orgs (id, name, is_active) VALUES (1, 'Acme', 1), (2, 'Globex', 0)", []);

// ── auth.exchange ──────────────────────────────────────────────────────────
$externalTokens = [
    'tis-token-1' => ['id' => 'a3f1-guid-user', 'email' => 'alice@acme.test', 'role' => 'Referent', 'OrganizationId' => 1],
    'scoped'      => ['id' => 'u1', 'role' => 'user', 'OrganizationId' => 2],
    'admin'       => ['id' => 'u2', 'role' => 'admin', 'OrganizationId' => 2],
    'extra'       => ['id' => 'u7', 'role' => 'user', 'OrganizationId' => 1, 'organization_name' => 'Acme', 'password_hash' => 'secret-hash'],
];

$authConfig = [
    'driver' => 'jwt',
    'jwt_secret' => str_repeat('s', 40),
    'tenancy' => ['table' => 'orgs', 'column' => 'OrganizationId', 'unscoped_roles' => ['admin']],
    'exchange' => [
        'source' => static fn(string $token, Database $db): ?array => $externalTokens[$token] ?? null,
        'claim_mapping' => ['OrganizationId' => 'tenant_id'],
        'role_mapping' => ['Referent' => 'user'],
        // issue_refresh_token omitted → stateless default
    ],
];
$login = new LoginService(['dsn' => $dsn], $authConfig, new JwtIssuer($authConfig));

// Stateless happy path: mapped claims, Guid id verbatim, no refresh keys.
$response = $login->exchange(['token' => 'tis-token-1']);
Assert::type('string', $response['access_token']);
Assert::false(array_key_exists('refresh_token', $response));
$claims = $jwtPayload($response['access_token']);
Assert::same('a3f1-guid-user', $claims['sub']);       // Guid survives verbatim
Assert::same('user', $claims['role']);                // role mapping applied
Assert::same(1, $claims['tenant_id']);                // claim mapping applied
Assert::same('a3f1-guid-user', $response['user']['id']);

// Unknown external token → AUTH_EXCHANGE_INVALID 401.
$err = $catch(fn() => $login->exchange(['token' => 'forged']));
Assert::same('AUTH_EXCHANGE_INVALID', $err['code']);
Assert::same(401, $err['status']);

// Suspended tenant blocks a scoped role at exchange…
$err = $catch(fn() => $login->exchange(['token' => 'scoped']));
Assert::same('TENANT_SUSPENDED', $err['code']);
// …but not the unscoped admin (recovery path); its claim is nulled.
$adminResponse = $login->exchange(['token' => 'admin']);
Assert::null($jwtPayload($adminResponse['access_token'])['tenant_id']);

// Extra identity keys pass through to the user object; secrets never do.
$extraResponse = $login->exchange(['token' => 'extra']);
Assert::same('Acme', $extraResponse['user']['organization_name']);
Assert::false(array_key_exists('password_hash', $extraResponse['user']));

// Missing token field → AUTH_VALIDATION_ERROR.
$err = $catch(fn() => $login->exchange([]));
Assert::same('AUTH_VALIDATION_ERROR', $err['code']);

// No exchange config → AUTH_NOT_CONFIGURED 501.
$bare = new LoginService(['dsn' => $dsn], ['driver' => 'jwt', 'jwt_secret' => str_repeat('s', 40)], new JwtIssuer($authConfig));
$err = $catch(fn() => $bare->exchange(['token' => 'anything']));
Assert::same('AUTH_NOT_CONFIGURED', $err['code']);
Assert::same(501, $err['status']);

// ── security: 'strict' ─────────────────────────────────────────────────────
$buildFromEntities = new ReflectionMethod(ShapeApp::class, 'buildFromEntities');
$buildFromEntities->setAccessible(true);

$strictBase = [
    'security' => 'strict',
    'auth' => ['driver' => 'jwt', 'jwt_secret' => str_repeat('s', 40)],
    'manifest' => [],
    'masks' => ['report' => ['sql' => 'SELECT 1 AS one', 'roles' => ['admin']]],
];
$strictCase = static function (array $overrides) use ($buildFromEntities, $strictBase): string {
    $cfg = array_replace($strictBase, $overrides);
    try {
        $buildFromEntities->invoke(null, $cfg, [], 'sqlite');
    } catch (ApiException $e) {
        return $e->apiCode() . ':' . $e->getMessage();
    }
    return 'OK';
};

// Fully gated config passes.
Assert::same('OK', $strictCase([]));
// Driver none → boot failure.
Assert::contains('auth driver', $strictCase(['auth' => []]));
// Role-less mask → boot failure naming the mask.
Assert::contains('open', $strictCase(['masks' => $strictBase['masks'] + ['open' => ['sql' => 'SELECT 1 AS one']]]));
// Unset manifest roles → boot failure ([] passes, absence fails).
Assert::contains('manifest', $strictCase(['manifest' => null]));

// ── TenantExposurePlanner ──────────────────────────────────────────────────
$db->exec('CREATE TABLE Organizations (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name VARCHAR(50) NOT NULL)', []);
// Verified FK → exposed.
$db->exec('CREATE TABLE Requests (Id INTEGER PRIMARY KEY AUTOINCREMENT, OrganizationId INT NOT NULL REFERENCES Organizations(Id), Title VARCHAR(100))', []);
// Column with the right name but NO foreign key → not exposed.
$db->exec('CREATE TABLE Drafts (Id INTEGER PRIMARY KEY AUTOINCREMENT, OrganizationId INT NOT NULL, Body VARCHAR(100))', []);
// No tenant column at all → not exposed.
$db->exec('CREATE TABLE Lookups (Id INTEGER PRIMARY KEY AUTOINCREMENT, Code VARCHAR(20))', []);
// Internal table with a valid FK → excluded explicitly.
$db->exec('CREATE TABLE Tokens (Id INTEGER PRIMARY KEY AUTOINCREMENT, OrganizationId INT REFERENCES Organizations(Id))', []);

$plan = TenantExposurePlanner::planTables(
    new DatabaseIntrospector($db),
    ['table' => 'Organizations', 'column' => 'OrganizationId'],
    ['Tokens']
);
Assert::same(['Requests'], $plan);
