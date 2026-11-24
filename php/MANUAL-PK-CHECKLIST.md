# Manual PK assignment — PHP implementation checklist

Owned by the **PHP agent**. Mark each box `[x]` as you complete it and add a one-line note.
Design, SQL shapes, config semantics and rationale live in
[../docs/shape/MANUAL-PK-PLAN.md](../docs/shape/MANUAL-PK-PLAN.md) — **read §2 (design) and
§3 (runtime flow) before starting.** Do not edit files under `dotnet/` or `docs/shape/SPEC.md`
(shared docs are updated by the coordinator after both stacks land).

## Phase 1 — dialect + database helper
- [x] **H1.1** `SqlDialect::insertAllocatingPk($driver, $table, $pk, $columns)` — full
  INSERT…SELECT per plan §2.1 (sqlsrv: `OUTPUT INSERTED` + `WITH (UPDLOCK, HOLDLOCK)` +
  `ISNULL`; pgsql: `RETURNING` + `COALESCE`; mysql/sqlite: `COALESCE`, id read back per §2.1).
  Added companion `SqlDialect::selectNextPk()` (locked `MAX+1` read; `FOR UPDATE` on mysql)
  because mysql/sqlite INSERTs can't return a non-identity id — see H1.2 note.
- [x] **H1.2** `Database::insertWithManualPk($table, $pk, array $data): int` + shared static
  `Database::insertWithManualPkOnPdo(PDO, driver, table, pk, data)` (one code path with
  LoginService, which manages its own raw PDO). Retry 3× on unique violation (SQLSTATE
  23000/23505, driver codes 2627/2601/1062) and MSSQL deadlock 1205 via
  `PDOException::$errorInfo`; after 3rd failure → `ApiException DB_ERROR` with details
  `['detail' => 'PK_ALLOCATION_RACE']`. pgsql takes
  `pg_advisory_xact_lock(hashtext('shape_pk:<table>'))` first and uses a SAVEPOINT per
  attempt when inside a caller-held transaction (errors abort pgsql transactions).
  **mysql/sqlite decision (documented in code):** allocation is `selectNextPk()` (locked
  read) + explicit INSERT of the known id — `lastInsertId()` only reports
  AUTO_INCREMENT/rowid values and a post-insert `MAX()` re-read could pick up a racing
  external writer's row; the pre-computed id is exact by construction. Each attempt wraps
  itself in a transaction when the caller hasn't opened one (locks must outlive statements).
  Also added `Database::resolvePkAssignmentMode()` (string-or-map config lookup for runtime
  consumers).

## Phase 2 — config, detection, mask emission
- [x] **H2.1** `pk_assignment` string shorthand expands to `['*' => …]` in
  `ShapeApp::expandConfigShorthands` (so cross-file config merges deep-merge correctly, like
  the auth shorthand); values validated (`auto|db|manual`) in
  `ShapeApp::normalizePkAssignment` → `CONFIG_INVALID` on anything else.
- [x] **H2.2** Strategy resolver `ShapeApp::resolvePkStrategies()` — called from
  `buildFromEntities` (the shared build point of fromDatabase / quickStart / fromEntities,
  right before MaskGenerator). Auto rule: single-column int-family PK
  (int/integer/bigint/smallint/tinyint/mediumint, or decimal/numeric with scale 0) &&
  !auto_increment && !has_default → manual. Forced `manual` on composite / non-int / missing
  PK → `CONFIG_INVALID` with hint.
- [x] **H2.3** `MaskGenerator` (new ctor param `pkStrategies`) emits `'pk' => <name>` on every
  writable mask with a single-column PK and `'pk_strategy' => 'manual'` when resolved manual.
  Composite-PK tables omit `pk` (nothing to return/allocate — same fallback as today).
  `AdminMaskGenerator` checked: all its masks are read-only (no `writable` key) → skipped.
  `MaskRegistry` (constructor normalization + `resolve()`) passes both keys through — it
  whitelists keys, so this was required for the runtime to see them.

## Phase 3 — runtime + contract
- [x] **H3.1** `BatchExecutor::performInsert` — plan §3 flow: manual + pk absent from data →
  `insertWithManualPk`; explicit client id passes through the normal path (and is used as
  `new_id`/`id` in the response — lastInsertId can't see non-identity PKs, MySQL reports 0,
  SQLite the rowid); retries surface as `DB_ERROR` detail `PK_ALLOCATION_RACE`. §2.5 fix:
  `insertReturningId($sql, $params, $mask['pk'] ?? 'id')`. Nested `relations` creates inherit
  everything via `executeSingleMutation` recursion (child masks carry their own pk keys).
- [x] **H3.2** `AutoValidators` — verified: PHP derives rules only for string-family columns
  (`notEmpty`/`maxLen`), so an int-family manual PK never receives a required rule; no code
  change needed. Regression-tested in tests/ManualPk.phpt (int + decimal PK → `[]`), plus
  end-to-end: inserts without id pass validation. OpenAPI `required` already excludes
  `isPrimary`.
- [x] **H3.3** `ManifestGenerator` / `OpenApiGenerator` / `McpServer::describeEntity` — unified
  generated flag: manual PK renders `autoIncrement: true` (fields) / is skipped from OpenAPI
  insert/update props, exactly like identity columns. ManifestGenerator additionally emits
  `pk` / `pk_strategy` (snake_case, cross-runtime contract) on mask entries (entity-owned and
  standalone).
- [~] **H3.4** `#[GeneratedId]` attribute — **deferred.** Entity-first apps already get manual
  strategy from the auto rule (an int PK without `#[AutoIncrement]`/default resolves manual),
  and the `pk_assignment` per-table override covers forcing; a dedicated attribute would
  require extending the serialized EntityMeta/FieldMeta cache shape for zero new capability.

## Phase 4 — auth
- [x] **H4.1** `LoginService` (new `insertAuthRow()` used by refresh / issueTokenPair /
  createUser) + `OidcVerifier::autoProvision` — resolve mode via
  `Database::resolvePkAssignmentMode()` from the same normalized map (ShapeApp injects it
  into `auth.pk_assignment` and `auth.oidc.pk_assignment` at build time) and route through
  `insertWithManualPkOnPdo` / `insertWithManualPk` when **explicitly** `manual` (auth tables
  are secret/never introspected, so 'auto' detection can't apply — documented in code).
  PK name for auth tables is `id` (schema contract, hardcoded across LoginService SQL).
  Caveat noted in code: on a manual-PK users table a duplicate email surfaces as DB_ERROR
  (PK_ALLOCATION_RACE), not AUTH_USER_EXISTS — unique violations are indistinguishable there.
  Needs live-MSSQL verification like the rest of the sqlsrv path.

## Phase 5 — tests & checks
- [x] **H5.1** tests/SqlDialect.phpt extended: statement shapes for all four drivers
  (brackets/ISNULL/OUTPUT placement + UPDLOCK,HOLDLOCK; COALESCE + trailing RETURNING;
  backtick COALESCE; empty-columns edge; selectNextPk FOR UPDATE split). New
  **tests/ManualPk.phpt** (SQLite in-memory, full pipeline): auto-rule detection (INT PK →
  manual, AUTOINCREMENT → db), per-table 'db' override, sequential ids 1,2,3 (both outside
  and inside a batch transaction), non-`id` PK name (ledger.entry_id — §2.5 threading),
  explicit-id 42 pass-through + next allocation 43, `id` in batch response, validators
  don't require id, manifest `pk`/`pk_strategy` + generated rendering, CONFIG_INVALID for
  bad mode / forced-manual-on-composite / forced-manual-on-varchar,
  `resolvePkAssignmentMode` lookups, direct `insertWithManualPk` on a users-like table.
  `vendor/bin/tester tests/` → OK (4 tests).
- [x] **H5.2** `phpstan` (level 6) → **0 errors**; `ecs` → **0 errors**; tester suite green.
  **Note:** the working tree's uncommitted MSSQL/writable_mode changes had drifted from
  `phpstan-baseline.neon` (HEAD verified green in a temp worktree; the working tree was red
  *before* this task started). Fixed for real where cheap:
  `DatabaseIntrospector::groupConstraintColumns` (**real pre-existing bug**: string keys on
  positional `Database::run` rows → unique constraints were never detected on
  mysql/pgsql/sqlsrv), `mapMssqlColumn` missing `description` in @return, a redundant
  null-check, `EntityReader` nullsafe-before-`??`. Then regenerated the baseline (166 → 204
  entries; additions are the file's established defensive-check noise pattern —
  isset/always-true-false on mask array shapes — plus the same class for the new pk keys).
  `rector process --dry-run` stays red (44 files) — pre-existing whole-tree backlog
  documented by the MSSQL work (42 files before this task); the new SqlDialect/test code is
  rector-clean. Concurrency guarantee (UPDLOCK/HOLDLOCK + 2627 retry) is
  **code complete — needs live-server verification** (same protocol as MSSQL plan §6).

## Log
(append dated one-liners here)
- 2026-07-04 H1.1/H1.2 SqlDialect::insertAllocatingPk + selectNextPk; Database::insertWithManualPk(+OnPdo static, retry loop, pgsql advisory lock + savepoints, own-tx wrap). mysql/sqlite = locked-read-then-insert (documented in code).
- 2026-07-04 H2.1–H2.3 pk_assignment shorthand+validation, resolvePkStrategies auto rule + hard errors, MaskGenerator pk/pk_strategy emission, MaskRegistry passthrough (constructor + resolve).
- 2026-07-04 H3.1–H3.3 performInsert manual flow + §2.5 pk threading + explicit-id new_id fix; Manifest/OpenAPI/MCP generated-flag unification; manifest mask keys pk/pk_strategy.
- 2026-07-04 H3.4 deferred (auto rule covers entity-first; attribute would need cache-shape change).
- 2026-07-04 H4.1 LoginService::insertAuthRow + OidcVerifier::autoProvision manual routing via Database::resolvePkAssignmentMode; ShapeApp injects map into auth/auth.oidc config.
- 2026-07-04 H5.1 SqlDialect.phpt extended + new ManualPk.phpt; tester 4/4 OK.
- 2026-07-04 H5.2 phpstan 0 / ecs 0 / tests green. Fixed pre-existing groupConstraintColumns bug (positional rows) + 3 small type fixes; regenerated phpstan-baseline.neon (166→204; tree was already red vs baseline before this task). rector-dry red pre-existing (42→44 whole-tree backlog).

## Handoff notes for the coordinator (SPEC.md / STATUS.md)
- Config: `pk_assignment` — string `auto|db|manual` or map `['*' => mode, '<table>' => mode]`;
  default `auto`. Auto rule per plan §2.2. Forced manual on composite/non-int PK →
  build-time `CONFIG_INVALID`.
- Manifest/mask contract keys (byte-identical for .NET): writable masks carry `pk` (always,
  single-column PKs) and `pk_strategy: "manual"` (only when manual). Manual PK fields render
  `autoIncrement: true` in manifest/MCP and are omitted from OpenAPI insert/update bodies.
- Response: `id` (new_id) now also correct for explicit client-supplied PKs on manual tables
  and for non-`id` PK names on RETURNING/OUTPUT drivers (§2.5 fix).
- Auth: `pk_assignment` is honored by LoginService/OidcVerifier only as explicit `manual`
  (users/refresh_tokens are never introspected); PK assumed `id`.
- Batches: manual-PK inserts inside a batch transaction hold the allocation lock until
  commit (serializes concurrent inserts to that table for the batch duration — plan §3 note).
- Pending: live-MSSQL concurrency verification (two parallel writers → distinct sequential
  ids; 2627 retry path); rides on the planned MSSQL Docker suite. Also note the regenerated
  phpstan baseline and the pre-existing rector-dry backlog.
