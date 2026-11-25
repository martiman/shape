# ShapeRuntime

> **AI agents:** start at [`AGENTS.md`](AGENTS.md) — single canonical entry point, ~5k tokens, covers everything from install to MCP. Also discoverable via [`llms.txt`](llms.txt).

ShapeRuntime is a small PHP backend built around one JSON endpoint, server-side query masks, and a shared library used by multiple apps.

Package naming in this repo:
- `packages/shaperuntime` is the low-level runtime source, documented as ShapeRuntime in `../docs/shaperuntime`
- `packages/shape` is the higher-level Shape framework documented in `../docs/shape`

## What is here
- `apps/bookshop` - Shape example application (SQLite, three backend modes, Vue frontend)
- `../web` - presentation website source (shared, at the monorepo root)
- `packages/shaperuntime` - shared ShapeRuntime source package
- `packages/shape` - entity-driven framework on top of ShapeRuntime
- `bruno/bookshop` - API test collection
- `apps/bookshop/database` - bookshop schema and seed data
- `docs` - architecture, deployment, and spec

## Config model
- Source tree: `apps/{app}/config/app.php` and `apps/{app}/config/env.php`
- Deploy layout: `config/{app}/app.php` and `config/{app}/env.php`
- `packages/shaperuntime/src/ShapeRuntime/Http/AppRouter.php` loads `app.php`, then merges `env.php` if present

## Core API endpoint
- `GET /api.php` - health/info
- `POST /api.php` - execute operation via JSON body

Operation names (`op`):
- `query` - fetch data with sorting, paging, grouping, aggregation
- `mutation` - insert, update, or delete data (supports transactions, validators, hooks)
- `command` - domain-level business logic operations
- `auth.token` (alias `auth.login`) - issue token pair
- `auth.refresh` - rotate token pair
- `auth.logout` - revoke session
- `auth.admin.create-user` - create user (admin only)
- `auth.admin.change-password` - reset password (admin only)

Query options for `op = query` (per query item):
- `orderby`: array of `{ "field", "dir" }`
- `skip`: integer >= 0
- `top`: integer >= 0 (max 200)
- `count`: boolean (adds `total_count`)
- `groupby`: array of field names
- `aggregate`: array of `{ "field", "fn", "as" }` where `fn` is `count|sum|avg|min|max`

Mutation actions for `op = mutation` (per mutation item):
- `insert` - requires `table`, `data`
- `update` - requires `table`, `filter`, `data`
- `delete` - requires `table`, `filter`
- Optional `transaction: true` to wrap mutations in atomic transaction
- Optional field validators and before/after hooks per table/action

Command operations for `op = command`:
- Custom domain-level business logic
- Registered in config with handler functions
- Handlers receive parameters and execution context with transaction support

## Routing model
- Backend apps use a single front controller: `public/api.php`
- Route dispatch is shared in `packages/shaperuntime/src/ShapeRuntime/Http/AppRouter.php`
- No server rewrite rules are required

## Run the bookshop example
```bash
cd php
composer install
cp apps/bookshop/config/env.example.php apps/bookshop/config/env.local.php   # set jwt_secret
sqlite3 apps/bookshop/database/bookshop.sqlite < apps/bookshop/database/schema.sqlite.sql
# MySQL instead: mysql -u USER -p DBNAME < apps/bookshop/database/schema.mysql.sql, and set the DSN in env.local.php
php -S 127.0.0.1:8099 -t apps/bookshop/public
```
Create a user (hash with `password_hash`) in the `users` table, then log in via `POST /api.php` with `{"op":"auth.token","payload":{"email":"...","password":"..."}}`.

## Local workflow
1. Edit app code in `apps/bookshop`, shared runtime in `packages/shaperuntime`, Shape framework code in `packages/shape`, or web code in `../web`.
2. Run tests with `cd bruno/bookshop && bru run --env local`.

## Main docs
- `../docs/shaperuntime/ARCHITECTURE.md` - ShapeRuntime structure and deployment shape
- `../docs/shaperuntime/SPEC.md` - request and response contract
- `../docs/shaperuntime/MUTATION-API.md` - mutation rules, validators, hooks, and transactions
- `../docs/shaperuntime/COMMAND-API.md` - command handlers and command execution model
- `../docs/shaperuntime/config-example.php` - example `app.php` and `env.php` fragments
- `../docs/shape/ARCHITECTURE.md` - Shape framework structure
- `../docs/shape/GETTING-STARTED.md` - Shape quick start

## Notes
- Source of truth is in `apps/` and `packages/`.
- `.deploy/` is generated packaging output for hosting.
