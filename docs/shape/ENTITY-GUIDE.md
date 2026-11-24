# Entity Guide

## Quick Start

Create a file in your `entities/` directory:

```php
<?php
namespace App\Entity;

use Shape\Attribute\{Entity, Column, Id, AutoIncrement, FK, Validate, Access, Unique};

#[Entity('products')]
#[Access(roles: ['admin', 'user'])]
class Product
{
    #[Id, AutoIncrement]
    public int $id;

    #[Column(type: 'varchar', length: 255)]
    #[Validate('notEmpty')]
    public string $name;

    #[Column(type: 'decimal', precision: 10, scale: 2)]
    #[Validate('positive')]
    public float $price;

    #[FK(entity: Category::class)]
    public int $category_id;

    #[Column(type: 'text')]
    public ?string $description = null;

    public bool $is_active = true;
}
```

This single file gives you:
- `products.list` — list all products
- `products.get` — get product by ID
- `products.getMany` — get products by multiple IDs
- `products.byCategoryId` — filter by category
- `products` — writable mask for mutations
- Auto-validation: name must not be empty, price must be positive
- Role access: admin and user can read and write

## Type Inference

When you omit `#[Column]`, Shape infers the DB type from PHP types:

| PHP Type | DB Type | Notes |
|----------|---------|-------|
| `int` | `int` | |
| `float` | `double` | |
| `bool` | `tinyint` | |
| `string` | `varchar` | |
| `?string` | `varchar` | Nullable |

Override with `#[Column(type: 'text')]` or `#[Column(type: 'date')]` when needed.

## Primary Keys

Mark the primary key with `#[Id]`:

```php
#[Id, AutoIncrement]
public int $id;
```

For non-auto-increment PKs:

```php
#[Id]
#[Column(unsigned: true)]
public int $product_id;
```

## Foreign Keys

Declare relationships with `#[FK]`:

```php
#[FK(entity: Order::class)]
public int $order_id;
```

This:
- Marks the field as a foreign key
- Generates a `items.byOrderId` mask for filtering

You can customize FK behavior:

```php
#[FK(entity: Category::class, column: 'id', onDelete: 'set null', onUpdate: 'cascade')]
public ?int $category_id = null;
```

## Validation Rules

Add `#[Validate]` to enforce field constraints. Multiple rules can be stacked:

```php
#[Validate('notEmpty')]
#[Validate('email')]
public string $email;
```

Built-in rules:

| Rule | PHP Check | Error Message |
|------|-----------|---------------|
| `positive` | `is_numeric($v) && $v > 0` | `{field} must be positive` |
| `nonNegative` | `is_numeric($v) && $v >= 0` | `{field} must be non-negative` |
| `notEmpty` | `is_string($v) && trim($v) !== ''` | `{field} is required` |
| `email` | `filter_var($v, FILTER_VALIDATE_EMAIL)` | `{field} must be a valid email` |

Validators apply to both `insert` and `update` mutations.

## Descriptions, Enums, Unique Constraints

Use `description` fields when you want generated docs and tool descriptions to stay human-readable without hand-written overrides:

```php
#[Entity('products', description: 'Catalog products', writableMode: 'append_only')]
#[Access(roles: ['admin', 'user'])]
class Product
{
    #[Id, AutoIncrement]
    public int $id;

    #[Column(type: 'varchar', length: 40, values: ['draft', 'published', 'archived'], description: 'Lifecycle state')]
    #[Validate('in', values: ['draft', 'published', 'archived'])]
    public string $status = 'draft';
}
```

What that gives you:

- `description` shows up in the manifest, OpenAPI, and MCP docs.
- `Column(values: [...])` becomes `enum` in the manifest/OpenAPI field schema.
- `Validate('in', values: [...])` generates the same closed-value validator.
- `#[Unique(['tenant_id', 'slug'])]` declares a composite unique constraint and surfaces it in metadata.
- `writableMode: 'append_only'` makes the entity insert-only even though it still appears writable in the source model.

If you want a hard read-only entity, `#[Entity(writable: false)]` and `#[Entity(writableMode: 'none')]` are equivalent in effect.

## Access Control

Control who can read and write:

```php
// Same roles for read and write
#[Access(roles: ['admin', 'user'])]
class Order { ... }

// Different roles for read vs write
#[Access(read: ['admin', 'user', 'viewer'], write: ['admin'])]
class Product { ... }

// No Access attribute = public (no role check)
#[Entity('public_pages')]
class Page { ... }
```

## Default Values

Use PHP default property values:

```php
public string $status = 'draft';
public int $in_stock = 0;
public bool $is_active = true;
public ?string $notes = null;
```

Nullable types (`?string`) indicate the field is optional.

## Read-Only Entities

Prevent mutations entirely:

```php
#[Entity('audit_logs', writable: false)]
#[Access(roles: ['admin'])]
class AuditLog
{
    #[Id, AutoIncrement]
    public int $id;

    public string $action;
    public string $timestamp;
}
```

This generates only read masks (list, get, getMany) — no writable mask.

## Complete Example: E-Commerce

### entities/Customer.php
```php
#[Entity('customers')]
#[Access(roles: ['admin', 'user'])]
class Customer
{
    #[Id, AutoIncrement]
    public int $id;

    #[Column(type: 'varchar', length: 190)]
    #[Validate('notEmpty')]
    #[Validate('email')]
    public string $email;

    #[Column(type: 'varchar', length: 120)]
    #[Validate('notEmpty')]
    public string $name;

    #[Column(type: 'varchar', length: 20)]
    public string $phone = '';
}
```

### entities/Order.php
```php
#[Entity('orders')]
#[Access(read: ['admin', 'user'], write: ['admin'])]
class Order
{
    #[Id, AutoIncrement]
    public int $id;

    #[FK(entity: Customer::class)]
    public int $customer_id;

    #[Column(type: 'decimal', precision: 10, scale: 2)]
    #[Validate('positive')]
    public float $total;

    #[Column(type: 'varchar', length: 20)]
    public string $status = 'draft';

    #[Column(type: 'date')]
    public ?string $shipped_at = null;
}
```

### entities/OrderItem.php
```php
#[Entity('order_items')]
#[Access(roles: ['admin', 'user'])]
class OrderItem
{
    #[Id, AutoIncrement]
    public int $id;

    #[FK(entity: Order::class)]
    public int $order_id;

    #[FK(entity: Product::class)]
    public int $product_id;

    public int $quantity = 1;

    #[Column(type: 'decimal', precision: 10, scale: 2)]
    #[Validate('nonNegative')]
    public float $unit_price;
}
```

### Generated masks (automatic)

From these 3 entities, Shape generates:
- `customers.list`, `customers.get`, `customers.getMany`, `customers` (writable)
- `orders.list`, `orders.get`, `orders.getMany`, `orders.byCustomerId`, `orders` (writable)
- `order_items.list`, `order_items.get`, `order_items.getMany`, `order_items.byOrderId`, `order_items.byProductId`, `order_items` (writable)

**15 masks from 3 entity files.** Zero manual configuration needed.

## Raw SQL: The Escape Hatch

Entities cover standard CRUD patterns. For JOINs, aggregations, subqueries, or any custom SQL, add raw masks in `config/app.php`:

```php
return [
    'masks' => [
        // JOIN across entities
        'orders.withCustomer' => [
            'sql' => 'SELECT o.id, o.total, c.name AS customer_name
                      FROM orders o
                      JOIN customers c ON c.id = o.customer_id
                      WHERE o.id = :id',
            'params' => ['id' => ['type' => 'int', 'required' => true]],
            'roles' => ['admin', 'user'],
        ],

        // Aggregation
        'orders.revenueByCustomer' => [
            'sql' => 'SELECT c.name, COUNT(o.id) AS orders, SUM(o.total) AS revenue
                      FROM orders o
                      JOIN customers c ON c.id = o.customer_id
                      GROUP BY c.id, c.name
                      ORDER BY revenue DESC',
            'params' => [],
            'roles' => ['admin'],
        ],

        // Subquery
        'customers.withoutOrders' => [
            'sql' => 'SELECT id, name, email FROM customers
                      WHERE id NOT IN (SELECT DISTINCT customer_id FROM orders)',
            'params' => [],
            'roles' => ['admin'],
        ],
    ],
];
```

Raw SQL masks work side-by-side with entity-generated masks. Use the same naming convention (`{table}.{operation}`) for consistency.

See [SPEC.md](SPEC.md) section 8 for full details on raw SQL masks and merge rules.
