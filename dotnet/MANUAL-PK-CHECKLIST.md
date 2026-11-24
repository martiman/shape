# Manual PK assignment — .NET implementation checklist

Owned by the **.NET agent**. Mark each box `[x]` as you complete it and add a one-line note.
Design, SQL shapes, config semantics and rationale live in
[../docs/shape/MANUAL-PK-PLAN.md](../docs/shape/MANUAL-PK-PLAN.md) — **read §2 (design) and
§3 (runtime flow) before starting.** Do not edit files under `php/` or `docs/shape/SPEC.md`
(shared docs are updated by the coordinator after both stacks land).

Manifest JSON keys must stay byte-identical to PHP: `pk`, `pk_strategy`, config key
`pk_assignment` with values `auto|db|manual`.

## Phase 1 — dialect + database helper
- [x] **N1.1** `Database` — static SQL builder for the allocating INSERT…SELECT per plan §2.1
  (next to `MssqlInsertReturningIdBatch`, Database.cs:202): sqlsrv `OUTPUT INSERTED` +
  `WITH (UPDLOCK, HOLDLOCK)` + `ISNULL`; pgsql `RETURNING` + `COALESCE`; mysql/sqlite
  `COALESCE` + id read-back. Plus `InsertWithManualPk` with retry 3× on unique violation
  (`SqlException.Number` 2627/2601, deadlock 1205; MySql 1062; Npgsql 23505; SQLite 19).
  — `InsertAllocatingPkSql` + `InsertWithManualPk` + `IsPkAllocationRace`. mysql/sqlite
  read the id back via `SELECT MAX(pk)` inside the same transaction (LAST_INSERT_ID /
  last_insert_rowid only track identity/rowid columns); the helper opens its own
  transaction when the caller has none, so the read-back and the pg advisory lock are
  always transaction-scoped.

## Phase 2 — config, detection, mask emission
- [x] **N2.1** `ShapeConfig.PkAssignment` (`Dictionary<string,string>?`, `"*"` default key);
  `ConfigLoader` accepts plain-string shorthand; validate values `auto|db|manual`.
  — JSON accepts both `pkAssignment` and PHP-parity `pk_assignment`; string shorthand
  normalizes to `{"*": value}`; invalid values → `CONFIG_INVALID` at load.
- [x] **N2.2** Strategy resolver in `ShapeApp.FromDatabase`: auto rule = single-column
  int-family PK (int/bigint/smallint/tinyint/decimal-scale-0) && !IsAutoIncrement &&
  !HasDefault → manual. Hard error on `manual` forced onto composite/non-int PK.
  — `Metadata/PkStrategyResolver.Apply`, invoked from `ShapeApp.Build` so the entity-first
  path (`FromAssembly`/`FromTypes`, incl. `[GeneratedId]`) resolves through the same code.
- [x] **N2.3** `MaskConfig.Pk` / `MaskConfig.PkStrategy` + `Mask` copy + compile mapping in
  `MaskRegistry` (~:922); `MaskGenerator` always emits `Pk`, emits `PkStrategy = "manual"`
  only when resolved manual.
  — Also copied through `BatchExecutor.WrapMaskWithFilter` (with `WritableMode`, which the
  wrapper previously dropped).

## Phase 3 — runtime + contract
- [x] **N3.1** `BatchExecutor.PerformInsert` (~:618) — plan §3 flow (manual + pk absent →
  allocate; explicit id passes through; typed `DB_ERROR` detail `PK_ALLOCATION_RACE` after
  3 failed retries). Pass `mask.Pk ?? "id"` to `InsertReturningId` on the db path (§2.5 fix).
  — a `null` pk value counts as absent (PHP `isset()` parity). NOTE for the coordinator:
  the .NET batch response reports the allocated id under the pre-existing key `id`
  (PHP uses `new_id`); kept as-is to avoid breaking the .NET response contract.
- [x] **N3.2** `AutoValidators` equivalent — manual-strategy PK NOT required on insert.
  — .NET auto-validators only ever emit `notEmpty` for string-family columns, and a manual
  PK is by definition int-family, so nothing required it in the first place; the resolver
  still strips a `notEmpty` from the manual PK defensively, with a test pinning the contract.
- [x] **N3.3** `ManifestGenerator`/`OpenApiGenerator` — unified IsGenerated so manual PKs
  render readonly/auto-assigned; manifest keys byte-identical to PHP.
  — manifest field flag `autoIncrement` now = `IsAutoIncrement || manual PK` (§2.4); mask
  summaries emit `pk` / `pk_strategy`; OpenAPI insert schema omits the manual PK;
  `McpServer` entity resource updated the same way.
- [x] **N3.4** Code-first parity: `[GeneratedId]` attribute (implement if straightforward,
  otherwise mark deferred with a note).
  — `Attributes/GeneratedIdAttribute.cs`; `EntityReader` pre-seeds `PkStrategy = "manual"`,
  validated by the resolver like an explicit force; per-table `PkAssignment` override wins.

## Phase 4 — auth
- [x] **N4.1** `LoginService` + `OidcVerifier` inserts — resolve strategy for
  `users`/`refresh_tokens` from `PkAssignment`; route through `InsertWithManualPk` when manual.
  — map rides on `AuthConfig.PkAssignment` (copied from `ShapeConfig` in `ShapeApp.Build`,
  so `OpDispatcher`/`JwtGuard` signatures stay unchanged); covers `CreateUser`, `Refresh`
  rotation, `IssueTokenPair`, and OIDC auto-provision. Only an explicit `manual` (table
  key or `*`) triggers allocation — `auto` detection needs introspection the per-request
  auth path doesn't do.

## Phase 5 — tests & checks
- [x] **N5.1** Tests: statement-shape unit tests per driver (extend
  `MssqlDialectGeneratorTests` or a new fixture); SQLite-backed integration test — forced
  manual strategy → sequential ids 1,2,3, explicit-id pass-through, `new_id` in batch
  response, validator no longer requires id. Mark MSSQL concurrency test
  `[Trait("db","mssql")]` (skipped unless live server configured).
  — `ManualPkTests.cs` (24 cases: statement shapes incl. pk-only edge, race-code
  classification, resolver auto/forced/error paths, config shorthand, mask/manifest/OpenAPI
  emission, SQLite pipeline with auto-detected `INT PRIMARY KEY`, batch-transaction case,
  auth allocator) + `MssqlManualPkLiveTests.cs` (env-gated: live auto-detection,
  sequential OUTPUT INSERTED ids, 8×10 parallel writers → distinct dense ids).
- [x] **N5.2** `dotnet test tests/Shape.Tests` fully green; README config docs; bump version
  in Shape.csproj (0.6.1 → 0.6.2). Note live-MSSQL-only steps as
  `[x] code complete — needs live-server verification`.
  — 426/426 passing (399 before the feature); README gained "Manual primary keys (legacy
  tables)"; version 0.6.2. MSSQL range-lock concurrency + 2627/1205 retry:
  `[x] code complete — needs live-server verification` (rides the existing
  SHAPE_MSSQL_DSN Docker protocol).

## Log
(append dated one-liners here)
- 2026-07-04 N1.1: allocator SQL builder + `InsertWithManualPk` (retry ×3, own-transaction management, pg advisory lock, MAX read-back for mysql/sqlite) in Core/Database.cs; static `QuoteIdentifier(DbDriver,…)` extracted for connection-free tests.
- 2026-07-04 N2.1–N2.3: `ShapeConfig.PkAssignment` + ConfigLoader shorthand (`pkAssignment`/`pk_assignment`), `PkStrategyResolver` (auto rule, forced-manual guards), `EntityMeta.PkStrategy`, mask `Pk`/`PkStrategy` end to end (MaskConfig → registry → Mask), MaskGenerator emission.
- 2026-07-04 N3.1–N3.4: BatchExecutor manual-allocation flow + §2.5 pk threading; manifest/OpenAPI/MCP report manual PKs as generated; `pk`/`pk_strategy` manifest keys; `[GeneratedId]` attribute.
- 2026-07-04 N4.1: auth inserts (LoginService users/refresh_tokens, OidcVerifier auto-provision) honor forced-manual via `AuthConfig.PkAssignment` copied by ShapeApp.
- 2026-07-04 N5.1–N5.2: ManualPkTests + MssqlManualPkLiveTests; suite 426/426 green (was 399); README docs; version 0.6.2.
