# Getting Started with Shape

## 1. Create your app directory

```
myapp/
  entities/
  config/
  database/
  public/
  bootstrap.php
```

## 2. Write the autoloader

```php
<?php
// bootstrap.php
require_once dirname(__DIR__, 2) . '/packages/autoload.php';
```

## 3. Create the entry point

```php
<?php
// public/api.php
require_once __DIR__ . '/../bootstrap.php';

use Shape\ShapeApp;
use Shape\ShapeRouter;

$config = ShapeApp::fromEntities(
    __DIR__ . '/../entities',
    __DIR__ . '/../config/app.php'
);

ShapeRouter::run($config, 'myapp');
```

## 4. Define entities

```php
<?php
// entities/Product.php
namespace MyApp\Entity;

use Shape\Attribute\{Entity, Column, Id, AutoIncrement, Validate, Access};

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

    public bool $in_stock = true;
}
```

## 5. Configure database and auth

```php
<?php
// config/env.php
return [
    'db' => [
        'dsn' => 'mysql:host=localhost;dbname=myapp;charset=utf8mb4',
        'user' => 'myapp',
        'pass' => 'secret',
    ],
    'auth' => [
        'driver' => 'jwt',
        'jwt_secret' => 'your-secret-key',
        'jwt_issuer' => 'myapp.local',
        'jwt_audience' => 'myapp.clients',
        'access_token_ttl' => 3600,
        'refresh_token_ttl' => 2592000,
        'users_table' => 'users',
        'refresh_tokens_table' => 'refresh_tokens',
        'password_min_length' => 8,
        'assignable_roles' => ['user', 'admin'],
    ],
];
```

## 6. Add raw SQL masks, commands, hooks (optional)

Everything in `config/app.php` is optional. Use it for things entities can't express: JOINs, aggregations, business logic commands, and mutation hooks.

```php
<?php
// config/app.php
use ShapeRuntime\Core\ApiException;
use ShapeRuntime\Core\CommandContext;

return [
    // Raw SQL masks — for JOINs, aggregations, custom queries
    'masks' => [
        'products.bestsellers' => [
            'sql' => 'SELECT p.id, p.name, SUM(oi.quantity) AS sold
                      FROM products p
                      JOIN order_items oi ON oi.product_id = p.id
                      GROUP BY p.id, p.name
                      ORDER BY sold DESC
                      LIMIT :limit',
            'params' => ['limit' => ['type' => 'int', 'required' => true]],
            'roles' => ['admin'],
        ],
    ],

    // Commands — business logic workflows
    'commands' => [
        'product.archive' => function(array $params, CommandContext $ctx): array {
            $id = $params['id'] ?? 0;
            if ($id <= 0) {
                throw new ApiException('COMMAND_VALIDATION_ERROR', 'id required', 400);
            }
            $ctx->executeMutations([
                ['table' => 'products', 'action' => 'update',
                 'filter' => ['id' => $id], 'data' => ['in_stock' => false]]
            ], true);
            return ['status' => 200, 'archived' => $id];
        },
    ],
];
```

Raw SQL masks work alongside entity-generated masks. Use for queries that need JOINs, subqueries, or complex WHERE clauses. See [SPEC.md](SPEC.md) section 8 for details.

### Production tip: enable caching

For production, add `'cache' => true` to your config to cache entity metadata. Gives ~10–20x speedup on the boot path. Invalidates automatically when entity or config files change — for DB schema changes in `fromDatabase` mode, clear cache on deploy.

```php
return [
    'cache' => true,  // or '/var/cache/shape'
    'commands' => [...],
];
```

See [DB-FIRST.md](DB-FIRST.md#cache-optional) for details.

## 7. Create the database

```sql
CREATE TABLE products (
  id INT UNSIGNED NOT NULL AUTO_INCREMENT,
  name VARCHAR(255) NOT NULL,
  price DECIMAL(10,2) NOT NULL,
  in_stock TINYINT(1) NOT NULL DEFAULT 1,
  PRIMARY KEY (id)
);
```

Don't forget to create `users` and `refresh_tokens` tables for auth (see `docs/shaperuntime`).

## 8. Run it

```bash
php -S localhost:8080 -t public/
```

## 9. Test it

```bash
# Health check
curl http://localhost:8080/api.php

# Manifest (no auth needed)
curl -X POST http://localhost:8080/api.php \
  -H "Content-Type: application/json" \
  -d '{"op":"manifest"}'

# Login
curl -X POST http://localhost:8080/api.php \
  -H "Content-Type: application/json" \
  -d '{"op":"auth.token","payload":{"email":"admin@example.com","password":"secret"}}'

# List products
curl -X POST http://localhost:8080/api.php \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <token>" \
  -d '{"op":"query","payload":{"queries":[{"ref":"products","sql":"products.list","params":{}}]}}'

# Insert product (auto-validated)
curl -X POST http://localhost:8080/api.php \
  -H "Content-Type: application/json" \
  -H "Authorization: Bearer <token>" \
  -d '{"op":"mutation","payload":{"mutations":[{"table":"products","action":"insert","data":{"name":"Widget","price":9.99}}]}}'
```

## What you didn't write

For that single `Product` entity, Shape auto-generated:
- `products.list` mask
- `products.get` mask
- `products.getMany` mask
- `products` writable mask
- `products.insert` validator for `name` (notEmpty) and `price` (positive)
- `products.update` validator for `name` (notEmpty) and `price` (positive)
- Manifest entry with full field metadata

**Zero manual masks. Zero manual validators. One entity file.**
