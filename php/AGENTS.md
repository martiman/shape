# Shape — Agent Guide

> ### The framework your AI was waiting for.
> *Schema-driven. Three lines of PHP. Production-grade by the second request.*

> **Shape is a backend framework optimized for AI agents building APIs.** The schema is the spec, masks are the gate, the manifest is the SDK. This file is everything an agent needs to be productive in Shape — read it once, then reach into the deep docs only when you need detail.

**Repository:** `github.com/martiman/shape` (PHP 8.4 reference implementation)
**Sibling stacks:** .NET shipping today; TypeScript / Python / Go on the roadmap. Conventions and manifest format are byte-compatible across implementations — an agent fluent in one is fluent in all.
**Deep documentation:** `../docs/shape/*.md`
**Stays under ~5k tokens by design** so it always fits in your context.

---

## 1. What Shape produces

Point Shape at a database (MySQL / PostgreSQL / SQLite) and you get, automatically:

- **REST/JSON API** — `POST /api.php` with `{op, payload}` envelopes
- **GET surface (three styles)** — REST: `GET /api.php/books/1`. OData: `GET /api.php/books(1)`. Generic mask: `GET /api.php/_/<mask.name>?<params>`. All three reach the same masks and produce identical ETags / Cache-Control. The generic path is the universal escape hatch — it reaches every mask, including hand-written ones and admin masks for secret tables.

**When to POST vs when to GET:**

| You are… | Prefer | Why |
|---|---|---|
| An AI agent / MCP client / programmatic SDK | `POST {op, payload}` | Batch queries, ref-chaining, JSON-native output, single mental model |
| A browser frontend / `fetch()` for a single read | `GET` | HTTP cache, ETag/304, dev-tools-friendly, shareable URLs |
| A read-heavy public endpoint behind a CDN | `GET` + `cache_control: 'public, max-age=N'` | Cloudflare/Fastly/Vercel cache it for free; Shape sees ~one DB hit per cache TTL |
| Mutations (insert/update/delete) | `POST` | Always — GET is for safe, idempotent reads only |
| A complex multi-query report you want cached | Write a single mask; GET it via `/_/<mask>` | One URL, one cache key, full control |
- **Manifest** — `?manifest` (machine-readable schema description)
- **OpenAPI spec** — `?openapi` (for human tooling and SDK generators)
- **LLM tool definitions** — `?manifest&format=anthropic-tools` or `openai-tools`
- **MCP server** — `/api.php?mcp` (HTTP) and `bin/shape-mcp` (stdio)
- **Auth** — primary: local users table + Shape-issued HS256 JWT (login + refresh rotation, role-per-mask authorization). Optional: validate external OIDC tokens (Entra ID, Auth0, Okta, Google, Cognito) via JWKS; the IdP's `oid` is resolved to a local `users.id` so audit surfaces use a single canonical actor identifier with clean FK to `users(id)`. Both paths coexist, dispatched by the inbound token's `alg`. Deep doc: [`../docs/shape/AUTH.md`](../docs/shape/AUTH.md).
- **REST / OData GET shortcuts** — `/api.php/books/42/reviews` (REST) or `/api.php/books(42)/reviews` (OData) for FK navigation

Zero entity files. Zero scaffolding. The schema is the only thing you author.

---

## 2. The minimum app

```php
<?php
// public/api.php — the only file you write
require __DIR__ . '/../bootstrap.php';
Shape\ShapeApp::quickStart(__DIR__ . '/../.env')->serve();
```

For local prototyping, an **empty `.env`** is enough — Shape provisions a SQLite file at `./.shape/data.sqlite` automatically. Apply a schema (`sqlite3 .shape/data.sqlite < schema.sql`) and the next request introspects it.

For production:

```ini
APP_NAME   = "myapp"
APP_ENV    = "prod"
DB_DSN     = "mysql:host=localhost;dbname=myapp;charset=utf8mb4"
DB_USER    = "appuser"
DB_PASS    = "secret"
JWT_SECRET = "<32+ random characters>"
JWT_TTL    = 3600
```

Boot: `php -S localhost:8000 -t public` (dev) or wire `public/api.php` into your existing PHP runtime.

---

## 3. Conventions you MUST know

These are framework conventions, not configurable defaults. Working with the grain saves enormous tokens.

| Convention | Behavior |
|---|---|
| `access` config: `['*' => ['admin', 'user']]` | Opens every introspected table to both roles |
| `access` config: `['books' => ['admin', 'user']]` | Opens only listed tables |
| `access` empty | Nothing introspected (safe default — production-ready posture) |
| Column `deleted_at TIMESTAMP NULL` | Soft-delete: reads auto-filter `IS NULL`; DELETE becomes UPDATE setting `CURRENT_TIMESTAMP`; restore via `update data: {deleted_at: null}` |
| Columns `created_at`, `created_by`, `updated_at`, `updated_by` | Audit columns: auto-stamped on insert (all four) and update (only `updated_*`). User id comes from `authContext.id` / `oid` / `sub`. Dev's explicit value in payload always wins. Don't include them in mutation `data` unless overriding intentionally. |
| Table `audit_log` (with the conventional schema) | Tier-1 audit trail: every successful insert/update/delete appends a row recording actor, table, row_id, action, and changed column names. Detected at boot. Joins the secret-table baseline automatically. Built-in admin masks: `audit_log_adminList`, `audit_log_byActor`, `audit_log_byTable`, `audit_log_byTarget`. Opt out with `'audit' => false`. |
| `relations` block on insert mutations | Bulk nested create: `{ table: 'orders', action: 'insert', data: {...}, relations: { items: [...] } }` walks the FK chain and inserts parent + children in one transaction. Framework auto-detects FK columns from the schema. Nests recursively (orders → items → item_options). Atomic by default. Each child inherits audit-columns / audit-log / role-checks. Disambiguate ambiguous FKs with `relations: { "<table>.<fk_column>": [...] }`. |
| `uploads` config namespace | File uploads via dedicated route: `POST /api.php/upload/<name>` with `multipart/form-data` and a single `file` field. Per-endpoint config sets `roles`, `max_size`, `mime_types`, `storage` (driver `local` ships v1; S3/GCS/R2 via `StorageInterface`). Returns `{id, filename, size, mime_type, url}`; mime is detected from file content via `finfo`, not the client header. Endpoints surface in `?manifest` under `uploads`. Chain a normal mutation to link the URL into a domain row. |
| Tables `users`, `refresh_tokens` | **Never** auto-CRUDed (secret-table baseline). Extend via `'secret_tables' => [...]` |
| `'admin_masks' => true` | Generates `<secret>.adminList` / `.adminGet` with sensitive columns (`password*`, `*_hash`, `*_secret`, etc.) stripped |
| `NOT NULL VARCHAR(N)` columns | Auto `notEmpty` + `maxLen:N` validators (override with explicit `validate` config) |
| FK columns | Auto-generate `<table>.by<FieldName>` filter mask |
| Mask roles | Empty role list = nobody can call. Authorization is per-mask |
| Auto `.list` / `by<FK>` masks ship a `defaultLimit` (default 100) | Framework wraps the mask SQL with `LIMIT N` at the SQL layer — `?$top` overrides; `?$skip` works the same way; `?$count=true` runs a separate `SELECT COUNT(*)` so `total_count` is unbounded. `?$orderby=…` falls back to in-memory and respects an explicit `?$top`. Hard ceiling: `MAX_TOP=200` per request. Override via top-level config `'default_list_limit' => N` (0 disables). |

---

## 4. Adding custom masks

When auto-CRUD isn't enough, write a mask in `app.php`:

```php
return [
    'masks' => [
        'reports.topAuthors' => [
            'sql' => 'SELECT a.name, COUNT(b.id) AS book_count
                      FROM authors a JOIN books b ON b.author_id = a.id
                      GROUP BY a.id ORDER BY book_count DESC LIMIT 10',
            'params' => [],
            'roles' => ['admin'],
            'description' => 'Top 10 authors by book count.',
        ],
    ],
];
```

**Always set `description`** — it flows into the manifest, OpenAPI summary, and LLM tool description. It's how an agent picks the right endpoint without reading the SQL.

**Optional `cache_control`** — emitted as the `Cache-Control` header on the GET surface (`/api.php/<entity>` and similar). Combined with the always-on ETag, this makes a Shape backend behind a CDN dramatically cheaper to run. Use `'public, max-age=60'` for shareable reads, `'private, max-age=10'` for per-user reads. Default: no header (no caching).

---

## 5. Adding commands

For business logic that doesn't fit a CRUD mask:

```php
return [
    'commands' => [
        'order.process' => function (array $params, CommandContext $ctx): array {
            $orderId = $ctx->batch->executeMutations([[
                'ref' => 'order',
                'table' => 'orders',
                'action' => 'insert',
                'data' => ['customer_id' => $params['customer_id']],
            ]], $ctx->authContext, true)['results'][0]['affected'];

            return ['order_id' => $orderId, 'status' => 'placed'];
        },
    ],
];
```

Commands appear as MCP tools, OpenAPI operations, and an `op: 'command'` POST endpoint.

---

## 6. MCP

Shape ships a built-in Model Context Protocol server. Two transports, same protocol class.

**Local (Claude Desktop, Cursor, Cline) — stdio:**
```json
{
  "mcpServers": {
    "myapp": {
      "command": "php",
      "args": ["vendor/shape/shape/bin/shape-mcp", ".env"]
    }
  }
}
```

**Hosted — streamable-HTTP:**
```json
{
  "mcpServers": {
    "myapp": {
      "url": "https://api.myapp.com/api.php?mcp",
      "headers": { "Authorization": "Bearer <jwt>" }
    }
  }
}
```

Tools come from the same `LlmToolGenerator` that powers `?manifest&format=anthropic-tools`. `tools/list` is **role-filtered** — agents never see tools their role can't call.

Resources exposed:

- `shape://README.md` — narrated, human-readable overview of THIS backend (attach in your agent client for instant context)
- `shape://manifest` — JSON manifest
- `shape://entities/{table}` — per-entity drill-down
- `shape://tools` — tool list as JSON data

**Highest-leverage tip for any agent using MCP**: attach `shape://README.md` to the session once. It replaces 5–10 exploratory tool calls and grounds every subsequent decision.

Deep docs: `../docs/shape/MCP.md`.

---

## 7. Errors are self-documenting — read them, don't re-fetch

Every actionable error returns:

```json
{
  "error": {
    "code": "PARAM_VALIDATION_ERROR",
    "message": "Missing required parameter 'id'.",
    "expected": { "id": "int (required)" },
    "example": { "id": 42 }
  }
}
```

**Read `expected` and `example`, correct your call, retry once.**
**Do NOT re-fetch the manifest after a parameter error.** The error already tells you the shape.

The same envelope flows through MCP as `isError: true` content. Same recovery pattern.

Instrumented error sites: missing required param, unknown param, type mismatch, missing `op` / `payload` / `queries` / `mutations`, missing `table` / `action` / `data` / `filter` on mutations, `MASK_NOT_ALLOWED`, `AUTH_FORBIDDEN`, `COMMAND_NOT_FOUND`.

---

## 8. Common mistakes (and how to avoid the retry loop)

- **Calling `users.list`** → `users` is a secret table; the mask doesn't exist. Use `users.adminList` (requires admin role) or write a custom mask with explicit safe columns.
- **Sending `delete` on a soft-deletable table and expecting the row gone** → it's tombstoned. Read the `softDelete` flag on the entity in the manifest. To restore: `update` with `data: {deleted_at: null}`.
- **Re-fetching the manifest after a parameter error** → don't. Read the `expected` / `example` fields in the error envelope.
- **Sending `params` as an array instead of an object** → `{queries: [{ref, sql, params: {...}}]}`. The `params` value is an object keyed by parameter name.
- **Forgetting `ref` on a query/mutation** → every batch entry needs a unique `ref` string. The error response uses `ref` to attribute failures.
- **Calling MCP `tools/call` without first sending `initialize`** → protocol violation. Always handshake first.
- **Assuming PostgreSQL booleans return `true`/`false` in JSON** → Shape coerces all booleans to `int 0/1` in result rows for cross-driver parity.
- **Looking for `password_hash` in `users.adminList`** → it's stripped by the safe-column filter. Filter out by name pattern (`password*`, `*_hash`, `*_secret`, `salt*`, `mfa_*`, `totp_*`).

---

## 9. Where to read for depth

| Topic | File |
|---|---|
| DB-first configuration, soft-delete, secret tables, validators | `../docs/shape/DB-FIRST.md` |
| Authentication (local users + JWT, optional OIDC for Entra/Auth0/etc.) | `../docs/shape/AUTH.md` |
| MCP server (transports, resources, auth flow) | `../docs/shape/MCP.md` |
| Manifest shape, error envelope, LLM tool format | `../docs/shape/MANIFEST-API.md` |
| Architectural case for the framework | `../docs/shape/WHY-SHAPE.md` |
| Implementation status (PHP + .NET) & roadmap | `../STATUS.md` |

This file is the canonical entry point. Read it first; reach into the deep docs only when you need detail. If this file disagrees with a deep doc, the deep doc is authoritative — but file an issue, because they should never disagree.

---

## 10. Quick-reference cheat sheet

```
Read row by id        → POST {op:'query', payload:{queries:[{ref,sql:'<table>.get',params:{id:N}}]}}
                        OR: GET /api.php/<table>/<id>           (REST)
                        OR: GET /api.php/<table>(<id>)          (OData)
                        OR: GET /api.php/_/<table>.get?id=N     (generic mask)
List rows             → POST {op:'query', payload:{queries:[{ref,sql:'<table>.list',params:{}}]}}
                        OR: GET /api.php/<table>                (REST/OData)
                        OR: GET /api.php/_/<table>.list         (generic mask)
Hand-written mask     → POST {op:'query', payload:{queries:[{ref,sql:'<mask.name>',params:{...}}]}}
                        OR: GET /api.php/_/<mask.name>?k=v      (URL params)
Filter by FK          → POST {op:'query', payload:{queries:[{ref,sql:'<table>.by<Field>',params:{<field>:N}}]}}
Insert                → POST {op:'mutation', payload:{mutations:[{ref,table,action:'insert',data:{...}}]}}
Update                → POST {op:'mutation', payload:{mutations:[{ref,table,action:'update',filter:{id:N},data:{...}}]}}
Delete (or soft-del)  → POST {op:'mutation', payload:{mutations:[{ref,table,action:'delete',filter:{id:N}}]}}
Discover the API      → GET  /api.php?manifest
Tools for agents      → GET  /api.php?manifest&format=anthropic-tools
MCP                   → POST /api.php?mcp  (or bin/shape-mcp for stdio)
Login                 → POST {op:'auth.token', payload:{email,password}}
Refresh token         → POST {op:'auth.refresh', payload:{refresh_token}}
Run a command         → POST {op:'command', payload:{name,params:{...}}}
Upload a file         → POST /api.php/upload/<name>  (multipart, single `file` field)
```
