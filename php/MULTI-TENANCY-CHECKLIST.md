# Multi-tenancy — PHP implementation checklist

Tracks the PHP side of [docs/shape/MULTI-TENANCY-PLAN.md](../docs/shape/MULTI-TENANCY-PLAN.md).
Item ids mirror §4 of the plan (`T*`). `[x]` done, `[~]` partial/deferred with note,
`[ ]` not started.

## Phase A — core isolation (the leak class)

- [x] **T1.1** `ShapeApp::normalizeTenancy` — bool shorthand → full array; validate
  keys/modes; `CONFIG_INVALID` on bad `enforce` / non-array. Normalized form written
  back to `$config['tenancy']` for the runtime.
- [x] **T1.2** Build-time detection over the business entities in
  `ShapeApp::buildFromEntities`: per-table tenancy-column presence → `tenantScopes`
  map; registry-table-must-be-global check; nullable-column strict error; strict
  sweep (every scoped-config table lacking the column and not in `global_tables`
  fails the boot). Users-table column check runs in `buildDatabaseConfig` where the
  users columns are already introspected (DB-first).
- [x] **T2.1** `MaskGenerator` — predicate `<col> = :__tenant` baked into every
  generated read mask (`list`/`get`/`getMany`/`by<FK>`) combined with the soft-delete
  guard; `tenant_scope` carrier key on reads + the writable mask; `sql_unscoped`
  variant emitted when `unscoped_roles` is configured. COUNT inherits automatically
  (it wraps the chosen `sql`).
- [x] **T3.1** `BatchExecutor` write path — `performInsert` stamping from the auth
  context; client-supplied mismatch → `VALIDATION_ERROR` (`TENANT_MISMATCH`); unscoped
  caller must supply the tenant; UPDATE/DELETE/soft-delete WHERE gets
  `AND <col> = :__tenant` for scoped callers; update that moves a row cross-tenant →
  `TENANT_MISMATCH`. `relations` children inherit stamping (they recurse through
  `executeSingleMutation`).
- [x] **T3.2** `BatchExecutor` read path — variant selection (unscoped role → the
  `sql_unscoped` variant; scoped → bind `__tenant` from `authContext['tenant_id']`).
  OData GET + generic mask GET + COUNT all inherit it.
- [x] **T3.3** Reserved-param hygiene — any client param starting with `__` is
  rejected in `ParamResolver` (`PARAM_VALIDATION_ERROR`). Tenancy column stays a
  non-string int so `AutoValidators` never marks it required (documented — no code
  change needed there).
- [x] **T4.1** `LoginService` — `tenant_id` claim issued on login; suspended tenant
  (`is_active = 0` / soft-deleted registry row) → `TENANT_SUSPENDED` (403) at login
  **and** refresh; tenancy injected into auth config by `ShapeApp`.

## Phase B — management surface

- [x] **T5.1 (registry auto-exposure)** — when tenancy is enabled the registry table's
  generated masks are gated to `unscoped_roles` (override applied in
  `buildFromEntities` unless an explicit `access['tenants']` entry exists).
- [x] **T5.1 (`tenant.provision` command)** — DONE (S7). Atomic registry-row + first-admin
  provisioning needs password-hashing inside a command context (users table is
  secret / has no mask); design noted, not built this pass.
- [x] **T4.2 OIDC tenant resolution** — DONE (S6). Local-JWT claim path is complete;
  OIDC `tenant_claim` / `auto_provision_tenant` not wired.
- [x] **T4.3 admin-handler tenant scoping** — DONE (S1/S2, `tests/TenancySimpleTier.phpt`) (`AdminCreateUserHandler` /
  `AdminChangePasswordHandler` cross-tenant guards + unscoped-role-assignment guard).

## Phase C — contract & polish

- [x] **T5.2 (manifest)** — entities gain `tenantScoped`; scoped masks carry
  `tenant_scope`. Byte-identical snake_case keys for .NET parity.
- [x] **T5.2 (OpenAPI / MCP / `Vary`)** — DONE (S9). Manifest is done; OpenAPI scoped-mask
  sentence, MCP executor tenancy wiring, and `Vary: Authorization` on scoped GETs not
  yet added.
- [x] **T2.2 manual-mask contract + cache guards** — DONE (S3/S8) (placeholder-presence
  check, opt-out role-subset check, strict table-name scan, `public` cache_control
  build error).
- [x] **T3.1 opt-in FK validation (`validate_fks`)** — DONE (S4).
- [x] **T6.1 (tests)** — `tests/MultiTenancy.phpt`: two-tenant SQLite isolation matrix
  (list/get/getMany/byFK scoped; superadmin sees all; insert auto-stamp; cross-tenant
  update/delete → affected 0; `TENANT_MISMATCH`; reserved `__` param rejected; login
  claim + suspension). phpstan L6 + ecs green.
- [x] **T6.1 (docs)** — SPEC.md (config + mask keys + error codes), DB-FIRST.md
  (`tenancy` reference), STATUS.md row, plan status header.

## Deferred tier (plan §2.11) — untouched by design
multi-tenant membership, per-tenant unique emails, email-domain→tenant OIDC,
connection-per-tenant, per-tenant rate limiting, idempotency-key tenant scoping.
