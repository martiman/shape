# Manifest API

> **AI agents:** read [`AGENTS.md`](../../AGENTS.md) first for the framework overview. This file is the manifest, LLM-tool-format, and error-envelope deep reference.

## Overview

The manifest is a machine-readable description of the entire Shape API. It is designed for AI agents building frontends or other tools that need to discover available entities, fields, masks, and commands.

## Request

```json
{
  "op": "manifest"
}
```

No `payload` required. No authentication required. No database connection needed.

## Response

```json
{
  "entities": [
    {
      "name": "Order",
      "table": "orders",
      "writable": true,
      "description": "Customer orders and totals.",
      "uniqueConstraints": [["customer_id", "external_id"]],
      "fields": [
        {
          "name": "id",
          "type": "int unsigned",
          "nullable": false,
          "primary": true,
          "autoIncrement": true
        },
        {
          "name": "customer_id",
          "type": "int unsigned",
          "nullable": false,
          "fk": "App\\Entity\\Customer"
        },
        {
          "name": "total",
          "type": "decimal(10,2)",
          "nullable": false,
          "validators": ["positive"]
        },
        {
          "name": "status",
          "type": "varchar",
          "nullable": false,
          "default": "draft",
          "enum": ["draft", "paid", "cancelled"],
          "description": "Workflow state"
        }
      ],
      "masks": {
        "orders.list": { "params": [], "writable": false, "description": "List rows from orders." },
        "orders.get": { "params": ["id"], "writable": false, "description": "Get one row from orders by primary key (id)." },
        "orders.getMany": { "params": ["ids"], "writable": false, "description": "Get multiple rows from orders by a list of primary keys (id)." },
        "orders.byCustomerId": { "params": ["customer_id"], "writable": false, "description": "List orders rows filtered by customer_id." },
        "orders": { "params": [], "writable": true, "writableMode": "append_only", "description": "Insert only — UPDATE and DELETE rejected (append-only contract)." }
      },
      "roles": {
        "read": ["admin", "user"],
        "write": ["admin"]
      }
    }
  ],
  "commands": [
    "order.process",
    "inventory.adjust"
  ]
}
```

## Entity Fields

`entities` is a **JSON array** (not an object keyed by table name). Iterate it; the order is stable across requests for the same schema, which agents rely on for diffing manifests after deploys. Look up by `entity.table` if you need a specific one.

Each entity in the manifest contains:

| Key | Type | Description |
|-----|------|-------------|
| `name` | string | PHP class short name |
| `table` | string | Database table name |
| `writable` | bool | Whether mutations are allowed |
| `description` | string | (optional) Human-readable summary surfaced in manifest, OpenAPI, and MCP docs |
| `uniqueConstraints` | array | (optional) Repeatable composite unique constraints, each inner array preserving declaration order |
| `writableMode` | string | (optional) Explicit writable policy: `full`, `append_only`, `no_delete`, or `none` |
| `fields` | array | Field definitions |
| `masks` | object | Available query masks and their params |
| `roles` | object | `read` and `write` role arrays |
| `softDelete` | string | (optional) Name of the soft-delete column when present. Indicates that DELETE on the writable mask is translated to UPDATE and reads filter out tombstoned rows. |
| `tenantScoped` | bool | (optional, when true) Rows are automatically limited to the caller's tenant. The discriminator column is server-assigned on insert and never sent by clients — see `tenancy` in [DB-FIRST.md](DB-FIRST.md#tenancy-optional). |

## Field Properties

| Key | Type | Presence | Description |
|-----|------|----------|-------------|
| `name` | string | always | Column name |
| `type` | string | always | DB type with modifiers (e.g. `decimal(10,2) unsigned`) |
| `nullable` | bool | always | Whether the field accepts null |
| `primary` | bool | when true | Is primary key |
| `autoIncrement` | bool | when true | Value is server-assigned — a DB identity/auto-increment column, **or** a manual-assignment PK the framework allocates itself (`pk_assignment`, see [DB-FIRST.md](DB-FIRST.md#pk_assignment-optional)). Either way: never send it on insert |
| `fk` | string | when FK | Target entity class name |
| `validators` | array | when has rules | List of validation rule names |
| `default` | mixed | when has default | Default value |
| `enum` | array | when enum-like | Closed value set for the field |
| `description` | string | when present | Human-readable field description |

## Mask Properties

Each mask entry contains:

| Key | Type | Description |
|-----|------|-------------|
| `params` | array | Parameter names (empty for parameterless masks) |
| `writable` | bool | Whether this is a writable mask |
| `writableMode` | string | (optional) Explicit writable policy for the mask when different from the entity default |
| `description` | string | (optional) One-sentence plain-English description of what the mask does. Auto-generated for standard CRUD masks (e.g. `"List rows from books, excluding soft-deleted rows."`); set explicitly via `'description' => '…'` in the mask config for hand-written masks. Used as the OpenAPI summary and the LLM-tool description so agents can pick the right endpoint without reading SQL. |
| `pk` | string | (writable masks) Primary-key column name (single-column PKs) — the column whose value the insert response returns as `id` |
| `pk_strategy` | string | (optional) `"manual"` when the framework allocates the PK itself (`MAX(pk)+1`) instead of the database — see `pk_assignment` in [DB-FIRST.md](DB-FIRST.md#pk_assignment-optional) |
| `tenant_scope` | string | (optional) The tenancy discriminator column when the mask is tenant-scoped. Reads are automatically filtered to the caller's tenant; the runtime binds it internally, clients never send it — see `tenancy` in [DB-FIRST.md](DB-FIRST.md#tenancy-optional) |

## Use Cases

### AI Agent Building a Frontend

An agent can:
1. Fetch manifest to discover all entities and their fields
2. Generate TypeScript types from field definitions
3. Build API client functions from mask definitions
4. Create forms with validation from validator rules
5. Handle relationships from FK fields

### API Documentation Generator

The manifest contains everything needed to generate OpenAPI or similar docs:
- Entity names and tables
- Field types and constraints
- Available query endpoints with parameters
- Role requirements

### Testing Tools

The manifest tells you:
- What masks exist (for query tests)
- What fields are required (for mutation tests)
- What validators will reject (for error case tests)
- What commands are available (for command tests)

## LLM tool-call format

The manifest can be served directly as a ready-to-paste tool-definitions array for LLM agents — no client-side translation step. Two flavors:

```
GET /api.php?manifest&format=anthropic-tools
GET /api.php?manifest&format=openai-tools
```

Or via POST:

```json
{ "op": "manifest", "payload": { "format": "anthropic-tools" } }
```

**Anthropic shape** — list of tool objects with `name`, `description`, `input_schema`:

```json
{
  "tools": [
    {
      "name": "books_get",
      "description": "Get one row from books by primary key. Returns rows with columns: id, title, author_id.",
      "input_schema": {
        "type": "object",
        "properties": { "id": { "type": "integer" } },
        "required": ["id"]
      }
    },
    {
      "name": "books_mutate",
      "description": "Insert / update / delete rows in books. Columns: id, title, author_id.",
      "input_schema": {
        "type": "object",
        "properties": {
          "action": { "type": "string", "enum": ["insert", "update", "delete"] },
          "data": { "type": "object", "additionalProperties": true },
          "filter": { "type": "object", "additionalProperties": true }
        },
        "required": ["action"]
      }
    }
  ]
}
```

**OpenAI shape** — same content wrapped as `{ type: "function", function: { name, description, parameters } }`.

### Tool naming conventions

- Read masks become `<table>_<verb>` (`books.list` → `books_list`, `books.byAuthorId` → `books_byAuthorId`).
- Writable masks collapse into one `<table>_mutate` tool with an `action` enum (`insert` / `update` / `delete`).
- Commands become tools with the dot replaced by underscore (`order.process` → `order_process`); their input schema is permissive (`additionalProperties: true`) because PHP closures don't carry per-parameter type info today.
- Auto-generated descriptions follow the verb: `list` / `get` / `getMany` / `by<Field>` / `adminList` / `adminGet` get readable English; standalone masks fall back to "Run mask '<name>'".

### Param type mapping

| Shape param `type` | JSON Schema |
|---|---|
| `int` / `integer` | `{"type":"integer"}` |
| `float` / `double` / `decimal` / `number` | `{"type":"number"}` |
| `bool` / `boolean` | `{"type":"boolean"}` |
| `array_int` | `{"type":"array","items":{"type":"integer"}}` |
| `array_string` | `{"type":"array","items":{"type":"string"}}` |
| anything else | `{"type":"string"}` |

## Error envelope

Every error returns a JSON object with `code`, `message`, and (where useful) self-documenting fix hints — so an LLM agent can correct the call without re-fetching the manifest:

```json
{
  "error": {
    "code": "PARAM_VALIDATION_ERROR",
    "message": "Missing required parameter 'id'.",
    "expected": { "id": "int (required)", "tag": "string" },
    "example": { "id": 42 }
  }
}
```

| Field | Always present | Description |
|---|---|---|
| `code` | yes | Machine-readable error code (e.g. `PARAM_VALIDATION_ERROR`, `MASK_NOT_ALLOWED`, `MUTATION_VALIDATION_ERROR`) |
| `message` | yes | Human-readable single-sentence message |
| `expected` | when applicable | Schema fragment describing what the field should look like |
| `example` | when applicable | Minimal valid value/object the agent can submit as a corrected request |
| `hint` | when applicable | One-line plain-English guidance (e.g., "Allowed parameters: id, tag.") |
| `ref` | when in batch | The query/mutation `ref` that triggered the error (for error → request correlation in batch ops) |

Agents should treat the new fields as **additive**: read `code` + `message` as before, then prefer `example` + `expected` when present.

### Where hints appear

These error sites are instrumented (the highest-frequency agent retry loops):

- **Param validation** (`PARAM_VALIDATION_ERROR`): missing required param, unknown param, type mismatch, empty array, all include `expected` + `example`.
- **Top-level request** (`REQUEST_VALIDATION_ERROR`, `QUERY_VALIDATION_ERROR`, `MUTATION_VALIDATION_ERROR`, `COMMAND_VALIDATION_ERROR`): missing `op`, missing `payload`, missing `queries` / `mutations` / `name` arrays — each gets a complete envelope example.
- **Mutation shape** (`MUTATION_VALIDATION_ERROR`): missing `table`, invalid `action`, missing `data` for insert/update, missing `filter` for update/delete — each example shows the corrected mutation object.
- **Mask resolution** (`MASK_NOT_ALLOWED`): includes `hint` pointing at `?manifest`.
- **Auth** (`AUTH_FORBIDDEN`): lists allowed roles, suggests `auth.token` if unauthenticated.
- **Commands** (`COMMAND_NOT_FOUND`): lists configured commands.

Other errors (db connection failures, hook failures, internal errors) keep the basic `code + message` shape — they aren't fixable by retrying the same request.

## Notes

- The manifest reflects the current state of entity definitions at server start.
- Manual masks merged into the config are included in entity mask lists.
- SQL queries are not exposed in the manifest (only mask names and params).
- The manifest is generated from in-memory metadata, not from database introspection.
