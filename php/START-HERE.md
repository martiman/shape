# Shape — the framework your AI was waiting for

> ### The framework your AI was waiting for.
> *Schema-driven. Three lines of PHP. Production-grade by the second request.*

The schema is the spec. The manifest is the SDK. AGENTS.md is your AI agent's onboarding doc. Drop one file into your project; deploy a production-grade API on the next request.

---

## The three things that matter

### 1. One file is the whole backend

```php
<?php
require __DIR__ . '/../vendor/autoload.php';
Shape\ShapeApp::quickStart(__DIR__ . '/../.env')->serve();
```

That's it. No entity classes. No controllers. No DTO layer. Point Shape at MySQL, PostgreSQL, or SQLite and it auto-generates the JSON / OData / REST / OpenAPI / MCP / LLM-tool surface from your tables — including auth, audit log, soft-delete, ETag/Cache-Control, and pagination.

If you don't even have a database, an empty `.env` boots Shape with an auto-provisioned SQLite file. Apply your schema; the next request is live.

### 2. Your AI gets it right on the first try

Drop [`AGENTS.md`](./AGENTS.md) into Claude / ChatGPT / Cursor / Codex once. ~5k tokens of framework conventions in — your AI's defaults are now Shape-shaped. Errors are self-documenting (every error returns `expected` / `example` / `hint`); the manifest is one fetch away; the MCP server is one config block away.

This is the only PHP backend framework where "have an AI build the API" is the supported path, not a hack.

### 3. Production posture, by default — not behind a paywall

| You don't write | Shape ships |
|---|---|
| Routes | Auto-generated from schema |
| Auth | JWT (HS256) + OIDC (RS256, Entra/Auth0/Okta/Google/Cognito) — JWKS validation included |
| Audit log | Detected by table convention; appended atomically to every mutation |
| Soft-delete | Convention: a `deleted_at TIMESTAMP NULL` column |
| Audit columns | Convention: `created_by` / `updated_by` / `created_at` / `updated_at` auto-stamped |
| Pagination | `?$top` / `?$skip` push down to SQL; bounded memory regardless of table size |
| Caching | ETag + per-mask `Cache-Control` so a CDN handles the read traffic |
| MCP server | `bin/shape-mcp` ships in the box; both stdio and streamable-HTTP |
| Manifest / SDK / OpenAPI | Always derived from current state; never out of date |

---

## Not just PHP — Shape is a pattern, not a single stack

Shape is a *backend architecture* — schema as spec, manifest as SDK, masks as the gate, AGENTS.md as the AI onboarding. The PHP package is the reference implementation; the same shape is portable to other ecosystems.

| Stack | Status |
|---|---|
| **PHP 8.4** | Reference implementation. Production-ready. |
| **.NET** | Shipped — same conventions, same manifest format. |
| **TypeScript / Node** | On the roadmap. |
| **Python** | On the roadmap. |
| **Go** | On the roadmap. |

A team that picks Shape today on PHP isn't betting on a language — they're picking an architecture that follows them across stacks as the team grows.

---

## Who this is for

- **The developer who has to ship.** Less code, less debugging, less time.
- **The CTO / stakeholder.** Backend MVPs in days; compliance-grade audit trail by default; one entry point makes scope-creep easy to govern.
- **The AI agent doing the work.** Token-frugal manifest, role-filtered MCP tools, errors that tell the agent how to fix the call.

---

## Start in 90 seconds

```bash
composer require shape/shape
echo "" > .env                          # auto-SQLite kicks in
cat > public/api.php <<'PHP'
<?php
require __DIR__ . '/../vendor/autoload.php';
Shape\ShapeApp::quickStart(__DIR__ . '/../.env')->serve();
PHP
php -S localhost:8000 -t public
```

Visit `http://localhost:8000/api.php?manifest`.

Or paste [`AGENTS.md`](./AGENTS.md) into your AI and ask it to build the rest.

---

📖 Deep docs: `../docs/shape/*.md`
🛣️ Status & roadmap: [`STATUS.md`](../STATUS.md)
🏗️ Architecture: [`../docs/shape/WHY-SHAPE.md`](../docs/shape/WHY-SHAPE.md)
