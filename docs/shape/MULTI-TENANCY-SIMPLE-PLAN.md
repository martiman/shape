# Multi-tenancy SIMPLE tier — completion & hardening plan

> **Scope contract:** global role vocabulary, **one tenant per user**, no
> tenant-defined roles, no field-level permissions. Everything beyond that lives in
> [MULTI-TENANCY-FULL-PLAN.md](MULTI-TENANCY-FULL-PLAN.md). The base design (pooled
> `tenant_id` discriminator, `:__tenant` dual-variant masks, stamp-and-guard writes,
> `unscoped_roles`) is [MULTI-TENANCY-PLAN.md](MULTI-TENANCY-PLAN.md) — this document
> does not restate it; it lists what remains to **implement or refactor** to call the
> simple tier done, based on a per-stack code audit (2026-07-06, post code-review fixes).
>
> **Status (2026-07-07): Phases 1–3 + R1 implemented.** S1–S12 landed in both stacks with
> regression tests (`php/tests/TenancySimpleTier.phpt`, `dotnet .../RolesByActionTests.cs`);
> R1 (scope-machinery parameterization) shipped as the `ScopeSpec`/`ScopeConfig` refactor
> underpinning row scopes — see [ROW-SCOPES-PHP-PLAN.md](ROW-SCOPES-PHP-PLAN.md) /
> [ROW-SCOPES-DOTNET-PLAN.md](ROW-SCOPES-DOTNET-PLAN.md).
>
> Applies to both stacks: **PHP** (`php/packages/shape` + `php/packages/shaperuntime`)
> and **.NET** (`dotnet/src/Shape`). As of the audit, **.NET is the more complete
> implementation** of the tenancy surface; PHP has the security-relevant gaps.

## 1. What the simple tier is

| Concern | Simple tier answer |
|---|---|
| Tenancy model | Pooled: shared schema, `tenancy.column` discriminator (plan §2.1) |
| Roles | One global vocabulary (`auth.assignable_roles`); a user has exactly one `role` |
| Membership | A user belongs to exactly **one** tenant (`users.<tenant column>`) |
| Cross-tenant admin | `tenancy.unscoped_roles` (e.g. `superadmin`) — registry CRUD, `sql_unscoped` reads, explicit-tenant writes |
| Tenant admin | Global role `admin`, confined to own tenant by the scoping machinery (§2.6) |
| Endpoint access | Per-mask `roles` list; entity `#[Access(roles, read, write)]`; DB-first `access` map |
| NOT in scope | Multi-tenant membership, tenant-switch, tenant-defined roles, permission tables, field-level read/write, row ownership |

## 2. Current state (audited per stack)

Legend: ✅ implemented · ❌ missing · `!` security-relevant gap.

| Item (plan §) | PHP | .NET |
|---|---|---|
| Config normalization, detection, strict sweep (§2.2) | ✅ | ✅ |
| Read scoping: `:__tenant` + `sql_unscoped` variants, COUNT, OData, admin masks (§2.4) | ✅ | ✅ |
| Write stamp/guard incl. nested `relations`, soft delete (§2.5) | ✅ | ✅ |
| JWT claim + `TENANT_SUSPENDED` login/refresh, unscoped-role exemption (§2.3) | ✅ | ✅ |
| Registry auto-exposure gated to `unscoped_roles` (§2.7) | ✅ | ✅ |
| Manifest `tenantScoped` / `tenant_scope` keys (§2.8) | ✅ | ✅ |
| `auth.admin.create-user`: tenant stamp, cross-tenant denial, **unscoped-role assignment guard** (§2.6) | ✅ | ✅ |
| `auth.admin.change-password`: `AND tenant = :ctx` for scoped admins (§2.6) | ✅ | ✅ |
| `tenant.provision` command (§2.7) | ✅ | ✅ |
| Opt-in `validate_fks` cross-tenant FK check (§2.5) | ✅ | ✅ |
| `check_active_per_request` (§2.3) | ✅ | ✅ |
| Manual-mask declaration contract / `ValidateMaskContracts` (§2.4) | ✅ | ✅ |
| OIDC tenant resolution: `tenant_claim`, `auto_provision_tenant` (§2.3) | ✅ | ✅ |
| `Vary: Authorization` on scoped GETs (§2.9) | ✅ | ✅ |
| `public` cache_control build-time error on scoped masks (§2.9) | ✅ | ✅ |
| Per-operation write roles (create/update/delete split — §4 below) | ✅ | ✅ |

## 3. Recommended implementation items

Ordered by risk, not by plan-section number. S-ids for tracking.

### 3.1 PHP parity — security first

- [x] **S1 `!` `LoginService::createUser` tenancy guards.** Today the PHP handler has
  **zero** tenancy handling: a scoped tenant `admin` can (a) create a user in another
  tenant and (b) **assign a role from `unscoped_roles`** — tenant admin → superadmin in
  one API call. Port the .NET behavior exactly: stamp the caller's tenant; differing
  payload tenant → `VALIDATION_ERROR`/`TENANT_MISMATCH`; scoped caller assigning an
  unscoped role → `AUTH_FORBIDDEN`; unscoped caller must supply the target tenant
  (rows for unscoped-role users still get a home tenant; the token nulls it).
  Anchor: `LoginService::createUser` (`assignable_roles` check already there).
- [x] **S2 `!` `LoginService::changePassword` tenant scoping.** Target-user lookup gains
  `AND <col> = :ctx` for scoped admins — today a tenant admin can reset any tenant's
  user password by id/email.
- [x] **S3 PHP manual-mask declaration contract.** Port `TenancyResolver.ValidateMaskContracts`:
  `tenant_scope => '<col>'` requires `:__tenant` in the SQL (writable masks exempt —
  probe SQL only); `tenant_scope => false` requires `roles ⊆ unscoped_roles` in strict
  mode; undeclared manual mask referencing a scoped table name in strict mode →
  `CONFIG_INVALID`. Without this, every hand-written PHP mask is an undetected leak.
- [x] **S4 PHP `validate_fks` runtime check.** Config already normalizes the flag; the
  per-FK `SELECT 1 … AND <col> = :__tenant` check (`TENANT_FK_MISMATCH`) is not built.
  .NET `BatchExecutor.CheckForeignKeys` is the template — note it must read the
  **configured** tenant column from the mask's `tenant_scope`, not a literal
  `tenant_id` (regression fixed in .NET; don't reintroduce).
- [x] **S5 PHP `check_active_per_request`.** Wire the per-request registry lookup where
  `JwtGuard::authorize` returns the payload; .NET `TenancyResolver.EnsureTenantActive`
  is the template (single SELECT, inspect returned columns for `is_active` /
  `deleted_at` — no separate introspection query).
- [x] **S6 PHP OIDC tenant resolution.** `oidc.tenant_claim` + `oidc.auto_provision_tenant`;
  tenant comes from the **local user row** after `oid → users.id` resolution, never the
  IdP token; auto-provision without a tenant source → build-time `CONFIG_INVALID`.
- [x] **S7 PHP `tenant.provision` command.** Atomic registry-row + first-admin insert,
  gated to `unscoped_roles`, returns `{tenant_id, user_id}`. Port from
  `ShapeApp.cs` (`config.Commands["tenant.provision"]`).

### 3.2 Both stacks — close the caching leak (§2.9)

- [x] **S8 `public` cache_control on a scoped mask → build-time `CONFIG_INVALID`.**
  Missing in both stacks; cheapest remaining item in the leak class.
- [x] **S9 PHP `Vary: Authorization` on scoped GET responses.** .NET `ShapeRouter.cs`
  already emits it; PHP `ShapeRouter`/`AppRouter` do not.

### 3.3 Per-operation write roles (the one RBAC extension worth having in the simple tier)

The read/write split cannot express "sales rep may create and update, only manager
deletes" — the most common CRM/ERP authorization ask that does **not** need permission
tables.

- [x] **S10** Extend `#[Access]` (entity-first) and the `access` map (DB-first) with
  optional `create` / `update` / `delete` keys. Override cascade: action key → `write`
  → `roles` (same precedence pattern as today's `read`/`write` over `roles`).
- [x] **S11** Writable mask carries `roles_by_action` (snake_case, byte-identical across
  stacks, like `pk_strategy`); absent key = fall back to the mask's `roles`.
- [x] **S12** `BatchExecutor` checks `roles_by_action[action]` next to the existing role
  check — same chokepoint, no new dispatch surface. Manifest/OpenAPI render the split;
  MCP `tools/list` filtering unchanged (a tool is listed if *any* action is allowed).

### 3.4 Refactors (do while completing, not after)

- [x] **R1 Parameterize the scope machinery by `(column, context-key)`** — plan §2.11. **Done** — shipped as the `ScopeSpec`/`ScopeConfig` refactor underpinning row scopes.
  Now planned in detail as the first work item of the row-scopes feature:
  [ROW-SCOPES-PHP-PLAN.md](ROW-SCOPES-PHP-PLAN.md) P1 / [ROW-SCOPES-DOTNET-PLAN.md](ROW-SCOPES-DOTNET-PLAN.md) N1.
  The predicate compilation, variant selection, stamping, and WHERE guards should take
  the pair (`tenant_id` ↔ `authContext.tenant_id`) as data, not hardcode the tenant
  case. This is a rename-level refactor now and the prerequisite for both `user_id`
  row-ownership (STATUS roadmap item 7) and the FULL plan — retrofitting later means
  touching every call site twice.
- [x] **R2 Single tenancy-normalization source per stack.** PHP `normalizeTenancy` is
  reused across ShapeApp/AppRouter/McpServer since the executor-wiring fix — keep it
  that way; .NET equivalents live in `ConfigLoader`/`TenancyConfig`. Any new entrypoint
  (S7's command, future CLI) must consume the normalized form, never raw config.
- [x] **R3 Keep role decisions at the two chokepoints** — `MaskRegistry::resolve()`
  (mask-level `roles`) and `BatchExecutor` (S12's per-action check). S1/S2 admin-handler
  guards are auth-context checks inside `LoginService`, not a third authorization layer.
- [x] **R4 Error-code discipline.** New denials reuse the §2.10 vocabulary
  (`TENANT_MISMATCH`, `AUTH_FORBIDDEN`, `CONFIG_INVALID` + hint). Missing-claim after
  tenancy enablement is `AUTH_INVALID`/401 in **both** stacks (PHP was 403 — already
  aligned; keep the parity test).

## 4. Testing additions

Extends the §6 matrix of the base plan (all SQLite-in-memory CI):

- **S1/S2**: scoped admin cross-tenant create → `TENANT_MISMATCH`; scoped admin assigns
  `superadmin` → `AUTH_FORBIDDEN`; scoped admin resets other-tenant password → not found;
  unscoped caller creates into an explicit tenant → OK.
- **S3**: each contract violation boots `CONFIG_INVALID` with hint text (PHP mirror of
  `TenancyFeatureTests` contract cases, incl. the writable-mask exemption).
- **S4**: cross-tenant FK insert → `TENANT_FK_MISMATCH`; same-tenant passes; renamed
  discriminator column still checked.
- **S8/S9**: `public` cache on scoped mask fails build; `Vary: Authorization` present;
  cross-tenant `If-None-Match` never 304s.
- **S10–S12**: role allowed for `create` but not `delete` → insert OK, delete
  `AUTH_FORBIDDEN`; fallback cascade (`delete` unset → `write` roles apply); manifest
  snapshot byte-compares `roles_by_action` across stacks.

## 5. Suggested phasing

1. **Hardening (PHP security parity):** S1, S2, S3 — closes the escalation and
  manual-mask leak classes. *Ship-blocker for any PHP production tenant.*
2. **Parity completion:** S4–S7, S8, S9 — the checklist's deferred items.
3. **Per-operation roles:** S10–S12 with R1 folded in.

After phase 3 the simple tier is feature-complete; anything further belongs to
[MULTI-TENANCY-FULL-PLAN.md](MULTI-TENANCY-FULL-PLAN.md).
