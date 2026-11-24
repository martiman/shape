# Why Shape — A Backend Library Built for AI-Built Apps

## Executive Summary

Shape is a PHP backend framework engineered around one specific user: an **AI agent** (or a developer paired with one) producing a working, secure backend with the smallest possible amount of code, configuration, and retry-loop tokens.

Most frameworks ask you to write code, then add safety. Shape ships with safety as a structural property of the runtime, and asks you to write code only where convention can't decide for you. Every architectural choice — from masks to manifests, from `deleted_at` semantics to error envelopes — is in service of one measurable outcome:

> *Fewer tokens spent producing or operating a working backend.*

This document is the senior product case for choosing Shape when an AI agent is the primary author.

---

## 1. The Agent's Point of View

When an AI agent builds a backend, it spends tokens on five recurring line items:

1. **Reading framework docs** to discover what's available
2. **Writing repetitive boilerplate** — entity definitions, CRUD endpoints, validation, auth wiring
3. **Recovering from errors** that a clearer contract would have prevented
4. **Translating between formats** — schema → manifest → tool definitions → API calls
5. **Reasoning about security** that should already be enforced by the framework

A well-designed framework reduces #2; Shape reduces all five — by making the framework itself act as the agent's specification, security policy, and error coach.

---

## 2. Architectural Pillars

### Pillar 1 — SQL is the contract; masks are the gate

Every readable or writable surface in a Shape app is a named SQL **mask**. The runtime accepts no arbitrary SQL from clients. The agent declares what should be possible once, and that declaration *is* the security policy.

- Masks are vetted SQL templates with typed parameters
- Each mask has an explicit role list (`['admin', 'user']` or split read/write)
- The runtime resolves a mask name → SQL → params; nothing else reaches the database

**Result:** the agent cannot accidentally expose SQL injection or arbitrary querying. Authorization is a property of the mask, not a middleware to remember.

### Pillar 2 — Database-first by default

Shape introspects the live schema and generates the entire CRUD surface. No entity classes. No ORM annotations. No migration framework to manage in addition to the schema you already own.

- For a 2000-table legacy DB exposing 3 tables, the agent writes 3 lines of `access` config
- For a fresh project, the agent writes a SQL schema and Shape produces every endpoint
- For an entity-first project, PHP classes with `#[Entity]` / `#[Column]` attributes work just as well

The schema is the source of truth. The agent doesn't have to keep two definitions in sync.

### Pillar 3 — Convention over configuration; configuration over code

Shape encodes the choices a sensible backend would make:

| Convention | Behavior |
|---|---|
| Tables `users`, `refresh_tokens` | Hardcoded as secret — never auto-CRUDed, even if listed in `access` |
| Column `deleted_at` (nullable timestamp) | Soft-delete: reads filter, deletes UPDATE, restore by setting `null` |
| `NOT NULL` string column without default | Auto `notEmpty` validator |
| `VARCHAR(N)` / `CHAR(N)` | Auto `maxLen:N` validator |
| FK column | `<table>.byFieldId` filter mask generated automatically |
| `'access' => ['*' => [...]]` | Open every introspected table with the same roles |
| `'admin_masks' => true` | Built-in safe-column read masks for secret tables |

Each one is a line of code the agent doesn't write. The agent overrides only when its app deliberately diverges.

### Pillar 4 — Self-describing at every layer

A Shape app describes itself in formats the agent natively consumes:

- **Manifest endpoint** — every entity, field, mask, role, soft-delete column, with plain-English descriptions
- **LLM tool-call format** — `?format=anthropic-tools` or `openai-tools` returns ready-to-paste tool definitions; no client-side translation step
- **OpenAPI** — auto-generated for human/SDK tooling
- **Self-documenting errors** — every actionable error returns `expected`, `example`, `hint`, `ref`; the agent corrects the call without re-fetching the manifest

The agent doesn't have to write a TypeScript SDK or a Postman collection. The framework *is* the SDK.

### Pillar 5 — Driver-portable

MySQL, PostgreSQL, and SQLite share the same code path:

- DSN-based driver detection
- Identifier quoting per driver (backticks vs double-quotes)
- Insert-id strategy per driver (`lastInsertId` vs `RETURNING`)
- Schema introspection per driver (`INFORMATION_SCHEMA` / `pg_index` / `PRAGMA`)
- Type normalization (`integer` → `int`, `numeric` → `decimal`, `boolean` → `tinyint`)
- Boolean coercion in result rows (PG native bool → integer 0/1 for JSON parity)

The agent's `app.php` works against all three drivers without dialect switches.

---

## 3. Token Economics

| Concern | Without Shape | With Shape |
|---|---|---|
| Define a table's API | Write entity class + repository + controller + validation | Add table name to `access` config |
| Soft-delete an entity | Hand-roll WHERE clauses, DELETE-as-UPDATE, restore endpoint | Add `deleted_at TIMESTAMP NULL` |
| Validate inputs | Per-column rules in code | Inferred from `NOT NULL` / `VARCHAR(N)` |
| Document the API for an LLM | Hand-write tool schemas | `GET /api.php?manifest&format=anthropic-tools` |
| Recover from a wrong call | Read docs, re-read schema, retry | `expected` + `example` in the error response |
| Switch from MySQL to PG | Rewrite dialect-specific SQL | Change DSN |
| Hide sensitive columns from admins | Custom view + endpoint + role check | `'admin_masks' => true` |
| Open everything for prototyping | Generate boilerplate per table | `'access' => ['*' => ['admin', 'user']]` |

Every cell on the right is the agent typing fewer characters, asking fewer follow-up questions, and producing fewer broken calls.

---

## 4. Security Model

Shape's security comes from **structure**, not vigilance:

| Threat | Shape's structural defense |
|---|---|
| SQL injection | No client-supplied SQL — only mask names |
| Privilege escalation | Per-mask role enforcement; empty role list means denied, not allowed |
| Sensitive column leak | Hardcoded `secret_tables` baseline + safe-column denylist on admin masks (`password*`, `*_hash`, `*_secret`, `salt*`, `mfa_*`, `totp_*`) |
| Soft-deleted row exposure | Reads auto-filter `deleted_at IS NULL`; admin sees explicitly via admin masks |
| Mass-assignment on insert | Only declared columns flow through validators; auto-increment columns excluded |
| Refresh-token replay | Rotation chain with `replaced_by_token_id` and `revoked_at` audit |
| Stale auth metadata | JWT issuer + verifier in the runtime; admin operations gated by `AdminAuth` |

The agent can produce a misconfigured Shape app, but it cannot easily produce one that exposes raw SQL, leaks `password_hash`, or accidentally opens write access to `users`. The dangerous choices are not in the choice space.

---

## 5. Productivity Surface (Beyond AI-Token Economics)

Shape ships with the production-grade primitives a real backend needs:

- **Batch operations** — multiple queries in one round-trip
- **Param refs** — use the result of one query as input to the next within the same batch
- **Transactions** — mutation batches can wrap in a single DB transaction
- **Hooks** — before/after callbacks per `<table>.<action>`
- **Commands** — PHP closures for business logic that doesn't fit a CRUD mask, with a `CommandContext` that exposes the executor for nested mutations
- **OData-style GET** — `/api.php/books?$filter=price gt 10&$orderby=price desc&$top=10&$select=id,title` for human-friendly URLs
- **Cache layer** — entity metadata cached on disk, mtime-invalidated, ~19× boot speedup on large schemas
- **OpenAPI generation** — for human tooling and existing SDK generators
- **Manifest auth** — optional role gate on the manifest endpoint itself

These aren't experimental. They're how production apps using Shape ship today.

---

## 6. Comparison

| Concern | **Shape** | Laravel/Symfony | Hasura/PostgREST | Prisma/Drizzle |
|---|---|---|---|---|
| Setup for first endpoint | DSN + 6-line entry | Scaffold + controller + route | Container + JWT config | Schema + codegen + import |
| Build step | None | Composer + cache | None (it's a service) | TypeScript codegen |
| Multi-DB support | MySQL / PG / SQLite | All majors | PG, MS-SQL, BigQuery | Most via adapters |
| Manifest for AI consumers | Built-in + LLM tool format | Hand-rolled | GraphQL schema (translate) | Hand-rolled |
| Soft-delete | Convention (column name) | Trait or package | Hand-rolled view | Middleware/extension |
| Self-documenting errors | `expected` + `example` + `hint` | Stack traces / ad-hoc | GraphQL errors | Throws |
| Custom business logic | First-class commands | Yes (controllers) | Limited (computed fields, actions) | Yes (in app code) |
| Authorization granularity | Per-mask role list | Middleware/policies | Per-table permissions | App-side |
| AI-built backend | Designed for it | Possible, lots of boilerplate | Limited custom logic | Possible, build step adds latency |
| File count for minimal app | 4 (`app.php`, `env.php`, `api.php`, `bootstrap.php`) | 20+ scaffolded | Container + config | Schema + generated client + handlers |

Shape is not trying to be the most flexible framework, the most popular framework, or the most enterprise framework. It's trying to be the **framework an AI picks** because it produces a working, secure, self-describing backend with the fewest tokens spent.

---

## 7. Roadmap & Trajectory

Already shipped (recent work):

- DB-first introspection with cache, wildcard `access`, secret tables, admin masks
- Driver portability (MySQL / PG / SQLite) with insert-id and boolean parity
- Schema-driven validators (`notEmpty`, `maxLen:N` derived from columns)
- LLM-tool-format manifest (Anthropic + OpenAI shapes)
- Self-documenting error envelopes with `expected` / `example` / `hint`
- Soft-delete by convention (`deleted_at` column triggers full lifecycle)
- Plain-English descriptions on every mask, surfaced in manifest, OpenAPI, and tool format

Next on the roadmap:

- **One-line quick start** — `ShapeApp::quickStart('.env')->serve()` collapses the four-file minimum to one line + 3 env vars
- **Auto access by convention** — tables with a `user_id` FK auto-scope to the authenticated user
- **DDL-from-manifest** — declarative schema (feed JSON, get `CREATE TABLE`)
- **CSV seeding, audit log, idempotency keys** — production primitives the agent shouldn't have to build

Every roadmap item is judged against one question: *does it remove tokens an AI agent would otherwise spend?*

---

## 8. The Bottom Line

The frameworks that came before Shape were designed for human developers — to maximize their flexibility, ergonomics, and code-organization preferences. Shape is designed for the next generation of authors: AI agents that need a framework which is its own specification, its own SDK, its own security policy, and its own error coach.

If you're shipping a backend by talking to an LLM, Shape is the library that makes the LLM successful on the first try.
