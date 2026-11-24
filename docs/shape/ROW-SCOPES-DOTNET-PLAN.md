# Row-level security by dimension (row scopes) — .NET implementation plan

> **Status: IMPLEMENTED (2026-07-07).** N1–N9 done; `dotnet/tests/Shape.Tests/RowScopesTests.cs`
> (8 tests) green; full suite 460. Two notes from implementation: (1) the multi-valued
> predicate compiles as `IN @__scope_<col>` **without** parentheses — Dapper's native
> list expansion needs the bare token (`IN (@x)` → SQLite "row value misused"); PHP keeps
> parens because its Database expands array params itself (a per-stack SQL difference, not
> a contract one). (2) A caller is unscoped **per scope** — a global admin must appear in
> every scope's `unscoped_roles`, not just tenancy's (unscoped-ness is not transitive).
>
> **Feature:** an ERP user sees only rows of their department / cost center /
> branch — any *dimension* column. The **`scopes`** config: tenancy machinery
> generalized to N conventions (MULTI-TENANCY-PLAN §2.11; refactor **R1** of
> [MULTI-TENANCY-SIMPLE-PLAN.md](MULTI-TENANCY-SIMPLE-PLAN.md)).
>
> Companion: [ROW-SCOPES-PHP-PLAN.md](ROW-SCOPES-PHP-PLAN.md) — **the design
> contract lives in its §2** (config keys, claim rules, fast-path/wrap-path
> read composition, write semantics, error codes, out-of-v1 list) and the
> manifest keys must stay **byte-identical** across stacks
> (`row_scopes` on masks, `rowScopes` on entities, `:__scope_<column>` /
> `@__scope_<column>` reserved params, `SCOPE_MISMATCH` / `SCOPE_REQUIRED`
> details). This document covers only the .NET-specific mapping and work items.
> Scope: `dotnet/src/Shape` + `dotnet/tests/Shape.Tests`.

## 1. .NET-specific design notes

- **Config surface**: JSON config → `ConfigLoader` (`ReadTenancy` at
  `ConfigLoader.cs:235` is the template for a `ReadScopes`); typed surface →
  `ShapeConfig.Scopes`. Snake_case JSON keys identical to PHP
  (`unscoped_roles`, `claim_table`, `multi`, `nullable`).
- **DB-first access map**: `ShapeConfig.Access` is a flat
  `Dictionary<string, List<string>>` — no change needed; scope participation
  is column-presence detection, not access-map syntax.
- **Placeholder dialect**: masks compile `@__scope_<column>`;
  `MaskRegistry.RewriteColonParams` already converts `:`-style from
  PHP-copied configs.
- **Value types**: `multi` claims arrive as `JsonElement` arrays from the JWT
  payload — unwrap via `ParamResolver.UnwrapJsonElement` before binding; the
  getMany `IN (@pks)` expansion is the array-bind template.
- **Wrap path**: `WrapMaskWithFilter` / `CloneMask`
  (`BatchExecutor.cs:1068/:1109`) is exactly the runtime composition mechanism
  the partial-unscoped path needs — reuse, don't reinvent.

## 2. Internal shape (R1)

```csharp
internal sealed record ScopeSpec(
    string Column,          // discriminator column
    string ClaimKey,        // auth-context key ("tenant_id", "department_id", …)
    IReadOnlySet<string> UnscopedRoles,
    bool Multi,             // claim is a list → IN predicate
    ScopeKind Kind);        // Tenant | Dimension — Tenant keeps registry/suspension extras
```

`TenancyConfig` constructs the `Tenant` spec; each `ScopeConfig` a `Dimension`
one. Detection, predicate compilation, variant/wrap selection, stamping, and
WHERE guards iterate `IReadOnlyList<ScopeSpec>` — tenancy behavior must be
bit-for-bit unchanged (its test suites are the refactor harness).

## 3. .NET work items

Anchors are the tenancy implementation — each item generalizes an existing
mechanism rather than adding a new one.

- [x] **N1 (R1) Parameterize the executor.** Keeping
  `TenancyFeatureTests` + `RolesByActionTests` green throughout:
  - `SelectTenantVariant` (`BatchExecutor.cs:1071`) → `SelectScopeVariant`:
    fully-scoped caller executes `Sql` (fast path); caller unscoped for ≥1
    dimension starts from `SqlUnscoped` and wraps via `WrapMaskWithFilter`
    with the predicates it *is* subject to; binds `@__scope_<col>` per spec
    (array-aware for `Multi`).
  - `ApplyTenantColumn` (`:1130`) → per-spec stamp loop: `Dimension` kind
    throws `SCOPE_MISMATCH` / `SCOPE_REQUIRED` (PHP-identical details);
    `Tenant` kind keeps `TENANT_MISMATCH` / `TENANT_REQUIRED`.
  - `BuildWhereClause` (`:800`) guard → `AND <col> = @__scope_<col>` (or `IN`)
    per spec; the update cross-scope move check generalizes with it.
  - `MissingTenantClaim` helper → `MissingScopeClaim(spec)`.
- [x] **N2 Config.** `ScopeConfig` class beside `TenancyConfig`
  (`Core/TenancyConfig.cs`); `ShapeConfig.Scopes`; `ConfigLoader.ReadScopes`
  (+ `ConfigDto`) with validation (`CONFIG_INVALID` on bad column/enforce
  values), mirroring `ReadTenancy`/`ValidateTenancy` (`:235–273`).
- [x] **N3 Detection.** `TenancyResolver.Apply` (`TenancyResolver.cs:12`) →
  iterate specs: `EntityMeta` gains `ScopeColumns` (list) beside
  `TenantColumn`; nullable dimension column → warning (or error when
  `nullable: "error"`); **no strict sweep** for dimensions (tenancy's stays).
- [x] **N4 Generator.** `MaskGenerator` (`softWhere`/`tenantCol` region,
  `MaskGenerator.cs:74–149`): compile every spec's predicate into `Sql`; emit
  `SqlUnscoped` when tenancy **or any scope** has unscoped roles;
  `RowScopes` carrier on read masks + the writable mask.
  `AdminMaskGenerator` untouched (users stay tenancy-only).
- [x] **N5 Mask plumbing.** `MaskConfig.RowScopes` /
  `Mask.RowScopes` (`Dictionary<string, RowScopeMeta>`; `RowScopeMeta { Multi }`);
  copy in `MaskRegistry` normalization (`:23–41`) and `CloneMask`
  (`BatchExecutor.cs:1109`) — the clone copy is load-bearing: the OData
  filter-wrap and unscoped-variant paths lose the contract without it.
- [x] **N6 Auth.** `LoginService`: claim per spec in token issuance +
  `FindUserByEmail`/refresh SELECTs via the `TenantColumn()`-style validated
  helper (aliased selects; `claim_table` variant = one indexed SELECT);
  `JwtGuard.NormalizeTenancy` (`JwtGuard.cs:105`) → `NormalizeScoping`
  (per-spec unscoped-null / `AUTH_INVALID` on missing claim);
  `OidcVerifier.ResolveLocalUserId` (`OidcVerifier.cs:380`) SELECT gains scope
  columns; auto-provision stamps single-valued scopes from
  `scopes.<col>.oidc_auto_provision_value` or rejects (`AUTH_CONFIG_ERROR`),
  mirroring `ResolveProvisionTenant` (`:561`).
- [x] **N7 Contract + caching.** `TenancyResolver.ValidateMaskContracts`
  (`:108`): declared `row_scopes` list → each `@__scope_<col>` placeholder
  present (writable exempt); `row_scopes: false` opt-out → roles ⊆ union of
  that scope's unscoped roles; undeclared manual mask touching a
  dimension-scoped table → **warning**, not boot error. `public`
  cache-control error + `Vary: Authorization`
  (`ShapeRouter.cs:285/:325–328`) trigger on `tenant_scope` **or** `row_scopes`.
- [x] **N8 Surfaces.** `ManifestGenerator.MaskSummary`
  (`ManifestGenerator.cs:134`) emits `row_scopes`; entity block emits
  `rowScopes`; `OpenApiGenerator` insert/update schema treatment (scope
  columns optional on insert — required note for `multi` — excluded from
  update, mirroring the tenant-column handling at `OpenApiGenerator.cs:~512`)
  + the per-scope description sentence; MCP `tools/list` unchanged (parity
  test).
- [x] **N9 Tests + docs.** `RowScopesTests.cs` per §4; version bump; SPEC
  parity notes; this plan's checkboxes flipped.

## 4. Testing strategy (SQLite, mirrors the PHP suite 1:1)

Same matrix as ROW-SCOPES-PHP-PLAN §4 — 4-quadrant tenant×department
isolation, dept-unscoped wrap path, `multi` membership (read both / insert
outside → `SCOPE_MISMATCH` / omitted → `SCOPE_REQUIRED`), write
stamp/guard/nested-relations, `__scope_*` client-param rejection, OData
compose-not-replace, claim round-trip + pre-feature token rejection, config
error cases, and the **manifest parity snapshot** byte-comparing
`row_scopes`/`rowScopes` against PHP.

.NET-specific additions:

- `CloneMask` carries `RowScopes` (regression: OData `$filter` on a scoped
  mask keeps the dimension predicate).
- `JsonElement` array claim unwraps and binds as an `IN` list.
- `RewriteColonParams` leaves `@__scope_*` intact and converts `:__scope_*`.

## 5. Risks

| Risk | Mitigation |
|---|---|
| Clone/copy sites miss the new key (the recurring .NET bug class) | N5 lists both copy sites; a test asserts the wrap path preserves `RowScopes` |
| Literal-column regressions in auth SQL | Reuse the validated `TenantColumn()`-helper pattern with `AS` aliasing (the org_id regression test is the template) |
| R1 regression in tenancy | `TenancyFeatureTests` + full suite green at every step; tenancy error codes and manifest keys must not change |
| PHP/.NET drift in §2 | Parity snapshot test + the shared-contract header rule (edit both plans or neither) |

## 6. Phasing

1. **N1–N3** (refactor + config + detection) — tenancy suites green throughout.
2. **N4–N6** (compile + runtime + auth) — 4-quadrant matrix green. *Leak class closed.*
3. **N7–N9** (contract, surfaces, parity, docs).

~3 days, can run in parallel with the PHP plan after §2 is frozen. STATUS.md:
flip **Row-level auto-scoping** (roadmap item 7) for the .NET row when this
lands — a `user_id` scope is `"scopes": { "user_id": { "claim": "sub" } }`.
