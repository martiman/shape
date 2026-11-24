# Building a brownfield adapter — an existing database + existing auth → Shape API

> How to write apps like **ExampleCorp.Shape**: your organization already has a
> production database and its own login system, and you want a secure,
> tenant-isolated, self-documenting API over it — without migrating users,
> rewriting auth, or hand-writing a single mask. The result is one thin host
> project (~100 lines of real code) plus one class of app-specific SQL.
>
> Companion references: [TOKEN-EXCHANGE.md](TOKEN-EXCHANGE.md) (auth bridge),
> [DB-FIRST.md](DB-FIRST.md) (introspection), [MULTI-TENANCY-PLAN.md](MULTI-TENANCY-PLAN.md)
> (isolation design), the brownfield adapter guide
> (why the boundary is where it is).

## The pattern in one picture

```
┌────────────────────────── your adapter project ──────────────────────────┐
│  connection string     ITokenExchangeSource      exposure + role config  │
│  (app deployment)      (your tables' SQL)        (app policy)            │
└───────────┬──────────────────────┬──────────────────────┬────────────────┘
            ▼                      ▼                      ▼
┌─────────────────────────── Shape library ────────────────────────────────┐
│  introspection → masks → tenancy predicates → role gates → manifest/MCP  │
│  auth.exchange op · security:strict preset · TenantExposurePlanner       │
└───────────────────────────────────────────────────────────────────────────┘
```

**The split rule:** if another Shape-backed app would need the exact code, it
belongs in the library (and probably already exists there). Your project
keeps only what names *your* tables, *your* roles, *your* connection.

## Step by step

### 1. Reference the library, resolve the connection

```csharp
// csproj: <ProjectReference Include="..\..\shape\dotnet\Shape.csproj" />
var connectionString = builder.Configuration.GetConnectionString("MyAppConnection")
    ?? throw new InvalidOperationException("Set ConnectionStrings:MyAppConnection.");
```

Deployment wiring (appsettings, env overlays) is yours; Shape never reads it.

### 2. Decide the tenancy shape

Find your tenant registry (ExampleCorp: `Organizations`) and discriminator
column (`OrganizationId`), and which role sees across tenants:

```csharp
var tenancy = new TenancyConfig
{
    Table = "Organizations",
    Column = "OrganizationId",
    UnscopedRoles = new() { "admin" },
    Enforce = "relaxed",       // brownfield: not every table carries the column
    ValidateFks = true         // cross-tenant FK references rejected
};
```

`relaxed` + the exposure planner (step 3) is the brownfield combination:
instead of demanding the column everywhere (`strict`), you expose *only* the
tables that verifiably belong to tenants.

### 3. Plan the exposure — don't hand-pick tables

```csharp
using var db = new Database(dbConfig);
var introspector = new DatabaseIntrospector(db);
var tables = TenantExposurePlanner.PlanTables(
    introspector, tenancy,
    excludeTables: new[] { "AspNetUsers", "Tokens" });   // internal tables, never API surface
if (tables.Count == 0) throw new InvalidOperationException("No tenant-linked tables found.");
```

A table is exposed iff its tenancy column is a **verified foreign key** to the
registry PK — a column that merely shares the name proves nothing. New
tenant-linked tables appear in the API on the next boot; unlinked ones never
leak in.

### 4. Bridge your existing auth — one class

Implement `ITokenExchangeSource` with the SQL against *your* token/user/role
tables (see [TOKEN-EXCHANGE.md](TOKEN-EXCHANGE.md) for the full contract):

```csharp
config.Auth = new AuthConfig
{
    Driver = "jwt",
    UsersTable = "AspNetUsers",
    LocalLoginDisabled = true,          // no Shape passwords — exchange is the only door
    Exchange = new TokenExchangeConfig { Source = new MyTokenSource() }
};
```

Clients call `{op: "auth.exchange", payload: {token}}` once, get a Shape JWT,
and re-exchange when it expires. Your app's session lifecycle stays the
source of truth.

### 5. Turn on the safety rails

```csharp
config.Security = "strict";        // boot fails if any surface is left ungated
config.ManifestRoles = new List<string>();   // manifest requires an authenticated caller
```

`strict` converts "someone forgot to gate a mask" from a silent anonymous
endpoint into a `CONFIG_INVALID` boot failure with a hint. It replaces any
hand-written "block anonymous /api" middleware — delete yours.

**Dev convenience:** gate discovery in production only, so local `?docs` /
`?openapi` / `?manifest` need no token (data ops always do — the auth driver
stays `jwt` everywhere):

```csharp
var isDev = environment.IsDevelopment();
config.Security = isDev ? null : "strict";           // strict requires gated manifest roles
config.ManifestRoles = isDev ? null : new List<string>();
```

### 6. Build and map

```csharp
var shapeConfig = ShapeApp.FromDatabase(new ShapeConfig
{
    ServiceName = "MyApp.Shape",
    Endpoint = "/api",
    Db = dbConfig,
    Auth = /* step 4 */,
    Tenancy = tenancy,
    Security = "strict",
    ManifestRoles = new(),
    AccessByRole = new() { ["user"] = tables, ["admin"] = tables },
    ExcludeTables = new() { "__EFMigrationsHistory" }
});
var app = builder.Build();
app.MapShape(shapeConfig);
app.Run();
```

That's the whole host. Introspection runs at boot; masks, validators, OData,
manifest, OpenAPI, and MCP surfaces are generated; every read carries the
tenant predicate; every write is stamped and guarded.

## What you get without writing it

- **Row isolation**: `WHERE OrganizationId = :__tenant` compiled into every
  generated mask; cross-tenant ids read as "not found", writes as affected-0.
- **Suspension**: deactivate the registry row → logins/exchanges blocked
  (`TENANT_SUSPENDED`); the unscoped admin keeps access to fix it.
- **Typed errors**: every denial carries `expected`/`hint` so client agents
  self-correct instead of guessing.
- **Discovery**: `/api?manifest`, OpenAPI, and MCP tools — role-filtered per
  caller. Add `DocsUi = true` for an interactive API reference at
  `/api?docs` (Scalar viewer, zero dependencies; the spec fetch stays gated
  by `ManifestRoles` and the page prompts for a Bearer token).

## Checklist of things NOT to build (they exist)

| Temptation | Use instead |
|---|---|
| Middleware blocking anonymous `/api` | `Security = "strict"` + `Driver = "jwt"` |
| FK scanner classifying tenant tables | `TenantExposurePlanner.PlanTables` |
| Per-request tenant filter / claim parsing | tenancy config + the normalized auth context |
| Cross-tenant FK guard on inserts | `Tenancy.ValidateFks = true` |
| A second users/password store | `auth.exchange` + `LocalLoginDisabled = true` |
| Custom manifest auth | `ManifestRoles` |

## PHP

The same pattern works on the PHP stack — every piece exists there too:
`auth.exchange` with a callable `source` (see the PHP example in
[TOKEN-EXCHANGE.md](TOKEN-EXCHANGE.md)), `'security' => 'strict'`,
`Shape\Metadata\TenantExposurePlanner::planTables()`, and the `'manifest'`
roles key. Config keys and error codes are identical; only the source is a
closure instead of a C# interface.

## Worked example

A real-world adapter host is the living reference: one
`Program.cs`, one config builder, one `ExampleTokenExchangeSource` holding
the `Tokens`/`AspNetUsers`/`AspNetRoles` SQL — everything else is library.
