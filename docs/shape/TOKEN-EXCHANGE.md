# Token exchange (`auth.exchange`) — bridging an existing auth system into Shape

> **Status:** implemented in **.NET and PHP** — .NET:
> `Auth/ITokenExchangeSource.cs`, `LoginService.Exchange`, op wiring in
> `OpDispatcher` (tests: `TokenExchangeTests.cs`); PHP:
> `LoginService::exchange()`, op wiring in `ShapeRouter`/`AppRouter`
> (tests: `php/tests/TokenExchange.phpt`). Contract byte-identical.
> Origin: the brownfield adapter guide §3;
> PHP port plan: [TOKEN-EXCHANGE-PHP-PLAN.md](TOKEN-EXCHANGE-PHP-PLAN.md) (done).

## When you need this

Your app already has users, sessions, and its own tokens — opaque DB-backed
bearers, session ids, anything that is **not** a JWT Shape can verify. You
want those users to call a Shape API without maintaining a second password
store. (If your tokens *are* IdP-issued JWTs — Entra, Auth0, Okta — use the
OIDC path in [AUTH.md](AUTH.md) instead; it verifies them directly.)

## How it works — exchange once, then plain Shape

```
client ──(external token)──▶ auth.exchange ──▶ your ITokenExchangeSource.Validate()
                                                    │  identity {id, email, role, tenant_id}
                                              ◀─────┘
client ◀──(Shape JWT: access_token)──────────┘
client ──(Bearer <Shape JWT>)──▶ every other op — ordinary Shape client
```

The external token is verified **once**, by *your* code. Shape then issues its
own JWT, and everything downstream — tenancy claims and predicates, role
gates, `roles_by_action`, suspension checks — applies through the standard
machinery with zero special-casing.

**Stateless by default:** the response carries an access token only. When it
expires, the client re-exchanges its (still valid) external token. No
`refresh_tokens` table, no rotation state — right for apps whose external
system already manages sessions. Set `IssueRefreshToken = true` only if you
want the full Shape refresh pair (requires the refresh_tokens table and
int-compatible user ids).

## Wiring it up

```csharp
// 1. Implement the source — the ONLY auth code your app keeps.
public sealed class MyTokenSource : ITokenExchangeSource
{
    public Dictionary<string, object?>? Validate(string externalToken, Database db)
    {
        // Look the token up in YOUR tables. Return null when it is
        // invalid / expired / revoked → Shape answers AUTH_EXCHANGE_INVALID (401).
        var row = db.Run("SELECT ... FROM my_tokens WHERE ...", new() { ["t"] = externalToken });
        if (row.Rows.Count == 0) return null;

        return new Dictionary<string, object?>
        {
            ["id"]        = /* user id — int or Guid, kept verbatim in `sub` */,
            ["email"]     = /* … */,
            ["role"]      = /* app role string */,
            ["tenant_id"] = /* org id, when tenancy is on */
        };
    }
}

// 2. Register it (code, not JSON — the source is an object).
config.Auth.Exchange = new TokenExchangeConfig
{
    Source       = new MyTokenSource(),
    ClaimMapping = new() { ["OrganizationId"] = "tenant_id" },  // rename app keys → canonical
    RoleMapping  = new() { ["BackendAdmin"] = "admin" },        // rename app roles → Shape roles
    // IssueRefreshToken = false  (default — stateless)
};
```

```php
// PHP equivalent — the source is a callable (closures are fine in app.php):
'auth' => [
    'exchange' => [
        'source' => function (string $token, Database $db): ?array {
            $row = $db->run('SELECT ... FROM my_tokens WHERE ...', ['t' => $token]);
            if (($row['count'] ?? 0) === 0) { return null; }   // → AUTH_EXCHANGE_INVALID (401)
            return ['id' => ..., 'email' => ..., 'role' => ..., 'tenant_id' => ...];
        },
        'claim_mapping' => ['OrganizationId' => 'tenant_id'],
        'role_mapping'  => ['BackendAdmin' => 'admin'],
        // 'issue_refresh_token' => false  (default — stateless)
    ],
],
```

```jsonc
// 3. Client side
POST /api  { "op": "auth.exchange", "payload": { "token": "<external token>" } }
// → { "access_token": "…", "token_type": "Bearer", "expires_in": 3600,
//     "user": { "id": …, "email": "…", "role": "…", "tenant_id": … } }
```

## Rules the exchange enforces (you don't have to)

| Rule | Behavior |
|---|---|
| Invalid/expired external token (`Validate` returned null) | `AUTH_EXCHANGE_INVALID` (401) with a re-login hint |
| No `token` in the payload | `AUTH_VALIDATION_ERROR` (400) |
| Exchange not configured | `AUTH_NOT_CONFIGURED` (501) |
| Source returned no `id` | `AUTH_CONFIG_ERROR` (500) — your source is broken, not the caller |
| Tenancy on + scoped role | Tenant must exist and be active → else `TENANT_SUSPENDED` (403); same §2.3 gate as login |
| Tenancy on + unscoped role (e.g. `admin`) | Exchanges even when its home tenant is suspended (the recovery path); token carries `tenant_id = null` |
| Claim mapping | Renames only; never overwrites an already-present canonical key |
| Role mapping | Unmapped roles pass through verbatim — mask role gates are the backstop |
| Extra identity keys (e.g. `organization_name`) | Surface on the response's `user` object — returning a key from your source IS the pass-through intent. `password`/`password_hash` are never echoed. |

Your source may also *throw* `ApiException` for app-specific denials (e.g.
"token org does not match user org") — it passes through verbatim.

## Boundaries — what stays app-side

Per the extraction split rule: the **SQL against your token/user/role tables,
your role semantics, and any org-reconciliation logic** live inside your
`ITokenExchangeSource`. Shape owns the op, the error contract, the mapping,
the tenancy gate, and the JWT. See
[BROWNFIELD-ADAPTER-GUIDE.md](BROWNFIELD-ADAPTER-GUIDE.md) for the full
pattern (exposure planning, `security: strict`, hosting).
