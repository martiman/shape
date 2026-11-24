# Shape (.NET)

Shape is an entity-driven .NET backend. You declare entities (or point at a database), and the framework generates a mask-based query/mutation/command runtime, OIDC support, audit logging, file uploads, an MCP server, and a self-documenting JSON API — wired into any ASP.NET Core minimal-API host.

```csharp
using Shape;

await ShapeApp.Run("config/shape.json", args);
```

That's the whole backend. The JSON config carries masks, access map, db, auth + OIDC; environment variables (`DB_DSN`, `DB_USER`, `DB_PASS`, `CLOUD_SQL_INSTANCE`, `JWT_SECRET`) overlay per-environment secrets. `--mcp-stdio` in `args` switches the binary into MCP stdio-server mode for Claude Desktop / Cursor / Cline. Apps that need richer hosting drop one level down to `ShapeApp.FromEnv` + manual `MapShape`:

```csharp
var config = ShapeApp.FromEnv("config/shape.json");
var app = WebApplication.CreateBuilder(args).Build();
app.MapShape(config);
app.Run();
```

Or, for code-driven entity-first apps:

```csharp
var app = WebApplication.CreateBuilder(args).Build();
var config = ShapeApp.FromAssembly(typeof(Book).Assembly, new ShapeConfig
{
    ServiceName = "bookshop",
    Db = new() { Driver = "sqlite", ConnectionString = "Data Source=bookshop.db" },
    Auth = AuthConfig.Jwt()
});
app.MapShape(config);
app.Run();
```

That's the whole backend. You get:

- **`GET /?manifest`** — full self-describing manifest (entities, fields, masks, validators, commands, soft-delete and audit columns, uploads).
- **`GET /?openapi`** — OpenAPI document.
- **`GET /?mcp`** + **`POST /?mcp`** — MCP (Model Context Protocol) server so Claude Desktop / Cursor / Cline can mount the backend as tools.
- **`GET /<entity>`**, **`GET /<entity>/<id>`** — OData-flavored read paths with ETag + 304 short-circuit.
- **`GET /_/<mask>?param=value`** — call any registered mask by name.
- **`POST /`** — Shape op envelope: `{ op: "query" | "mutation" | "command" | "auth.token" | "auth.refresh" | "auth.logout" | "auth.admin.create-user" | "auth.admin.change-password" | "manifest" | "openapi", payload: {…} }`.
- **`POST /upload/<name>`** — multipart file upload through the configured `IStorage` backend.

## Why Shape

The differentiators vs. every other .NET backend:

1. **One declaration, full API.** Entities define the schema; masks are derived. No controllers, no DTOs, no repositories. Soft-delete (`deleted_at`), audit columns (`created_at`/`updated_at`/`created_by`/`updated_by`), inverse-FK relations for nested-create — all auto-detected from convention.
2. **Self-documenting errors.** Every 400 response carries `expected` (the schema), `example` (a minimal valid payload), and `hint` (a one-line fix). LLMs can correct and retry without re-fetching the manifest.
3. **Mask-driven SQL.** Reads run pre-declared SQL. No string concatenation in app code, no untrusted ad-hoc filtering. Per-mask role gates enforce authorization at the runtime boundary.
4. **DB-first or entity-first.** `ShapeApp.FromDatabase(config)` introspects a live MySQL/PostgreSQL/SQLite/SQL Server schema and generates the same mask surface as the entity-first path. Useful for legacy databases or admin UIs.
5. **MCP built in.** Tools list is role-filtered per request. Resources include a narrated `shape://README.md` describing the entire backend so an agent gets the full surface in one resource fetch.

## Drivers

Pick a driver in `DatabaseConfig.Driver`:

| Driver | Connection string example |
|---|---|
| `mysql` | `Server=localhost;Database=app;User=app;Password=…;CharSet=utf8mb4` |
| `pgsql` | `Host=localhost;Database=app;Username=app;Password=…` |
| `sqlite` | `Data Source=app.db` (or `:memory:` for tests) |

Backed by **Dapper** with **MySqlConnector**, **Npgsql**, and **Microsoft.Data.Sqlite**. Array parameters (`WHERE id IN @ids`) work natively across all three.

### PHP/PDO-style DSN

Configs ported from `papi-lib` (PHP) can pass a DSN verbatim — `DatabaseConfig.FromDsn` translates to the native form:

```csharp
config.Db = DatabaseConfig.FromDsn(
    "pgsql:host=db.internal;port=5432;dbname=aps_dev",
    user: "aps",
    password: env["DB_PASS"]);
```

In a JSON config file (`Shape.Config.ConfigLoader`):

```jsonc
{
    "db": {
        "dsn": "pgsql:host=db.internal;port=5432;dbname=aps_dev",
        "user": "aps",
        "pass": "..."
    }
}
```

Supported prefixes: `pgsql:` (alias `postgres:` / `postgresql:`), `mysql:`, `sqlite:`, `sqlsrv:` (alias `mssql:` / `sqlserver:`). The same key names PHP uses (`host`, `port`, `dbname`, `unix_socket`, `sslmode`) carry across. SQL Server also accepts the PDO `sqlsrv` spelling (`Server=host,port`, `Database=`) and passes through `Encrypt` / `TrustServerCertificate` (plus any other SqlClient options, e.g. `LoginTimeout`; PDO-style `1`/`0`/`yes`/`no` booleans are normalized to `true`/`false`). When `TrustServerCertificate` is not given, the translator defaults it to `true` **for local hosts only** (`localhost`, loopback IPs, `(localdb)`) so a dev server without a CA-trusted cert connects out of the box — remote servers keep full certificate validation (SqlClient's `Encrypt=true` default); set `TrustServerCertificate=true` explicitly in the DSN if a remote dev box really needs it.

## Externalizing mask SQL

For non-trivial masks, point each `masks.<name>.sql` at a `.sql` file
instead of embedding the body inline:

```jsonc
// shape.json
{
    "masks": {
        "stock.byLocation": {
            "sqlFile": "masks/stock.byLocation.sql",
            "params": { "location_id": { "type": "string", "required": true } },
            "roles": ["admin"],
            "description": "Stock per commodity at one location."
        }
    }
}
```

```sql
-- masks/stock.byLocation.sql
SELECT id, location_id, qty
FROM stock
WHERE location_id = :location_id
  AND deleted_at IS NULL
```

`sqlFile` paths are resolved relative to the directory containing the
JSON config (so config + masks travel as a self-contained folder).
Absolute paths and `~/`-style paths are rejected with `CONFIG_INVALID`.
`sql` and `sqlFile` are mutually exclusive on the same mask. PHP-style
`:param` placeholders work in either form — the `MaskRegistry` rewriter
runs regardless.

## Manual primary keys (legacy tables)

Legacy databases (typical SQL Server migration sources) often have plain `int`
primary keys with no IDENTITY, no default, no sequence — the original app
computed the next id in code. Shape detects those tables and allocates
`MAX(pk) + 1` **atomically** on insert (single `INSERT … SELECT` statement;
`WITH (UPDLOCK, HOLDLOCK)` range lock on SQL Server, `pg_advisory_xact_lock`
on Postgres, retry ×3 on unique violation against external writers). No DDL
touches the source database.

Configure via `PkAssignment` (`pkAssignment` / PHP-parity `pk_assignment` in JSON):

```jsonc
{
    // whole-database default (string shorthand)…
    "pkAssignment": "auto",
    // …or per table, "*" = database default:
    "pkAssignment": { "*": "auto", "invoices": "manual", "audit_log": "db" }
}
```

- **`auto`** (default) — detect from schema: single-column int-family PK
  (`int`/`bigint`/`smallint`/`tinyint`, plus scale-0 `decimal`/`numeric`) that is
  neither auto-increment nor defaulted → `manual`; anything else → `db`.
- **`manual`** — force framework allocation. Boot-time `CONFIG_INVALID` when the
  table's PK is composite or non-int — never guessed.
- **`db`** — trust the database (identity / trigger / sequence / client assigns).

Externally the PK keeps behaving like a generated column: the manifest and
OpenAPI report it auto-assigned, validators never require it, and the mutation
response returns the allocated `id`. A client-supplied id in `data` always wins
verbatim (import/migration flows). Entity-first apps mark the property with
`[GeneratedId]` instead. Batches hold the allocation lock until commit, which
serializes concurrent inserts into the same manual-PK table for the batch's
duration — fine for the backoffice workloads this targets.

## Cloud Run / Cloud SQL

Cloud SQL connects from Cloud Run via a Unix socket at `/cloudsql/<instance>` instead of TCP. The same config can boot locally (TCP) and on Cloud Run (socket) by selecting at runtime:

```csharp
var instance = "to-aps:europe-central2:to-psql1";
var dsn = Directory.Exists($"/cloudsql/{instance}")
    ? $"pgsql:host=/cloudsql/{instance};dbname=aps_dev"        // Cloud Run
    : $"pgsql:host=34.118.115.29;port=5432;dbname=aps_dev";    // local

config.Db = DatabaseConfig.FromDsn(dsn, user: "aps", password: Env("DB_PASS"));
```

`DsnTranslator` detects the Unix-socket form (host starting with `/`) and emits the native string without a port — Npgsql rejects an explicit port on socket connections.

A minimal `Dockerfile`:

```dockerfile
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY publish/ ./
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "MyApp.dll"]
```

Then on Cloud Run set `--add-cloudsql-instances=<instance>` and pass `DB_PASS` (and `JWT_SECRET` if you want explicit rotation — otherwise `JwtSecretMaterializer` writes one to `<base>/.shape/jwt-secret` on first boot, which Cloud Run's ephemeral filesystem rotates on each cold-start).

## Auth

- **HS256 local users** via `LoginService` (BCrypt password hashes, refresh-token rotation, admin endpoints).
- **OIDC** (RS256/RS384/RS512) for Entra ID / Auth0 / Okta / Google / Cognito with `IdpPresets`. JWKS fetched + cached + stale-fallback on IdP outages. Local-user resolution via `oid → users.id` with optional auto-provision.
- Both coexist on the same endpoint — `JwtGuard` dispatches by `alg`.

## MCP

Two transports:

- **HTTP**: built into `MapShape`. POST `/?mcp` with a JSON-RPC envelope.
- **stdio**: ship as `dotnet tool install --global Shape.Mcp.Cli`. Claude Desktop / Cursor / Cline spawn the binary; configuration via `appsettings.json` or `SHAPE__*` env vars.

The `tools/list` response is filtered by the caller's role — an agent never sees a forbidden tool. The `resources/list` response includes a narrated `shape://README.md` describing every entity, mask, and command in this specific backend.

## Errors

Every response that fails carries the same envelope:

```json
{
  "error": {
    "code": "PARAM_VALIDATION_ERROR",
    "message": "Missing required parameter 'id'.",
    "ref": "q1",
    "expected": { "id": "int (required)", "tag": "string" },
    "example": { "id": 42 },
    "hint": "Provide a value for 'id' (type: int)."
  }
}
```

Agents read `expected` + `example` + `hint` and retry once. They do not re-fetch the manifest after a parameter error — the response told them everything.

## Project layout

```
Shape.csproj                     The library
src/Shape/                       Source
  Attributes/                    [Entity], [Column], [Id], [Fk], [Validate], [Access]
  Audit/                         AuditLogger
  Auth/                          JwtGuard, JwtIssuer, OidcVerifier, IdpPresets, LoginService
  Core/                          BatchExecutor, MaskRegistry, ParamResolver, Database, OpDispatcher, ShapeConfig
  Generator/                     MaskGenerator, ManifestGenerator, ValidatorGenerator, AdminMaskGenerator, OpenApiGenerator, LlmToolGenerator
  Http/                          AppRouter, JsonResponse, RequestEnvelope, FatalHandler, CacheHeaders, AdminAuth
  Mcp/                           McpServer, ReadmeBuilder, HttpTransport, StdioTransport, McpException
  Metadata/                      EntityReader, EntityMeta, FieldMeta, AutoValidators, DatabaseIntrospector
  Upload/                        IStorage, LocalStorage, UploadManager
  ShapeApp.cs                    FromAssembly / FromTypes / FromDatabase
  ShapeRouter.cs                 MapShape extension method
apps/Shape.Mcp.Cli/              Stdio MCP bridge — packs as `dotnet tool`
tests/Shape.Tests/               239 xUnit tests
```

## Building

```bash
dotnet build Shape.csproj
dotnet test tests/Shape.Tests/Shape.Tests.csproj
dotnet pack Shape.csproj                                # → bin/Release/Shape.0.1.0.nupkg
dotnet pack apps/Shape.Mcp.Cli/Shape.Mcp.Cli.csproj      # → bin/Release/Shape.Mcp.Cli.0.1.0.nupkg
```

## Status

Phase | Status
---|---
Pre-work + simplification | ✅
Phase 0 (runtime parity) | ✅
Phase 1 (multi-driver + OIDC) | ✅
Phase 2 (generators) | 🟡 OpenAPI expansion deferred
Phase 3 (Upload + Audit + DB-first) | ✅
Phase 4 (MCP) | ✅
Phase 5 (polish + NuGet + stdio) | ✅

Tests: **337 passing**, library: ~8.5k LOC.

See [`../STATUS.md`](../STATUS.md) for the cross-language (PHP + .NET) implementation status & roadmap.
