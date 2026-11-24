# Row-level security by dimension (row scopes) — PHP implementation plan

> **Status: IMPLEMENTED (2026-07-07).** P1–P9 done; `php/tests/RowScopes.phpt` green;
> full PHP suite 10 tests. Cross-stack parity verified against .NET: manifest keys
> (`rowScopes` entity / `row_scopes` mask `{multi}`), error details
> (`SCOPE_MISMATCH` / `SCOPE_REQUIRED` + `scope`), and `AUTH_INVALID` on a missing scope
> claim all match. (The multi-valued `IN (:__scope_<col>)` keeps parens here — PHP's
> Database expands array params itself; .NET drops them for Dapper. SQL text was never
> part of the contract, same as `:__tenant` vs `@__tenant`.)
>
> **Feature:** an ERP user sees only rows of their department / cost center /
> branch — any *dimension* column. Marketed as **row-level security (RLS) by
> dimension**; in the framework it is the **`scopes`** config: the tenancy
> machinery generalized to N conventions, exactly the seam MULTI-TENANCY-PLAN
> §2.11 reserved ("same machinery, second convention") and refactor **R1** of
> [MULTI-TENANCY-SIMPLE-PLAN.md](MULTI-TENANCY-SIMPLE-PLAN.md) prepares.
>
> Companion: [ROW-SCOPES-DOTNET-PLAN.md](ROW-SCOPES-DOTNET-PLAN.md). The design
> contract in §2 is shared and the manifest keys must stay **byte-identical**
> across stacks — change §2 in both documents or in neither. PHP is the
> reference implementation; scope: `php/packages/shape` +
> `php/packages/shaperuntime` (never `php/.deploy/**` — deployed copy).

## 1. Problem

Tenancy isolates *organizations*. Inside one organization, an ERP needs a
second axis: a sales rep sees only `department_id = 12` rows, an accountant
only their `cost_center_id`s. Today that means hand-writing predicates into
every mask — the same leak class tenancy §1 eliminated, one level down.
Industry names: SQL Server/Postgres RLS, Oracle EBS MOAC, Dynamics XDS,
NetSuite role restrictions, OLAP dimension security.

## 2. Design contract (shared with .NET — keep in sync)

### 2.1 Config

```php
'scopes' => [
    'department_id' => [                    // key = discriminator column
        'claim'          => 'department_id',   // JWT claim name (default: the column name)
        'unscoped_roles' => ['admin', 'controller'],  // see across this dimension (default: none)
        'multi'          => false,           // true → claim is a LIST, predicate becomes IN
        'claim_table'    => null,            // multi-membership source, e.g.
                                             // ['table' => 'user_departments', 'user_column' => 'user_id']
                                             // default: single value from users.<column>
        'nullable'       => 'warn',          // 'warn' (default) | 'error' — NULL rows are invisible to scoped callers
    ],
    // 'cost_center_id' => [...], 'branch_id' => [...]
],
```

Differences from `tenancy` (deliberate — these are *not* bugs to fix later):

| Aspect | tenancy | scopes |
|---|---|---|
| Table participation | strict sweep: exposed table w/o column fails boot | **scoped iff the column is present** — dimensions naturally apply to a subset of tables; no sweep |
| Registry / suspension / provision | `tenants` table, `TENANT_SUSPENDED`, `tenant.provision` | none — dimensions are plain lookup tables |
| Claim cardinality | exactly one | one or many (`multi: true` → `IN` predicate) |
| Nullable column | strict error | boot **warning** by default (`'nullable' => 'error'` opts in) |

Composition rule: predicates **AND together** — a table carrying `tenant_id`
and `department_id` requires both to match. The tenancy feature keeps its own
config and behavior untouched; internally both run through one parameterized
mechanism (§2.6 / R1).

### 2.2 Auth: dimension claims ride in the token

- Server-issued only — a client can no more choose its department than its
  role. `LoginService` reads `users.<column>` (or the `claim_table` rows for
  `multi`) at login/refresh and stamps one claim per scope. OIDC resolves from
  the **local user row** after `oid → users.id`, never from the IdP token.
- `JwtGuard` normalization per scope: role in the scope's `unscoped_roles` →
  claim forced to `null`; scoped caller missing the claim → `AUTH_INVALID`
  ("re-login required after scopes were enabled") — pre-feature tokens never
  fall through to unscoped.
- No per-request registry check (nothing to suspend).

### 2.3 Read path

Reserved parameter per scope: **`:__scope_<column>`** (the `__` prefix is
already rejected framework-wide from clients — S-plan §2.4 hygiene applies
for free).

- **Fast path (the common case — fully compiled):** every generated read mask
  on a scoped table gets `AND <col> = :__scope_<col>` (or `IN (…)` when
  `multi`) baked in **alongside** the tenancy predicate — same
  build-time-compile / run-time-bind mechanism, COUNT and OData inherit it
  because they wrap the executed SQL.
- **Exception path (caller unscoped for ≥1 dimension):** rather than compiling
  2^N variants, the runtime composes: start from `sql_unscoped` (already
  predicate-free) and **wrap** it in a subselect appending only the predicates
  the caller *is* subject to — the sanctioned in-tree wrap pattern (COUNT wrap
  `BatchExecutor.php:1113`; .NET `WrapMaskWithFilter`). Optimizers flatten the
  subselect; the fully-scoped fast path never pays for it.
- Mask carrier key (snake_case, cross-runtime):

```jsonc
"orders.list": {
  "tenant_scope": "tenant_id",                    // unchanged
  "row_scopes": { "department_id": { "multi": false } },
  "sql": "… WHERE tenant_id = :__tenant AND department_id = :__scope_department_id …",
  "sql_unscoped": "… (no predicates) …"           // now emitted when tenancy OR any scope has unscoped_roles
}
```

### 2.4 Write path

- **INSERT**: single-valued scope → stamp from the claim; differing
  client value → `VALIDATION_ERROR` detail **`SCOPE_MISMATCH`** (+
  `scope: <column>` in details). `multi` scope → the framework cannot guess
  among N memberships: the client **must** supply the column and the value
  must be within the claim set (else `SCOPE_MISMATCH`).
- **UPDATE / DELETE / soft delete**: WHERE gains `AND <col> = :__scope_<col>`
  (or `IN`) per scope — cross-dimension ids yield affected-rows 0, no
  existence oracle. UPDATE moving a row across a dimension → `SCOPE_MISMATCH`
  (single-valued) or out-of-membership check (`multi`).
- **AutoValidators**: scope columns treated like the tenancy column — never
  required, stripped from writable-field validators (multi-valued scopes stay
  *writable* since the client supplies them; validator only checks membership).
- Nested `relations` children inherit stamping via recursion — test it anyway.
- Unscoped-for-the-dimension callers: may write any value in that column;
  supplying none on a single-valued scope → `VALIDATION_ERROR`
  (`SCOPE_REQUIRED`) — never guess.

### 2.5 Manual masks, caching, surfaces

- **Declaration contract** (extends `validateMaskContracts`): declared
  `'row_scopes' => ['department_id']` → each `:__scope_<col>` placeholder must
  appear in the SQL (writable masks exempt — probe SQL); `'row_scopes' =>
  false` = opt-out, roles must be ⊆ the union of that scope's
  `unscoped_roles`. Undeclared manual mask referencing a scoped table →
  **manifest warning** (not boot error — dimensions default relaxed; tenancy's
  strict rule is unchanged).
- **Caching**: any `row_scopes` on a mask ⇒ `Vary: Authorization` on GETs and
  the `public` cache_control boot error — same predicate as `tenant_scope`,
  same code path.
- **Manifest**: entity gains `"rowScopes": ["department_id"]`; masks carry
  `row_scopes` as above. **OpenAPI**: scope columns optional in insert schemas
  (required note for multi), excluded from update; description sentence per
  scope: *"Rows are automatically limited to the caller's department_id."*
  **MCP**: role-filtering unchanged; parity test required.

### 2.6 Internal shape (R1)

One value object per stack drives everything:

```
ScopeSpec { column, claimKey, unscopedRoles, multi, kind: 'tenant'|'dimension' }
```

Tenancy constructs the `tenant` ScopeSpec (plus its registry/suspension
extras layered outside the shared machinery); each `scopes` entry constructs a
`dimension` one. Detection, predicate compilation, variant/wrap selection,
stamping, and WHERE guards iterate `List<ScopeSpec>` — one detection pass, one
generator path, one executor path, N conventions.

### 2.7 Error codes (new)

| Code | HTTP | When |
|---|---|---|
| `VALIDATION_ERROR` detail `SCOPE_MISMATCH` (+`scope`) | 400 | Client value outside the caller's dimension (write or cross-dimension move) |
| `VALIDATION_ERROR` detail `SCOPE_REQUIRED` (+`scope`) | 400 | Unscoped caller (or `multi` caller) omitted a required scope column on insert |
| `AUTH_INVALID` | 401 | Scoped caller missing a scope claim (pre-feature token) |
| `CONFIG_INVALID` | — | Bad `scopes` config; declared placeholder missing; opt-out reachable by scoped role; `public` cache on scoped mask; `nullable: 'error'` hit |

### 2.8 Out of v1

Hierarchies (manager sees department + children — needs a rollup/closure
source), `read_own` vs `read_all` per-role actions (FULL-plan F2/F3
territory), per-dimension FK validation (tenant-only `validate_fks` stays),
dimension-scoped admin masks for `users` (users stay tenancy-scoped only).

## 3. PHP work items

In-tree anchors are the tenancy implementation itself — every item is "make
the existing mechanism iterate `ScopeSpec[]` instead of hardcoding tenant".

- [x] **P1 (R1) Parameterize the machinery.** Introduce `ScopeSpec` (plain
  array shape or small class in `shaperuntime`). Rework, keeping tenancy tests
  green as the regression harness:
  - `BatchExecutor::applyTenantScope` (`:853`) → `applyRowScopes` — variant
    selection + per-scope bind; caller-partial-unscoped composes via the
    subselect wrap (COUNT wrap `:1113` is the template).
  - `stampTenantOnInsert` / `resolveTenantGuard` / `assertNoTenantMove` →
    per-spec loops (`SCOPE_MISMATCH`/`SCOPE_REQUIRED` for dimension kind,
    existing TENANT_* codes for tenant kind).
  - `callerTenantId` → `callerScopeValue(spec)` (claim lookup + normalize;
    array-aware for `multi` — `Database::run` already expands array params).
- [x] **P2 Config.** `ShapeApp::normalizeScopes()` next to `normalizeTenancy()`
  (`ShapeApp.php` §config): validate columns/claims/roles, `CONFIG_INVALID` on
  bad values; write normalized form back; inject into `auth` config (as
  tenancy does) and `BatchExecutor::setTenancy` → `setScoping(tenancy, scopes)`.
- [x] **P3 Detection.** `resolveTenantScopes` generalizes: per entity, per
  spec, column present → scoped; nullable column → warning (or error when
  configured). No strict sweep for dimensions.
- [x] **P4 Generator.** `MaskGenerator::withTenant` (`:233`) → `withScopes`:
  compile all predicates into `sql`; emit `sql_unscoped` when tenancy **or any
  scope** has unscoped roles; `row_scopes` carrier on reads + the writable
  mask. `AdminMaskGenerator` untouched (users = tenancy only, §2.8).
- [x] **P5 Registry.** `MaskRegistry` normalization + `resolve()` carry
  `row_scopes` (same pattern as `tenant_scope` / `fk_checks` / `roles_by_action`).
- [x] **P6 Auth.** `LoginService::buildTokenResponse` stamps one claim per
  spec (users-row read like `tenantColumn()`; `claim_table` variant runs one
  indexed SELECT); refresh re-reads; `JwtGuard::normalizeTenancy` →
  `normalizeScoping` (per-spec unscoped-null / missing-claim rejection);
  `OidcVerifier::resolveLocalUserId` SELECT gains the scope columns,
  auto-provision stamps single-valued scopes from
  `scopes.<col>.oidc_auto_provision_value` (mirror of the tenancy keys) or
  rejects.
- [x] **P7 Contract + caching.** `validateMaskContracts` extends per §2.5;
  `Vary`/`public`-cache predicates switch from `isset(tenant_scope)` to
  "tenant_scope **or** row_scopes" (`ShapeRouter.php:509/:632` emission sites).
- [x] **P8 Surfaces.** `ManifestGenerator` (`rowScopes` on entities,
  `row_scopes` on masks); `OpenApiGenerator` insert/update schema treatment +
  description sentence; MCP parity test.
- [x] **P9 Tests + docs.** `php/tests/RowScopes.phpt` per §4; phpstan L6 +
  ecs green (no `withPreparedSets`); SPEC.md config + mask-keys + error codes;
  DB-FIRST.md `scopes` reference; this plan's checkboxes flipped.

## 4. Testing strategy (SQLite in-memory, mirrors tenancy suite)

- **4-quadrant matrix**: 2 tenants × 2 departments; user(t1,d1) sees exactly
  the (t1,d1) rows on list/get/byFK/COUNT/OData/generic-GET; dept-unscoped
  `controller` in t1 sees all t1 rows and no t2 rows (the wrap path);
  superadmin sees everything.
- **Multi-valued**: claim `[d1,d2]` reads both; insert into d3 →
  `SCOPE_MISMATCH`; insert without the column → `SCOPE_REQUIRED`.
- **Writes**: stamp; differing value → `SCOPE_MISMATCH`; cross-department
  update/delete → affected 0; nested relations stamped; soft-delete guard.
- **Bypass suite**: client sends `__scope_department_id` → rejected; OData
  `$filter=department_id eq 99` composes with, never replaces, the predicate.
- **Auth**: claims round-trip login→refresh; pre-feature token → `AUTH_INVALID`;
  unscoped role claim nulled.
- **Build**: each `CONFIG_INVALID`/warning case; contract cases incl. writable
  exemption and `public` cache.
- **Parity**: manifest snapshot byte-compares `row_scopes`/`rowScopes` with .NET.

## 5. Risks

| Risk | Mitigation |
|---|---|
| Wrap path (partial-unscoped) misses a surface | Both paths funnel through `MaskRegistry::resolve` + `BatchExecutor::execute`; COUNT wraps whatever SQL executes — matrix tests cover the wrap caller explicitly |
| `multi` claim bloats the JWT | Document a practical cap (~50 values); beyond that the deployment wants FULL-plan F2 permissions, not claims |
| NULL dimension rows silently invisible | Boot warning + `nullable: 'error'` opt-in; migration recipe: backfill, then flip |
| R1 regression in tenancy | Tenancy suites (MultiTenancy.phpt, TenancySimpleTier.phpt) are the harness — refactor lands only green |

## 6. Phasing

1. **P1–P3** (refactor + config + detection) — tenancy green throughout.
2. **P4–P6** (compile + runtime + auth) — 4-quadrant matrix green. *Leak class closed.*
3. **P7–P9** (contract, surfaces, docs, parity).

~3 days. STATUS.md: flip **Row-level auto-scoping** (roadmap item 7 — a
`user_id` scope is literally `'scopes' => ['user_id' => ['claim' => 'sub']]`)
when this lands.
