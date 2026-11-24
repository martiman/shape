# Multi-tenancy (tenant-scoped users & data) — implementation plan

> **Status (2026-07-06): PHP core implemented** (Phase A + auth claim/suspension +
> registry auto-exposure + manifest). Roadmap item **1** in [STATUS.md](../../STATUS.md)
> ("Auto access by convention / multi-tenant scoping", items 7 + B) — this document is the
> common design both implementations follow. Companion to
> [MANUAL-PK-PLAN.md](MANUAL-PK-PLAN.md) in structure and conventions.
>
> **PHP progress** (per-item state in [php/MULTI-TENANCY-CHECKLIST.md](../../php/MULTI-TENANCY-CHECKLIST.md)):
> the read/write isolation core is done and SQLite-verified — config normalization +
> schema detection + strict-mode build guards (T1), scoped/unscoped mask compilation (T2.1),
> runtime read scoping + write stamping/guarding + reserved-`__`-param hygiene (T3), the
> `tenant_id` JWT claim + `TENANT_SUSPENDED` on login/refresh (T4.1), registry
> auto-exposure gated to `unscoped_roles` (T5.1), and manifest `tenantScoped`/`tenant_scope`
> rendering (T5.2). Everything previously deferred (`tenant.provision`, OIDC tenant resolution, admin-handler guards, opt-in FK validation, manual-mask contract, OpenAPI/MCP/`Vary`) has since landed on both stacks as S1–S12 of the simple-tier plan.
>
> **Tier plans:** the remaining/refactor work for this design (global roles, one tenant
> per user) is tracked in [MULTI-TENANCY-SIMPLE-PLAN.md](MULTI-TENANCY-SIMPLE-PLAN.md)
> (includes a per-stack implementation-state audit — the .NET stack has since landed);
> the §2.12 deferred tier (multi-tenant membership, tenant-defined roles, field-level
> access) is designed in [MULTI-TENANCY-FULL-PLAN.md](MULTI-TENANCY-FULL-PLAN.md).

Applies to both stacks: **PHP** (`php/packages/shape` + `php/packages/shaperuntime`) and
**.NET** (`dotnet/src/Shape`). PHP is the reference; the .NET port follows with
byte-identical manifest keys.

## 1. Problem

Shape backends today are single-tenant. Roles gate *which masks* a user may call, but every
authenticated user sees **every row** of every exposed table. A B2B SaaS deployment needs:

1. A **tenant registry** — a `tenants` (or `organizations`, name is the app's choice) table
   holding each customer organization.
2. **Users belong to a tenant** — the same backend serves many organizations, each with its
   own user accounts.
3. **Business data is tenant-isolated** — every query, mutation, OData filter, COUNT,
   nested create, and admin mask is automatically constrained to the caller's tenant.
   No app code, no per-mask boilerplate — isolation by convention, in the framework.
4. A **tenant administration surface** — a superadmin manages tenants (create, suspend,
   provision the first admin user); a tenant admin manages users *within their own tenant*.

The token-frugality test: today an AI building a SaaS on Shape must hand-write a
`WHERE tenant_id = :tid` into every mask, remember to stamp `tenant_id` on every insert,
and guard every UPDATE/DELETE — one forgotten predicate is a cross-tenant data leak.
This feature removes all of that code *and* the entire class of leak bugs.

## 2. Design

### 2.1 Tenancy model: shared schema, `tenant_id` discriminator (pooled)

One database, one schema, every tenant-owned table carries a `tenant_id` column; the
framework injects the predicate and stamps the column. This is the industry-default
"pooled" model (the same one AWS SaaS Factory, Azure SaaS patterns, and every major
multi-tenant framework treat as the starting tier) and the only one that fits Shape's
architecture — the live schema *is* the API definition, and the introspection bundle
cache is per-database, not per-tenant.

Rejected alternatives (documented so nobody relitigates):

| Model | Why not (now) |
|---|---|
| **Database-per-tenant** | Strongest isolation, but breaks Shape's core loop: introspection cache, mask compilation, and the manifest would fan out per tenant; `bin/shape migrate` (itself still 📋) would need N-database orchestration. Deferred tier — the design keeps a seam for it (§2.12): everything downstream of "resolve connection for this request" is tenant-agnostic. |
| **Schema-per-tenant** (PG schemas / MySQL databases) | Same ops fan-out, and driver-uneven — SQLite has no schemas at all, so the CI-runnable test story dies. |
| **DB-native Row-Level Security** (PG `CREATE POLICY`) | PostgreSQL-only; Shape targets MySQL / PG / SQLite / MSSQL. Framework-level predicate injection is portable across all four. PG RLS remains available to an app as *defense-in-depth* underneath Shape — document, don't build. |

### 2.2 Conventions & configuration

New top-level config key, same shape in both stacks (snake_case in PHP config and
manifest JSON, PascalCase properties in .NET):

```php
// PHP shape config
'tenancy' => true,                       // convention defaults, or:
'tenancy' => [
    'table'          => 'tenants',       // registry table name ('organizations', …)
    'column'         => 'tenant_id',     // discriminator column ('organization_id', …)
    'unscoped_roles' => ['superadmin'],  // roles that see across tenants (default: none)
    'enforce'        => 'strict',        // 'strict' (default) | 'relaxed'
    'global_tables'  => [],              // shared lookup tables exempt from strict check
    'check_active_per_request' => false, // suspension lag = JWT TTL unless true
],
```

```csharp
// .NET ShapeConfig
public TenancyConfig? Tenancy { get; set; }   // Table, Column, UnscopedRoles, Enforce, GlobalTables
```

Conventions (all detected during the existing introspection pass, exactly like
`deleted_at` / `created_at` detection today):

- A table is **tenant-scoped** iff it has the tenancy column. No column → **global table**
  (readable/writable per `access` config, unscoped).
- The **registry table** (`tenants`) is itself global. Minimal expected schema:
  `id` PK, `name`; recognized-by-convention extras: `is_active` (suspension),
  `deleted_at` (soft delete — existing convention applies), audit columns.
- The **`users` table must carry the tenancy column** when `tenancy` is enabled —
  build-time `CONFIG_INVALID` otherwise. Fail loud, never guess.
- **`enforce: 'strict'`** (default): build-time `CONFIG_INVALID` listing every table in
  `access` that has neither the tenancy column nor a `global_tables` entry. This is the
  guard rail that turns "forgot to add tenant_id to the new table" from a silent
  cross-tenant leak into a boot failure. `'relaxed'` downgrades it to a manifest warning
  for brownfield schemas mid-migration.
- A **nullable** tenancy column earns a strict-mode error too (a `NULL` row is invisible
  to every scoped caller — almost always a backfill bug, see §7).

### 2.3 Auth: the tenant rides in the token

The tenant is a **server-issued claim**, never a request parameter — a client can no more
choose its tenant than it can choose its role.

- **Login (`auth.token`)**: `LoginService` already loads the user row; it now also reads
  `users.tenant_id`, joins the registry row, rejects when the tenant is suspended
  (`is_active = 0` / soft-deleted) with new error `TENANT_SUSPENDED` (403), and the issued
  JWT carries `tenant_id` alongside `sub` / `email` / `role`.
- **Refresh (`auth.refresh`)**: rotation re-reads the user row (it already does, for
  revocation) — re-validate tenant active there too, so suspension propagates at most one
  access-token TTL later. `check_active_per_request: true` adds a per-request registry
  lookup for deployments where suspension must be immediate (costs one indexed SELECT per
  request — off by default, documented).
- **OIDC**: the tenant comes from the **local user row** after the existing `oid →
  users.id` resolution — the IdP token never decides the tenant. Auto-provision needs a
  tenant to stamp: new keys `oidc.tenant_claim` (map an IdP claim → registry PK, for IdPs
  that carry org context) and `oidc.auto_provision_tenant` (fixed default). Neither
  configured + auto-provision on → build-time `CONFIG_INVALID`. Email-domain → tenant
  mapping is deferred (§2.12).
- **Auth context** gains `tenant_id` (int|null). Unscoped roles (`superadmin`) carry
  `tenant_id = null`.
- **Backward compatibility**: pre-tenancy refresh chains / long-lived tokens without the
  claim are rejected with `AUTH_INVALID` + hint "re-login required after tenancy was
  enabled" — never fall through to unscoped.

### 2.4 Read path: predicate compiled at build time, bound at run time

The scoping predicate is **baked into the generated SQL by the mask generator** — no
runtime SQL parsing or string surgery, mirroring how soft-delete already bakes
`deleted_at IS NULL` into read masks. New reserved parameter `__tenant`:

```sql
-- books.list on a scoped table compiles to:
SELECT id, tenant_id, title, author_id FROM books
WHERE tenant_id = :__tenant AND deleted_at IS NULL
```

- Every generated read mask on a scoped table gets the predicate: `list`, `get`,
  `getMany`, `by<FK>`, the OData GET base query, **and the separate `COUNT(*)` query**
  (a wrong `$count` is also a leak). The generic mask GET (`/_/<mask>`) and the OData
  surface execute the same compiled masks, so they inherit it for free.
- The mask carries the contract keys (cross-runtime, snake_case — same pattern as
  `pk` / `pk_strategy`):

```jsonc
"books.list": {
  "tenant_scope": "tenant_id",     // column name; absent = unscoped mask
  "sql": "… WHERE tenant_id = :__tenant …",
  "sql_unscoped": "… (no predicate) …"   // emitted only when unscoped_roles configured
}
```

- **Run time**: when the executing mask has `tenant_scope`, the runtime binds `__tenant`
  from the auth context; a scoped caller executes `sql`, an unscoped-role caller executes
  `sql_unscoped`. Two compiled strings beat one `(:__tenant IS NULL OR tenant_id =
  :__tenant)` predicate — that OR-shape defeats index usage on every driver we target.
- **Reserved-param hygiene**: any client-supplied parameter starting with `__` is rejected
  at param validation (`PARAM_VALIDATION_ERROR`) — today unknown params are already
  rejected, this closes the "mask declares it, so the client may send it" hole by making
  the prefix reserved framework-wide.
- **Manual / raw SQL masks** can't be parsed reliably, so they get a declaration contract:
  - `'tenant_scope' => 'tenant_id'` — author put `:__tenant` in the SQL themselves;
    build-time error if the placeholder is missing from the SQL text.
  - `'tenant_scope' => false` — deliberate opt-out (cross-tenant reports). In strict mode
    the mask's `roles` must then be a subset of `unscoped_roles` — an opted-out mask
    reachable by a tenant role is a leak by construction, so it's a build error.
  - **No declaration** + strict mode + the SQL text references a scoped table name →
    build-time `CONFIG_INVALID`. (Table-name scan over the SQL is a documented
    best-effort heuristic — it can false-positive on strings; the fix is always "declare
    intent explicitly", which is the point.)

### 2.5 Write path: stamp on INSERT, guard on UPDATE / DELETE

Exactly the audit-columns pattern (`created_by` stamping is the in-tree analog):

- **INSERT**: `tenant_id` auto-stamped from the auth context. Client-supplied value that
  differs → `VALIDATION_ERROR` with typed detail `TENANT_MISMATCH` (matching value passes —
  idempotent clients re-send what they read). Unscoped callers may supply any tenant
  (that's how a superadmin seeds a new tenant's data); an unscoped caller supplying none →
  `VALIDATION_ERROR` ("tenant_id required for unscoped writes" — never guess).
- **Nested create (`relations` FK-chain walk)**: children insert through the same
  `performInsert`, so every child row is stamped for free — but tests must prove it,
  since a nested leak is the sneakiest variant.
- **UPDATE / DELETE**: the compiled writable path appends `AND tenant_id = :__tenant` to
  the WHERE — same mechanism as the soft-delete `deleted_at IS NULL` guard. A cross-tenant
  id yields affected-rows 0, indistinguishable from "row doesn't exist" — no existence
  oracle across tenants (deliberate; document it).
- **Soft delete** (DELETE → UPDATE rewrite) gets the same guard on its WHERE.
- **AutoValidators**: the tenancy column is treated like an audit column — never required,
  stripped from writable-field validators, not listed as a writable param.
- **Cross-tenant FK escape** (insert an `order` pointing at another tenant's
  `customer_id`): v1 ships an **opt-in** `tenancy.validate_fks: true` — for each FK column
  whose target table is scoped, one indexed `SELECT 1 … WHERE pk = :v AND tenant_id =
  :__tenant` inside the same transaction, `VALIDATION_ERROR` (`TENANT_FK_MISMATCH`) on
  miss. Off by default (per-FK SELECT cost); the schema-level alternative — composite FKs
  `(tenant_id, id)` — is documented as the zero-cost belt-and-braces option. Residual risk
  listed in §7 honestly rather than hidden.

### 2.6 Users per tenant; tenant-admin boundaries

- `users.tenant_id` is required (§2.2). **Email stays globally unique in v1** — local
  login is by email alone, so per-tenant emails would need a tenant hint at login
  (subdomain / slug); that's the deferred "same email in two tenants" tier (§2.12).
- **`auth.admin.create-user`**: a scoped `admin` creates users **only in their own
  tenant** — the handler stamps `tenant_id` from the auth context and hard-rejects a
  differing payload value. An unscoped caller must supply `tenant_id` explicitly.
  **Privilege-escalation guard**: a scoped admin cannot assign any role in
  `unscoped_roles` — otherwise tenant admin → superadmin is one API call. Build the check
  into the handler, not the docs.
- **`auth.admin.change-password`**: the target-user lookup gains `AND tenant_id = :ctx`
  for scoped admins.
- **Admin masks** (`users.adminList` / `adminGet`, `refresh_tokens.*`, `audit_log.*`):
  compiled with the same dual-variant scoping — a tenant admin audits their tenant, a
  superadmin audits everything.
- **`audit_log`**: when the table carries the tenancy column it is stamped on write
  (rides on the existing audit path) and its admin masks scope like everything else.

### 2.7 Tenant management surface (the "tenants page")

Shape is headless — its "pages" are the manifest / OpenAPI / MCP surfaces plus whatever
frontend the app builds on them. The feature therefore ships as a complete, superadmin-gated
API, and the framework's own generators make it one-prompt work for an agent to build the
actual page:

- **Registry CRUD**: when `tenancy` is enabled, the registry table is auto-exposed with
  the standard generated masks (`tenants.list/get/getMany` + writable) **gated to
  `unscoped_roles`** — even under `quickStart()` with no `access` config. An explicit
  `access['tenants']` entry overrides, as always (manual wins).
- **Built-in command `tenant.provision`** — the canonical "new customer" flow, atomic in
  one transaction: insert registry row + insert the tenant's first `admin` user
  (email/password or `oid`). Returns `{tenant_id, user_id}`. Gated to `unscoped_roles`.
  This is the most-reinvented piece of every SaaS backend; ~40 LOC in the framework.
- **Suspension**: `is_active = false` (or soft delete) on the registry row blocks logins
  and refreshes immediately, in-flight access tokens per §2.3 timing. New error
  `TENANT_SUSPENDED` carries the standard self-documenting envelope.
- **Example app**: each stack's in-repo example gains a minimal tenants admin page
  (list / create / suspend / provision-admin) driven purely by the manifest — it doubles
  as the end-to-end demo that the requirement "a special page with tenants" is met and as
  living documentation for app authors.

### 2.8 Manifest / OpenAPI / LLM tools / MCP

- **Manifest**: entities gain `"tenantScoped": true`; the tenancy column renders like an
  audit column (auto-stamped, read-only — the existing `isGenerated`-style unification);
  masks carry `tenant_scope` as in §2.4. Keys byte-identical across stacks.
- **OpenAPI**: the tenancy column disappears from writable request schemas; scoped-mask
  descriptions get one generated sentence — *"Rows are automatically limited to the
  caller's tenant."* — so an agent never wastes tokens re-filtering client-side.
- **LLM tools / MCP**: `tools/list` is already role-filtered per request; registry CRUD
  and other unscoped-role-only masks are simply invisible to tenant users. No new
  mechanism needed — parity test required.

### 2.9 HTTP caching interplay (the classic multi-tenant leak)

Same URL, different tenants, one shared proxy cache = leak. Two rules:

- `cache_control` containing `public` on a tenant-scoped mask → build-time
  `CONFIG_INVALID`. (`private, max-age=…` stays fine — browser caches are per-user.)
- Scoped GET responses always emit `Vary: Authorization`, so even `private` intermediaries
  key correctly. ETags already hash the response body, so per-tenant ETags differ
  naturally — `If-None-Match` across tenants can never 304.

The schema-level introspection bundle cache is tenant-agnostic and unchanged.

### 2.10 Error codes (new)

| Code | HTTP | When |
|---|---|---|
| `TENANT_SUSPENDED` | 403 | Login/refresh (or per-request check) against an inactive/deleted tenant |
| `VALIDATION_ERROR` detail `TENANT_MISMATCH` | 400 | Client-supplied discriminator differs from the caller's tenant |
| `VALIDATION_ERROR` detail `TENANT_FK_MISMATCH` | 400 | Opt-in FK check found a cross-tenant reference |
| `CONFIG_INVALID` (build time) | — | users table missing column; nullable column (strict); undeclared manual mask on scoped table (strict); opted-out mask reachable by scoped role; `public` cache on scoped mask; auto-provision without tenant source |

All carry the standard `expected` / `example` / `hint` envelope.

### 2.11 Same machinery, second convention: `user_id` auto-scoping (roadmap item 7)

The predicate-compilation + stamp-and-guard machinery this plan builds is generic
"scope-column" machinery. Roadmap **item 7** (rows with a `user_id` FK auto-scope to the
authenticated user) is the identical mechanism with column = `user_id` and value =
`authContext.id` instead of `tenant_id` / `authContext.tenant_id` — one detection pass,
one generator path, one executor path, two conventions. Implementers should parameterize
by (column, context-key) from the start; STATUS.md's item 7 row then closes nearly for
free, and the two scopes compose (predicates AND together when a table carries both).

### 2.12 What stays out of v1 (deferred tier)

- **Multi-tenant membership** (one user in N tenants via a `user_tenants` join table +
  tenant switching endpoint) — v1: a user belongs to exactly one tenant.
- **Per-tenant unique emails** (needs a tenant hint at login — subdomain / slug routing).
- **Email-domain → tenant OIDC mapping.**
- **Connection-per-tenant / database-per-tenant** — the §2.1 seam: a future
  `tenancy.dsn_resolver` hook resolving the connection after auth; everything downstream
  is already tenant-agnostic.
- **Per-tenant rate limiting** — rides on roadmap **G** when it lands; note the tenant is
  then the natural bucket key alongside role.
- **Idempotency keys** (roadmap) must include the tenant in their uniqueness scope when
  both features exist — recorded here so neither plan forgets.

### 2.13 Migration path for existing single-tenant apps

Documented recipe (no tooling in v1; `bin/shape migrate` is itself still planned):

1. `CREATE TABLE tenants (…)`; insert tenant #1.
2. `ALTER TABLE <each> ADD COLUMN tenant_id INT NOT NULL DEFAULT 1 REFERENCES tenants(id)`
   (+ index — see §7 on indexes), backfill, then drop the DEFAULT.
3. Add `'tenancy' => true` to config; boot; fix every `CONFIG_INVALID` the strict mode
   reports (that list *is* the migration checklist).
4. Re-issue tokens (old tokens lack the claim and are rejected — §2.3).

## 3. Runtime flow (both stacks)

```
request → JwtGuard/OidcVerifier → authContext{id, email, role, tenant_id}
  → mask lookup → role check (existing)
  → if mask.tenant_scope:
        caller unscoped-role? → execute mask.sql_unscoped
        else                  → bind __tenant := authContext.tenant_id → execute mask.sql
  → params: any client param with '__' prefix rejected

mutation → per §2.5:
  insert  → stamp tenant_id (mismatch → VALIDATION_ERROR); relations children inherit
  update/delete/soft-delete → WHERE … AND tenant_id = :__tenant (scoped callers)
  audit_log row → stamped when column present
```

## 4. PHP work items

In-tree anchors for the two central mechanisms: the soft-delete predicate is compiled
into generated mask SQL at `Generator/MaskGenerator.php:57–108` (`$softWhere`/`$softAnd`)
and flagged on the writable mask at `:153` — the tenancy predicate and `tenant_scope` key
follow that exact shape. Audit-column stamping runs at
`Core/BatchExecutor.php:240` (`applyAuditColumns`, convention map carried on the mask,
"dev's explicit value wins" — tenant stamping reuses the mechanism with the *mismatch*
rule from §2.5 instead of dev-wins), and `performUpdate`/`performDelete` already receive
the mask (`:254`/`:258`) for the WHERE guard.

*(Implementers: `php/.deploy/**` is a deployed copy — edit `php/packages/**` only.)*

- [ ] **T1.1** `ShapeApp::loadConfig` — normalize `tenancy` (bool shorthand → full array),
  validate keys/modes; `CONFIG_INVALID` on bad values. Model:
  `normalizePkAssignment()` (`ShapeApp.php:1195`) + `resolvePkStrategies()` (`:1252`).
- [ ] **T1.2** Introspection: detect the tenancy column per table alongside the existing
  conventions — `EntityMeta::detectSoftDeleteColumn()` (`Metadata/EntityMeta.php:77`) /
  `detectAuditColumns()` (`:111`), invoked from `DatabaseIntrospector::introspect()`
  (`:125–126`). Users-table column check slots where `password_hash` / lookup-column
  detection already runs (`ShapeApp::buildDatabaseConfig`, `:293–311`); registry-table
  presence check mirrors the `audit_log` presence check (`:264–273`); strict-mode sweep
  (§2.2) at build time; everything into the cached introspection bundle (`:315–323`).
- [ ] **T2.1** `MaskGenerator` — compile predicate into all generated read masks + COUNT;
  emit `tenant_scope` + `sql_unscoped` (§2.4); writable mask carries `tenant_scope` for
  the executor. `AdminMaskGenerator` same treatment.
- [ ] **T2.2** Manual-mask declaration contract + build-time checks (placeholder presence,
  opt-out role subset, strict-mode table-name scan, `public` cache check §2.9).
- [ ] **T3.1** `BatchExecutor` — implement the INSERT stamp as a new column-kind in
  `applyAuditColumns()` (`:683`; kinds `timestamp`/`user_id` at `:704–710` — the tenant
  kind reads `authContext['tenant_id']`, with the §2.5 *mismatch* rule instead of
  dev-wins); UPDATE/DELETE/soft-delete guard AND-ed in `buildWhereClause()` (`:630`,
  soft-delete rewrite `:613–620`); `relations` children recurse through
  `executeSingleMutation` via `walkRelations()` (`:321`) so the stamp propagates — test
  it anyway; opt-in FK validation (§2.5); reject `__`-prefixed client params.
- [ ] **T3.2** Query execution — variant selection + `__tenant` binding from auth
  context. Good news baked into the current architecture: the OData GET surface
  (`Shape/ShapeRouter.php:356–452`) executes the *compiled* masks, and the separate
  `$count` query wraps `mask['sql']` in a COUNT subquery
  (`Core/BatchExecutor.php:793`; .NET `BatchExecutor.cs:887`) — so once the predicate is
  compiled into the mask and the right variant is chosen, OData filtering, pagination
  *and* counts inherit the scope automatically. The one rule: COUNT must wrap the same
  variant the caller executes. Every read (POST-query, OData GET, generic `/_/` mask,
  MCP) funnels through `MaskRegistry::resolve()` (`Core/MaskRegistry.php:88`, role check
  `:103–120`) then `BatchExecutor::execute()` (`:751`, SQL run `:798`) — variant
  selection lives at that pair of chokepoints and covers all surfaces at once.
- [ ] **T3.3** `AutoValidators` — tenancy column never required / stripped (audit-column
  rule reused).
- [ ] **T4.1** `JwtIssuer` / `LoginService` — claim issuance, `TENANT_SUSPENDED` at
  login + refresh; missing-claim rejection (§2.3). Anchor: `JwtGuard::authorize`
  (`Auth/JwtGuard.php:105`) returns the **raw verified payload as the auth context**, so
  a `tenant_id` claim flows to every consumer with zero plumbing — the new work is claim
  issuance in `LoginService::buildTokenResponse()` (`Auth/LoginService.php:399–403`,
  today `sub`/`email`/`role`/`name`; user row already in hand) and the
  enabled-but-claim-missing rejection right where `authorize()` returns.
- [ ] **T4.2** `OidcVerifier` — tenant from the local user row (rides the existing
  `resolveLocalUserId()`, `:442–540`); auto-provision stamps the tenant in the hardcoded
  INSERT column list (`autoProvision()`, `:570`); `tenant_claim` /
  `auto_provision_tenant` config + build check.
- [ ] **T4.3** `AdminCreateUserHandler` / `AdminChangePasswordHandler` (gated by
  `AdminAuth::authorizeAdmin`, dispatched at `ShapeRouter.php:254–263`) — tenant
  stamping, cross-tenant denial, unscoped-role assignment guard (§2.6); the
  `assignable_roles` check to extend lives in `LoginService::createUser` (`:583`).
- [ ] **T5.1** Registry auto-exposure gated to `unscoped_roles`; built-in
  `tenant.provision` command (§2.7).
- [ ] **T5.2** `ManifestGenerator` / `OpenApiGenerator` / `McpServer` — §2.8 rendering;
  `Vary: Authorization` on scoped GETs.
- [ ] **T6.1** Tests per §6; phpstan L6 + ecs green; SPEC.md §config + §mask-keys +
  §error-codes; DB-FIRST.md + AUTH.md sections; example app tenants page.

## 5. .NET work items

Mirror of §4 with the repo's established mapping (`ShapeConfig` property, `ConfigLoader`
shorthand acceptance, `MaskConfig`/`Mask`/`MaskRegistry` key plumbing, `MaskGenerator`,
`BatchExecutor`, `AutoValidators`, `JwtIssuer`/`LoginService`/`OidcVerifier`, admin
handlers, `ManifestGenerator`/`OpenApiGenerator`/MCP, tests + version bump).

In-tree anchors, mirroring §4's: convention detection in `Metadata/EntityMeta.cs`
(`DetectSoftDeleteColumn` `:93`, `DetectAuditColumns` `:114`) invoked from
`Metadata/DatabaseIntrospector.cs:172–173`; predicate compilation at
`Generator/MaskGenerator.cs:74–149` (`softWhere`/`softAnd` — identical shape to PHP);
resolve chokepoint `Core/MaskRegistry.cs` `Resolve()` `:185` (role check `:203–222`);
guard construction + admin-op dispatch `Core/OpDispatcher.cs:84–144`; stamping at
`Core/BatchExecutor.cs:276` (`ApplyAuditColumns`, definition `:743`); `PerformInsert`
`:595`, `PerformDelete` `:672`, `BuildWhereClause` `:695`, `WalkRelations` `:358`;
COUNT `:887`. **Port nuances:** (a) unlike PHP, `PerformUpdate` (`:643`) does *not*
currently receive the mask — the update-guard item must thread `Mask` through its
signature first; (b) the OData `$filter` path wraps the mask SQL via
`WrapMaskWithFilter()` (`:937`) — the wrap must receive the *chosen variant*, and that
existing wrapper doubles as a working template for any runtime predicate injection;
(c) `CheckForeignKeys()` (`:562`) is the in-tree template for the opt-in
`validate_fks` check (PHP builds its equivalent fresh).

- [ ] **U1.1–U6.1** one-to-one with T-items; manifest JSON keys byte-identical
  (`tenant_scope`, `sql_unscoped`, `tenantScoped`); reserved `__tenant` name shared;
  config lands on `ShapeConfig` + `ConfigDto` (`Config/ConfigLoader.cs:304`) with the
  snake_case `[JsonPropertyName]` alias for PHP parity, as `pk_assignment` does.

## 6. Testing strategy

All CI-runnable on SQLite in-memory (the whole feature is driver-agnostic SQL):

- **Two-tenant isolation matrix** — tenants A/B, one user each + one superadmin; for every
  surface (list, get, getMany, byFK, OData `$filter`/`$count`, generic mask GET, admin
  masks): A sees only A's rows; counts scoped; B's ids via `get` → empty; superadmin sees
  all.
- **Write isolation** — insert auto-stamps; explicit foreign `tenant_id` →
  `TENANT_MISMATCH`; update/delete of B's row by A → affected 0; soft-delete guard;
  **nested `relations` create stamps every child**; opt-in FK check rejects cross-tenant
  reference.
- **Auth** — claim round-trip login→refresh; suspended tenant blocks login/refresh;
  pre-tenancy token rejected; OIDC auto-provision stamps configured tenant; scoped admin
  cannot create user in another tenant nor assign an unscoped role.
- **Bypass attempts** (the security suite) — client sends `__tenant` param → rejected;
  OData `$filter=tenant_id eq 2` composes with (never replaces) the baked predicate;
  crafted mask params can't reach `sql_unscoped`.
- **Build-time checks** — each `CONFIG_INVALID` case from §2.10 has a test asserting the
  boot failure and its hint text.
- **Caching** — `public` cache on scoped mask fails build; `Vary: Authorization` present;
  cross-tenant `If-None-Match` never 304s.
- **Parity** — manifest snapshots byte-compare the new keys across stacks (existing
  parity-test pattern).

## 7. Risks & edge cases

| Risk | Mitigation |
|---|---|
| Manual mask leaks a scoped table | Declaration contract + strict-mode build error (§2.4); the heuristic scan false-negatives on dynamic SQL — documented residual, code review remains the backstop for hand-written SQL |
| Cross-tenant FK reference on insert | Opt-in `validate_fks`; composite-FK schema recipe documented; residual risk stated (§2.5) |
| Shared-cache leak via `public` cache_control | Build-time error + `Vary: Authorization` (§2.9) |
| `NULL` tenant_id rows invisible to everyone scoped | Strict-mode error on nullable column; migration recipe backfills before enabling (§2.13) |
| Missing index on `tenant_id` → every query a scan | Boot-time **warning** (introspection already reads indexes for `[Unique]`): scoped table without an index leading on the tenancy column |
| Scoped/unscoped SQL variants drift | Both compiled by one generator function from one source; snapshot tests assert the pair |
| Tenant admin escalates to superadmin | Role-assignment guard in create-user handler (§2.6), with test |
| Suspension lag (JWT TTL) | Documented; `check_active_per_request` opt-in for immediate cutoff |
| Existing app enables tenancy mid-flight | Strict-mode error list doubles as migration checklist; old tokens rejected, forced re-login (§2.3/§2.13) |
| Registry table itself scoped by accident (has `tenant_id`) | Build-time error — the registry is global by definition |

## 8. Suggested phasing

1. **Phase A — core isolation (~2 days/stack):** T1/U1 config + detection, T2.1/U2.1
   generated-mask compilation, T3.x/U3.x runtime scoping + stamping + guards, T4.1/U4.1
   JWT claim; the two-tenant SQLite matrix green. *After Phase A the leak class is closed.*
2. **Phase B — management surface (~1 day/stack):** admin handlers, registry
   auto-exposure, `tenant.provision`, suspension, OIDC tenant resolution (T4.2–T5.1).
3. **Phase C — contract & polish (~1 day/stack):** manual-mask contract + build checks,
   manifest/OpenAPI/MCP rendering, caching guards, docs (SPEC/DB-FIRST/AUTH/STATUS rows),
   example tenants admin page, parity snapshots.
4. **Phase D — deferred tier (§2.12):** separate plans when demand exists.

Like the MSSQL and manual-PK work, this splits into two implementation agents (one per
stack) with per-stack checklists cloned from §4/§5; the design rules in §2 stay
single-sourced here.

## 9. STATUS.md updates on completion

- Flip **Multi-tenant scoping** (Access & scoping matrix) to ✅ per stack as each lands,
  keeping the link to this plan.
- If T/U items were parameterized per §2.11, flip **Row-level auto-scoping (`user_id`)**
  in the same rows (it ships as the second convention of the same machinery) — otherwise
  flag it as the immediate follow-up.
- Strike roadmap item **1** from the ranked list; note the deferred tier (§2.12) as new
  candidate items only if demand exists.
- Add the new error codes and config keys to `docs/shape/SPEC.md` and the runtime SPEC's
  error-code list; extend `DB-FIRST.md` (config reference) and `AUTH.md` (claims,
  suspension, OIDC tenant resolution) — the doc items in T6.1/U6.1.
