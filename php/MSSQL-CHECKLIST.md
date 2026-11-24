# MSSQL support — PHP implementation checklist

Owned by the **PHP agent**. Mark each box `[x]` as you complete it and add a one-line note.
Shared dialect rules & rationale live in
[../docs/shape/MSSQL-SUPPORT-PLAN.md](../docs/shape/MSSQL-SUPPORT-PLAN.md) — **read §2 (the
MySQL-fallthrough trap) and §3 (dialect differences) before starting.** Do not edit files
under `dotnet/`.

Driver token: PDO driver name **`sqlsrv`** (via `pdo_sqlsrv`).

## Phase 1 — connect, quote, dialect
- [x] **P1.0** ⚠️ **DO FIRST** — `ShapeApp::detectDriver` (`packages/shape/.../ShapeApp.php:1020`): add `if (str_starts_with($dsn, 'sqlsrv:')) return 'sqlsrv';`. This *build-time* detection runs before any connection; without it every generator emits backtick SQL for a `sqlsrv:` target and nothing else works.
- [x] **P1.1** `Database::quoteIdentifier` — now delegates to `SqlDialect::quoteIdentifier`, which has the `'sqlsrv' => '[' . str_replace(']', ']]', $name) . ']'` arm.
- [x] **P1.2** Confirmed — `getDriver()` returns PDO's `ATTR_DRIVER_NAME` verbatim (`'sqlsrv'` for pdo_sqlsrv). No code change; doc comment updated to list `sqlsrv`.
- [x] **P1.3** Added `ShapeRuntime\Core\SqlDialect` (static `quoteIdentifier()` + `paginate()`). Consumed by Database, BatchExecutor, MaskGenerator, AdminMaskGenerator, ShapeApp audit closure. `TOP 1`/`TOP N` sites are few and shape-specific → kept as inline `sqlsrv` arms rather than a `topOne()` (placement differs per call site).

## Phase 2 — introspection (`packages/shape/.../Metadata/DatabaseIntrospector.php`)
Add an explicit `if ($this->driver === 'sqlsrv')` arm **before** the MySQL fallback in each. Use `TABLE_SCHEMA = SCHEMA_NAME()` (dbo), keep bulk `IN (:tables)` form (MSSQL supports it — no per-table fallback).
- [x] **P2.1** `getTables` arm — `INFORMATION_SCHEMA.TABLES … TABLE_SCHEMA = SCHEMA_NAME()`. *(needs live-server verification)*
- [x] **P2.2** `getMssqlColumns` + `bulkColumns` sqlsrv arm; shared `mapMssqlColumn()` row mapper (bulk strips the leading TABLE_NAME via `array_slice`). *(needs live-server verification)*
- [x] **P2.3** `getPrimaryKeys` + `bulkPrimaryKeys` sqlsrv arms (TABLE_CONSTRAINTS ⋈ KEY_COLUMN_USAGE, `CONSTRAINT_TYPE = 'PRIMARY KEY'`). *(needs live-server verification)*
- [x] **P2.4** `getForeignKeys` + `bulkForeignKeys` sqlsrv arms (mirror the pgsql tc⋈kcu⋈ccu join). *(needs live-server verification)*
- [x] **P2.5** Identity via `COLUMNPROPERTY(OBJECT_ID(QUOTENAME(schema)+'.'+QUOTENAME(table)), col, 'IsIdentity') = 1`. *(needs live-server verification)*
- [~] **P2.6** N/A for PHP — the introspector never surfaces column/table descriptions for *any* driver (MySQL/PG don't read COLUMN_COMMENT/col_description, and `FieldMeta` has no description field). Adding an `sys.extended_properties` query would be dead code with no consumer. Deferred until a description field exists; belongs to the .NET side per plan §3.5.
- [x] **P2.7** Added `normalizeMssqlType()` (called by both per-table and bulk readers): `nvarchar`/`varchar`→varchar, `nchar`/`char`→char, `bit`→boolean, int family→int, `decimal`/`numeric`/`money`→decimal, `float`/`real`→double, datetime family→timestamp, `uniqueidentifier`→varchar. `dbTypeToPhp` already maps all resulting buckets (boolean→bool, decimal/double→float, rest→string). `nvarchar(max)` CHARACTER_MAXIMUM_LENGTH = -1 nulled in the mapper.

## Phase 3 — pagination & insert-id
- [x] **P3.1** `BatchExecutor::planPagination` now calls `SqlDialect::paginate($driver, …)` — SQL Server branch emits `… ORDER BY (SELECT NULL) OFFSET {skip} ROWS [FETCH NEXT {top} ROWS ONLY]` (FETCH omitted when skip-only). Unit-tested (P5.1).
- [x] **P3.2** `Database::insertReturningId` sqlsrv branch injects `OUTPUT INSERTED.<id>` before `VALUES (` (regex) and reuses the pgsql fetch path. *(needs live-server verification)*

## Phase 4 — auth, masks, types
- [x] **P4.1** `LoginService::refresh` + `findUserByEmail` — `sqlsrv` branch uses `SELECT TOP 1 …`, and `FOR UPDATE` → `WITH (UPDLOCK, ROWLOCK)` table hint; revocation check `TOP 1`. *(needs live-server verification)*
- [x] **P4.2** `LoginService::insertReturningId` — `sqlsrv` injects `OUTPUT INSERTED.id` before VALUES, reads it back. *(needs live-server verification)*
- [x] **P4.3** `OidcVerifier` lookup — leading `TOP 1` for `sqlsrv`, trailing `LIMIT 1` dropped. *(needs live-server verification)*
- [x] **P4.4** `LoginService::createUser` catch — also treats driver-code **2627 / 2601** (via `PDOException::$errorInfo[1]`) as `AUTH_USER_EXISTS`, alongside SQLSTATE 23000. *(needs live-server verification)*
- [x] **P4.5** `MaskGenerator::q` → `SqlDialect::quoteIdentifier`; writable-mask placeholder `LIMIT 1` → `SELECT TOP 1 …` on `sqlsrv`.
- [x] **P4.5b** `AdminMaskGenerator::q` → `SqlDialect::quoteIdentifier` (4th quoting copy consolidated).
- [x] **P4.6** `ShapeApp::buildAuditLogMasks` — closure delegates to `SqlDialect::quoteIdentifier`; `LIMIT 200` → `SELECT TOP 200 …` on `sqlsrv` (ORDER BY already present).
- [x] **P4.7** `AutoValidators` — added `nvarchar`/`nchar`/`ntext` to STRING_TYPES (defensive; introspector normalizes to varchar anyway). `CHARACTER_MAXIMUM_LENGTH = -1` already treated as unbounded by the `$length > 0` guard *and* nulled in `mapMssqlColumn`.

## Phase 5 — tests & docs
- [x] **P5.1** `tests/SqlDialect.phpt` (nette/tester) covers bracket quoting (+ `]` escaping) and all pagination shapes (sqlsrv top+skip / skip-only / top-only, plus mysql/pgsql). Runs without a DB — `vendor/bin/tester tests/SqlDialect.phpt` → OK.
- [~] **P5.2** `phpstan` (level 6) and `ecs` are **green**. `rector-dry` reports 42 files — **pre-existing**: confirmed identical count with my changes stashed (whole-tree `ReadOnlyClassRector`/`LocallyCalledStaticMethodToNonStaticRector`/`CombineIfRector` backlog on untouched files like `Attribute/*`, Http handlers). My changes are rector-neutral; new `SqlDialect.php` is rector-clean. No `phpstan-baseline.neon` change.
- [x] **P5.3** `docs/shape/SPEC.md` §2 lists `pdo_sqlsrv`; `packages/shaperuntime/composer.json` gains `suggest: { ext-pdo_sqlsrv }`.

## Verification note
Steps touching **live introspection / CRUD** cannot be fully verified without a running SQL
Server (no in-memory analogue). Write the code correctly, get `make check-quick` green, and
mark any live-only step as `[x] code complete — needs live-server verification`.

## Log
- 2026-07-03 P1.0 detectDriver `sqlsrv:` arm (build-time sniff) done — gates everything downstream.
- 2026-07-03 P1.3 added `ShapeRuntime\Core\SqlDialect` (quoteIdentifier + paginate); consolidated all 4 quoting copies + the pagination wrapper.
- 2026-07-03 P1.1/P1.2 Database quoteIdentifier delegates to SqlDialect; getDriver confirmed (doc updated).
- 2026-07-03 P3.2 Database::insertReturningId sqlsrv OUTPUT INSERTED.<id> branch done.
- 2026-07-03 P3.1 BatchExecutor::planPagination now driver-aware via SqlDialect::paginate.
- 2026-07-03 P2.1–P2.5/P2.7 DatabaseIntrospector sqlsrv arms (tables/columns/PK/FK bulk+per-table, COLUMNPROPERTY identity, normalizeMssqlType). P2.6 descriptions N/A for PHP (no consumer).
- 2026-07-03 P4.1–P4.4 LoginService/OidcVerifier: TOP 1, UPDLOCK/ROWLOCK hint, OUTPUT insert-id, 2627/2601 unique-violation.
- 2026-07-03 P4.5/P4.5b/P4.6 MaskGenerator/AdminMaskGenerator/ShapeApp audit — bracket quoting via SqlDialect + TOP N.
- 2026-07-03 P4.7 AutoValidators recognizes nvarchar/nchar/ntext; nvarchar(max) -1 stays unbounded.
- 2026-07-03 P5.1 tests/SqlDialect.phpt green. P5.3 SPEC §2 + composer suggest ext-pdo_sqlsrv.
- 2026-07-03 P5.2 phpstan+ecs green; rector-dry red is pre-existing (verified via stash), unrelated to MSSQL work.
