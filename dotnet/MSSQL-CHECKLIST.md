# MSSQL support — .NET implementation checklist

Owned by the **.NET agent**. Mark each box `[x]` as you complete it and add a one-line note.
Shared dialect rules & rationale live in
[../docs/shape/MSSQL-SUPPORT-PLAN.md](../docs/shape/MSSQL-SUPPORT-PLAN.md) — **read §2 (the
silent-empty-result trap) and §3 (dialect differences) before starting.** Do not edit files
under `php/`.

Driver token: `DbDriver.Mssql`; provider `Microsoft.Data.SqlClient`. Target `net10.0`.
Every dispatcher has a `_ =>` fallback that returns empty/neutral — an unrecognized driver
fails **silently**, so every arm below must be added explicitly.

## Phase 1 — connect, quote, DSN
- [x] **N1.1** `Shape.csproj` — added `<PackageReference Include="Microsoft.Data.SqlClient" Version="6.1.3" />`; Description/PackageTags/ReleaseNotes updated for SQL Server.
- [x] **N1.2** `Core/Database.cs` — added `Mssql` to `DbDriver`.
- [x] **N1.3** `Core/Database.cs` `QuoteIdentifier` — `DbDriver.Mssql => "[" + name.Replace("]", "]]") + "]"`.
- [x] **N1.4** `Core/Database.cs` `ParseDriver` — `"sqlsrv" or "mssql" or "sqlserver" => DbDriver.Mssql`.
- [x] **N1.5** `Core/Database.cs` `CreateConnection` — `DbDriver.Mssql => new SqlConnection(cs)` (+ `using Microsoft.Data.SqlClient`).
- [x] **N1.6** `Core/DsnTranslator.cs` — prefix arm + new `BuildMssql`: `Server=host,port` (comma), `Database=`, `User Id=`/`Password=`; passes through `Encrypt`/`TrustServerCertificate`, defaults `TrustServerCertificate=true` (documented in-code) so dev DSNs connect.

## Phase 2 — introspection (`Metadata/DatabaseIntrospector.cs`)
- [x] **N2.1** `ListTables` arm — `information_schema.TABLES … TABLE_SCHEMA = SCHEMA_NAME() AND TABLE_TYPE='BASE TABLE'`. _(code complete — needs live-server verification)_
- [x] **N2.2** `DescribeColumnsMssql` wired into switch — identity via `COLUMNPROPERTY(...,'IsIdentity')`, PK via information_schema, descriptions via `sys.extended_properties`, `char_len = -1` → unbounded. _(code complete — needs live-server verification)_
- [x] **N2.3** `DescribeForeignKeysMssql`. _(code complete — needs live-server verification)_
- [x] **N2.4** `DescribeTableCommentMssql` — `sys.extended_properties` `MS_Description` (minor_id=0). _(code complete — needs live-server verification)_
- [x] **N2.5** `DescribeUniqueConstraintsMssql` — `table_constraints … constraint_type='UNIQUE'`. _(code complete — needs live-server verification)_
- [x] **N2.6** `ClrTypeFor` — `bit`→`bool`, `nvarchar`/`nchar`→`string`, `uniqueidentifier`→`Guid`, `datetime2`→`DateTime`, `datetimeoffset`→`DateTimeOffset`; `char_len = -1` handled in `DescribeColumnsMssql`.

## Phase 3 — pagination & insert-id
- [x] **N3.1** `Core/BatchExecutor.cs` `PlanPagination` — added `DbDriver.Mssql` branch: `... ORDER BY (SELECT NULL) OFFSET {skip} ROWS [FETCH NEXT {top} ROWS ONLY]` (FETCH omitted when only skip). FK-existence `LIMIT 1` → driver-aware `TOP 1` via `TopClause`/`RowLimitSuffix`.
- [x] **N3.2** `Core/Database.cs` `InsertReturningId` — `DbDriver.Mssql` arm: INSERT and `SELECT CAST(SCOPE_IDENTITY() AS bigint)` fused into **one batch** (`MssqlInsertReturningIdBatch`) — SCOPE_IDENTITY() is batch-scoped and returns NULL from a separate command; trigger-safe unlike `@@IDENTITY`. _(code complete — needs live-server verification)_

## Phase 4 — errors & auth
- [x] **N4.1** `Core/Database.cs` `DbError` — added `SqlException ms => ms.Number.ToString()` arm.
- [x] **N4.2** `Auth/LoginService.cs` — both `LIMIT 1` sites → driver-aware `TOP 1`; `LooksLikeUniqueViolation` accepts codes **2627 / 2601**.

## Phase 5 — tests & docs
- [x] **N5.1** `DatabaseQuotingTests.cs` — added `Mssql_uses_brackets` + embedded-bracket + TOP/LIMIT facts.
- [x] **N5.2** `DsnTranslatorTests` (comma-port, PDO keys, aliases, encrypt/trust) + `DatabaseConfigTests` (mssql/sqlsrv/sqlserver parse-acceptance) cases added.
- [x] **N5.3** `dotnet build` + `dotnet test` green — **371 passed / 0 failed** (was ~362, +9 new).
- [x] **N5.4** Docs: README driver list + DSN-prefix section updated; SQL Server noted in `Shape.csproj` Description/Tags/ReleaseNotes (v0.6.1).

## Verification note
Full **introspection / CRUD** verification needs a real SQL Server (no in-memory analogue to
SQLite `:memory:`). Write correct code, keep `dotnet build` + `dotnet test` green, and mark
any live-only step `[x] code complete — needs live-server verification`. If you add a
container-gated integration test, put it behind `[Trait("db","mssql")]` so default
`dotnet test` stays green without Docker.

## Log
- 2026-07-03 — Phase 1: added `DbDriver.Mssql`, bracket quoting, `sqlsrv`/`mssql`/`sqlserver` parse + DSN arms, `SqlConnection` factory, `Microsoft.Data.SqlClient` 6.1.3 (net10.0). Added `TopClause`/`RowLimitSuffix` dialect helpers on `Database` (lightweight §3.8 consolidation for the TOP/LIMIT decision).
- 2026-07-03 — Phase 2: introspection MSSQL arms (ListTables, columns w/ identity+MS_Description, FKs, table comment, unique constraints) + `ClrTypeFor` MSSQL types + `nvarchar(max)` -1 handling. Code complete; needs live SQL Server to verify.
- 2026-07-03 — Phase 3: `PlanPagination` OFFSET/FETCH branch with mandatory `ORDER BY (SELECT NULL)` shim; FK-check `LIMIT 1` → driver-aware TOP; `InsertReturningId` SCOPE_IDENTITY arm.
- 2026-07-03 — Phase 4: `DbError` `SqlException` arm (error Number); `LooksLikeUniqueViolation` accepts 2627/2601.
- 2026-07-03 — Phase 5: added quoting + DSN + parse-acceptance unit tests; `dotnet test tests/Shape.Tests` → 371 passed / 0 failed. README + csproj docs updated. No live SQL Server available, so introspection/CRUD/insert-id remain code-complete pending live verification; no container integration test added (would need Docker — default `dotnet test` stays green).
- 2026-07-03 — Code review found 5 critical sites the plan's catalog missed; all fixed same day: `InsertReturningId` MSSQL arm rewritten to a single batch (SCOPE_IDENTITY is batch-scoped — separate command returned NULL→0); `MaskGenerator.Quote` + `AdminMaskGenerator.Quote` gained bracket arms (were falling through to backticks, accepting `mssql`/`sqlsrv`/`sqlserver` case-insensitively); writable-mask `LIMIT 1` → `TOP 1`; `OidcVerifier` user lookup `LIMIT 1` → `TopClause`/`RowLimitSuffix`. +9 pinning tests in `MssqlDialectGeneratorTests.cs` → 380 passed / 0 failed.
- 2026-07-03 — Added the container-gated live integration suite the Verification note asks for: `MssqlLiveIntegrationTests.cs` (`[Trait("db","mssql")]`, activated by `SHAPE_MSSQL_DSN`/`_USER`/`_PASSWORD`, no-op otherwise so default `dotnet test` stays green — 399 passed / 0 failed). Covers the plan's §6 shared fixture end-to-end: identity+PK+FK+composite-UNIQUE+bit+nvarchar(100)+nvarchar(max)+MS_Description introspection, live SCOPE_IDENTITY insert-id (incl. in-transaction), OFFSET/FETCH paging with and without mask-level ORDER BY, and 2627/2601 unique-violation codes. Docker one-liner in the file header — running it against `mcr.microsoft.com/mssql/server:2022` is all that's left to flip the `[x] code complete` boxes to verified.
- 2026-07-03 — Review's Moderate + Minor items all fixed too: `AutoValidators` accepts `nvarchar`/`nchar`/`ntext`; pagination extracted to `BuildMssqlPagination` with quote-aware `HasTopLevelOrderBy` (mask-level ORDER BY now drives OFFSET/FETCH directly instead of a wrapper MSSQL would reject); MSSQL introspection catalog references uppercased to `INFORMATION_SCHEMA.…` (case-sensitive server collations); DSN `TrustServerCertificate=true` default narrowed to local hosts only, PDO `1/0/yes/no` booleans normalized, extra SqlClient options passed through (credential keys excluded); generators accept `postgres`/`postgresql` aliases; csproj `<Version>` bumped to 0.6.1. +15 tests → **395 passed / 0 failed**. Remaining: live-server verification only.
