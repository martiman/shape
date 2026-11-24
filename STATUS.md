# Shape — Implementation Status & Roadmap

One place for *what is built* and *what is planned*, across **both** language
implementations of Shape:

- **PHP** — the reference implementation (`php/packages/shape` + `php/packages/shaperuntime`).
- **.NET / C#** — the port (`dotnet/`), currently **v0.6.2**, **487/487 tests passing on net10.0**.

Both share one specification (`docs/shape/SPEC.md`), one manifest format, and one
`AGENTS.md` contract — an agent that learned Shape on one stack produces correct calls
against the other. This file is the single source of truth for parity; it replaces the
former per-language `ROADMAP.md` (PHP) and `PORT-PLAN.md` (C#).

**Legend:** ✅ shipped · 🚧 partial / in progress · 📋 planned · ⏸ deferred · — n/a

The guiding test for every item, shipped or planned: *does it remove code an AI must
write, an error it must recover from, or a translation step it must perform?*

---

## Feature matrix

### Core & query
| Feature | PHP | .NET | Notes |
|---|:--:|:--:|---|
| DB-first auto-CRUD from live schema | ✅ | ✅ | Introspects MySQL / PostgreSQL / SQLite (+ SQL Server 🚧); no entity files needed |
| Entity-first (attributes → masks) | ✅ | ✅ | `#[Entity]` / `[Entity]` attribute surface |
| Multi-driver MySQL / PostgreSQL / SQLite | ✅ | ✅ | .NET via Dapper + provider packages |
| SQL Server (MSSQL) driver | 🚧 | 🚧 | Code-complete both stacks (bracket quoting, `OFFSET…FETCH` pagination, `SCOPE_IDENTITY`/`OUTPUT`, `dbo`/`COLUMNPROPERTY` introspection, 2627/2601 unique-violation); **live-server verification pending** — see [docs/shape/MSSQL-SUPPORT-PLAN.md](docs/shape/MSSQL-SUPPORT-PLAN.md). PHP via `pdo_sqlsrv`; .NET via `Microsoft.Data.SqlClient` |
| Manual PK assignment (non-IDENTITY int PKs) | 🚧 | 🚧 | `pk_assignment` config (`auto`/`db`/`manual`, per-DB or per-table) + schema auto-detection; atomic `MAX(pk)+1` allocation (MSSQL `UPDLOCK,HOLDLOCK` single statement, PG advisory lock, MySQL/SQLite locked read), 3× retry on races; PK rendered as generated in manifest/OpenAPI; SQLite-verified end-to-end both stacks — MSSQL concurrency path **live-verify pending**. See [docs/shape/MANUAL-PK-PLAN.md](docs/shape/MANUAL-PK-PLAN.md) |
| Masks as query gate, `:param` placeholders | ✅ | ✅ | .NET rewrites `:param`→`@param` (0.3.0), skipping strings/casts/comments |
| OData GET surface (`$top`/`$skip`/`$orderby`/`$filter`/`$select`/`$count`) | ✅ | ✅ | |
| SQL-pushed pagination + `default_limit` + separate `COUNT(*)` | ✅ | ✅ | Bounds memory on large tables |
| Generic mask GET `/_/<mask>` (cacheable URL) | ✅ | ✅ | |
| External `.sql` files referenced from config | ✅ | ✅ | PHP `sqlFile` now resolves relative to the config file; .NET `MaskDto.SqlFile` (0.4.0) |
| Computed mask columns | 📋 | 📋 | Roadmap **E** |
| Cursor pagination (`$cursor`) | 📋 | 📋 | Roadmap **D** — offset scales poorly at volume |

### Mutations
| Feature | PHP | .NET | Notes |
|---|:--:|:--:|---|
| Batch mutations + transactions | ✅ | ✅ | |
| `insert` returns new id | ✅ | ✅ | via `InsertReturningId` (driver-aware) |
| Bulk nested create (`relations`, FK-chain walk) | ✅ | ✅ | Any `relations` forces a transaction; arbitrary nesting depth |
| Soft-delete by convention (`deleted_at`) | ✅ | ✅ | DELETE → UPDATE with `IS NULL` guard; UPDATE restores |
| Audit columns by convention (`created_at/by`, `updated_at/by`) | ✅ | ✅ | Detected by name, stamped automatically |
| Append-only / no-delete writable modes | ✅ | ✅ | PHP `#[Entity(writableMode: ...)]` + runtime enforcement; .NET `WritableMode = full/append_only/no_delete/none` (0.6.0) |
| Idempotency keys (safe agent retries) | 📋 | 📋 | Roadmap, **high** — prevents double-insert on retry |
| Webhooks on writable masks | 📋 | 📋 | Roadmap **H** — HMAC-signed after-mutation POST |

### Auth
| Feature | PHP | .NET | Notes |
|---|:--:|:--:|---|
| Local users + HS256 JWT (login + refresh rotation) | ✅ | ✅ | `replaced_by_token_id` / `revoked_at` rotation |
| Role-per-mask authorization | ✅ | ✅ | |
| OIDC / JWKS (Entra, Auth0, Okta, Google, Cognito) | ✅ | ✅ | `oid → users.id` resolution, opt-in auto-provision, JWKS cache + stale fallback |
| `local_login_disabled` short-circuit (`AUTH_NOT_CONFIGURED`) | ✅ | ✅ | For OIDC-only `users` schemas |
| Auto JWT secret on first boot | ✅ | ✅ | .NET writes `<base>/.shape/jwt-secret` (0600 on Unix, 0.3.0) |
| Rate limiting per role | 📋 | 📋 | Roadmap **G** |
| Account lockout / failed-login throttle | 📋 | 📋 | Gap noted vs. sqlbackend; not yet scheduled |

### Access & scoping
| Feature | PHP | .NET | Notes |
|---|:--:|:--:|---|
| Access shorthand (`'*' => roles`, role-first) | ✅ | ✅ | .NET JSON `"auth": "jwt"` / `AccessByRole` (0.3.0) |
| Row-level auto-scoping by dimension (row scopes) | ✅ | ✅ | Generalized `scopes` config: any dimension column (department / branch / cost-center) binds to a caller claim. Scoped/unscoped mask variants (per-scope `unscoped_roles`, unscoped-ness is **not** transitive), INSERT stamp + UPDATE/DELETE guard (`SCOPE_MISMATCH` / `SCOPE_REQUIRED` + `scope`), multi-valued memberships bind as `IN`, `AUTH_INVALID` on pre-feature tokens (re-login hint). Manifest `rowScopes` (entity) / `row_scopes {multi}` (mask). **Cross-stack parity verified** (2026-07-07): manifest keys, error contract, auth rejection, **and human message + `hint` wording** all match verbatim (only the SQL text differs — `IN (:p)` vs `IN @p` — which was never part of the contract). See [PHP plan](docs/shape/ROW-SCOPES-PHP-PLAN.md) / [.NET plan](docs/shape/ROW-SCOPES-DOTNET-PLAN.md) |
| Multi-tenant scoping (`tenant_id` / `organization_id`) | 🚧 | 🚧 | Roadmap **7 + B** — opens B2B SaaS deployments. Tenancy is now the built-in special case of the shared row-scopes machinery (above), so **both stacks share the scoped/unscoped compilation, INSERT stamping, and UPDATE/DELETE guard** — the .NET port gained tenancy alongside row scopes (`TenancyConfig` + `TenancyResolver`, `TenancyFeatureTests`). PHP **core done + SQLite-verified**: `tenancy` config (bool/array, `unscoped_roles`, `enforce` strict/relaxed, `global_tables`); schema auto-detection + strict-mode build guards; baked `<col> = :__tenant`; `TENANT_MISMATCH` guard; reserved-`__`-param hygiene; `tenant_id` JWT claim + `TENANT_SUSPENDED` on login/refresh; registry auto-exposure; manifest `tenantScoped`/`tenant_scope`. **Simple tier complete (S1–S12, tracked in the simple-tier plan):** `tenant.provision`, OIDC tenant resolution, admin-handler guards, opt-in FK check, manual-mask contract, `Vary`, per-action roles. **Not built:** the full tier (multi-tenant membership, tenant-defined roles, field-level access). See [plan](docs/shape/MULTI-TENANCY-PLAN.md) + [php checklist](php/MULTI-TENANCY-CHECKLIST.md) |

### Agent surface (generators)
| Feature | PHP | .NET | Notes |
|---|:--:|:--:|---|
| Machine-readable manifest | ✅ | ✅ | `entities` is a stable-order array (diffable across deploys) |
| LLM tool format (Anthropic + OpenAI) | ✅ | ✅ | Read masks → tools; writable → `<table>_mutate` with action enum |
| OpenAPI 3.x (GET/POST/mutation/command paths, tags, schemas) | ✅ | ✅ | Runnable examples from real column names |
| Plain-English mask descriptions in manifest | ✅ | ✅ | Auto-generated + config override |
| Self-documenting errors (`expected`/`example`/`hint`/`ref`) | ✅ | ✅ | Corrected payload shipped in the error itself |
| Admin masks for secret tables (sensitive columns stripped) | ✅ | ✅ | `users`, `refresh_tokens`, `audit_log` baseline |
| SDK codegen (typed TypeScript client) | 📋 | 📋 | Roadmap **F** |

### MCP (Model Context Protocol)
| Feature | PHP | .NET | Notes |
|---|:--:|:--:|---|
| MCP server (initialize / tools / resources) | ✅ | ✅ | Custom JSON-RPC, no external SDK dependency |
| HTTP (streamable) transport (`?mcp`) | ✅ | ✅ | CORS preflight for browser clients |
| Stdio transport + CLI | ✅ | ✅ | PHP `bin/shape-mcp`; .NET `Shape.Mcp.Cli` (`dotnet tool`, `shape-mcp`) |
| `shape://README.md` narrated resource | ✅ | ✅ | Click-attachable backend overview |
| Role-filtered `tools/list` | ✅ | ✅ | Auth-aware; list built per request |

### HTTP & caching
| Feature | PHP | .NET | Notes |
|---|:--:|:--:|---|
| ETag + per-mask `Cache-Control` + 304 | ✅ | ✅ | SHA-256 ETag, `If-None-Match` |
| Fatal-error JSON envelope | ✅ | ✅ | OOM / timeout / DB-deadlock hints; strips absolute paths |

### Uploads & audit
| Feature | PHP | .NET | Notes |
|---|:--:|:--:|---|
| File uploads (`uploads` namespace, `/upload/<name>`, content-based mime sniff) | ✅ | ✅ | Local storage ships; S3 / GCS / R2 pluggable via `StorageInterface` |
| Append-only `audit_log` (Tier 1) + admin masks | ✅ | ✅ | Detected by table presence; compliance-grade history |

### Design-time attributes / signal density
*(These rows track the attribute-surface parity between PHP and .NET. Some
features shipped on one side first; the current matrix reflects what is now
implemented in-tree.)*

| Feature | PHP | .NET | Notes |
|---|:--:|:--:|---|
| Auto-validators from schema (`notEmpty`, `maxLen:N`) | ✅ | ✅ | Derived from NOT NULL + VARCHAR(N) |
| `description` on entity / column | ✅ | ✅ | Reads `pg_description` / `COLUMN_COMMENT`; surfaces in manifest/OpenAPI/LLM tools |
| `[Unique]` / composite-unique + DB introspection | ✅ | ✅ | Reads MySQL `STATISTICS` / PG `TABLE_CONSTRAINTS` / SQLite `index_list` |
| enum / `in:[…]` validator | ✅ | ✅ | `Column(values: [...])` and `Validate(rule: 'in', values: [...])` → manifest enum + validator |
| JSONB / JSON column handling | 📋 | ⏸ | PHP proposal #2; .NET deferred (touches write path) |

### Config & hosting ergonomics
| Feature | PHP | .NET | Notes |
|---|:--:|:--:|---|
| One-line quick start | ✅ | ✅ | PHP `ShapeApp::quickStart('.env')`; .NET `await ShapeApp.Run("config/shape.json", args)` (0.5.0) |
| Auto-SQLite default (empty config = working backend) | ✅ | ✅ | Provisions `<base>/.shape/data.sqlite` |
| PDO-style DSN parsing | ✅ | ✅ | .NET `DsnTranslator` (0.3.0); `sqlsrv:` prefix added both stacks (🚧, live-verify pending) |
| Env-file overlay (`DB_DSN` / `JWT_SECRET` / …) | ✅ | ✅ | .NET `FromEnv` / `EnvLoader` (0.5.0) |
| Introspection bundle cache (warm boot: zero DB I/O) | ✅ | ✅ | Entities + admin masks + audit flag cached together |
| Bulk introspection (fewer round trips on cold boot) | ✅ | ✅ | Bulk catalog reads for columns / PKs / FKs / comments / uniques collapse cold boot RTTs |
| Cloud SQL Unix-socket detection | ✅ | ✅ | .NET DSN auto-omits port on socket hosts (0.3.0) |

### Operational tooling
| Feature | PHP | .NET | Notes |
|---|:--:|:--:|---|
| `bin/shape migrate` (ordered idempotent SQL runner) | 📋 | 📋 | Roadmap, **high** — every prod app hand-rolls this |
| `bin/shape dump-schema` (DDL from introspection) | 📋 | 📋 | Introspection stays the source of truth |
| `bin/shape drift` (diff checked-in dump vs live DB) | 📋 | 📋 | Suggests `ALTER`s |
| CSV seeding | 📋 | 📋 | Low priority — LLMs write INSERTs fluently |
| DDL-from-manifest (declarative schema authoring) | ⏸ | ⏸ | Deferred indefinitely — modern LLMs write dialect DDL from a description; keeps live-schema as source of truth |

---

## Roadmap — planned work, ranked by token-frugality impact

Applies to both implementations unless noted. PHP is the reference and usually ships a
feature first; the .NET port follows.

1. **Auto access by convention / multi-tenant scoping** (item 7 + B) — highest per-app saving; opens B2B SaaS. *Core shipped on **both** stacks* as the generalized row-scopes machinery (tenancy is its built-in special case); remaining work is the deferred sub-items in the feature-matrix note. Design plans: [row scopes](docs/shape/ROW-SCOPES-PHP-PLAN.md), [multi-tenancy](docs/shape/MULTI-TENANCY-PLAN.md).
2. **`bin/shape migrate`** — production necessity every app re-implements.
3. **Idempotency keys** — silent retry safety, prevents double-mutation under network jitter.
4. **Cursor pagination** (D) — scales reads past where offset breaks.
5. **Webhooks on writable masks** (H) — pairs with the audit-log trigger point.
6. **`bin/shape dump-schema` + `drift`** (I) — ops tooling, lower urgency than `migrate`.
7. **Rate limiting per role** (G) — mid-priority; most apps have a CDN/proxy option.
8. **Computed mask columns** (E) — convenience win.
9. **SDK generation** (F) — typed client from the manifest.
10. **CSV seeding** — small win, no urgency.

**PHP catch-up backlog**: closed. `description` on entity/column, `[Unique]` /
composite-unique, enum-values validator, and append-only writable mode now ship on both
stacks, so the design-time surface is aligned again.

**Additional polyglot ports** — Shape is a *pattern* (schema as spec, masks as gate,
manifest as SDK, `AGENTS.md` as onboarding). Reference implementations queued so a team
can carry Shape across stacks:

| Stack | Status | Notes |
|---|:--:|---|
| PHP 8.4 | ✅ | Reference implementation (this repo) |
| .NET / C# | ✅ | Same conventions, same manifest format; net10.0 |
| TypeScript / Node | 📋 | Likely Fastify/Hono; `pg` / `mysql2` / `better-sqlite3` |
| Python | 📋 | Likely FastAPI; SQLAlchemy reflection |
| Go | 📋 | Likely net/http or chi; `database/sql` + per-driver schema queries |

---

## Notes on the .NET port architecture

Where the C# implementation deliberately diverges from a line-for-line PHP port:

- **One package, sub-namespaces** — `Shape` with `Core` / `Auth` / `Http` / `Shape`,
  rather than PHP's split `shape` + `shaperuntime` packages.
- **Dapper** for the DB layer over an abstract `DbConnection`; driver chosen by a
  `DbDriver` enum. Array-param expansion (`WHERE id IN @ids`) is native to Dapper, so the
  PHP regex expander was dropped.
- **ASP.NET Core minimal API** front controller (`MapShape` / `MapShapeRuntime`).
- **Custom MCP JSON-RPC** (no `Microsoft.Extensions.Hosting` dependency) so MCP stays
  opt-in middleware; stdio ships as a separate `Shape.Mcp.Cli` console tool.
- **JWKS / OIDC** uses `RSA.ImportParameters` + `VerifyData` — the PHP hand-rolled DER
  encoder is intentionally *not* ported.
- **Distribution** as a NuGet package (`Shape.dll`) plus the `Shape.Mcp.Cli` dotnet tool;
  there is no PHAR analogue (single-file publish is an app concern, not a library one).

Historical detail lives in git history.

---

## Keeping this honest

Before adding an item, justify it against the token-frugality test above. Before closing
one, estimate the lines an AI no longer writes per use × how often the use occurs — if
neither number is meaningful, it belongs in a different doc. When a feature ships in one
language, update its row here (and flag the other language's row if it now lags). The
discipline is what makes this file a reliable view of reality instead of a wishlist.
