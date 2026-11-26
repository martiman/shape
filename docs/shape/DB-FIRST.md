# DB-First Mode

> **AI agents:** read [`AGENTS.md`](../../AGENTS.md) first — it covers Shape's conventions, MCP, and the error envelope in one ~5k-token pass. Use this file for deep configuration detail.

## Overview

`ShapeApp::fromDatabase()` reads your live database schema and generates the entire API automatically. No entity files, no PHP classes — the database IS the API definition.

## One-line Quick Start

If you don't need any custom configuration, the entire app collapses to one line plus a `.env` file:

```php
<?php
// public/api.php
require __DIR__ . '/../bootstrap.php';

Shape\ShapeApp::quickStart(__DIR__ . '/../.env')->serve();
```

### Prototype mode: empty `.env` works

For local prototyping you don't even need to fill in a `.env`. If `DB_DSN` is missing or empty, Shape provisions an auto-SQLite file at `<project>/.shape/data.sqlite` and creates the parent directory on demand. The minimum running app is:

```ini
# .env (literally empty)
```

Boot it (`php -S localhost:8000 -t public`) and you have a working backend with manifest, OpenAPI, and MCP endpoints — against an empty SQLite file. Apply a schema (`sqlite3 .shape/data.sqlite < schema.sql`) and the next request introspects it and generates the full mask surface.

### Production mode: full `.env`

```ini
# .env
APP_NAME = "myapp"
APP_ENV  = "prod"

DB_DSN  = "mysql:host=localhost;dbname=mydb;charset=utf8mb4"
DB_USER = "appuser"
DB_PASS = "secret"

JWT_SECRET = "<32+ random characters>"
```

That's it. No `app.php`, no manual `access` config, no `validate` config, no entity classes.

**Defaults applied** (override via the full `fromDatabase()` flow if you need to):

| Default | Value |
|---|---|
| `access` | `['*' => ['admin', 'user']]` — every introspected table exposed for both roles |
| `admin_masks` | `true` — built-in safe masks for `users` / `refresh_tokens` |
| `auto_validators` | `true` — `notEmpty` for NOT NULL strings, `maxLen:N` for VARCHAR(N) |
| `cache` | `APP_ENV === 'prod'` — entity cache enabled in prod, disabled in dev |
| `db.dsn` | `sqlite:<project>/.shape/data.sqlite` (auto-created) when `DB_DSN` is missing or empty |
| Auth driver | `jwt` if `JWT_SECRET` is set, else `none` |

**Recognised .env keys:**

| Key | Maps to | Notes |
|---|---|---|
| `DB_DSN` | `db.dsn` | Required |
| `DB_USER` | `db.user` | |
| `DB_PASS` | `db.pass` | |
| `JWT_SECRET` | `auth.jwt_secret` | Switches auth driver to `jwt` when set |
| `JWT_TTL` | `auth.access_token_ttl` | Seconds |
| `JWT_REFRESH_TTL` | `auth.refresh_token_ttl` | Seconds |
| `APP_NAME` | service name | Defaults to `shape` |
| `APP_ENV` | cache toggle | `prod` enables cache; anything else disables it |

When you outgrow `quickStart()` — when you need custom masks, commands, hooks, or a non-standard `access` policy — switch to the full `fromDatabase()` flow below. The same `.env` keys work; you just write a real `app.php` and call `ShapeApp::fromDatabase($configPath)`.

## Quick Start

### 1. Create the database

**MySQL / MariaDB:**

```sql
CREATE TABLE authors (
  id INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  name VARCHAR(120) NOT NULL,
  email VARCHAR(190),
  bio TEXT
);

CREATE TABLE books (
  id INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  title VARCHAR(255) NOT NULL,
  author_id INT UNSIGNED NOT NULL,
  price DECIMAL(8,2) NOT NULL,
  isbn VARCHAR(13),
  stock INT UNSIGNED NOT NULL DEFAULT 0,
  FOREIGN KEY (author_id) REFERENCES authors(id) ON DELETE CASCADE
);

CREATE TABLE reviews (
  id INT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  book_id INT UNSIGNED NOT NULL,
  reviewer VARCHAR(120) NOT NULL,
  rating INT NOT NULL,
  comment TEXT,
  FOREIGN KEY (book_id) REFERENCES books(id) ON DELETE CASCADE
);
```

**PostgreSQL** (same schema, native types):

```sql
CREATE TABLE authors (
  id BIGSERIAL PRIMARY KEY,
  name VARCHAR(120) NOT NULL,
  email VARCHAR(190),
  bio TEXT
);

CREATE TABLE books (
  id BIGSERIAL PRIMARY KEY,
  title VARCHAR(255) NOT NULL,
  author_id BIGINT NOT NULL REFERENCES authors(id) ON DELETE CASCADE,
  price NUMERIC(8,2) NOT NULL,
  isbn VARCHAR(13),
  stock INT NOT NULL DEFAULT 0
);

CREATE TABLE reviews (
  id BIGSERIAL PRIMARY KEY,
  book_id BIGINT NOT NULL REFERENCES books(id) ON DELETE CASCADE,
  reviewer VARCHAR(120) NOT NULL,
  rating INT NOT NULL,
  comment TEXT
);
```

The PHP config and entry point in steps 2–3 below are identical for both databases — Shape detects the driver from the DSN in `env.php` and adjusts introspection, identifier quoting, and insert-id strategy automatically.

### 2. Write the config

```php
<?php
// config/app.php
return [
    'access' => [
        'authors' => ['admin', 'user'],
        'books'   => ['admin', 'user'],
        'reviews' => ['read' => ['admin', 'user'], 'write' => ['user', 'admin']],
    ],

    'validate' => [
        'books.title'      => 'notEmpty',
        'books.price'      => 'positive',
        'authors.name'     => 'notEmpty',
        'reviews.reviewer' => 'notEmpty',
        'reviews.rating'   => 'positive',
    ],

    'exclude_tables' => ['users', 'refresh_tokens'],
];
```

### 3. Create the entry point

```php
<?php
// public/api.php
require_once __DIR__ . '/../bootstrap.php';

use Shape\ShapeApp;
use Shape\ShapeRouter;

$config = ShapeApp::fromDatabase(
    __DIR__ . '/../config/app.php'
);

ShapeRouter::run($config, 'myapp');
```

### 4. Done

The framework reads the database, discovers tables, columns, types, primary keys, foreign keys — and generates the full API.

When the driver exposes them, table comments, column comments, and UNIQUE constraints are also read from the catalog and carried through to the manifest, OpenAPI, and MCP docs.

## What Gets Auto-Generated from DB

| DB Feature | API Result |
|---|---|
| Table `books` | `books.list`, `books.get`, `books.getMany`, `books` (writable) |
| `FOREIGN KEY (author_id) REFERENCES authors(id)` | `books.byAuthorId` filter mask |
| `PRIMARY KEY (id)` | `.get` and `.getMany` param binding |
| `AUTO_INCREMENT` | Excluded from insert validation |
| Column types | PHP type mapping for param binding |
| `NOT NULL` | Field required info in manifest |
| `DEFAULT 0` | Default value in manifest |
| `TABLE_COMMENT` / `obj_description` | Entity description in manifest and docs |
| `COLUMN_COMMENT` / `col_description` | Field description in manifest, OpenAPI, and MCP docs |
| `UNIQUE` constraint | `uniqueConstraints` metadata, surfaced in manifest and docs |

## GET surface — three styles, one mask layer

Every read mask in the config is reachable via three URL styles. All three produce identical responses, ETags, and Cache-Control headers — pick whichever matches your client's idioms:

| Mask | REST style | OData style | Generic style |
|---|---|---|---|
| `books.list` | `GET /api.php/books` | `GET /api.php/books` | `GET /api.php/_/books.list` |
| `books.get` | `GET /api.php/books/42` | `GET /api.php/books(42)` | `GET /api.php/_/books.get?id=42` |
| `<related>.by<FK>` | `GET /api.php/books/42/reviews` | `GET /api.php/books(42)/reviews` | `GET /api.php/_/reviews.byBookId?book_id=42` |
| `reports.topByPrefix` (hand-written) | — | — | `GET /api.php/_/reports.topByPrefix?prefix=Book%201` |
| `users.adminList` | — | — | `GET /api.php/_/users.adminList` |

`$filter`, `$orderby`, `$top`, `$skip`, `$select`, `$count` query parameters work on the REST and OData paths.

**`$filter` syntax.** Conditions are compiled to SQL and applied *before* paging and `$count`, so a page and its count always describe the filtered set. Operators `eq ne gt ge lt le`; combine with `and`, `or`, `not` and parentheses (precedence `not` > `and` > `or`); functions `contains(col,'x')`, `startswith(col,'x')`, `endswith(col,'x')`; literals are `'text'` (`''` escapes a quote), numbers, `true`, `false`, `null` (`eq null` / `ne null`) and dates (`2026-11-20`, `2026-11-20T10:00:00Z`). Example: `seconds ge 3600 and (project_id eq 2 or contains(description,'review'))`. A malformed expression or a column the mask does not return is a `QUERY_VALIDATION_ERROR` (the latter lists the available columns). The filter composes with, and never widens, tenant and row scopes. The generic `_/<mask>` path is the universal escape hatch — it reaches **any** mask, including hand-written ones and admin masks for secret tables, with parameters supplied as URL query keys.

**Array params on the generic path** accept either form:

```
GET /api.php/_/books.getMany?ids=1,3,5         (comma-separated)
GET /api.php/_/books.getMany?ids[]=2&ids[]=4   (PHP array form)
```

For batch reads, ref-chaining, or anything beyond a single mask, use the `POST {op:'query', ...}` envelope instead.

## Config Reference

### db.persistent (optional)

Opt-in persistent PDO connection. When enabled (and the SAPI is `fpm-fcgi` / `apache2handler` / similar long-lived worker), each worker reuses the same PDO instance across requests — the TCP/TLS/auth handshake (~150–200 ms over public-IP remote PG) collapses to ~0 on the warm path.

```php
'db' => [
    'dsn'        => 'pgsql:host=...;dbname=...',
    'user'       => '...',
    'pass'       => '...',
    'persistent' => true,
],
```

**When to enable:**

- Production behind PHP-FPM or mod_php and the database is on a different host (any RTT > ~5 ms).
- Public-internet PG/MySQL backends where TLS handshake dominates request latency.

**When to leave off:**

- `php -S` development — process-per-request anyway, no win.
- SQLite — connection setup is local, the flag is a no-op cost.
- Any deployment where you `SET search_path` / `SET timezone` / `LISTEN` per request without idempotent re-application logic.

**Caveats** (operators must read these):

- **Stranded transactions.** If a worker is killed mid-transaction (OOM, fatal, signal), the persistent connection retains the open transaction state. On the next request that worker handles, Shape's `Database::__construct` runs `rollBack()` defensively before returning — but if your application code starts transactions outside `BatchExecutor`, you should add an equivalent guard.
- **Connection-level state.** `SET search_path = ...`, `SET timezone = ...`, `LISTEN <channel>` and similar PG session settings are not auto-reapplied. Either set them on every request inside the request handler (idempotent), or include them in the DSN so PG applies them at connection time.
- **Connection limits.** With N PHP-FPM workers and persistent connections, you'll hold N idle DB connections per host even at zero traffic. Size your DB's `max_connections` accordingly.

### access

Maps table names to roles. Three forms (mix freely):

```php
// 1. Per-table list (same roles for read and write)
'access' => [
    'books' => ['admin', 'user'],
],

// 2. Per-table split (different roles for read vs write)
'access' => [
    'reviews' => ['read' => ['admin', 'user'], 'write' => ['user']],
],

// 3. Wildcard "all tables" + per-table overrides
'access' => [
    '*'      => ['admin', 'user'],     // every introspected table
    'orders' => ['admin'],             // override for one table
],
```

**Resolution rules:**

| Config | What gets introspected |
|---|---|
| `access` empty (or absent) | Nothing — no `DESCRIBE`, no masks, no overhead |
| `access` with specific keys only | Just those tables |
| `access` includes `'*'` | Every table from `SHOW TABLES`, minus `exclude_tables` and secret tables |
| `include_tables` set | Exactly those (overrides the above) |

So in a 2000-table database, `'access' => ['books' => [...], 'authors' => [...]]` introspects 2 tables, while `'access' => ['*' => [...]]` introspects all 2000-minus-excludes. Switch between them as the schema and audience demand.

### validate

Maps `table.column` to validation rules:

```php
'validate' => [
    'books.title'  => 'notEmpty',       // single rule
    'books.price'  => 'positive',       // single rule
    'reviews.rating' => ['positive'],   // array of rules
],
```

Available rules: `positive`, `nonNegative`, `notEmpty`, `email`, `maxLen:N`.

Validators apply to fields **present in the payload**, on both `insert` and `update` mutations. (A missing field on update simply does not trigger its validator.)

#### Auto-derived validators

By default Shape derives the obvious rules straight from the schema, so you do not need to repeat them:

| Schema fact | Auto rule |
|---|---|
| `NOT NULL` string column with no DEFAULT and not auto-increment | `notEmpty` |
| `VARCHAR(N)` / `CHAR(N)` | `maxLen:N` |

Manual rules from the `validate` config are **appended** to the auto-derived chain (they extend rather than replace). So:

```php
'validate' => [
    'books.title' => 'notEmpty',   // already auto-derived, harmless duplicate
    'users.email' => 'email',      // adds 'email' on top of auto notEmpty + maxLen:190
],
```

Disable schema-driven derivation entirely with:

```php
'auto_validators' => false,
```

Useful when you want to opt fields out of the default `notEmpty`/`maxLen` checks (e.g., your column accepts deliberately-empty strings). With this flag off, only your `validate` config applies.

### exclude_tables

Extra tables to skip during introspection — applied on top of the hardcoded secret-table baseline. Useful when using `access['*']` and you want to keep, say, audit or temp-import tables out of the API:

```php
'access'         => ['*' => ['admin', 'user']],
'exclude_tables' => ['legacy_audit', 'tmp_import'],
```

Ignored when `include_tables` is set or when `access` lists specific keys without `'*'` — an explicit allowlist always wins.

### secret_tables (optional)

The framework hardcodes `users` and `refresh_tokens` as secret: they are **never** auto-introspected for CRUD even if listed in `access` or `include_tables`. The only way to expose them is through hand-written masks (or the opt-in `admin_masks` below).

You can extend — but not shrink — this baseline:

```php
'secret_tables' => ['api_keys', 'oauth_clients'],
```

Effective list = `['users', 'refresh_tokens', 'api_keys', 'oauth_clients']`.

### admin_masks (optional)

Opt-in built-in safe masks for secret tables. When enabled, Shape introspects each secret table that exists in the DB, drops sensitive columns by name pattern, and emits read-only masks gated to the `admin` role:

```php
'admin_masks' => true,
```

Generated masks (when the table exists):
- `users.adminList` — `SELECT (safe cols) FROM users`
- `users.adminGet` — same, filtered by `id`
- `refresh_tokens.adminList`, `refresh_tokens.adminGet`
- …and the same pair for any custom `secret_tables` you added

**Safe-column filter** (case-insensitive): drops exact `token`, `secret`, `password`, `salt`; prefix `password_`, `secret_`, `salt_`, `mfa_`, `totp_`; suffix `_hash`, `_secret`, `_token`. Any column that survives is selected.

If a column you want to expose gets filtered out, or you need a join, write your own mask in `config['masks']` — your manual masks override the built-ins for the same key.

### include_tables (optional)

Explicit allowlist of tables to introspect. When set, Shape skips `SHOW TABLES` entirely and only touches the listed tables:

```php
'include_tables' => ['books', 'authors', 'reviews'],
```

When omitted, Shape infers the allowlist from `array_keys($access)`. Use `include_tables` explicitly only when you want to introspect a table that's **not** in `access` — for example, a table exposed only through custom masks but whose metadata you still want available (rare).

**Why this matters:** for a 2000-table database exposing 3 via the API, introspection drops from ~16 000 queries to ~24.

### masks (optional)

Raw SQL masks for JOINs, aggregations, custom queries — same as entity mode:

```php
'masks' => [
    'books.withAuthor' => [
        'sql' => 'SELECT b.*, a.name AS author_name FROM books b JOIN authors a ON a.id = b.author_id WHERE b.id = :id',
        'params' => ['id' => ['type' => 'int', 'required' => true]],
        'roles' => ['admin', 'user'],
        'description' => 'Get one book joined with its author name by book id.',
        'cache_control' => 'public, max-age=60',
    ],
],
```

The optional `description` field is a one-sentence plain-English summary that flows into the manifest, OpenAPI `summary`, and LLM-tool description. Auto-generated CRUD masks (`<table>.list`, `<table>.get`, `<table>.byFK`, etc.) carry their own descriptions automatically — including soft-delete awareness when applicable. Setting `description` on a hand-written mask is what stops an agent from having to read the SQL to know what the endpoint does.

#### `cache_control` — opt-in HTTP caching for GET reads

The optional `cache_control` field is emitted verbatim as the `Cache-Control` response header on the GET surface (OData-style `/api.php/<entity>`, etc.). Combined with the always-on ETag (computed from the response body), this makes the read endpoint cacheable by browsers, CDNs, and reverse proxies.

```php
'masks' => [
    'books.list' => [
        'sql' => '...',
        'cache_control' => 'public, max-age=60',     // Cloudflare / Fastly / Vercel cache for 60s
    ],
    'orders.recent' => [
        'sql' => '...',
        'cache_control' => 'private, max-age=10',    // browser only, per-user
    ],
],
```

**Behavior:**

- **ETag is always emitted** on GET responses (independent of `cache_control`). Computed as a SHA-256 of the response body.
- **`If-None-Match: <etag>`** request header collapses unchanged responses to `304 Not Modified` with no body — saves bandwidth on hot endpoints.
- **`If-None-Match: *`** also triggers `304` (clients use this when they have any cached copy and want to revalidate).
- **`cache_control` is verbatim** — the dev declares the privacy posture (`public` / `private` / `no-store`) and the freshness window. Shape doesn't second-guess.
- **Default:** no `Cache-Control` header. The framework opts out of caching unless the dev opts in — safer default for stateful or auth-scoped reads.

This is the architectural unlock that makes Shape backends behind a CDN dramatically cheaper to run: a `public, max-age=60` mask serves at most one DB hit per minute per cache node, regardless of traffic.

### commands (optional)

Business logic endpoints — same as entity mode:

```php
'commands' => [
    'order.process' => function(array $params, CommandContext $ctx): array {
        // ...
    },
],
```

### cache (optional)

Without cache, Shape introspects the database on every request. With a remote DB, that's RTT-bound — tens of seconds for an 80-table schema over a 50ms link. The cache stores everything the framework derived from the schema (entities, admin masks, audit-log detection) so warm requests run with zero DB I/O during boot.

```php
// Cache to system temp directory (sys_get_temp_dir()/shape_cache/) — quickStart default
'cache' => true,

// Cache to custom directory
'cache' => '/var/cache/shape',

// Disable (only useful for debugging — and even then, prefer clearCache())
'cache' => false,
```

**Default:** `quickStart()` ships `cache => true` in every environment. `fromDatabase()` defaults to `false` and expects an explicit choice.

**What gets cached:** the full *bundle* — entity metadata, generated admin masks, the `audit_log` table-presence flag, and audit masks. Warm boot doesn't construct a `Database` or `DatabaseIntrospector` at all.

**What doesn't get cached:** validator closures, mask SQL for entity-derived masks, commands — all regenerated from entities each request (cheap once metadata is known).

**Cache invalidation** (automatic, mtime-based):
- `app.php` changes
- `env.php` / `env.local.php` changes

**Cache invalidation** (manual, required for):
- DB schema changes — the framework has no way to detect table/column modifications in the live database. Add a `Shape\ShapeApp::clearCache(__DIR__ . '/../config/app.php')` step to your deploy and any `ALTER TABLE` workflow.

Clear cache programmatically:
```php
Shape\ShapeApp::clearCache(__DIR__ . '/../config/app.php');
```

Or just delete the cache files:
```bash
rm -rf /tmp/shape_cache
```

**Cache format versioning:** the bundle is tagged with `v: 1`. Older cache files written before bundling existed are auto-rejected on first warm read and re-introspected without operator intervention.

### default_list_limit (optional)

Auto-generated `.list` and `by<FK>` masks ship with an SQL-side `LIMIT` to prevent OOM on large tables. The framework wraps the mask SQL with `LIMIT N` at execute time so the database returns at most `N` rows regardless of the table's size — a `companies.list` against a 5M-row table consumes the same memory as one against 50.

```php
// Default: 100 rows per .list and per by<FK> read
// Override globally:
'default_list_limit' => 500,

// Disable the default cap (not recommended unless every table is small):
'default_list_limit' => 0,
```

**Behavior:**

- `?$top=N` (or POST `options.top`) overrides the default; OData/REST/POST surfaces all flow through the same path.
- `?$skip=M` works the same way — `LIMIT N OFFSET M` runs at the SQL layer.
- `?$count=true` triggers an additional `SELECT COUNT(*)` query so `total_count` is the unbounded total even though the page is limited.
- `?$orderby=...` falls back to the in-memory path because pushing `ORDER BY` requires column-validating against the inner SQL's output (a v2 enhancement). For ordered pagination on large tables, write a hand-written mask that puts `ORDER BY` in the SQL itself.
- Hard ceiling of 200 rows per request applies to explicit `top` values (`MAX_TOP`); the default limit can be lower but the explicit override cannot exceed it.
- The active default surfaces in the manifest as `defaultLimit` per mask, so agents know the page size without trial calls.

This default is non-negotiable for auto-CRUD against real schemas — the alternative is silent OOMs on any production-shape table.

### pk_assignment (optional)

Legacy databases — typically SQL Server migration sources — often have plain int primary keys with **no IDENTITY / AUTO_INCREMENT / default**: the original application computed the next id in code. `pk_assignment` tells Shape to take over that job:

```php
// Whole-database default (this is already the default — usually you write nothing):
'pk_assignment' => 'auto',

// Per table; '*' sets the database default:
'pk_assignment' => ['*' => 'auto', 'invoices' => 'manual', 'codes' => 'db'],
```

Three modes:

- **`auto`** (default) — recognized from the schema during introspection: a single-column int-family PK (`int`/`bigint`/`smallint`/`tinyint`, or `decimal`/`numeric` with scale 0) that is not auto-increment and has no default resolves to `manual`; everything else to `db`. For a genuinely legacy MSSQL schema this means **zero config** — detection just works.
- **`manual`** — force framework allocation (detection fooled, or an identity column exists but is abandoned). Build-time `CONFIG_INVALID` error if the PK is composite or non-int.
- **`db`** — trust the database. The opt-out for int PKs that are semantically client-assigned (business codes, externally allocated ranges).

**Behavior on insert** into a `manual` table when the request doesn't carry the PK: the runtime allocates `MAX(pk) + 1` atomically (SQL Server: one `INSERT … OUTPUT INSERTED … SELECT ISNULL(MAX(pk),0)+1 … WITH (UPDLOCK, HOLDLOCK)` statement; PostgreSQL: advisory lock + `RETURNING`; MySQL/SQLite: locked read + explicit insert in one transaction) and retries up to 3× on unique-violation or deadlock — a race with a legacy app writing its own `MAX+1` alongside surfaces as `DB_ERROR` with detail `PK_ALLOCATION_RACE` only after all retries fail. An explicitly supplied PK value always passes through untouched, so imports and migrations keep working.

Externally the PK behaves like a generated column: validators never require it, manifest/OpenAPI mark it auto-assigned, and the batch response returns the allocated value under `id`. Auth tables (`users`, `refresh_tokens`) honor only an explicit `manual` entry (table name or `'*'`) — the auth path performs no introspection, so `auto` cannot apply there.

Design rationale (and why not a counter table or SQL `SEQUENCE`): [MANUAL-PK-PLAN.md](MANUAL-PK-PLAN.md).

### tenancy (optional)

Turns a single-tenant backend into a pooled B2B multi-tenant one: every tenant-owned table carries a `tenant_id` column, and Shape injects the `WHERE tenant_id = …` predicate on reads and stamps the column on writes — automatically, by convention, with no per-mask boilerplate and no class of "forgot the predicate" cross-tenant leaks.

```php
// Convention defaults (registry table `tenants`, column `tenant_id`):
'tenancy' => true,

// Explicit:
'tenancy' => [
    'table'          => 'tenants',       // the registry table ('organizations', …)
    'column'         => 'tenant_id',     // the discriminator column
    'unscoped_roles' => ['superadmin'],  // roles that see across tenants (default: none)
    'enforce'        => 'strict',         // 'strict' (default) | 'relaxed'
    'global_tables'  => [],              // shared lookup tables exempt from the strict check
],
```

How it works:

- A table is **tenant-scoped** iff it has the `column`. No column → **global** (shared, unscoped). The registry `table` is global by definition — carrying the column is a build error.
- **`enforce: 'strict'`** (default) fails the boot with `CONFIG_INVALID` listing every exposed table that has neither the column nor a `global_tables` entry — that list *is* your migration checklist. A nullable discriminator is a strict error too (a `NULL` row is invisible to every scoped caller). `'relaxed'` skips the sweep for brownfield migrations.
- The **`users` table must carry the column** when tenancy is on (DB-first boot check) — each account belongs to a tenant.
- **Reads**: every generated read mask (`list`/`get`/`getMany`/`by<FK>`, OData GET, and the `$count`) filters to the caller's tenant. An `unscoped_roles` caller (e.g. a superadmin) runs a predicate-free variant and sees all tenants.
- **Writes**: INSERT auto-stamps `tenant_id` from the caller's token; a client-supplied value that differs is rejected `VALIDATION_ERROR` (`detail: TENANT_MISMATCH`). UPDATE/DELETE are constrained to the caller's tenant, so a cross-tenant id simply affects 0 rows (no existence oracle across tenants). Nested `relations` children inherit the stamp.
- **Auth**: the tenant is a **server-issued JWT claim** (`tenant_id`), never a request parameter. Login and refresh reject a suspended tenant (`is_active = 0` or soft-deleted registry row) with `TENANT_SUSPENDED` (403).
- The registry table's CRUD is auto-exposed, gated to `unscoped_roles` — a superadmin manages tenants out of the box (an explicit `access['tenants']` entry overrides).

Client params starting with `__` are reserved (the scope binding is `:__tenant`) and rejected at validation. Full design, rejected alternatives, and the deferred tier (multi-tenant membership, per-tenant emails, database-per-tenant): [MULTI-TENANCY-PLAN.md](MULTI-TENANCY-PLAN.md).

### scopes — row-level security by dimension (optional)

Tenancy isolates *organizations*; `scopes` isolates any *dimension* inside one — a sales rep sees only their `department_id`, an accountant only their `cost_center_id`. It is the tenancy machinery (predicate-compile / stamp / guard) generalized to N discriminator columns, composed with tenancy so predicates **AND together**.

```php
'scopes' => [
    'department_id' => [                          // key = discriminator column
        'claim'          => 'department_id',        // JWT claim (default: the column name)
        'unscoped_roles' => ['controller'],         // roles that see across this dimension (default: none)
        'multi'          => false,                  // true → claim is a LIST, predicate becomes IN
        'self'           => false,                  // true → the value is the caller's own users.id (see below)
        'claim_table'    => null,                   // multi-membership source, e.g.
                                                    // ['table' => 'user_departments', 'user_column' => 'user_id']
                                                    // default: single value read from users.<column>
        'nullable'       => 'warn',                 // 'warn' (default) | 'error' — NULL rows are invisible to scoped callers
    ],
    // 'cost_center_id' => [...], 'branch_id' => [...]
],
```

**Own rows only (`self`).** For "a user sees only their own rows" — time tracking, notes, orders — set `'self' => true`. The scope value is then the logged-in user's own `users.id`, so the discriminator column on the data tables (e.g. `worklogs.user_id`) needs no matching column on `users` and no membership table:

```php
'scopes' => [
    'user_id' => ['self' => true, 'unscoped_roles' => ['admin']],
],
```

Users see and write only rows with their own `user_id` (a forged value → `SCOPE_MISMATCH`; omitted → stamped); `admin` sees and writes all rows. `self` cannot be combined with `multi` or `claim_table`.

Deliberately different from `tenancy` (these are not bugs to fix later):

- A table is **scoped iff it carries the column** — dimensions naturally apply to a subset of tables, so there is **no strict sweep**; a table without the column just stays unscoped.
- No registry / suspension / provisioning — dimensions are plain lookup tables.
- The claim may be **multi-valued** (`multi: true` → the predicate becomes `IN (…)` and the caller may hold a list of memberships).
- A nullable discriminator is a boot **warning** by default; opt into a hard `CONFIG_INVALID` with `'nullable' => 'error'`.
- Unscoped is **per scope**: a global admin must appear in every scope's `unscoped_roles`.

How it works:

- **Reads**: every generated read mask gets `AND <col> = :__scope_<col>` (or `IN (:__scope_<col>)` for `multi`) baked in alongside the tenancy predicate — COUNT and OData inherit it. A caller unscoped for a dimension takes a wrap path (the predicate-free variant wrapped with only the predicates it *is* subject to) and sees every value of that dimension within its tenant.
- **Writes**: INSERT stamps the column from the claim (single-valued); a differing client value → `VALIDATION_ERROR` (`detail: SCOPE_MISMATCH`, `scope: <column>`). A `multi` or dimension-unscoped caller **must** name the column (the framework never guesses) — omitting it → `VALIDATION_ERROR` (`detail: SCOPE_REQUIRED`). UPDATE/DELETE are constrained to the caller's dimension (cross-dimension id → affected 0); a cross-dimension move → `SCOPE_MISMATCH`. Nested `relations` children inherit the stamp.
- **Auth**: each scope rides as a **server-issued JWT claim**, read from `users.<column>` (or the `claim_table` rows for `multi`) at login/refresh — never a request parameter, and OIDC resolves it from the local user row. A role in the scope's `unscoped_roles` gets the claim forced to `null`; a scoped caller whose (pre-feature) token carries no claim is rejected with `AUTH_INVALID` (re-login required).
- **Manual masks**: declare `'row_scopes' => ['department_id']` (each `:__scope_<col>` placeholder must appear in the SQL; writable masks are exempt) or opt out with `'row_scopes' => false` (roles must be ⊆ the scope's `unscoped_roles`). Any `row_scopes` on a mask forces `Vary: Authorization` and forbids `public` cache_control — same rule as `tenant_scope`.
- **Manifest / OpenAPI**: entities gain `"rowScopes": ["<col>"]`; masks carry `row_scopes` as `{"<col>": {"multi": bool}}`. Scope columns are optional in insert schemas (required note for `multi`) and excluded from update.

Reserved param `:__scope_<column>` is rejected from clients like `:__tenant`. Full design and the shared cross-stack contract: [ROW-SCOPES-PHP-PLAN.md](ROW-SCOPES-PHP-PLAN.md).

## File uploads — `uploads` config namespace

Files are the one thing the JSON envelope can't carry cleanly. Shape's upload feature lives in its own config namespace and route — declarative endpoints, multipart-form requests, JSON responses with a stable URL the client can use.

```php
'uploads' => [
    'covers' => [
        'storage' => [
            'driver'     => 'local',
            'path'       => '/var/www/myapp/uploads/covers',
            'public_url' => '/uploads/covers',
        ],
        'roles'      => ['admin'],
        'max_size'   => 10485760,                // 10 MB
        'mime_types' => ['image/jpeg', 'image/png', 'image/webp'],
        'description' => 'Upload a book cover image.',
    ],
],
```

Then the client uploads:

```bash
curl -X POST -F "file=@cover.jpg" \
  -H "Authorization: Bearer eyJhbGc..." \
  https://api.myapp.com/api.php/upload/covers
```

And gets:

```json
{
  "id":        "1de5084eedcbb7ed.jpg",
  "filename":  "cover.jpg",
  "size":      102400,
  "mime_type": "image/jpeg",
  "url":       "/uploads/covers/1de5084eedcbb7ed.jpg"
}
```

The dev typically chains a normal mutation that links the URL or `id` into a domain row.

### Behavior

- **Auth.** Same `JwtGuard` pipeline as the rest of the API. `roles` on the upload entry gates access; empty role list means public. OIDC users' `oid` is resolved to `users.id` before the role check, so audit-related conventions still apply when the dev wires a follow-up mutation.
- **Mime type detection.** The framework reads actual file content via `finfo_open(FILEINFO_MIME_TYPE)` — the client's `Content-Type` header is ignored. A spoofed `Content-Type: image/jpeg` for what's actually a text file gets rejected with `text/plain` in the error envelope.
- **Filename normalization.** The original filename is preserved in the response (`filename` field) but never used in storage paths or URLs. Storage keys are 16 hex characters + the (lowercased, alpha-numeric only) extension. No path traversal is possible.
- **Size limits.** `max_size` is enforced in bytes. PHP's `upload_max_filesize` and `post_max_size` ini limits still apply at the runtime level — `max_size` is the per-endpoint policy on top.
- **Per-driver storage.** v1 ships `local` (disk). The `StorageInterface` is a clean three-method contract; S3, GCS, R2 drop in as additional drivers without changing the upload manager.
- **Errors flow through the standard self-documenting envelope.** Missing file, unknown endpoint, role forbidden, oversized, disallowed mime — all return `code` + `message` + `expected` + `hint` so agents recover without re-fetching docs.

### What it doesn't do (current scope)

- **No DB row insertion by default.** A plain upload entry stores bytes and returns metadata; the dev chains a `POST {op:'mutation'}` if they want a row. For documents that must be access-controlled (case files, contracts, damage photos) declare `attachments` — see *Private attachments* below.
- **No multi-file uploads.** Send one file per request under the `file` form field. A future variant could batch.
- **No signed URLs.** The `public_url` returned is the static-served path; the web server is expected to serve it. For private content use *Private attachments* below (authorised streaming through Shape).
- **No image processing.** Resize, crop, format-convert all happen in the dev's app or a downstream worker. Shape just stores bytes.

### Manifest exposure

When `uploads` is present in config, the manifest output includes the names and descriptions of upload endpoints so agents discover them as part of the API surface (without needing to read the SQL — there is no SQL).

### Schema linkage pattern

Common shape after upload:

```json
// 1. Upload the file
POST /api.php/upload/covers
→ { "url": "/uploads/covers/abc123.jpg", ... }

// 2. Insert the domain row referencing the URL
POST /api.php
{
  "op": "mutation",
  "payload": { "mutations": [{
    "ref": "book",
    "table": "books",
    "action": "insert",
    "data": { "title": "Example", "cover_url": "/uploads/covers/abc123.jpg" }
  }]}
}
```

Two requests, but each has clear semantics. Agents handle this well — the upload completes synchronously and the URL is in scope by the time the mutation runs.

### Private attachments — files bound to a row, readable only through Shape

Static URLs are fine for covers and logos, not for a lawyer's case file. Add an `attachments` block to the upload entry and the file becomes a **row of an ordinary table**; Shape stores it outside the webroot and streams it only after the table's own roles and row scopes allow it. Whoever cannot see the case cannot fetch its documents.

```php
'uploads' => [
    'case_files' => [
        'storage'     => ['driver' => 'local', 'path' => '/srv/app-files/case_files'],  // NOT under the webroot
        'roles'       => ['admin', 'user'],
        'max_size'    => 20 * 1024 * 1024,
        'mime_types'  => ['application/pdf', 'image/jpeg'],
        'attachments' => [
            'table'   => 'case_files',             // ordinary table (below); needs an `access` entry
            'fields'  => ['case_id'],              // form fields copied into the row
            'parents' => ['case_id' => 'cases'],   // optional: caller must be able to read cases.get(case_id)
        ],
    ],
],
```

```sql
CREATE TABLE case_files (
  id INT AUTO_INCREMENT PRIMARY KEY,
  case_id INT NOT NULL REFERENCES cases(id),
  owner_id INT NOT NULL,                 -- any scope column your app uses (self / tenant) works as usual
  file_key VARCHAR(64) NOT NULL,         -- storage key, never exposed
  filename VARCHAR(255) NOT NULL, mime_type VARCHAR(100) NOT NULL,
  size_bytes BIGINT NOT NULL, sha256 CHAR(64) NOT NULL
);
```

| Route | Does |
|---|---|
| `POST /api.php/upload/<name>` (multipart: `file` + declared fields) | stores the file, inserts the row, returns `{id, filename, size, mime_type, sha256, <fields>}` — **no URL** |
| `GET /api.php/files/<name>/<id>` | streams the file (`attachment`, `nosniff`, `Cache-Control: private, no-store`, `ETag` = sha256) |
| `DELETE /api.php/files/<name>/<id>` | deletes the row, then the file |

Guarantees:

- **Same rules as every mutation.** The row is inserted and deleted through `BatchExecutor`, so access roles, row scopes, tenancy, validators and the audit log apply; the scope value is stamped from the caller's token.
- **404, not 403.** A row the caller may not see answers exactly like a missing one — no existence oracle.
- **Parent checked first.** With `parents`, the referenced row is read through its scoped `.get` mask *before* anything is stored; uploading to somebody else's case stores nothing.
- **No orphans.** If the insert is refused the stored file is removed; on delete the file goes after the row.
- **Integrity.** `sha256` is computed from the uploaded bytes and kept in the row (useful for contracts and invoices).
- Primary keys of the attachment and parent tables are assumed to be named `id`. Reading the table through the normal API (`/api.php/case_files`) lists metadata only; bytes are reachable solely through `/files/`.

The .NET port has the same routes and behaviour (uploads are declared in code there):

```csharp
Uploads = new() {
    ["case_files"] = new UploadEntry {
        Storage = new() { Driver = "local", Path = "/srv/app-files/case_files" },
        Roles = new() { "admin", "user" },
        MimeTypes = new() { "application/pdf" },
        Attachments = new AttachmentsConfig {
            Table = "case_files",
            Fields = new() { "case_id" },
            Parents = new() { ["case_id"] = "cases" },
        },
    },
},
```

With an `mime_types` allow-list the type must be **proven by the content** (magic bytes / finfo). A file the sniffer cannot identify is rejected even when the client sends `Content-Type: application/pdf`; without an allow-list the .NET port still falls back to the client header for unknown content, as before.

Not covered yet: antivirus hook, versions of one document, and storage backends other than `local`.

## Bulk nested create — `relations` blocks

When a parent insert needs to create FK-related child rows in the same transaction, attach a `relations` block to the mutation. The framework walks the FK chain — auto-detected from the schema — and injects the parent's new PK into each child's FK column. No `ref` orchestration, no manual transaction flag.

**Two levels:**

```json
{
  "op": "mutation",
  "payload": {
    "mutations": [{
      "ref": "order",
      "table": "orders",
      "action": "insert",
      "data": { "customer_id": 7, "total": 28.99 },
      "relations": {
        "items": [
          { "product_id": 12, "quantity": 1, "price": 9.99 },
          { "product_id": 25, "quantity": 2, "price": 4.50 }
        ]
      }
    }]
  }
}
```

**Three levels (relations nest recursively):**

```json
{
  "table": "orders",
  "action": "insert",
  "data": { "customer_id": 8, "total": 50.00 },
  "relations": {
    "items": [{
      "product_id": 30, "quantity": 1, "price": 50.00,
      "relations": {
        "item_options": [
          { "name": "Size",  "value": "Large" },
          { "name": "Color", "value": "Red" }
        ]
      }
    }]
  }
}
```

The response mirrors the request shape with assigned PKs:

```json
{
  "results": [{
    "ref": "order", "action": "insert", "table": "orders",
    "affected": 1, "id": 1,
    "relations": {
      "items": [{
        "action": "insert", "table": "items", "affected": 1, "id": 1,
        "relations": {
          "item_options": [
            { "action": "insert", "table": "item_options", "affected": 1, "id": 1 },
            { "action": "insert", "table": "item_options", "affected": 1, "id": 2 }
          ]
        }
      }]
    }
  }]
}
```

**Behavior:**

- **Auto-detected from FK columns.** `MaskGenerator` builds an inverse-FK map at boot — for every parent table, which child tables have FKs back to it, through which columns. The map flows onto the writable mask; `BatchExecutor` reads it at request time.
- **Atomic by default.** Any mutation with `relations` automatically forces `transaction: true` for the whole batch. A failure mid-tree rolls everything back; you never end up with an order whose items couldn't be inserted.
- **Audit columns + audit log all run uniformly.** Each child insert is a normal mutation through `executeSingleMutation`, so `created_at`, `created_by`, `audit_log.actor_id`, role checks, validators, soft-delete, and OIDC user-id resolution all behave identically at every nesting level.
- **Nesting depth is unlimited.** Each child row can carry its own `relations` block. The keyword `relations` is reserved within child data; if you have a real column literally named `relations`, rename it.
- **Per-row PK in the response.** The parent insert returns its new `id`; nested children return their own ids inside their corresponding `relations` arrays. Map back to your application objects however you want.

### Disambiguating multiple FKs to the same parent

When a child table has more than one FK pointing at the same parent (e.g., `notes.order_id` AND `notes.follow_up_order_id`), the relation key alone is ambiguous. The framework rejects the call with a self-documenting error and lists the candidate FK columns:

```json
{ "code": "MUTATION_VALIDATION_ERROR",
  "message": "'notes' has multiple FKs to 'orders'; disambiguate the relation key.",
  "expected": ["notes.order_id", "notes.follow_up_order_id"],
  "hint": "Use 'relations: { \"notes.<fk_column>\": [...] }' to pick which FK the children attach through." }
```

Then call with the explicit key:

```json
"relations": {
  "notes.order_id": [{ "body": "first note" }]
}
```

### Manifest exposure

The writable mask of every parent table carries a `relations` field listing each detected child table and its FK column(s). The LLM-tool description for the mutate tool also names the relation:

> *"Insert / update / delete rows in orders. Audit columns are populated automatically — do not include them in `data`. Bulk-create children inline via `relations: { <child_table>: [...] }` — framework walks the FK chain in one transaction."*

So an agent reading the manifest knows the bulk-create surface exists per-table without needing to discover it experimentally.

### What it doesn't do (current scope)

- **Inserts only.** No nested updates, no nested deletes, no upserts. Each is its own future feature; bundling them now would multiply edge cases unproductively.
- **No many-to-many junction-table inference.** A `posts.tags` block won't auto-create rows in `post_tags` for you — that's too much disambiguation. Use an explicit `post_tags` mutation in the same batch.
- **No reverse-FK creates.** `relations` always walks parent → child via the child's FK column. Inserting a parent ON BEHALF OF a child you just created is not supported (and rarely needed).

For everything else, the existing `ref` system stays available — it can do anything `relations` can plus more, just with explicit orchestration.

## Audit trail (full history) by convention

Add an `audit_log` table to your schema and Shape automatically writes one row per successful mutation — who did what, when, against which row in which table. The audit trail is the *history* (every mutation, in order); the per-row `created_*` / `updated_*` audit columns are the *current state* (last touch only). Different jobs, both shipped.

**Schema convention** (the dev creates this table; Shape detects it at boot):

```sql
CREATE TABLE audit_log (
  id          BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  occurred_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
  actor_id    VARCHAR(64) NULL,           -- from authContext.id / oid / sub
  actor_email VARCHAR(190) NULL,
  actor_role  VARCHAR(40) NULL,
  table_name  VARCHAR(64) NOT NULL,
  row_id      VARCHAR(64) NULL,           -- best-effort PK of affected row
  action      VARCHAR(16) NOT NULL,       -- 'insert' | 'update' | 'delete'
  changed     TEXT NULL                   -- JSON list of column names changed
);

CREATE INDEX idx_audit_log_occurred_at ON audit_log(occurred_at);
CREATE INDEX idx_audit_log_actor       ON audit_log(actor_id);
CREATE INDEX idx_audit_log_target      ON audit_log(table_name, row_id);
```

**Behavior:**

- **Detected at boot.** If the `audit_log` table is present, Shape registers it in `_shape.audit_log_table` and ShapeRouter wires an `AuditLogger` into `BatchExecutor`. If absent, no logging happens — opt-in by table presence.
- **One row per successful mutation.** Inserts, updates, deletes all log. Reads do not.
- **Atomic with the mutation when in a transaction.** When the mutation batch sets `transaction: true`, the audit-log INSERT is part of the same transaction — either both happen or neither does. Without a transaction, audit is best-effort but loud-fail (an exception bubbles up).
- **Actor identifier from `authContext.id` / `oid` / `sub`.** Same lookup as the per-row `created_by` / `updated_by` columns, so a single actor shows up identically in both places. Local Shape JWT (HS256, `sub`) and OIDC tokens (Entra ID `oid` after claim mapping) both work.
- **Insert `row_id` from the new row's PK.** `BatchExecutor::performInsert` uses `Database::insertReturningId` for portable lastInsertId across all drivers.
- **Update / delete `row_id` from `filter.id`** (or first scalar filter value) — best-effort, documented as such.
- **`changed` records column NAMES only**, not values. Keeps PII out of the log by default. Tier 2 will add an opt-in `'audit_diff' => true` for full before/after value capture.

### Built-in admin masks

When `audit_log` is detected, four read-only masks ship automatically (all admin-only, all generated regardless of whether `admin_masks: true` is set):

| Mask | Purpose |
|---|---|
| `audit_log.adminList` | Most recent 200 entries, newest first |
| `audit_log.byActor` (param: `actor_id`) | Full history for one actor |
| `audit_log.byTable` (param: `table_name`) | All mutations against one table |
| `audit_log.byTarget` (params: `table_name`, `row_id`) | Full mutation history of one row |

These appear as MCP tools (`audit_log_adminList`, `audit_log_byTarget`, etc.) in the same surface as everything else. An admin opening Claude Desktop against a Shape backend can query the audit trail in plain English.

### Tamper resistance

`audit_log` joins the framework's `SECRET_TABLES_BASELINE` automatically — no auto-CRUD, no writable mask, can't be mutated through the normal API surface. The recursion guard in `BatchExecutor` also prevents direct writes to `audit_log` from triggering audit entries about themselves (no infinite regress).

To clear or trim the log, run SQL directly against the database (a periodic retention job is the recommended pattern — Shape doesn't auto-prune).

### Opt out

Don't create the table, or set `'audit' => false` in app.php config. Either way disables the feature entirely.

### Composes with everything

Soft-delete + audit columns + audit log + secret tables + admin masks + OIDC auth all work together. A delete on a soft-deletable table still reaches the audit log as `action: 'delete'`. A Microsoft-authenticated user updating a row stamps both `updated_by = oid` on the row AND `actor_id = oid` in the audit log. The same actor identifier flows through every audit surface.

### What this is NOT (Tier 1 limits)

- **No read auditing.** Reads aren't logged. Read-audit doubles storage cost and is typically only required for HIPAA-grade compliance — defer until needed.
- **No before/after value capture.** Column names changed, not values. Tier 2 (opt-in) will add full diffs.
- **No retention enforcement.** The log grows forever; document a SQL-driven retention job as part of your deployment runbook.
- **No CDC streaming.** This is a database table, not a Kafka topic. If you need a real-time event stream, plug it into a downstream consumer of the table.

## Audit columns by convention

Same idea as soft-delete, but for tracking who created/updated each row and when. Add any of these columns to a table and Shape stamps them automatically on insert / update — no hooks, no triggers, no ORM annotations.

| Column name | Required type | Stamped on |
|---|---|---|
| `created_at` | `TIMESTAMP` / `DATETIME` | INSERT only |
| `created_by` | `INT` / `BIGINT` / `VARCHAR` / `CHAR` | INSERT only |
| `updated_at` | `TIMESTAMP` / `DATETIME` | INSERT and UPDATE |
| `updated_by` | `INT` / `BIGINT` / `VARCHAR` / `CHAR` | INSERT and UPDATE |

**Schema example:**

```sql
CREATE TABLE notes (
  id          INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  title       VARCHAR(120) NOT NULL,
  body        TEXT NOT NULL,
  deleted_at  TIMESTAMP NULL,
  created_at  TIMESTAMP NULL,
  created_by  INT NULL,
  updated_at  TIMESTAMP NULL,
  updated_by  INT NULL
);
```

**Behavior:**

- The user identifier for `_by` columns comes from `authContext.id`, falling back to `oid`, then `sub`. Works for both Shape's HS256 tokens (which carry `sub`) and OIDC tokens (which carry `id` after claim mapping).
- **The dev's payload always wins** — if your insert includes `created_at` or `created_by` explicitly, Shape leaves your value alone. Essential for migrations and historical-data imports.
- **UPDATE never re-stamps `created_*` columns**, only `updated_*`. So a row's creator and creation time are immutable through the normal mutation path.
- **No authenticated user → `_by` columns left NULL**, but `_at` columns still stamped. This is rare in practice (writable masks usually require auth) but the framework handles it gracefully.
- Column type for `_by` matches your `users.id` type. Use `INT`/`BIGINT` for numeric ids, `VARCHAR`/`CHAR` for UUID-style external ids (Entra `oid`, etc.).

**Manifest exposure:** entities with audit columns carry an `audit` field:

```json
{
  "table": "notes",
  "audit": {
    "created_at": "timestamp",
    "created_by": "user_id",
    "updated_at": "timestamp",
    "updated_by": "user_id"
  }
}
```

The LLM-tool description for the writable mask mentions audit columns explicitly, so an agent knows not to include them in `data`:

> "Insert / update / delete rows in notes. DELETE soft-deletes (sets deleted_at). Audit columns (created_at, created_by, updated_at, updated_by) are populated automatically — do not include them in `data`."

**Composes with everything:**

- Soft-delete + audit + secret-table baseline + admin masks all work together. A `users` table with `created_at` / `updated_at` is still a secret table; admin masks for it pick up the timestamps; soft-delete still tombstones; audit columns still populate.
- Schema-driven validators don't run on auto-stamped fields (they're injected after validation), so you don't need a `notEmpty` rule on `created_at`.
- Works on every supported driver: MySQL/MariaDB, PostgreSQL, SQLite — same convention, same behavior.

**Opt out:** rename the column. If `archived_at` is a regular nullable timestamp without soft-delete intent, name it that — Shape only matches the literal names `created_at` / `updated_at` / `created_by` / `updated_by`.

## Soft-delete by convention

Any table that has a **nullable timestamp column literally named `deleted_at`** is treated as soft-deletable. No config needed.

**Schema:**

```sql
CREATE TABLE notes (
  id         INT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,  -- or INT AUTO_INCREMENT on MySQL
  body       TEXT NOT NULL,
  deleted_at TIMESTAMP NULL                                  -- this column triggers the convention
);
```

**Behavior on detection:**

| Surface | Without `deleted_at` | With `deleted_at` |
|---|---|---|
| `notes.list` | `SELECT … FROM notes` | `SELECT … FROM notes WHERE deleted_at IS NULL` |
| `notes.get` | `… WHERE id = :id` | `… WHERE id = :id AND deleted_at IS NULL` |
| `notes.getMany` | `… WHERE id IN (:ids)` | `… WHERE id IN (:ids) AND deleted_at IS NULL` |
| `notes.byFK` | `… WHERE fk = :fk` | `… WHERE fk = :fk AND deleted_at IS NULL` |
| `mutation` `action: insert` | `INSERT INTO notes …` | unchanged |
| `mutation` `action: update` | `UPDATE notes SET …` | unchanged (so you can restore by `data: {deleted_at: null}`) |
| `mutation` `action: delete` | `DELETE FROM notes WHERE …` | `UPDATE notes SET deleted_at = CURRENT_TIMESTAMP WHERE … AND deleted_at IS NULL` |

The extra `deleted_at IS NULL` guard on the soft-delete UPDATE means a second delete on the same row returns `affected: 0` (correct — the row is already tombstoned).

**Restore a soft-deleted row:**

```json
{
  "op": "mutation",
  "payload": { "mutations": [
    {"ref":"r1","table":"notes","action":"update","filter":{"id":2},"data":{"deleted_at":null}}
  ]}
}
```

UPDATE deliberately doesn't auto-filter on `deleted_at`, so restore works.

**Manifest exposure:** soft-deletable entities carry a `softDelete: "deleted_at"` field in the manifest output. The LLM-tools format describes the mutate tool with "DELETE on this table is a soft-delete (sets deleted_at = CURRENT_TIMESTAMP); reads automatically filter out soft-deleted rows."

**Detection rules** (must all be true):
- Column name is exactly `deleted_at`
- Column is nullable
- Column type is `timestamp` or `datetime` (after PG type normalization)

**Opt out:** rename the column. There's intentionally no per-table config flag — if you don't want the convention, don't use the column name.

**Hard delete:** if you genuinely need a hard DELETE on a soft-deletable table (GDPR erasure, etc.), write a custom mask in `config['masks']` that issues raw `DELETE` and gate it to `admin`.

## Directory Structure

```
myapp/
  config/
    app.php         ← access, validate, commands
    env.php         ← DB connection, auth settings
  database/
    schema.sql      ← reference schema (applied separately)
  public/
    api.php         ← entry point (6 lines)
  bootstrap.php     ← autoloader
```

No `entities/` folder. The app config is ~30 lines.

## fromEntities vs fromDatabase

| | fromEntities | fromDatabase |
|---|---|---|
| **Source of truth** | PHP entity classes | Database schema |
| **Entity files** | Required | None |
| **Validation** | `#[Validate]` attributes | `validate` config |
| **Access roles** | `#[Access]` on class | `access` config |
| **FK detection** | `#[FK]` attribute | `FOREIGN KEY` constraint |
| **Type inference** | PHP type hints | `INFORMATION_SCHEMA` / PRAGMA |
| **Best for** | New projects (code-first) | Existing databases, AI agents |

## Supported Databases

- **MySQL** — reads `INFORMATION_SCHEMA`
- **SQLite** — reads `PRAGMA table_info`, `PRAGMA foreign_key_list`, `sqlite_master`
