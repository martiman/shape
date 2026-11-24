# Mutation API Design

## Overview

`op: "mutation"` handles role-checked INSERT, UPDATE, and DELETE operations.

Mutations use the same allowlist model as queries: the client sends table/action/data, while the server decides what is writable from configured masks.

## Request Contract

```json
{
  "op": "mutation",
  "payload": {
    "transaction": false,
    "mutations": [
      {
        "table": "products",
        "action": "insert|update|delete",
        "ref": "optional_result_reference",
        "filter": { /* for update/delete */ },
        "data": { /* for insert/update */ }
      }
    ]
  }
}
```

### Payload Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `transaction` | boolean | ✗ | Wrap mutations in database transaction; default `false`. If `true`, all mutations commit together or rollback on error |
| `mutations` | array | ✓ | List of mutation operations |

### Mutation Item Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `table` | string | ✓ | Table name; must exist in mask registry for user's role |
| `action` | string | ✓ | One of: `insert`, `update`, `delete` |
| `ref` | string | ✗ | Optional label for result; enables composition in batch operations |
| `filter` | object | ✗ | WHERE conditions; required for UPDATE/DELETE; ignored for INSERT |
| `data` | object | ✗ | Column assignments; required for INSERT/UPDATE; ignored for DELETE |

## Security Model

### Access Control
- User's role determines which tables are writable via mask registry
- Client cannot mutate any table not in their role's mask
- Attempting to mutate a restricted table returns `403 Forbidden`

### Column-Level Restrictions (Optional)
- Masks can define read-only columns that cannot be modified
- Attempting to set a read-only column returns `400 Bad Request`
- Read-only columns in INSERT operations are ignored if not required by schema

### Filter Validation
- Filters must reference columns that exist in the table schema
- Filter values are parameterized (no SQL injection possible)
- Complex filters use simple AND logic: `{ col1: val1, col2: val2 }` → `col1 = ? AND col2 = ?`
- NULL comparisons supported: `{ status: null }` → `status IS NULL`

### Data Validation
- Column names must exist in table schema
- Column types are validated against data (e.g., integer columns reject strings)
- NOT NULL columns must be provided in INSERT statements
- Foreign key constraints are enforced by database

## Validation & Hooks

Mutations can be extended with field validators and before/after hooks for business rules and side effects.

### Field Validators

Validators are registered per table/action and check field values before mutation execution:

```php
// In config/{app}/app.php
'validators' => [
    'products.insert' => [
        'validators' => [
            'price' => fn($val) => $val > 0 ? null : 'Price must be positive',
            'stock' => fn($val) => is_int($val) && $val >= 0 ? null : 'Stock must be non-negative',
            'sku' => fn($val) => strlen($val) >= 3 ? null : 'SKU must be at least 3 characters',
        ]
    ],
    'products.update' => [
        'validators' => [
            'price' => fn($val) => $val > 0 ? null : 'Price must be positive',
        ]
    ],
]
```

**Validator Signature:** `fn($value): ?string` — returns error message if invalid, null if valid

### Before/After Hooks

Hooks execute before or after mutation operations for side effects like notifications, logging, or cascading changes:

```php
// In config/{app}/app.php
'validators' => [
    'orders.insert' => [
        'hooks' => [
            'before' => fn($context) => validateInventory($context),
            'after' => fn($context) => notifyWarehouse($context),
        ]
    ],
    'products.update' => [
        'hooks' => [
            'before' => fn($context) => checkPriceChangePolicy($context),
            'after' => fn($context) => invalidateCache($context),
        ]
    ],
]
```

**Hook Context:** `['table' => string, 'action' => string, 'data' => array, 'filter' => array]` for before; `['table' => string, 'action' => string, 'affected' => int]` for after

**Hook Signature:** `fn($context): void` — throws exception on error

## Transactions

Wrap multiple mutations in a database transaction for atomicity. All mutations succeed together or all rollback on any error:

```json
{
  "op": "mutation",
  "payload": {
    "transaction": true,
    "mutations": [
      {
        "table": "orders",
        "action": "insert",
        "ref": "new_order",
        "data": {
          "user_id": 42,
          "total": 199.99,
          "status": "pending"
        }
      },
      {
        "table": "order_items",
        "action": "insert",
        "data": {
          "order_id": "$new_order.id",
          "product_id": 7,
          "quantity": 2,
          "price": 99.99
        }
      },
      {
        "table": "inventory",
        "action": "update",
        "filter": { "product_id": 7 },
        "data": { "stock": "stock - 2" }
      }
    ]
  }
}
```

**Behavior:**
- If any mutation fails (validation error, constraint violation, etc.), entire transaction rolls back
- If all mutations succeed, transaction commits atomically
- Default: `transaction: false` (each mutation independent)

## Operations

### INSERT
Creates a new row with provided column values.

```json
{
  "op": "mutation",
  "payload": {
    "mutations": [
      {
        "table": "products",
        "action": "insert",
        "ref": "new_product",
        "data": {
          "name": "Widget Pro",
          "price": 99.99,
          "category": "electronics",
          "in_stock": true
        }
      }
    ]
  }
}
```

**Response:**
```json
{
  "status": 200,
  "results": [
    {
      "ref": "new_product",
      "action": "insert",
      "table": "products",
      "affected": 1,
      "id": 42
    }
  ]
}
```

`id` is the new row's primary-key value: the database-assigned identity/auto-increment
value, the client-supplied value when the insert `data` carried the PK explicitly, or —
on tables resolved to manual PK assignment (see `pk_assignment` in
[DB-FIRST.md](../shape/DB-FIRST.md#pk_assignment-optional)) — the value the framework
allocated atomically as `MAX(pk) + 1`. In the manual case a persistent allocation race
(e.g. a legacy app inserting alongside with its own `MAX+1`) surfaces after 3 retries as
`DB_ERROR` with detail `PK_ALLOCATION_RACE`.

### UPDATE
Modifies existing rows matching filter conditions.

```json
{
  "op": "mutation",
  "payload": {
    "mutations": [
      {
        "table": "products",
        "action": "update",
        "ref": "update_price",
        "filter": { "id": 42, "category": "electronics" },
        "data": { "price": 89.99, "updated_at": "2026-04-10T10:30:00Z" }
      }
    ]
  }
}
```

**Response:**
```json
{
  "status": 200,
  "results": [
    {
      "ref": "update_price",
      "action": "update",
      "table": "products",
      "affected": 1
    }
  ]
}
```

**Safety Note:** If filter matches 0 rows, `affected: 0` is returned (not an error). If filter is omitted, update is rejected as unsafe.

### DELETE
Removes rows matching filter conditions.

```json
{
  "op": "mutation",
  "payload": {
    "mutations": [
      {
        "table": "audit_logs",
        "action": "delete",
        "ref": "cleanup",
        "filter": { "id": 9999999 }
      }
    ]
  }
}
```

**Response:**
```json
{
  "status": 200,
  "results": [
    {
      "ref": "cleanup",
      "action": "delete",
      "table": "audit_logs",
      "affected": 0
    }
  ]
}
```

**Safety Note:** DELETE without a filter is rejected (no unfiltered deletes allowed).

## Ready-to-run examples

### Transaction example

```json
{
  "op": "mutation",
  "payload": {
    "transaction": true,
    "mutations": [
      {
        "table": "orders",
        "action": "insert",
        "data": {
          "user_id": 1,
          "total": 199.99,
          "status": "pending"
        }
      },
      {
        "table": "order_items",
        "action": "insert",
        "data": {
          "order_id": 1,
          "product_id": 42,
          "quantity": 2,
          "price": 99.99
        }
      }
    ]
  }
}
```

### Validation example (expected to fail)

Requires validator for `products.update.price` such as `price > 0`.

```json
{
  "op": "mutation",
  "payload": {
    "mutations": [
      {
        "table": "products",
        "action": "update",
        "filter": { "id": 42 },
        "data": { "price": -15 }
      }
    ]
  }
}
```

### Hook example (before + after)

Requires hooks on `products.update` in config. The same mutation triggers both hooks.

```json
{
  "op": "mutation",
  "payload": {
    "mutations": [
      {
        "table": "products",
        "action": "update",
        "filter": { "id": 42 },
        "data": {
          "price": 109.99,
          "stock": 12
        }
      }
    ]
  }
}
```

## Batch Mutations

Mutations can reference results from previous queries or mutations in the same batch using `$ref.field` syntax:

```json
{
  "op": "batch",
  "payload": [
    {
      "sql": "order_list",
      "query": "SELECT id, user_id FROM orders WHERE status = 'pending'",
      "ref": "pending_orders"
    },
    {
      "table": "order_status_log",
      "action": "insert",
      "data": {
        "order_id": "$pending_orders.id",
        "status": "processing",
        "timestamp": "2026-04-10T10:30:00Z"
      }
    }
  ]
}
```

**Supported References:**
- `$ref.field` — value from first row of referenced query result
- `$ref[index].field` — value from specific row (e.g., `$pending_orders[2].id`)
- Array references for multi-row mutations coming in future version

## Error Handling

### Client Errors (4xx)

| Status | Condition |
|--------|-----------|
| 400 | Invalid table name, action, or schema validation failure |
| 400 | Required fields missing (data for INSERT/UPDATE, filter for UPDATE/DELETE) |
| 400 | Column type mismatch or constraint violation |
| 403 | Table not in user's writable mask |
| 403 | Attempting to modify read-only column |

### Server Errors (5xx)

| Status | Condition |
|--------|-----------|
| 500 | Database connection error |
| 500 | Unexpected constraint violation (should be validated client-side) |

**Error Response Format:**
```json
{
  "status": 400,
  "error": {
    "code": "INVALID_COLUMN",
    "message": "Column 'invalid_col' does not exist in table 'products'"
  }
}
```

## Best Practices

1. **Always use filters for UPDATE/DELETE** — even if targeting a single row, use explicit filter
2. **Avoid large batch updates** — consider chunking mutations to avoid timeouts
3. **Validate data shape before sending** — client-side type checking prevents unnecessary server round trips
4. **Reference mutation results carefully** — ensure field names exist in results you're referencing
5. **Handle partial failures** — early mutations in batch may succeed even if later ones fail; check `affected` count

## Future Enhancements

- **Transactions:** Group mutations in explicit transaction block with rollback on any failure
- **Bulk operations:** Multi-row INSERT/UPDATE/DELETE support
- **Foreign key cascades:** Configure cascade behavior per mutation
- **Audit logging:** Automatic tracking of who changed what, when
- **Soft deletes:** Support for logical deletion with timestamp
