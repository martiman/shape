# Shape — start here

Shape generates a JSON, OData, OpenAPI and MCP API from your database schema. Point it at MySQL, PostgreSQL, SQLite or SQL Server; it handles auth, validation, soft delete, audit columns and pagination by convention. With no database configured it creates a SQLite file.

## Three lines of PHP

```php
<?php
require __DIR__ . '/../vendor/autoload.php';
Shape\ShapeApp::quickStart(__DIR__ . '/../.env')->serve();
```

No entity classes, controllers or DTOs are needed. Apply your schema and the endpoints exist.

## Start in about a minute

```bash
composer require shape/shape
echo "" > .env                          # empty config uses an auto-created SQLite file
mkdir -p public && cat > public/api.php <<'PHP'
<?php
require __DIR__ . '/../vendor/autoload.php';
Shape\ShapeApp::quickStart(__DIR__ . '/../.env')->serve();
PHP
php -S localhost:8000 -t public
```

Then open `http://localhost:8000/api.php?manifest`.

For a complete example with auth and a frontend, see [`apps/bookshop`](apps/bookshop) and the instructions in [`README.md`](README.md).

## What you get

| Area | What Shape provides |
|---|---|
| Routes | Generated from the schema |
| Auth | JWT with refresh-token rotation, and OIDC with JWKS validation |
| Audit | `audit_log` table detected by convention and written on every mutation |
| Soft delete | A nullable `deleted_at` column |
| Audit columns | `created_by`, `updated_by`, `created_at`, `updated_at` stamped automatically |
| Pagination | `$top` / `$skip` pushed down to SQL |
| Caching | ETag and per-mask `Cache-Control` |
| MCP | `bin/shape-mcp` over stdio or streamable HTTP |
| API description | Manifest and OpenAPI generated from current state |
| Tenancy | Optional tenant isolation and row-level scopes by dimension |

## Using an AI agent

Give [`AGENTS.md`](AGENTS.md) to your agent (about 5k tokens). It lists the conventions, error format and common mistakes. Errors include `expected`, `example` and `hint`, and the manifest is available at `?manifest`.

## .NET

A .NET implementation lives in [`../dotnet`](../dotnet). It follows the same spec and manifest format.

## More

- [`../docs/shape/GETTING-STARTED.md`](../docs/shape/GETTING-STARTED.md)
- [`../docs/shape/WHY-SHAPE.md`](../docs/shape/WHY-SHAPE.md) — design and security model
- [`../STATUS.md`](../STATUS.md) — what is built and what is planned
