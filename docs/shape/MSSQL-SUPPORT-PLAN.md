# Adding Microsoft SQL Server (MSSQL) support to Shape

A concrete, file-referenced plan to add SQL Server as a fourth driver alongside
MySQL / PostgreSQL / SQLite, in **both** implementations (PHP reference + .NET port).
Written against the parity discipline in [../../STATUS.md](../../STATUS.md): every item
below is justified by the same test — *does it let an agent point Shape at an existing
SQL Server schema and get the same auto-CRUD + manifest it gets on the other three
engines, writing zero extra code?*

**Status:** planned. Nothing MSSQL-specific exists in the tree yet (verified: no
`sqlsrv` / `SqlClient` references anywhere).

---

## 1. Scope & driver identity

| | PHP | .NET |
|---|---|---|
| Client library | PDO `pdo_sqlsrv` (Microsoft) — driver name **`sqlsrv`** | `Microsoft.Data.SqlClient` |
| DSN prefix (shared config) | `sqlsrv:Server=…;Database=…` | same DSN, translated by `DsnTranslator` |
| Internal driver token | `'sqlsrv'` | `DbDriver.Mssql` |

Both stacks boot from the **same PDO-style DSN** so one `env.local.php` drives both.
We standardize on the PDO `sqlsrv` spelling as the canonical prefix; the .NET
`DsnTranslator` and `ParseDriver` should also accept `mssql` / `sqlserver` aliases.

> Note on `pdo_dblib`/FreeTDS: its PDO driver name is `dblib`, its quoting/identity
> behavior differs, and it is not Microsoft-supported. **Out of scope** — we target
> `pdo_sqlsrv` only and document that.

---

## 2. The two structural traps (read before touching code)

The existing three drivers are dispatched two different ways, and **each stack fails
differently and silently for an unrecognized driver**:

- **.NET** — every dispatcher is a `switch` with a `_ =>` arm that returns *empty/neutral*
  (`_ => new List<...>()`, `_ => name`) or throws. Add `DbDriver.Mssql` and every one of
  the ~11 switch sites still compiles — but introspection would return **zero tables**
  with no error. **Every arm must be added explicitly.**
- **PHP** — introspection uses `if sqlite {…} if pgsql {…} else /* MySQL */`. An unknown
  driver **falls through into the MySQL branch** and runs MySQL-only SQL
  (`TABLE_SCHEMA = DATABASE()`, `CONSTRAINT_NAME = 'PRIMARY'`, backtick `PRAGMA`) against
  SQL Server — producing wrong results or errors. **Every `else` needs an explicit
  `if ($this->driver === 'sqlsrv')` arm placed before the MySQL fallback.**

There is no silent-safe default. Completeness is the whole game here.

**PHP has a third, earlier trap: build-time driver detection.** `ShapeApp::detectDriver`
(`ShapeApp.php:1020`) sniffs the driver from the **DSN prefix string** *before any
connection* — `str_starts_with($dsn,'pgsql:')` / `'sqlite:'`, else `mysql`. A `sqlsrv:` DSN
falls through to `mysql`, so every generator (masks, admin masks, audit masks) emits
backtick SQL for SQL Server before the runtime driver (`PDO::ATTR_DRIVER_NAME`) is ever
read. **This must get a `sqlsrv:` arm first — it gates everything downstream.**

---

## 3. Cross-cutting dialect differences (the real work)

These are engine semantics, independent of language. Each maps to concrete edit sites in
§4 / §5.

### 3.1 Pagination — the single biggest gap
`LIMIT … OFFSET …` **does not exist in SQL Server.** Today pagination is hardcoded and
**not driver-branched at all**:
- PHP `BatchExecutor.php:861-866` → `SELECT * FROM (<sql>) AS __m LIMIT {top} OFFSET {skip}`
  (with sentinel `LIMIT 9223372036854775807` when only `skip` is set).
- .NET `BatchExecutor.cs:961-966` → identical construction.

MSSQL equivalent: `OFFSET {skip} ROWS FETCH NEXT {top} ROWS ONLY`, which **requires an
`ORDER BY`**. Since the wrapper has none, use the standard no-op ordering:
```sql
SELECT * FROM (<sql>) AS __m ORDER BY (SELECT NULL) OFFSET {skip} ROWS
  [FETCH NEXT {top} ROWS ONLY]
```
`FETCH NEXT` is omitted when only `skip` is set (MSSQL allows `OFFSET n ROWS` alone) —
this also avoids the ugly bigint sentinel. This is the one place that must gain a driver
branch where it currently has none.

### 3.2 Hardcoded `LIMIT`/`FOR UPDATE` outside the paginator
Grep-confirmed literal `LIMIT` sites that MSSQL rejects:
- PHP auth: `LoginService.php:93` (`LIMIT 1 … FOR UPDATE`), `:120`, `:427`;
  `OidcVerifier.php:466` (`LIMIT 1`); audit mask `ShapeApp.php:408` (`LIMIT 200`).
- .NET auth: `LoginService.cs:103,374`; FK-existence `BatchExecutor.cs:573` (`… = @v LIMIT 1`).

MSSQL forms: `SELECT TOP 1 …` (or `OFFSET 0 ROWS FETCH NEXT 1 ROWS ONLY`). `FOR UPDATE`
becomes the table hint `WITH (UPDLOCK, ROWLOCK)` after the table reference. These are
few and fixed — cleanest handled by a tiny dialect helper (§3.8), not string-patching.

### 3.3 Identifier quoting — brackets
MSSQL delimits with `[name]`, escaping `]` → `]]` (double-quote also works only when
`SET QUOTED_IDENTIFIER ON`; brackets are unconditionally safe → use brackets).
Quoting helpers that need an MSSQL arm:
- PHP — **four** duplicated copies: `Database::quoteIdentifier` (`Database.php:67`),
  `MaskGenerator::q` (`MaskGenerator.php:189`), `AdminMaskGenerator::q` (`AdminMaskGenerator.php:87`),
  and the closure in `ShapeApp::buildAuditLogMasks` (`ShapeApp.php:397`). *(Worth
  consolidating; see §3.8.)*
- .NET: `Database.QuoteIdentifier` (`Database.cs:191`); the SQLite-only `QuoteSqliteIdent`
  (`DatabaseIntrospector.cs:461`) is unaffected.

### 3.4 Insert → new id
MSSQL has neither `LAST_INSERT_ID()` nor `RETURNING`. Use `SELECT SCOPE_IDENTITY()`
(returns `numeric`, cast to int/long) run on the same connection/transaction, or append
`OUTPUT INSERTED.<idcol>` to the INSERT (preferred — single round trip, transaction-safe).
Sites:
- PHP: `Database::insertReturningId` (`Database.php:148`) and the private copy in
  `LoginService::insertReturningId` (`LoginService.php:492`).
- .NET: `Database.InsertReturningId` (`Database.cs:156`); callers `BatchExecutor.cs:618`,
  `LoginService.cs:142,226`, `OidcVerifier.cs:478`.

### 3.5 Schema introspection
MSSQL exposes ANSI `INFORMATION_SCHEMA` (`TABLES`, `COLUMNS`, `KEY_COLUMN_USAGE`,
`TABLE_CONSTRAINTS`, `CONSTRAINT_COLUMN_USAGE`) so the **PostgreSQL queries port almost
verbatim** — main change is default schema `dbo` (use `SCHEMA_NAME()` / `TABLE_SCHEMA = SCHEMA_NAME()`
rather than `'public'` / `DATABASE()`). Two things are *not* in information_schema:
- **Identity/auto-increment:** `COLUMNPROPERTY(OBJECT_ID(QUOTENAME(SCHEMA_NAME())+'.'+QUOTENAME(@t)), c.COLUMN_NAME, 'IsIdentity') = 1`,
  or join `sys.columns.is_identity`. (PG detects via `is_identity` / `nextval(`; the PHP
  pgsql reader at `DatabaseIntrospector.php:233` is the template.)
- **Column / table descriptions:** `sys.extended_properties` where `name='MS_Description'`
  (join `sys.columns` / `sys.tables`), the analogue of PG `col_description` /
  MySQL `COLUMN_COMMENT`. This is the one genuinely new query to author.

Per-driver introspection methods to add:
- PHP (`DatabaseIntrospector.php`): `getMssqlColumns` + its bulk form `bulkColumns` arm,
  `getPrimaryKeys`/`bulkPrimaryKeys`, `getForeignKeys`/`bulkForeignKeys`, plus the
  `getTables` arm (`:154`). Follow the pgsql arms as templates; MSSQL supports the same
  bulk `IN (:tables)` shape so it need **not** fall back to per-table like SQLite.
- .NET (`DatabaseIntrospector.cs`): `ListTables` arm (`:22`) + private `DescribeColumnsMssql`,
  `DescribeForeignKeysMssql`, `DescribeTableCommentMssql`, `DescribeUniqueConstraintsMssql`,
  wired into the 4 dispatch switches (`:43,:52,:132,:144`).

### 3.6 Type mapping
- PHP maps by lowercased `DATA_TYPE` string in the column readers + `AutoValidators`;
  MSSQL type names (`nvarchar`, `varchar`, `bit`, `int`, `bigint`, `decimal/numeric`,
  `datetime2`, `datetimeoffset`, `uniqueidentifier`, `varbinary`) must land on the same
  logical buckets used for validators (`maxLen` from `CHARACTER_MAXIMUM_LENGTH`, which
  MSSQL reports as **-1 for `nvarchar(max)`** → treat as "no bound").
- .NET `ClrTypeFor` (`DatabaseIntrospector.cs:480`) must recognize `bit`→`bool`,
  `nvarchar`/`nchar`→`string`, `uniqueidentifier`→`Guid`/`string`, `datetime2`→`DateTime`,
  else values fall back to string.

### 3.7 Booleans
No native boolean — MSSQL uses `bit` (0/1). PHP already normalizes bool→0/1 in
`Database::normalizeRows` (`Database.php:232`); `bit` comes back as int already, so output
stays identical. Introspection must map `bit` to the logical boolean type (§3.6).

### 3.8 `:param` placeholders — already MSSQL-friendly
The runtime binds `:name` via PDO and (in .NET) rewrites `:param`→`@param`. `@` is
SQL Server's native parameter sigil, so **no rewrite work is needed** — a rare freebie.

### Recommended consolidation
Quoting (3 PHP copies / 2 .NET), the `LIMIT`/`TOP` choice, and the OFFSET/FETCH wrapper
are the same three decisions repeated. Rather than sprinkle `match`/`switch` arms, extract
a minimal **`SqlDialect`** value (per driver) exposing `quoteIdentifier()`, `limit(top, skip)`
→ the correct clause, and `topOne()` for the `LIMIT 1` sites. This keeps the MSSQL arm in
one place per stack and makes the *next* engine (the STATUS.md roadmap lists TS/Python/Go
ports) cheap. It is optional but strongly recommended — it converts a scavenger hunt into
one file.

---

## 4. PHP work items (`php/`)

Runtime is PDO; add `ext-pdo_sqlsrv` to the target-driver note in
[SPEC.md](SPEC.md) §2 and `composer.json` `suggest`.

0. **`shape/ShapeApp.php` — `detectDriver` (`:1020`)** *(do this first — §2)* — add
   `if (str_starts_with($dsn, 'sqlsrv:')) return 'sqlsrv';`. Without it every generator
   emits backtick SQL for a SQL Server target.
1. **`shaperuntime/Core/Database.php`**
   - `quoteIdentifier` (`:67`) — add `'sqlsrv' => '[' . str_replace(']', ']]', $name) . ']'`.
   - `insertReturningId` (`:148`) — add `sqlsrv` branch: append `OUTPUT INSERTED.<id>` (or
     `SELECT SCOPE_IDENTITY()`); the existing pgsql `fetch` path can be reused for OUTPUT.
   - Confirm `getDriver()` (`:61`) returns `'sqlsrv'` from PDO (it will).
2. **`shaperuntime/Core/BatchExecutor.php`** — pagination `:858-867` becomes driver-aware
   (OFFSET/FETCH + `ORDER BY (SELECT NULL)`); FK-existence `LIMIT 1` (if present) → `TOP 1`.
3. **`shaperuntime/Auth/LoginService.php`** — `:93` (`LIMIT 1 … FOR UPDATE` → `TOP 1 … WITH (UPDLOCK,ROWLOCK)`),
   `:120`, `:427` (`LIMIT 1` → `TOP 1`), and `insertReturningId` (`:492`, SCOPE_IDENTITY).
   Also `LooksLike…`/unique-violation handling if present — MSSQL errors **2627 / 2601**.
4. **`shaperuntime/Auth/OidcVerifier.php`** — `:466` `LIMIT 1` → `TOP 1` (quoting already
   goes through `quoteIdentifier`, so brackets come free).
5. **`shape/Metadata/DatabaseIntrospector.php`** — `getTables` (`:154`) arm; new
   `getMssqlColumns` + `bulkColumns`/`bulkPrimaryKeys`/`bulkForeignKeys` sqlsrv arms
   (`:176,:408,:510,:563`) and `getPrimaryKeys`/`getForeignKeys` per-table arms
   (`:307,:344`); identity via `COLUMNPROPERTY`, descriptions via `sys.extended_properties`.
6. **`shape/Generator/MaskGenerator.php`** — `q()` (`:189`) bracket arm (constructor already
   receives `$driver`); hardcoded `LIMIT 1` (`:131`) → `TOP 1`.
7. **`shape/Generator/AdminMaskGenerator.php`** — `q()` (`:87`) bracket arm.
8. **`shape/ShapeApp.php`** — `buildAuditLogMasks` quoting closure (`:397`) bracket arm and
   `LIMIT 200` (`:408`) → `TOP 200` (ORDER BY already present, so OFFSET/FETCH also fine).
9. **`shape/Metadata/DatabaseIntrospector.php` type normalization** — the PG raw-type→logical
   `match` is duplicated at `:241` and `:440`; add MSSQL twins (`nvarchar`/`nchar`→string,
   `bit`→bool, `int`/`bigint`/`smallint`/`tinyint`→int, `decimal`/`numeric`/`float`/`real`,
   `datetime2`/`datetimeoffset`, `uniqueidentifier`). Also `dbTypeToPhp` (`:655`).
10. **`shape/Metadata/AutoValidators.php`** — accept MSSQL type names + `nvarchar(max)` = -1.

*(If the `SqlDialect` consolidation is taken, items 1/6/7 quoting + 2/3/4/7 limit collapse
into one dialect class consumed everywhere.)*

---

## 5. .NET work items (`dotnet/`)

Per the .NET catalog — 13 sites:

1. `Shape.csproj:37` — add `<PackageReference Include="Microsoft.Data.SqlClient" .../>`;
   update Description/Tags (`:15-17`) to include SQL Server.
2. `Core/Database.cs:11` — add `Mssql` to `DbDriver`.
3. `Core/Database.cs:160` `InsertReturningId` — `OUTPUT INSERTED`/`SCOPE_IDENTITY` arm.
4. `Core/Database.cs:191` `QuoteIdentifier` — bracket arm.
5. `Core/Database.cs:217` `ParseDriver` — `"sqlsrv" or "mssql" or "sqlserver" => Mssql`.
6. `Core/Database.cs:226` `CreateConnection` — `new SqlConnection(cs)` (+ `using Microsoft.Data.SqlClient`).
7. `Core/Database.cs:252` `DbError` — add `SqlException` arm for `driver_error_code`;
   map unique-violation **2627/2601** (no SQLSTATE on MSSQL).
8. `Core/DsnTranslator.cs:31` — prefix arm + new `BuildMssql` (native keys: `Server=host,port`
   — MSSQL uses **comma** before port, not `Port=`; `Database=`, `User Id=`/`Password=`,
   `Encrypt`/`TrustServerCertificate` passthrough).
9. `Metadata/DatabaseIntrospector.cs` — `ListTables` arm (`:22`) + `DescribeColumnsMssql`,
   `DescribeForeignKeysMssql`, `DescribeTableCommentMssql`, `DescribeUniqueConstraintsMssql`
   wired into `:43,:52,:132,:144`; extend `ClrTypeFor` (`:480`) with MSSQL type names.
10. `Core/BatchExecutor.cs:961` `PlanPagination` — make driver-aware (OFFSET/FETCH); `:573`
    `LIMIT 1` → `TOP 1`.
11. `Auth/LoginService.cs:103,374` `LIMIT 1` → `TOP 1`; `LooksLikeUniqueViolation` (~`:390`)
    add 2627/2601.

---

## 6. Testing

- **.NET** (xunit, net10.0, `tests/Shape.Tests/`):
  - Unit, no server (fast, add to CI immediately): extend `DatabaseQuotingTests.cs`
    (reflection sets `_driver`) with an `Mssql_uses_brackets` fact; add `mssql:` cases to
    `DsnTranslatorTests` / `DatabaseConfigTests`; add an `InsertReturningId`-shape assertion.
  - Integration (needs a real server — there is **no in-memory MSSQL** analogue to SQLite
    `:memory:`): run introspection + CRUD against `mcr.microsoft.com/mssql/server:2022`
    in a container, gated behind an env flag / `[Trait("db","mssql")]` so laptop `dotnet test`
    stays green without Docker.
- **PHP**: the test system is still being stood up. MSSQL
  is the strongest argument for a **Docker-compose driver matrix** — add a
  `mssql` service beside MariaDB/Postgres so `make check` proves the "one code path"
  claim on all four engines. Until then, a Nette Tester `.phpt` for `quoteIdentifier` +
  the OFFSET/FETCH wrapper is runnable without a server.
- **Shared fixture:** one small schema (a table with identity PK, an FK, a composite
  UNIQUE, a `bit`, an `nvarchar(100)`, an MS_Description) exercises identity detection,
  unique introspection, description surfacing, type mapping, and pagination in one pass —
  the five MSSQL-specific risks.

---

## 7. Phasing & effort

1. **Core connect + quote + DSN** (both stacks) — connection opens, identifiers quote.
   Small, unblocks everything. *~0.5 day/stack.*
2. **Introspection** — the bulk of the work; pgsql queries port with `dbo`/identity/
   extended-properties changes. *~1 day/stack.*
3. **Pagination + `TOP 1`/id-return** — dialect helper + the fixed literal sites. *~0.5 day/stack.*
4. **Auth path** (`FOR UPDATE`→hints, unique-violation codes) + type/validator mapping. *~0.5 day/stack.*
5. **Tests + Docker service + docs** (STATUS.md matrix rows, SPEC §2, both READMEs). *~1 day.*

Roughly **4–5 focused days** to reach parity, .NET and PHP tracking together. Recommend
landing the optional `SqlDialect` extraction in phase 1 so phases 3–4 are edits to one
file rather than a hunt.

---

## 8. Risks / watch-list

- **Silent-empty (.NET) / MySQL-fallthrough (PHP)** for any dispatcher missed — §2.
  A single "list tables returns rows on MSSQL" smoke assertion catches most of these.
- **OFFSET/FETCH needs ORDER BY** — the `ORDER BY (SELECT NULL)` shim is mandatory in the
  paginator; forgetting it turns every paged read into a syntax error.
- **`nvarchar(max)` → CHARACTER_MAXIMUM_LENGTH = -1** — must not become `maxLen:-1`.
- **`SCOPE_IDENTITY()` vs `@@IDENTITY`** — use `SCOPE_IDENTITY()`/`OUTPUT` (trigger-safe);
  `@@IDENTITY` leaks ids from trigger-side inserts.
- **Encryption defaults** — `Microsoft.Data.SqlClient` defaults `Encrypt=true`; local/dev
  servers without a trusted cert need `TrustServerCertificate=true`. Document it in the
  DSN builder or connections fail out of the box.
- **Case sensitivity / collation** — table names follow the database collation; keep the
  introspection comparisons collation-agnostic (don't force-lower table names the way the
  MySQL path can).

---

## 9. STATUS.md updates on completion

Add SQL Server to the "Multi-driver" rows (Core & query), the DSN-parsing row, the
introspection-driver note, and — once the Docker matrix exists — flip the driver-portability
verification from "maintainer's laptop" to CI. Update the polyglot table's Notes to read
"MySQL / PostgreSQL / SQLite / SQL Server".
