# Shape Specification (v0.1)

## 1. Scope

Shape is an API framework built on top of ShapeRuntime. It auto-generates masks, validators, and an API manifest from either PHP entity classes or a live database schema.

Two modes:
- **`fromEntities`** — code-first. Define PHP entity classes with attributes.
- **`fromDatabase`** — DB-first. The database schema IS the API definition. No entity files needed.

Goals:
- Auto-generated CRUD query masks from entity/DB metadata.
- Auto-generated field validators from attributes or config.
- Machine-readable API manifest for discoverability.
- Full ShapeRuntime compatibility — queries, mutations, commands, auth all work unchanged.
- Zero additional runtime dependencies (PHP 8.2 native attributes + reflection + PDO).

Non-goals (v0.1):
- ORM or Active Record pattern.
- Auto-generated database migrations (planned).
- Relationship-aware JOINs (use batch cross-references instead).

See [DB-FIRST.md](DB-FIRST.md) for full DB-first mode documentation.

## 2. Runtime Requirements

- PHP 8.2+ with native attribute support.
- PDO extension and target driver (e.g. `pdo_mysql`, `pdo_pgsql`, `pdo_sqlite`, or `pdo_sqlsrv` for SQL Server).
- ShapeRuntime (`packages/shaperuntime` source package).
- Manual config masks may inline `sql` or point to a sibling `sqlFile`; the loader resolves the file relative to the config directory before the runtime sees the mask.

## 3. How It Works

```
Entity classes     →  EntityReader (reflection)  →  EntityMeta[]
                                                        │
                                    ┌───────────────────┼───────────────────┐
                                    ▼                   ▼                   ▼
                              MaskGenerator     ValidatorGenerator   ManifestGenerator
                                    │                   │                   │
                                    ▼                   ▼                   ▼
                              ShapeRuntime masks       ShapeRuntime validators JSON manifest
                                    │                   │
                                    └───────┬───────────┘
                                            ▼
                                   ShapeApp::fromEntities()
                                            │
                                            ▼
                                    ShapeRouter::run()
```

1. **EntityReader** scans a directory for PHP classes with `#[Entity]` attribute.
2. **MaskGenerator** produces query masks (list, get, getMany, byFK, writable) per entity.
3. **ValidatorGenerator** produces field validators per entity for insert/update actions.
4. **ManifestGenerator** produces a JSON manifest of all entities, fields, masks, and commands.
5. **ShapeApp::fromEntities()** merges auto-generated config with manual config (manual wins).
6. **ShapeRouter::run()** handles HTTP requests using ShapeRuntime's execution engine.

## 4. Entity Definition

Entities are plain PHP classes with attributes in a dedicated directory:

```php
<?php
namespace App\Entity;

use Shape\Attribute\{Entity, Column, Id, AutoIncrement, FK, Validate, Access, Unique};

#[Entity('orders')]
#[Access(roles: ['admin', 'user'])]
class Order
{
    #[Id, AutoIncrement]
    public int $id;

    #[Column(type: 'date')]
    public string $created_at;

    #[Column(type: 'decimal', precision: 10, scale: 2)]
    #[Validate('positive')]
    public float $total;
}
```

Rules:
- One class per file.
- Class must have `#[Entity('table_name')]` attribute.
- Only `public` properties are scanned.
- PHP type hints determine DB type inference when no `#[Column]` is specified.

## 5. Attribute Reference

### #[Entity(table, writable, description, writableMode)]

Applied to class. Marks it as a Shape entity.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `table` | string | required | Database table name |
| `writable` | bool | `true` | Whether mutations are allowed |
| `description` | ?string | `null` | Human-readable entity summary surfaced in manifest, OpenAPI, and MCP docs |
| `writableMode` | ?string | `null` | Explicit writable policy: `full`, `append_only`, `no_delete`, or `none` |

When `writableMode` is set, it overrides the boolean `writable` flag. Use `none` for read-only entities, or `append_only` / `no_delete` for mutation-restricted tables.

### #[Column(type, length, precision, scale, unsigned, values, description)]

Applied to property. Overrides DB type inference.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `type` | ?string | `null` | DB column type (`varchar`, `int`, `decimal`, `date`, `text`, etc.) |
| `length` | ?int | `null` | Column length (e.g. 255 for varchar) |
| `precision` | ?int | `null` | Decimal precision |
| `scale` | ?int | `null` | Decimal scale |
| `unsigned` | bool | `false` | Unsigned integer |
| `values` | ?list\<string> | `null` | Closed value set; surfaces as manifest/OpenAPI `enum` and generates an `in:` validator |
| `description` | ?string | `null` | Human-readable field description surfaced in docs and manifest |

When `type` is null, inferred from PHP type: `int` → `int`, `float` → `double`, `bool` → `tinyint`, `string` → `varchar`.

### #[Id]

Applied to property. Marks it as primary key.

### #[AutoIncrement]

Applied to property. Marks it as auto-increment. Implies `unsigned`.

Note: an int-family `#[Id]` property **without** `#[AutoIncrement]` and without a default
resolves to framework-side manual PK allocation under the default `pk_assignment: 'auto'`
mode (see [§10](#manual-primary-key-assignment-pk_assignment)) — the id is still
server-assigned, just computed by Shape instead of the database. Set
`pk_assignment: ['<table>' => 'db']` for a client-supplied int PK. (.NET additionally
offers a `[GeneratedId]` attribute to force manual allocation explicitly.)

### #[FK(entity, column, onDelete, onUpdate)]

Applied to property. Declares a foreign key relationship.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `entity` | string | required | Target entity class name |
| `column` | ?string | `null` | Target column (defaults to target entity's primary key) |
| `onDelete` | string | `'cascade'` | ON DELETE behavior |
| `onUpdate` | string | `'cascade'` | ON UPDATE behavior |

### #[Unique(columns)]

Applied to class. Declares a repeatable composite unique constraint.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `columns` | list\<string> | required | Column names in declaration order |

Repeat the attribute to declare multiple unique constraints on the same entity. DB-first mode reads the equivalent database constraints and surfaces them in the same manifest shape.

### #[Validate(rule, values)]

Applied to property. Repeatable. Declares a validation rule.

Built-in rules:
| Rule | Validates |
|------|-----------|
| `positive` | Numeric and > 0 |
| `nonNegative` | Numeric and >= 0 |
| `notEmpty` | String and not blank |
| `email` | Valid email format |
| `in` | String value is in the supplied `values` list |

For enum-like fields, either set `#[Column(values: [...])]` or `#[Validate('in', values: [...])]`. If both are present, the value lists are merged.

### #[Access(roles, read, write)]

Applied to class. Configures role-based access.

| Parameter | Type | Default | Description |
|-----------|------|---------|-------------|
| `roles` | list\<string\> | `[]` | Default roles for all operations |
| `read` | list\<string\> | `[]` | Override roles for read masks (if set, overrides `roles`) |
| `write` | list\<string\> | `[]` | Override roles for writable mask (if set, overrides `roles`) |

## 6. Auto-Generated Masks

For each entity, Shape generates these masks:

| Mask Name | SQL Pattern | Params |
|-----------|-------------|--------|
| `{table}.list` | `SELECT {cols} FROM {table}` | none |
| `{table}.get` | `SELECT {cols} FROM {table} WHERE {pk} = :pk` | pk (required) |
| `{table}.getMany` | `SELECT {cols} FROM {table} WHERE {pk} IN (:pks)` | pks (required, array) |
| `{table}.by{FkField}` | `SELECT {cols} FROM {table} WHERE {fk} = :fk` | fk (required) |
| `{table}` | `SELECT {first_col} FROM {table} LIMIT 1` | none (writable mask) |

Example: entity `Item` with FK `order_id` and FK `product_id` generates:
- `items.list`
- `items.get`
- `items.getMany`
- `items.byOrderId`
- `items.byProductId`
- `items` (writable)

All generated masks inherit roles from the entity's `#[Access]` attribute.

Writable masks additionally carry the primary-key contract keys (cross-runtime,
snake_case in the manifest): `pk` — the PK column name (single-column PKs only), and
`pk_strategy: "manual"` when the framework allocates the id itself (see
[§10 `pk_assignment`](#manual-primary-key-assignment-pk_assignment)). The runtime uses
`pk` to return the new row's id portably (`RETURNING` / `OUTPUT INSERTED` need the real
column name) and `pk_strategy` to decide whether to allocate.

When [`tenancy`](#multi-tenancy-tenancy) is enabled, scoped masks additionally carry
`tenant_scope` (the discriminator column) — on read masks and the writable mask alike — and
read masks emit a second `sql_unscoped` variant (the predicate-free SQL an `unscoped_roles`
caller executes) whenever `unscoped_roles` is configured. The runtime binds `:__tenant` into
the scoped SQL and uses `tenant_scope` to stamp inserts and guard the UPDATE/DELETE WHERE.

## 7. Auto-Generated Validators

For each entity field with `#[Validate]` attributes, Shape generates validators for both `insert` and `update` actions.

Example: `#[Validate('positive')]` on `Order.total` generates:
- `orders.insert` → validators → `total` → checks positive
- `orders.update` → validators → `total` → checks positive

Validation error format:
```json
{
  "error": {
    "code": "VALIDATION_ERROR",
    "message": "total must be positive"
  }
}
```

## 8. Raw SQL Masks

Entity-generated masks cover standard CRUD. For JOINs, aggregations, subqueries, or any custom SQL, add raw masks in `config/app.php`. They work alongside auto-generated masks.

### Adding new raw SQL masks

```php
// config/app.php
return [
    'masks' => [
        // JOIN query — not possible with entities alone
        'orders.withItems' => [
            'sql' => 'SELECT o.id, o.created_at, o.total, i.title AS item_title, i.price AS item_price
                      FROM orders o
                      JOIN items i ON i.order_id = o.id
                      WHERE o.id = :id',
            'params' => ['id' => ['type' => 'int', 'required' => true]],
            'roles' => ['admin', 'user'],
        ],

        // Aggregation query
        'orders.summary' => [
            'sql' => 'SELECT COUNT(*) AS order_count, SUM(total) AS revenue FROM orders',
            'params' => [],
            'roles' => ['admin'],
        ],

        // Filtered query with ordering
        'stock.lowStock' => [
            'sql' => 'SELECT product_id, title, in_stock FROM stock WHERE in_stock < :threshold ORDER BY in_stock ASC',
            'params' => ['threshold' => ['type' => 'int', 'required' => true]],
            'roles' => ['admin'],
        ],
    ],
];
```

These masks are called the same way as entity-generated ones:
```json
{
  "op": "query",
  "payload": {
    "queries": [
      { "ref": "details", "sql": "orders.withItems", "params": { "id": 42 } }
    ]
  }
}
```

### Overriding auto-generated masks

To replace an entity-generated mask with custom SQL, use the same mask name:

```php
'masks' => [
    // Replaces the auto-generated orders.list mask
    'orders.list' => [
        'sql' => 'SELECT id, total FROM orders WHERE status != :exclude',
        'params' => ['exclude' => ['type' => 'string', 'required' => true]],
        'roles' => ['admin'],
    ],
],
```

Manual masks are merged last, so they always win over auto-generated ones with the same key.

### Merge rules

| Mask name | Manual defined? | Result |
|-----------|:---:|---|
| `orders.list` | no | auto-generated from entity |
| `orders.list` | yes | manual replaces auto |
| `orders.withItems` | yes | added (no auto version exists) |
| `orders.get` | no | auto-generated from entity |

### Manual validators merge with auto validators

```php
return [
    'validators' => [
        'orders.insert' => [
            'validators' => [
                'total' => fn($v) => $v <= 10000 ? null : 'Max order is 10000',
            ],
            'hooks' => [
                'before' => fn($ctx) => logAudit($ctx),
            ],
        ],
    ],
];
```

Manual field validators override auto ones for the same field. Hooks are always from manual config.

### Commands are always manual

Commands contain business logic that cannot be auto-generated:

```php
return [
    'commands' => [
        'order.process' => function(array $params, CommandContext $ctx): array {
            // ...business logic...
        },
    ],
];
```

## 9. API Manifest

Shape exposes an `op: "manifest"` operation that returns a machine-readable description of all entities, fields, masks, and commands. No authentication required. No database connection needed.

The manifest is additive: when present, entity descriptions, field descriptions, enum value sets, composite unique constraints, and explicit writable modes are included alongside the base CRUD metadata.

Request:
```json
{
  "op": "manifest"
}
```

Response:
```json
{
  "entities": [
    {
      "name": "Order",
      "table": "orders",
      "writable": true,
      "description": "Customer orders and totals.",
      "uniqueConstraints": [["customer_id", "external_id"]],
      "fields": [
        { "name": "id", "type": "int unsigned", "nullable": false, "primary": true, "autoIncrement": true },
        { "name": "created_at", "type": "date", "nullable": false, "description": "Creation timestamp" },
        { "name": "total", "type": "decimal(10,2)", "nullable": false, "validators": ["positive"], "description": "Order total" },
        { "name": "status", "type": "varchar(20)", "nullable": false, "default": "draft", "enum": ["draft", "paid", "cancelled"] }
      ],
      "masks": {
        "orders.list": { "params": [], "writable": false, "description": "List rows from orders." },
        "orders.get": { "params": ["id"], "writable": false, "description": "Get one row from orders by primary key (id)." },
        "orders.getMany": { "params": ["ids"], "writable": false, "description": "Get multiple rows from orders by a list of primary keys (id)." },
        "orders": { "params": [], "writable": true, "writableMode": "append_only", "description": "Insert only — UPDATE and DELETE rejected (append-only contract)." }
      },
      "roles": {
        "read": ["admin", "user"],
        "write": ["admin", "user"]
      }
    }
  ],
  "commands": ["order.process", "inventory.adjust"]
}
```

## 10. Configuration

### Directory Structure

```
myapp/
  entities/         ← Entity class files
    Order.php
    Item.php
    Stock.php
  config/
    app.php         ← Commands, manual masks, manual validators
    env.php         ← DB connection, auth settings (not committed)
    env.local.php   ← Local dev overrides (takes priority over env.php)
  public/
    api.php       ← Entry point
  bootstrap.php     ← Autoloader
```

### Entry Point

```php
<?php
require_once __DIR__ . '/../bootstrap.php';

use Shape\ShapeApp;
use Shape\ShapeRouter;

$config = ShapeApp::fromEntities(
    __DIR__ . '/../entities',
    __DIR__ . '/../config/app.php'
);

ShapeRouter::run($config, 'myapp');
```

### Config Merge Order

1. `app.php` is loaded (masks, validators, commands).
2. `env.local.php` is merged if it exists, otherwise `env.php` (db, auth).
3. Entity-generated masks and validators are merged (manual overrides auto).

### Caching

Entity metadata can be cached to avoid rescanning entity files (or reintrospecting the database) on every request.

```php
return [
    'cache' => false,               // default — no caching
    'cache' => true,                // cache to sys_get_temp_dir()/shape_cache/
    'cache' => '/var/cache/shape',  // custom directory
];
```

Cache is invalidated automatically when source files change (entity files, `app.php`, `env.php`, `env.local.php`). DB schema changes in `fromDatabase` mode require manual cache clearing via `ShapeApp::clearCache($configPath)` or deleting the cache directory.

Only entity metadata is serialized — closures (validators, commands) are regenerated fresh on each request from the cached metadata. Typical speedup: **10–20x** on warm cache.

See [DB-FIRST.md](DB-FIRST.md#cache-optional) for details.

### Manual primary-key assignment (`pk_assignment`)

Legacy databases (typically SQL Server migration sources) often have plain int primary
keys with no IDENTITY / AUTO_INCREMENT / default — the original application computed the
next id in code. The `pk_assignment` config key tells Shape to do the same:

```php
return [
    'pk_assignment' => 'auto',                                   // whole-database default
    // or per table ('*' = database default):
    'pk_assignment' => ['*' => 'auto', 'invoices' => 'manual', 'codes' => 'db'],
];
```

(.NET: `ShapeConfig.PkAssignment`; JSON config accepts both `pkAssignment` and the
PHP-parity `pk_assignment`, plus the same string shorthand.)

Modes:

- **`auto`** (default) — recognized from schema: a single-column int-family PK
  (int/bigint/smallint/tinyint, or decimal/numeric with scale 0) that is not
  auto-increment and has no default resolves to `manual`; everything else to `db`.
- **`manual`** — force framework allocation. Build-time `CONFIG_INVALID` error when the
  table's PK is composite or non-int.
- **`db`** — trust the database (identity, trigger, sequence, or default assigns).

On insert into a `manual` table where the client did not supply the PK, the runtime
allocates `MAX(pk) + 1` atomically (SQL Server: single `INSERT … OUTPUT INSERTED …
SELECT ISNULL(MAX(pk),0)+1 … WITH (UPDLOCK, HOLDLOCK)` statement; PostgreSQL: advisory
lock + `RETURNING`; MySQL/SQLite: locked read + explicit insert in one transaction),
retrying up to 3× on unique-violation or deadlock before failing with `DB_ERROR`
(detail `PK_ALLOCATION_RACE`). An explicitly supplied PK value always passes through
untouched (import/migration flows). Externally the column behaves like a generated one:
validators never require it, the manifest/OpenAPI mark it auto-assigned, and the batch
response returns the allocated value under `id` in both runtimes.

Auth tables (`users`, `refresh_tokens`) honor only an **explicit** `manual` entry
(table name or `'*'`) — the per-request auth path performs no introspection, so `auto`
detection cannot apply there.

Invalid mode values fail at build time with `CONFIG_INVALID`.

### Multi-tenancy (`tenancy`)

Turns a single-tenant backend into a pooled B2B multi-tenant one. Every tenant-owned table
carries a discriminator column; Shape injects the scoping predicate on reads and stamps the
column on writes — by convention, framework-level, no per-mask boilerplate.

```php
return [
    'tenancy' => true,                          // convention defaults, or:
    'tenancy' => [
        'table'          => 'tenants',          // registry table
        'column'         => 'tenant_id',        // discriminator column
        'unscoped_roles' => ['superadmin'],     // roles that see across tenants (default: none)
        'enforce'        => 'strict',            // 'strict' (default) | 'relaxed'
        'global_tables'  => [],                 // shared lookups exempt from the strict check
    ],
];
```

Detection (during the same introspection pass as soft-delete / audit columns): a table is
tenant-scoped iff it carries `column`; no column → global. The registry `table` is global
by definition (carrying the column is a build error), and the `users` table must carry the
column. `enforce: 'strict'` (default) fails the boot with `CONFIG_INVALID` listing every
exposed table lacking both the column and a `global_tables` entry, and rejects a nullable
discriminator; `'relaxed'` skips the sweep for brownfield migrations.

- **Reads**: the predicate `<column> = :__tenant` is compiled into every generated read
  mask (`list`/`get`/`getMany`/`by<FK>`, the OData GET base, and the separate `COUNT`).
  An `unscoped_roles` caller executes a predicate-free variant. The runtime binds
  `:__tenant` from the auth context — a scoped mask reachable without a tenant is rejected.
- **Writes**: INSERT auto-stamps the discriminator; a differing client value is rejected
  `VALIDATION_ERROR` (`detail: TENANT_MISMATCH`), and an unscoped caller must supply it.
  UPDATE/DELETE (and the soft-delete rewrite) append `AND <column> = :__tenant` for scoped
  callers, so a cross-tenant id affects 0 rows. Nested `relations` children inherit the stamp.
- **Auth**: the tenant is a server-issued `tenant_id` JWT claim, never a request parameter;
  login and refresh reject a suspended tenant with `TENANT_SUSPENDED` (403).
- **Reserved params**: any client parameter starting with `__` is rejected at validation
  (`PARAM_VALIDATION_ERROR`) — the scope binding `:__tenant` is framework-only.

Mask carrier keys (cross-runtime, snake_case — same pattern as `pk` / `pk_strategy`):
`tenant_scope` (the column; present on every scoped mask) and `sql_unscoped` (the
predicate-free variant, emitted only when `unscoped_roles` is configured). The manifest
adds `"tenantScoped": true` to scoped entities. New error codes: `TENANT_SUSPENDED` (403),
and `VALIDATION_ERROR` with `detail` `TENANT_MISMATCH` / `TENANT_REQUIRED`; build-time
`CONFIG_INVALID` covers the users/registry/nullable/strict-sweep cases above. Full design:
[MULTI-TENANCY-PLAN.md](MULTI-TENANCY-PLAN.md).

### Row-level security by dimension (`scopes`)

Row scoping generalizes the tenancy machinery to any *dimension* column
(department, cost center, branch): the same predicate-compile / stamp / guard,
composed with tenancy (predicates AND together).

```php
'scopes' => [
    'department_id' => [
        'claim'          => 'department_id',        // JWT claim (default: the column name)
        'unscoped_roles' => ['controller'],          // see across this dimension (default: none)
        'multi'          => false,                    // true → claim is a LIST, predicate becomes IN
        'claim_table'    => null,                     // multi-membership source (else users.<column>)
        'nullable'       => 'warn',                   // 'warn' (default) | 'error'
    ],
],
```

Differs from `tenancy` deliberately: **scoped iff the column is present** (no
strict sweep — dimensions cover a subset of tables), no registry/suspension,
claims may be multi-valued, nullable column is a boot *warning* by default. A
caller is unscoped **per scope** — a global admin must be listed in every
scope's `unscoped_roles`. Reserved param `:__scope_<column>` (PHP) /
`@__scope_<column>` (.NET). Mask carrier key `row_scopes` (`{"<col>": {"multi":
bool}}`); manifest entities gain `"rowScopes": ["<col>"]`. Error details
`SCOPE_MISMATCH` / `SCOPE_REQUIRED` (both carry `scope`); a scoped caller with
no claim gets `AUTH_INVALID` (re-login). Full design:
[ROW-SCOPES-PHP-PLAN.md](ROW-SCOPES-PHP-PLAN.md).

### Token exchange (`auth.exchange`)

Brownfield bridge for apps with an existing non-JWT token source
([TOKEN-EXCHANGE.md](TOKEN-EXCHANGE.md)):

```php
'auth' => [
    'exchange' => [
        'source'              => fn(string $token, Database $db): ?array => /* verify, or null */,
        'claim_mapping'       => ['OrganizationId' => 'tenant_id'],  // rename app keys → canonical
        'role_mapping'        => ['BackendAdmin' => 'admin'],        // rename app roles → Shape roles
        'issue_refresh_token' => false,                              // default: stateless (access token only)
    ],
],
```

The op `{op: 'auth.exchange', payload: {token}}` is anonymous (the external
token is the credential). Invalid token → `AUTH_EXCHANGE_INVALID` (401);
unconfigured → `AUTH_NOT_CONFIGURED` (501); scoped roles pass the tenancy
suspension gate at exchange. Extra identity keys the source returns surface
on the response's `user` object (`password`/`password_hash` never do).

### Secure preset (`security`)

```php
'security' => 'strict',
```

Boot-time `CONFIG_INVALID` when the auth driver is `none`, any mask has an
empty `roles` list, or `manifest` roles are unset (`[]` = any authenticated
caller passes; absence fails). Replaces app-local anonymous-access middleware.

### Docs UI (`docs_ui`)

```php
'docs_ui' => true,                                   // GET /api.php?docs
'docs_ui_asset_url' => 'https://…/scalar.js',        // optional self-hosted viewer
```

Serves an interactive API reference (Scalar viewer over the generated
`?openapi` document) — one self-contained HTML page, no package dependency.
The page holds no schema data and is served without auth; the spec fetch it
makes stays gated by the `manifest` roles, and the page prompts for a Bearer
token on 401/403. .NET: `ShapeConfig.DocsUi` / `DocsUiAssetUrl`
(`"docs_ui"` / `"docs_ui_asset_url"` in JSON config), served at
`GET {endpoint}?docs`. Disabled → typed `NOT_FOUND` with an enable hint.

**Accessing gated docs.** With `manifest` roles set, viewing the reference
needs any valid token — obtain one via `auth.token` (local login),
`auth.exchange` (brownfield bridge), or the OIDC flow, and paste the
`access_token` into the page's prompt (stored in the browser, reused by the
viewer's "try it" requests). **Development pattern:** apps commonly keep
production gated but open discovery locally — set the manifest roles to
null (and skip `security: 'strict'`, which requires them) only when running
in a development environment; data ops still require a JWT either way. See
BROWNFIELD-ADAPTER-GUIDE.md step 5 and the ExampleCorp.Shape host for the
worked example.

Same as ShapeRuntime:
- Path: `/api.php`.
- `GET` returns health check with `"engine": "shape"`.
- `POST` with `{ "op": "...", "payload": {...} }`.

Supported operations:
- `query` — execute query masks (auto-generated or manual)
- `mutation` — insert/update/delete with auto-validation
- `command` — business logic workflows
- `manifest` — entity and API discovery (no auth, no DB)
- `auth.token` / `auth.login` — JWT authentication
- `auth.refresh` — token refresh
- `auth.exchange` — swap an app-validated external token for a Shape JWT
- `auth.logout` — revoke refresh token
- `auth.admin.create-user` — admin user creation
- `auth.admin.change-password` — admin password change

## 12. Error Codes

Same as ShapeRuntime, plus:
- `VALIDATION_ERROR` (400) — auto-generated validator rejected a field value.

## 13. Security Model

- Inherits ShapeRuntime's allowlist-only query execution.
- Role-based access derived from `#[Access]` attributes.
- Prepared statements only.
- Auto-generated validators enforce data integrity.
- Manifest exposes schema but not SQL or secrets.
