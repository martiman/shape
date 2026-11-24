# Manual primary-key assignment (non-IDENTITY int PKs) — implementation plan

> **Status (2026-07-04): IMPLEMENTED in both stacks.** Trackers: [php/MANUAL-PK-CHECKLIST.md](../../php/MANUAL-PK-CHECKLIST.md) (all `[x]`, H3.4 `#[GeneratedId]` deferred — the auto rule already covers entity-first) and [dotnet/MANUAL-PK-CHECKLIST.md](../../dotnet/MANUAL-PK-CHECKLIST.md) (all `[x]`). PHP: tester + phpstan L6 + ecs green. .NET: 426/426 tests, v0.6.2. Notable deviations from this plan: MySQL/SQLite read the allocated id via a **locked `MAX+1` read + explicit INSERT in one transaction** (last-insert-id mechanisms don't track non-identity columns — §2.1's read-back sketch was refined); the batch response returns the id under the pre-existing `id` key in both runtimes; auth tables allocate only on **explicit** `manual` (no introspection on the per-request path). The MSSQL concurrency guarantee (range lock + 2627/1205 retry) is code-complete with env-gated live tests (`[Trait("db","mssql")]`, `SHAPE_MSSQL_DSN`) — **live-server verification pending**, same protocol as [MSSQL-SUPPORT-PLAN.md](MSSQL-SUPPORT-PLAN.md).

Applies to both stacks: **PHP** (`php/packages/shape` + `php/packages/shaperuntime`) and
**.NET** (`dotnet/`). Companion to [MSSQL-SUPPORT-PLAN.md](MSSQL-SUPPORT-PLAN.md) — that plan
made the `sqlsrv` driver work; this one handles legacy MSSQL schemas where the int PK is
**not** `IDENTITY` and the framework must allocate `MAX(pk) + 1` itself on insert.

## 1. Problem

Legacy SQL Server databases (typical migration source) often have plain `int` primary keys
with no `IDENTITY`, no default, no sequence — the original application computed the next id
in code. Today Shape breaks on such tables twice:

1. **Insert fails or returns 0** — `insertReturningId` emits `OUTPUT INSERTED.id` /
   `SCOPE_IDENTITY()`-style plumbing, but nothing assigns the id, so the insert violates
   the NOT NULL PK (or inserts NULL).
2. **AutoValidators mark the PK as required** — `required = !nullable && !auto_increment
   && !has_default` (`AutoValidators.php:42`, mirrored in .NET), so the generated contract
   forces API clients to invent ids themselves. That is the wrong contract: id assignment
   is the server's job.

Goal: the framework detects (or is told) that a table needs manual id allocation, computes
`MAX(pk)+1` **atomically** on insert, and externally the PK keeps behaving like a
generated column (client never sends it, response returns `new_id`).

## 2. Design

### 2.1 Allocation strategy: single-statement locked `MAX+1` (recommended)

On MSSQL the allocation and the insert collapse into **one atomic statement** — no separate
`SELECT MAX` round trip, no window for a racing writer between read and insert:

```sql
INSERT INTO [t] ([id], [col1], [col2])
OUTPUT INSERTED.[id]
SELECT ISNULL(MAX([id]), 0) + 1, :col1, :col2
FROM [t] WITH (UPDLOCK, HOLDLOCK);
```

Why this shape:
- `WITH (UPDLOCK, HOLDLOCK)` takes a key-range lock at the top of the PK index; a second
  concurrent allocator blocks until the first commits — duplicates are impossible among
  framework writers. The `MAX` seek on the PK index is O(1), so the lock is cheap.
- `ISNULL(MAX,0)+1` handles the empty table (starts at 1).
- `OUTPUT INSERTED.[id]` returns the allocated id in the same round trip — plugs straight
  into the existing `insertReturningId` fetch path (`PDO::FETCH_NUM` / Dapper
  `ExecuteScalar`).
- **No DDL on the source database.** Rejected alternatives: SQL Server `SEQUENCE` object or
  a counter table both require schema changes to a database we usually don't own, and a
  counter drifts when the legacy app keeps inserting with its own `MAX+1` in parallel.
  `MAX+1` under a range lock is drift-proof by construction.

Safety net for **external** concurrent writers (the legacy app inserting alongside Shape,
not seeing our lock discipline): catch unique-violation (**2627/2601** on MSSQL — the
detection added by the MSSQL plan) and **retry the whole allocate+insert up to 3×**. Also
retry on deadlock victim (**1205**).

Other drivers get the same generic shape (the feature is driver-agnostic — a MySQL table
without `AUTO_INCREMENT` has the identical problem):

| Driver | Source clause | Concurrency guard |
|---|---|---|
| sqlsrv | `SELECT ISNULL(MAX(q),0)+1, … FROM t WITH (UPDLOCK, HOLDLOCK)` | range lock + retry 2627/2601/1205 |
| mysql | `SELECT COALESCE(MAX(q),0)+1, … FROM t` (InnoDB locks scanned index records in INSERT…SELECT) | + retry on 1062 / SQLSTATE 23000 |
| pgsql | `SELECT COALESCE(MAX(q),0)+1, … FROM t` + `RETURNING q` | `pg_advisory_xact_lock(hashtext('shape_pk:t'))` first, or plain retry on 23505 |
| sqlite | `SELECT COALESCE(MAX(q),0)+1, … FROM t` | single-writer by design — nothing needed |

MSSQL is the first-class target; the others ship with the generic form + retry so the
feature (and its tests) run everywhere, including the SQLite-backed CI suites.

### 2.2 Configuration: three modes, table-level granularity

New config key (name kept identical across stacks, snake_case in PHP config / manifest
JSON, PascalCase property in .NET):

```php
// PHP shape config
'pk_assignment' => 'auto',                          // whole-database default
// or per table, '*' = database default:
'pk_assignment' => ['*' => 'auto', 'invoices' => 'manual', 'audit_log' => 'db'],
```

```csharp
// .NET ShapeConfig
public Dictionary<string, string>? PkAssignment { get; set; }   // {"*": "auto", "invoices": "manual"}
```

Modes:
- **`auto`** (default) — recognize from schema: single-column PK, **int family**,
  `auto_increment == false`, `has_default == false` → `manual`; otherwise `db`. The
  introspectors already expose every input needed (`auto_increment` via
  `COLUMNPROPERTY(...,'IsIdentity')` on MSSQL, `has_default` via `COLUMN_DEFAULT` —
  a PK defaulted to `NEXT VALUE FOR seq` or `NEWID()` is correctly left to the DB).
- **`manual`** — force framework allocation (e.g. detection is fooled, or the identity
  column exists but is abandoned). Build-time **error** if the table's PK is composite or
  non-int — fail loud, never guess.
- **`db`** — trust the database (identity / trigger / default / sequence assigns). Opt-out
  for tables where `MAX+1` is semantically wrong (business codes, externally allocated
  ranges).

### 2.3 Carrier: the writable mask

The decision is made once at **build time** (generator side, where config + introspection
meet) and carried to the runtime in the writable mask — exactly like `soft_delete`,
`audit`, and `writable_mode` already are:

```jsonc
"invoices": {
  "writable": true,
  "pk": "invoice_id",          // PK column name — emitted for EVERY writable mask (see 2.5)
  "pk_strategy": "manual",     // only emitted when manual; absent = db-assigned
  ...
}
```

PHP: two new optional keys on the mask array. .NET: `MaskConfig.Pk` / `MaskConfig.PkStrategy`
(+ the `Mask` compiled copy in `MaskRegistry`). Manifest format change documented in SPEC.md
so both runtimes read either stack's manifest.

### 2.4 External contract stays "generated"

- **AutoValidators**: a manual-strategy PK is treated exactly like an auto-increment
  column — **not required** on insert. (Both stacks: extend the `required` predicate's
  inputs with the strategy, or flip the column's `auto_increment` flag to `generated`
  semantics before validator generation.)
- **ManifestGenerator / OpenApiGenerator / McpServer**: keep reporting the PK as
  auto-assigned/readonly (they key off `isAutoIncrement` today — feed them a unified
  `isGenerated = isAutoIncrement || pkStrategy === 'manual'`).
- **Client-supplied id**: if the insert `data` already contains the PK, **use it verbatim**
  (pass-through to the current insert path) — supports data migration/import flows.
  Allocation happens only when the PK is absent from `data`.
- **Response**: `new_id` is populated either way — the audit logger's `row_id` keeps
  working unchanged.

### 2.5 Bonus fix folded in: `insertReturningId` hardcodes `id`

`BatchExecutor::performInsert` (PHP `:534`, .NET `:618`) calls `insertReturningId`
**without an id column**, so PG `RETURNING id` / MSSQL `OUTPUT INSERTED.id` silently
assume the PK is named `id`. A table with `invoice_id` breaks today on those drivers.
Since the writable mask now carries `pk` for every table, thread it through:
`insertReturningId($sql, $params, $mask['pk'] ?? 'id')`. Free correctness fix, both stacks.

## 3. Runtime flow (`BatchExecutor::performInsert`, both stacks)

```
if mask.pk_strategy == 'manual' AND data[mask.pk] not set:
    attempt (max 3):
        build INSERT … OUTPUT INSERTED.pk SELECT <max+1 source>   ← SqlDialect helper
        execute; return allocated id
    on unique-violation / deadlock: retry; after 3rd failure → DB_ERROR (typed detail 'PK_ALLOCATION_RACE')
else:
    current path (insertReturningId with mask.pk as id column)
```

Notes:
- The nested-create path (`relations`) inserts children through the same `performInsert` —
  it inherits the behavior for free, but the child-table masks must also carry `pk`/
  `pk_strategy` (they do: any writable child has its own writable mask).
- Batches already run inside one transaction; the MSSQL range lock is held until commit.
  That serializes concurrent inserts into the same manual-PK table for the duration of a
  batch — correct, and acceptable for admin/backoffice workloads this targets. Document it.
- Auth tables: `LoginService` (users, refresh_tokens inserts at `:145`/`:264` PHP,
  `:142`/`:226` .NET) and `OidcVerifier` use their own `insertReturningId`. When the auth
  tables live in the same non-identity legacy DB, they need the same treatment — resolve
  their strategy from the same `pk_assignment` map (tables `users`, `refresh_tokens`) and
  route through the shared allocation helper. Phase 2 if auth-on-legacy-MSSQL isn't needed
  immediately, but wire the config lookup now so it's one code path.

## 4. PHP work items

- [ ] **H1.1** `SqlDialect::insertAllocatingPk(string $driver, string $table, string $pk, array $columns): string`
  — builds the full INSERT…SELECT statement per §2.1 (bracket/backtick/double-quote via
  existing `quoteIdentifier`; `OUTPUT INSERTED` for sqlsrv, `RETURNING` for pgsql, plain +
  `lastInsertId()`-free fetch for mysql/sqlite — the statement itself returns/implies the id).
- [ ] **H1.2** `Database::insertWithManualPk(string $table, string $pk, array $data): int` —
  executes H1.1 with retry loop (3×) on unique-violation (SQLSTATE 23000/23505, driver codes
  2627/2601/1062) and MSSQL deadlock 1205. pgsql: take `pg_advisory_xact_lock` first.
- [ ] **H2.1** `ShapeApp::loadConfig` — normalize `pk_assignment` (string shorthand →
  `['*' => …]`); validate values (`auto|db|manual`).
- [ ] **H2.2** Strategy resolver (generator side, where introspected columns + config meet —
  `ShapeApp::fromDatabase` before `MaskGenerator`): implement §2.2 auto rule; hard error on
  `manual` + composite/non-int PK. Int family for detection: `int, bigint, smallint, tinyint,
  decimal(p,0)` (MSSQL legacy schemas often use `numeric(9,0)` PKs — include scale-0 decimals).
- [ ] **H2.3** `MaskGenerator` writable mask: always emit `'pk' => $pk->name`; emit
  `'pk_strategy' => 'manual'` when resolved manual. Same in `AdminMaskGenerator` if its
  masks are writable (verify — likely read-only, then skip).
- [ ] **H3.1** `BatchExecutor::performInsert` — §3 flow; pass `$mask['pk']` to
  `insertReturningId` on the db path (fixes §2.5).
- [ ] **H3.2** `AutoValidators` — strategy-aware `required` (manual PK ⇒ not required).
- [ ] **H3.3** `ManifestGenerator`/`OpenApiGenerator`/`McpServer` — `isGenerated` unification
  (§2.4) so manual PKs render as readonly/auto.
- [ ] **H3.4** Code-first parity: new attribute `#[GeneratedId]` (or param on existing
  `#[AutoIncrement]`-adjacent metadata) in `EntityReader` for entity-defined apps targeting
  legacy tables. Low priority — DB-first is the migration path.
- [ ] **H4.1** `LoginService`/`OidcVerifier` — resolve strategy for `users`/`refresh_tokens`
  from config; route inserts through H1.2 when manual.
- [ ] **H5.1** Tests: `tests/SqlDialect.phpt` — statement shape per driver (brackets, ISNULL
  vs COALESCE, OUTPUT/RETURNING placement, empty-columns edge). **SQLite integration test**:
  force `pk_assignment: manual` on an in-memory table → sequential ids 1,2,3, explicit-id
  pass-through, `new_id` in batch response, validator no longer requires id. Runs in CI
  without MSSQL.
- [ ] **H5.2** `phpstan`/`ecs` green; SPEC.md §config + §manifest-mask-keys; MSSQL live-server
  verification note (same caveat protocol as the MSSQL plan).

## 5. .NET work items

- [ ] **N1.1** `Database` — static SQL builder mirroring H1.1 (next to the existing
  `MssqlInsertReturningIdBatch` helper, `Database.cs:202`) + `InsertWithManualPk` with the
  retry loop (`SqlException.Number` 2627/2601/1205; MySql 1062; Npgsql 23505).
- [ ] **N2.1** `ShapeConfig.PkAssignment` (`Dictionary<string,string>?`, `"*"` default key;
  also accept plain string in `ConfigLoader` for the shorthand). Validate values.
- [ ] **N2.2** Strategy resolver in `ShapeApp.FromDatabase` (same auto rule + hard errors,
  incl. scale-0 decimal PKs).
- [ ] **N2.3** `MaskConfig.Pk` / `MaskConfig.PkStrategy` (+ `Mask` copy + the compile mapping
  in `MaskRegistry` ~`:922`); `MaskGenerator.cs` emits them per H2.3.
- [ ] **N3.1** `BatchExecutor.PerformInsert` (~`:618`) — §3 flow; pass `mask.Pk` to
  `InsertReturningId` on the db path (§2.5 fix).
- [ ] **N3.2** `AutoValidators` equivalent — manual PK not required.
- [ ] **N3.3** `ManifestGenerator`/`OpenApiGenerator` — `IsGenerated` unification; keep
  manifest JSON keys byte-identical to PHP (`pk`, `pk_strategy`) for cross-runtime parity.
- [ ] **N3.4** Code-first parity: `[GeneratedId]` attribute (mirrors H3.4).
- [ ] **N4.1** `LoginService`/`OidcVerifier` — same as H4.1.
- [ ] **N5.1** Tests: unit tests for statement shape per driver (extend
  `MssqlDialectGeneratorTests`); SQLite-backed integration test mirroring H5.1; mark the
  MSSQL concurrency path `[Trait("db","mssql")]` for the future live suite.
- [ ] **N5.2** `dotnet test` green; README config docs; version bump.

## 6. Testing strategy

- **CI-runnable (no MSSQL):** SQLite in-memory with forced `manual` strategy exercises the
  entire pipeline — config → mask emission → allocation → retry plumbing → `new_id` →
  validators. Statement-shape unit tests cover the sqlsrv/mysql/pgsql SQL text.
- **Live MSSQL (pending, same gap as MSSQL-SUPPORT-PLAN §6):** the concurrency guarantee
  (UPDLOCK/HOLDLOCK range lock) and the 2627-retry path need a real server — two parallel
  transactions inserting into the same manual-PK table must produce distinct sequential ids.
  Add to the planned `[Trait("db","mssql")]` / Docker suite.

## 7. Risks & edge cases

| Risk | Mitigation |
|---|---|
| Legacy app inserts concurrently with its own `MAX+1` (doesn't honor our lock) | retry-on-unique-violation (3×); document residual race is identical to what the legacy app already had |
| Deadlocks under mixed read/write load (HOLDLOCK range vs other locks) | retry on 1205; lock is a narrow top-of-index range, short-lived outside batches |
| Long batch transactions serialize all inserts into that table | document; targets backoffice workloads; batch size already bounded |
| `OUTPUT` clause fails on tables with triggers | pre-existing caveat of the MSSQL insert path (same as identity tables); document `OUTPUT … INTO` as the escape hatch if it ever bites |
| PK approaching `int` max / negative seed values | `MAX+1` inherits whatever the data has; no special handling — same as the legacy app |
| Auto-detection false positive (int PK deliberately client-assigned, e.g. business code) | per-table `'db'` override; explicit-id pass-through means even a false positive only *adds* a fallback, never blocks a supplied id |
| Composite / non-int PK forced `manual` | build-time error, never silent |

## 8. Suggested phasing

1. **Phase A (core, ~1 day/stack):** H1/N1 dialect+retry, H2/N2 config+detection+mask keys,
   H3.1/N3.1 executor flow + §2.5 fix, SQLite tests.
2. **Phase B (contract, ~½ day/stack):** validators + manifest/OpenAPI/MCP generated-flag,
   docs, SPEC.md.
3. **Phase C (auth + live-verify):** LoginService/OidcVerifier wiring; MSSQL Docker
   concurrency test (rides on the pending MSSQL live-verification work).

Like the MSSQL driver work, this splits cleanly into two agents (one per stack) with
per-stack checklists cloned from §4/§5; the dialect rules in §2 stay single-sourced here.
