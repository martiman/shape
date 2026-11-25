# Why Shape

Shape generates a backend API from a database schema. You point it at MySQL, PostgreSQL, SQLite or SQL Server, list who may access what, and it serves JSON, OData, OpenAPI and MCP endpoints with auth, validation and an audit log. It is written for PHP (reference implementation) and .NET, which share one spec.

It is built so that an AI agent, or a developer working with one, can produce a correct backend with little code and few retries. Most of the design follows from that.

For current feature status see [STATUS.md](../../STATUS.md). For the full contract see [SPEC.md](SPEC.md).

## The problem it addresses

When an agent builds a backend it spends effort on:

1. reading framework docs to find out what exists
2. writing repetitive CRUD, validation and auth code
3. recovering from errors that gave no hint about the fix
4. translating between formats (schema, API client, tool definitions)
5. getting security right by hand

Shape reduces each of these by moving the decisions into the framework.

## Design

### Masks are the gate

Every readable or writable surface is a named SQL mask with typed parameters and an explicit role list. Clients send a mask name and parameters, never SQL. Authorization belongs to the mask, not to middleware that someone has to remember to add. An empty role list denies access.

### The schema is the source of truth

In database-first mode, Shape reads the live schema and generates the CRUD surface. There are no entity classes to keep in sync. You list the tables to expose, or open all tables and exclude some. An entity-first mode (`#[Entity]` / `[Entity]` attributes) is also available, and hand-written masks can sit beside either.

### Conventions replace configuration

| Convention | Behavior |
|---|---|
| Tables `users`, `refresh_tokens` | Never exposed as CRUD, even if listed in `access` |
| Nullable `deleted_at` column | Soft delete: reads filter it, deletes become updates |
| `created_at/by`, `updated_at/by` columns | Stamped automatically |
| `NOT NULL` string column without default | `notEmpty` validator |
| `VARCHAR(N)` column | `maxLen:N` validator |
| Foreign key column | A `by<Field>` filter mask |
| Table named `audit_log` | Every mutation is appended to it |
| `'access' => ['*' => [...]]` | Same roles for every introspected table |

You override a convention only where the app differs.

### The API describes itself

- **Manifest:** entities, fields, masks, roles and descriptions, served from the running app.
- **Tool definitions:** `?format=anthropic-tools` or `openai-tools` returns definitions an agent can use directly.
- **OpenAPI:** generated from the same metadata.
- **MCP server:** stdio and streamable HTTP, with the tool list filtered by the caller's role.
- **Errors:** actionable errors include `expected`, `example` and `hint`, so a client can correct the call without re-reading the manifest.

### One code path across databases

Identifier quoting, insert-id strategy, pagination, introspection and type normalization are handled per driver. Application config does not change when the DSN does. SQL Server support is code-complete but has not yet been verified against a live server (see STATUS.md).

## Security

| Threat | How Shape handles it |
|---|---|
| SQL injection | Clients send mask names and parameters, not SQL |
| Privilege escalation | Role check per mask; per-action roles for writes |
| Leaking sensitive columns | Secret tables are never exposed; admin masks omit columns such as `password*`, `*_hash`, `*_secret` |
| Reading deleted rows | Soft-deleted rows are filtered by default |
| Mass assignment | Only declared columns pass through validators |
| Refresh-token replay | Token rotation with `replaced_by_token_id` and `revoked_at` |
| Cross-tenant access | With `tenancy` enabled, a tenant predicate is added to every read and stamped or checked on every write |

Row-level scopes extend the tenancy mechanism to other dimensions such as department or cost centre. Tenancy covers one tenant per user; multi-tenant membership and tenant-defined roles are not built.

## Also included

- Batch operations, with results of one operation usable as parameters of the next
- Transactions for mutation batches
- Before/after hooks and commands for business logic outside plain CRUD
- OData-style GET (`$filter`, `$orderby`, `$top`, `$skip`, `$select`, `$count`) with SQL-side pagination
- JWT login with refresh-token rotation, plus OIDC (Entra, Auth0, Okta, Google, Cognito)
- Entity metadata cached on disk, so warm boots do no database introspection

## When Shape is not a good fit

- You need a rich domain model with logic spread through many classes. Shape is most useful where the API is mostly CRUD over a schema.
- You need GraphQL.
- You need cursor pagination, rate limiting, idempotency keys or webhooks today. They are on the roadmap but not built.
