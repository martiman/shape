# Command API Design

## Overview

`op: "command"` is the domain layer above raw mutations.

Use commands for workflows that need validation, authorization checks, and multiple writes wrapped in one business operation.

## Request Contract

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

### Payload Fields

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `name` | string | ✓ | Command name (e.g., `order.process`, `user.register`) |
| `params` | object | ✗ | Command parameters; structure depends on command implementation |

## Security Model

### Authorization
- User's role determines which commands are accessible
- Commands inherit auth context from the request (JWT token)
- Commands can enforce role checks and data ownership constraints
- Attempting to execute unauthorized command returns `403 Forbidden`

### Isolation
- Commands execute within their own context
- Each command receives auth context for permission checks
- Commands cannot directly access resources outside their defined scope

## Command Handlers

Commands are registered in configuration with handler functions that receive parameters and execution context:

```php
// In config/{app}/app.php
'commands' => [
    'order.process' => function(array $params, CommandContext $context) {
        $orderId = $params['order_id'] ?? null;
        $paymentMethod = $params['payment_method'] ?? null;

        if ($orderId === null || $paymentMethod === null) {
            throw new ApiException('COMMAND_VALIDATION_ERROR', 'Missing required parameters.', 400);
        }

        // Authorize: check user owns order
        if (!userOwnsOrder($context->getAuthContext(), $orderId)) {
            throw new ApiException('AUTHORIZATION_ERROR', 'Order not found or access denied.', 403);
        }

        // Execute mutations in transaction
        $mutations = [
            [
                'table' => 'orders',
                'action' => 'update',
                'filter' => ['id' => $orderId],
                'data' => ['status' => 'processing', 'payment_method' => $paymentMethod]
            ],
            [
                'table' => 'order_logs',
                'action' => 'insert',
                'data' => ['order_id' => $orderId, 'event' => 'payment_initiated', 'timestamp' => date('c')]
            ]
        ];

        $result = $context->executeMutations($mutations, useTransaction: true);

        // Return domain result
        return [
            'status' => 200,
            'order_id' => $orderId,
            'new_status' => 'processing',
            'mutations_applied' => count($result['results'])
        ];
    },

    'user.register' => function(array $params, CommandContext $context) {
        $email = $params['email'] ?? null;
        $password = $params['password'] ?? null;
        $name = $params['name'] ?? null;

        if (!$email || !$password || !$name) {
            throw new ApiException('COMMAND_VALIDATION_ERROR', 'Missing required fields.', 400);
        }

        if (!filter_var($email, FILTER_VALIDATE_EMAIL)) {
            throw new ApiException('VALIDATION_ERROR', 'Invalid email address.', 400);
        }

        // Check email not already registered
        $context->beginTransaction();
        try {
            $mutations = [
                [
                    'table' => 'users',
                    'action' => 'insert',
                    'ref' => 'new_user',
                    'data' => [
                        'email' => $email,
                        'name' => $name,
                        'password_hash' => password_hash($password, PASSWORD_BCRYPT),
                        'created_at' => date('c')
                    ]
                ],
                [
                    'table' => 'user_profiles',
                    'action' => 'insert',
                    'data' => [
                        'user_id' => '$new_user.id',
                        'role' => 'user',
                        'status' => 'active'
                    ]
                ]
            ];

            $result = $context->executeMutations($mutations, useTransaction: true);
            $context->commit();

            return [
                'status' => 201,
                'user_id' => $result['results'][0]['ref'],
                'email' => $email,
                'message' => 'User registered successfully'
            ];
        } catch (Throwable $e) {
            if ($context->inTransaction()) {
                $context->rollback();
            }
            throw $e;
        }
    },
]
```

## CommandContext API

Commands receive a `CommandContext` object providing access to the database and mutation executor:

### executeMutations
```php
$result = $context->executeMutations(
    array $mutations,
    bool $useTransaction = true
): array
```
Execute a batch of mutations. Returns results array.

### Transaction Methods
```php
$context->beginTransaction(): void
$context->commit(): void
$context->rollback(): void
$context->inTransaction(): bool
```
Explicit transaction control for complex workflows.

### getAuthContext
```php
$authContext = $context->getAuthContext(): array
```
Get the auth context (user, role, etc.) to check permissions or data ownership.

## Response Contract

Success (`200` or `201`):
```json
{
  "status": 200,
  "order_id": 42,
  "new_status": "processing",
  "mutations_applied": 2
}
```

## Ready-to-run command examples

### Example 1: Process order

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

```json
{
    "status": 200,
    "order_id": 42,
    "new_status": "processing",
    "mutations_applied": 2
}
```

### Example 2: Register user

```json
{
    "op": "command",
    "payload": {
        "name": "user.register",
        "params": {
            "email": "newuser@example.com",
            "password": "SecurePassword123",
            "name": "New User"
        }
    }
}
```

```json
{
    "status": 201,
    "message": "User registered successfully",
    "email": "newuser@example.com"
}
```

### Example 3: Custom command (your own)

In config:

```php
'commands' => [
        'billing.charge_invoice' => function(array $params, ShapeRuntime\Core\CommandContext $context) {
                $invoiceId = $params['invoice_id'] ?? null;
                $gateway = $params['gateway'] ?? 'stripe';

                if ($invoiceId === null) {
                        throw new ShapeRuntime\Core\ApiException('COMMAND_VALIDATION_ERROR', 'invoice_id required', 400);
                }

                $result = $context->executeMutations([
                        [
                                'table' => 'invoices',
                                'action' => 'update',
                                'filter' => ['id' => $invoiceId],
                                'data' => ['status' => 'paid', 'paid_via' => $gateway, 'paid_at' => date('c')]
                        ],
                        [
                                'table' => 'billing_events',
                                'action' => 'insert',
                                'data' => ['invoice_id' => $invoiceId, 'event' => 'charged', 'gateway' => $gateway, 'created_at' => date('c')]
                        ]
                ], useTransaction: true);

                return [
                        'status' => 200,
                        'invoice_id' => $invoiceId,
                        'result' => 'charged',
                        'mutations_applied' => count($result['results'])
                ];
        },
]
```

Request:

```json
{
    "op": "command",
    "payload": {
        "name": "billing.charge_invoice",
        "params": {
            "invoice_id": 9001,
            "gateway": "stripe"
        }
    }
}
```

Error (`4xx` or `5xx`):
```json
{
  "error": {
    "code": "COMMAND_VALIDATION_ERROR",
    "message": "Invalid parameters provided to command"
  }
}
```

## Error Codes

| Code | Status | Meaning |
|------|--------|---------|
| `COMMAND_NOT_FOUND` | 404 | Command name does not exist |
| `COMMAND_VALIDATION_ERROR` | 400 | Invalid command parameters |
| `COMMAND_ERROR` | 500 | Handler execution failed |
| `VALIDATION_ERROR` | 400 | Business logic validation failed |
| `AUTHORIZATION_ERROR` | 403 | User lacks permission for command |
| `DB_ERROR` | 500 | Database operation failed |

## Best Practices

1. **Fail Fast:** Validate all parameters and permissions before executing mutations
2. **Atomic Operations:** Use transactions for commands that modify multiple tables
3. **Clear Errors:** Return specific error messages to help clients diagnose issues
4. **Immutable Params:** Don't modify the `$params` array; it may be logged
5. **Document Side Effects:** Commands can trigger notifications or external calls; document them
6. **Explicit Transactions:** Use `$context->beginTransaction()` when you need manual control over rollback

## Examples

### Inventory Management
```php
'inventory.adjust' => function(array $params, CommandContext $context) {
    $productId = $params['product_id'] ?? null;
    $quantity = $params['quantity'] ?? null; // Can be negative for destock
    $reason = $params['reason'] ?? 'adjustment';

    $mutations = [
        ['table' => 'inventory', 'action' => 'update', 'filter' => ['product_id' => $productId], 'data' => ['stock' => 'stock + ' . $quantity]],
        ['table' => 'inventory_log', 'action' => 'insert', 'data' => ['product_id' => $productId, 'quantity_change' => $quantity, 'reason' => $reason, 'timestamp' => date('c')]]
    ];

    return $context->executeMutations($mutations, useTransaction: true);
},
```

### Batch Operations
```php
'orders.ship_batch' => function(array $params, CommandContext $context) {
    $orderIds = $params['order_ids'] ?? []; // Array of IDs
    $carrier = $params['carrier'] ?? 'standard';

    $mutations = [];
    foreach ($orderIds as $orderId) {
        $mutations[] = [
            'table' => 'orders',
            'action' => 'update',
            'filter' => ['id' => $orderId],
            'data' => ['status' => 'shipped', 'carrier' => $carrier, 'shipped_at' => date('c')]
        ];
    }

    return $context->executeMutations($mutations, useTransaction: true);
},
```

### State Machine
```php
'workflow.transition' => function(array $params, CommandContext $context) {
    $recordId = $params['record_id'] ?? null;
    $toState = $params['to_state'] ?? null;

    // Validate transition is allowed
    $allowedTransitions = ['draft' => ['review', 'archived'], 'review' => ['approved', 'draft'], 'approved' => ['published', 'rejected']];
    // ...check transition logic...

    $mutations = [
        ['table' => 'records', 'action' => 'update', 'filter' => ['id' => $recordId], 'data' => ['status' => $toState, 'updated_at' => date('c')]],
        ['table' => 'audit', 'action' => 'insert', 'data' => ['record_id' => $recordId, 'action' => 'state_transition', 'old_state' => $fromState, 'new_state' => $toState]]
    ];

    return $context->executeMutations($mutations, useTransaction: true);
},
```
