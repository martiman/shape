# ShapeRuntime Specification (v0.1)

## 1. Scope
ShapeRuntime is a declarative PHP SQL gateway inspired by the public Punkbase presentation.

Goals:
- Single HTTP POST endpoint for batched data queries.
- Strict allowlist execution via query masks.
- Parameter binding and reference resolution between batched queries.
- Zero runtime dependencies.

Non-goals (v0.1):
- SQL parsing beyond mask enforcement.
- Database migrations.
- Role-based policy DSL.
- Streaming chunked responses.

## 2. Runtime Requirements
- PHP 8.2+ (works on shared hosting).
- PDO extension and target driver (example: `pdo_mysql`).

## 3. Endpoint
- Path: `/api.php`.
- Method: `POST` for API operations (`GET /api.php` for health/info only).
- Content-Type: `application/json`.
- Auth: configurable (`none` or JWT HS256), enforced per operation.

## 4. Request Contract

```json
{
  "op": "query",
  "payload": {
    "queries": [
      {
        "ref": "order",
        "sql": "orderById",
        "params": { "id": 42 }
      },
      {
        "ref": "items",
        "sql": "itemsByOrderId",
        "params": {
          "order_id": { "ref": "order", "value": "id" }
        }
      },
      {
        "ref": "stock",
        "sql": "stockByProductIds",
        "params": {
          "product_ids": { "ref": "items", "list": "product_id" }
        }
      }
    ]
  }
}
```

Rules:
- `op` is required and selects operation handler.
- `payload` is required and must be an object.
- For `op = query`, `payload.queries` is required and must be a non-empty array.
- Each query requires:
  - `ref` (string, unique in request)
  - `sql` (string; mask key or exact configured SQL)
  - `params` (object; optional if mask has no params)
- Optional `options` object supports:
  - `orderby`: list of `{ field, dir }`, where `dir` is `asc|desc`
  - `skip`: integer >= 0
  - `top`: integer >= 0, max 200
  - `count`: boolean (includes `total_count` in result)
  - `groupby`: list of field names
  - `aggregate`: list of `{ field, fn, as }`, where `fn` is `count|sum|avg|min|max`
- Raw SQL from request is never executed unless it exactly matches a configured mask SQL.

## 5. Mutation Contract

For `op = mutation`, payload structure:

```json
{
  "op": "mutation",
  "payload": {
    "mutations": [
      {
        "table": "products",
        "action": "insert",
        "ref": "new_product",
        "data": { "name": "Widget", "price": 29.99 }
      },
      {
        "table": "products",
        "action": "update",
        "filter": { "id": 42 },
        "data": { "price": 24.99 }
      },
      {
        "table": "audit_logs",
        "action": "delete",
        "filter": { "created_before": "2025-01-01" }
      }
    ]
  }
}
```

Rules:
- `payload.mutations` is required and must be a non-empty array.
- Each mutation requires:
  - `table` (string; checked against user's writable masks)
  - `action` (string; one of `insert`, `update`, `delete`)
- For `insert`: requires `data` (object with column assignments)
- For `update`: requires `filter` (object with WHERE conditions) and `data`
- For `delete`: requires `filter` (object with WHERE conditions)
- Optional `ref` (string, unique in request) for result reference
- Filter values support `null` for IS NULL comparisons
- All column names and values are validated; joins are not supported

Mutation Response (`200`):
```json
{
  "results": [
    {
      "ref": "new_product",
      "action": "insert",
      "table": "products",
      "affected": 1
    },
    {
      "ref": null,
      "action": "update",
      "table": "products",
      "affected": 1
    },
    {
      "ref": null,
      "action": "delete",
      "table": "audit_logs",
      "affected": 127
    }
  ]
}
```

Mutation Error Codes (4xx):
- `MUTATION_VALIDATION_ERROR` - malformed mutation request
- `AUTHORIZATION_ERROR` - table not writable for user's role
- `DB_ERROR` - database constraint violation or connection failure

## 5b. Transactions & Validators

### Transactions
Wrap multiple mutations in atomic database transaction:
- Add `"transaction": true` to mutation payload
- All mutations succeed together or all rollback on any error
- Default: `false` (each mutation independent)

### Field Validators
Register field-level validators per table/action:
```php
'validators' => [
    'products.insert' => [
        'validators' => [
            'price' => fn($val) => $val > 0 ? null : 'Price must be positive',
        ],
        'hooks' => [
            'before' => fn($ctx) => validateInventory($ctx),
            'after' => fn($ctx) => notifyWarehouse($ctx),
        ]
    ]
]
```

### Hooks
Before/after hooks execute during mutation lifecycle:
- `before` hook: runs after validators, before database mutation
- `after` hook: runs after database mutation succeeds
- Hooks can throw exceptions to prevent mutation or trigger side effects

## 5c. Commands

The `op: "command"` operation executes domain-level business logic:

```json
{
  "op": "command",
  "payload": {
    "name": "order.process",
    "params": {
      "order_id": 42,
      "payment_method": "credit_card"
    }
  }
}
```

Command Handler Response (`200`):
```json
{
  "status": 200,
  "order_id": 42,
  "new_status": "processing",
  "mutations_applied": 2
}
```

Command Error Codes:
- `COMMAND_NOT_FOUND` (404) - command name does not exist
- `COMMAND_VALIDATION_ERROR` (400) - invalid command parameters
- `COMMAND_ERROR` (500) - handler execution failed
- `VALIDATION_ERROR` (400) - business logic validation failed

Rules:
- Commands are registered in config with handler functions
- Handlers receive (`params`, `CommandContext`)
- CommandContext provides: `executeMutations()`, `beginTransaction()`, `commit()`, `rollback()`, `getAuthContext()`
- Commands enforce authorization via auth context
- Commands can execute multiple mutations atomically

## 6. Mask Contract
Masks are configured server-side.

```php
[
  'orderById' => [
    'sql' => 'SELECT id, created_at, total FROM orders WHERE id = :id LIMIT 1',
    'params' => [
      'id' => ['type' => 'int', 'required' => true],
    ],
  ],
]
```

Mask fields:
- `sql` (required): SQL template with named placeholders.
- `params` (optional): parameter schema map.

Parameter schema fields:
- `type`: `int|float|string|bool|array_int|array_string|mixed`.
- `required`: boolean (default `false`).

## 7. Parameter Resolution
Params support 3 forms:

1. Scalar literal
```json
{ "id": 42 }
```

2. Scalar reference
```json
{ "order_id": { "ref": "order", "value": "id" } }
```
Semantics:
- Take first row of result named by `ref`.
- Resolve column by name from that result metadata.

3. List reference
```json
{ "product_ids": { "ref": "items", "list": "product_id" } }
```
Semantics:
- Collect values from all rows for the named column.
- Used with `IN (:product_ids)` style placeholders.

## 8. SQL Execution Rules
- Queries execute in request order.
- All placeholders must be bound.
- Unknown parameters are rejected.
- Array parameters are expanded to multiple PDO placeholders.
- Empty arrays are rejected.

## 9. Response Contract

Success (`200`):
```json
{
  "results": [
    {
      "ref": "order",
      "meta": {
        "columns": ["id", "created_at", "total"],
        "ms": 1.43,
        "rows": 1
      },
      "rows": [[42, "2026-03-15", 1388.99]],
      "count": 1
    }
  ]
}
```

Error (`4xx` or `5xx`):
```json
{
  "error": {
    "code": "MASK_NOT_ALLOWED",
    "message": "Query is not allowed by masks.",
    "ref": "order"
  }
}
```

## 10. Error Codes
- `BAD_REQUEST`
- `INVALID_JSON`
- `QUERY_VALIDATION_ERROR`
- `DUPLICATE_REF`
- `MASK_NOT_ALLOWED`
- `PARAM_VALIDATION_ERROR`
- `PARAM_REFERENCE_ERROR`
- `AUTH_REQUIRED`
- `AUTH_INVALID`
- `DB_ERROR`
- `INTERNAL_ERROR`

## 11. Security Model
- Allowlist-only query execution.
- Prepared statements only.
- Optional JWT verification with HMAC SHA-256.
- Minimal error leakage in production.

## 12. Configuration Contract

```php
return [
  'db' => [
    'dsn' => 'mysql:host=127.0.0.1;dbname=app;charset=utf8mb4',
    'user' => 'app',
    'pass' => 'secret',
    'options' => [],
  ],
  'auth' => [
    'driver' => 'none',
    'jwt_secret' => '',
    'jwt_issuer' => null,
    'jwt_audience' => null,
  ],
  'masks' => [],
];
```

## 13. Compatibility Notes
This is a clean-room implementation based only on public web presentation behavior.
Unknown private/internal Punkbase details are intentionally not claimed.
