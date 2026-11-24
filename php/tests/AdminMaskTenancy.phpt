<?php

/**
 * Admin masks under tenancy (MULTI-TENANCY-PLAN) — regression for the
 * cross-tenant leak where generated `<table>.adminList` / `.adminGet` masks on a
 * scoped secret table carried no tenant predicate, so a tenant `admin` could
 * read every tenant's rows. The generator must inject `<col> = :__tenant`, a
 * `tenant_scope` carrier, and an unscoped variant for superadmins.
 *
 *   vendor/bin/tester tests/AdminMaskTenancy.phpt
 */

declare(strict_types=1);

use Shape\Generator\AdminMaskGenerator;
use Shape\Metadata\DatabaseIntrospector;
use ShapeRuntime\Core\Database;
use Tester\Assert;

require __DIR__ . '/../vendor/autoload.php';

Tester\Environment::setup();

$db = new Database(['dsn' => 'sqlite::memory:']);
$db->exec('CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id INT NOT NULL, email VARCHAR(100) NOT NULL, password_hash VARCHAR(100) NOT NULL, role VARCHAR(20) NOT NULL)', []);

$introspector = new DatabaseIntrospector($db);
$tenancy = ['column' => 'tenant_id', 'unscoped_roles' => ['superadmin']];

$masks = AdminMaskGenerator::generate($introspector, ['users'], $tenancy);

// adminList: scoped SQL carries the tenant predicate + carrier keys.
Assert::contains(':__tenant', $masks['users.adminList']['sql']);
Assert::same('tenant_id', $masks['users.adminList']['tenant_scope'] ?? null);
Assert::true(isset($masks['users.adminList']['sql_unscoped']));
Assert::notContains(':__tenant', $masks['users.adminList']['sql_unscoped']);

// adminGet: both the id lookup and the tenant predicate; unscoped keeps :id only.
Assert::contains(':__tenant', $masks['users.adminGet']['sql']);
Assert::contains(':id', $masks['users.adminGet']['sql']);
Assert::same('tenant_id', $masks['users.adminGet']['tenant_scope'] ?? null);
Assert::contains(':id', $masks['users.adminGet']['sql_unscoped']);
Assert::notContains(':__tenant', $masks['users.adminGet']['sql_unscoped']);

// Both admin and the unscoped role can reach admin masks.
Assert::same(['admin', 'superadmin'], $masks['users.adminList']['roles']);

// Sensitive columns are still stripped from the SELECT.
Assert::notContains('password_hash', $masks['users.adminList']['sql']);

// Tenancy disabled → no tenant predicate, no carrier keys (unchanged behavior).
$plain = AdminMaskGenerator::generate($introspector, ['users'], null);
Assert::notContains(':__tenant', $plain['users.adminList']['sql']);
Assert::false(isset($plain['users.adminList']['tenant_scope']));
Assert::same(['admin'], $plain['users.adminList']['roles']);
