# Authentication

> **AI agents:** read [`AGENTS.md`](../../AGENTS.md) first for the framework overview. This file is the deep auth reference.

Shape's primary auth model is **a local `users` table + Shape-issued HS256 JWTs**. This is the default for any backend bootstrapped via `quickStart()`. The OIDC integration described in the second half of this document is **optional** — it lets external identity providers (Entra ID / Azure AD, Auth0, Okta, Google, AWS Cognito) issue tokens that Shape verifies, alongside the local flow.

A third path exists for brownfield apps whose tokens are **not** JWTs (opaque
DB-backed bearers, session ids): the `auth.exchange` op swaps an app-validated
external token for a Shape JWT — see [TOKEN-EXCHANGE.md](TOKEN-EXCHANGE.md).

---

## Primary auth — local users + Shape JWT

This is what every Shape app gets out of the box. No additional configuration beyond `JWT_SECRET` in `.env`.

### Endpoints

| Endpoint | Purpose |
|---|---|
| `POST {op: 'auth.token', payload: {email, password}}` | Username/password login. Returns access + refresh tokens. |
| `POST {op: 'auth.refresh', payload: {refresh_token}}` | Rotate the access token using a still-valid refresh token. |
| `POST {op: 'auth.logout', payload: {refresh_token}}` | Revoke a refresh token. |
| `POST {op: 'auth.admin.create-user', payload: {...}}` | Admin-only: create a new user. Requires `role: 'admin'` in the caller's JWT. |
| `POST {op: 'auth.admin.change-password', payload: {...}}` | Admin-only: reset a user's password. |

### How it works

1. The `users` table (with `email`, `password_hash`, `role`, `is_active` columns) is the source of truth. Hardcoded as a secret table — never auto-CRUDed.
2. `LoginService` validates email/password against `users.password_hash` (bcrypt by default) and issues an HS256 JWT signed with `JWT_SECRET`.
3. The JWT carries `sub` (user id), `email`, `role`. The role drives every mask's authorization check.
4. Refresh tokens are stored in `refresh_tokens` (also a hardcoded secret table) with rotation chain audit (`replaced_by_token_id`, `revoked_at`).
5. Every subsequent API call sends `Authorization: Bearer <jwt>`. `JwtGuard` validates signature, expiry, and (optionally) issuer/audience.

### Configuration

```php
// app.php (or via quickStart's .env)
'auth' => [
    'driver'              => 'jwt',
    'jwt_secret'          => '<32+ chars random>',
    'access_token_ttl'    => 3600,        // seconds
    'refresh_token_ttl'   => 2592000,     // seconds (30 days)
    'password_min_length' => 8,
    'users_table'         => 'users',
    'refresh_tokens_table' => 'refresh_tokens',
    'assignable_roles'    => ['user', 'admin'],
    'jwt_issuer'          => 'https://api.myapp.com',  // optional
    'jwt_audience'        => 'myapp-clients',           // optional
],
```

Or via `.env` for `quickStart()`:

```ini
JWT_SECRET = "<32+ chars>"
JWT_TTL    = 3600
JWT_REFRESH_TTL = 2592000
```

This setup is sufficient for any app where users sign in directly to Shape with their own credentials.

---

## Optional — OIDC / JWKS for external identity providers

When some users authenticate through Microsoft / Google / Auth0 / etc., add an `oidc` block to the auth config. The local users-table flow keeps working unchanged; OIDC is a **secondary path** dispatched at request time based on the inbound token's `alg` header.

### When to add OIDC

- Your app has users on Entra ID / Azure AD and you want SSO
- A frontend uses MSAL.js / msal-react / msal-node to obtain tokens directly from Microsoft
- You're integrating with Auth0, Okta, Cognito, or any OIDC-compliant IdP
- You don't want to maintain user passwords for those users

If users sign in **only** through Shape's `auth.token`, you don't need this section.

### Configuration

```php
'auth' => [
    'driver'      => 'jwt',
    'jwt_secret'  => '<...>',           // primary path stays as before

    'oidc' => [                          // optional secondary path
        'jwks_uri'    => 'https://login.microsoftonline.com/<tenant-id>/discovery/v2.0/keys',
        'issuer'      => 'https://login.microsoftonline.com/<tenant-id>/v2.0',
        'audience'    => '<your-app-client-id>',
        'algorithms'  => ['RS256'],      // default

        // Claim mapping — IdP claim names → Shape's auth-context fields
        'id_claim'     => 'oid',          // default 'sub'; Entra uses 'oid'
        'email_claim'  => 'preferred_username',  // default 'email'
        'role_claim'   => 'roles',        // default 'role'; Entra 'roles' is an array

        // Map IdP role strings → Shape role names (which mask configs check)
        'role_mapping' => [
            'BackendAdmin' => 'admin',
            'BackendUser'  => 'user',
        ],

        'jwks_cache_ttl' => 3600,         // seconds
        'jwks_cache_dir' => null,         // null = sys_get_temp_dir() . '/shape_jwks'
    ],
],
```

### What happens at request time

1. Caller sends `Authorization: Bearer <token>`.
2. `JwtGuard` decodes the token header.
3. If `alg` is HS256 → primary path (local users-table JWT verification).
4. If `alg` is RS256 / RS384 / RS512 **and** `oidc` config is present → OIDC path.
5. The OIDC verifier:
   - Fetches the JWKS (cached locally with TTL — falls back to stale cache if the IdP is briefly unreachable)
   - Looks up the signing key by `kid`
   - Converts the JWK to a PEM RSA public key
   - Verifies the signature with `openssl_verify`
   - Validates `exp`, `nbf`, `iss`, `aud`
   - Maps configured claims into Shape's auth context
6. Returns `['id' => …, 'email' => …, 'role' => …, '_oidc' => true, …]` plus all original claims.

### Entra ID / Azure AD setup

1. Register your app in Entra: **App registrations → New registration**
2. Note the **Application (client) ID** — that goes in `oidc.audience` (but read the v1.0 vs v2.0 note below first)
3. Note the **Directory (tenant) ID** — substitute in the `jwks_uri` and `issuer` URLs
4. Define **App roles** (Manifest → `appRoles`) like `BackendAdmin` and `BackendUser`
5. Assign users/groups to those roles in **Enterprise applications → Users and groups**
6. Configure your frontend (MSAL.js / msal-react / etc.) to acquire tokens with `scope: 'api://<client-id>/.default'`
7. The frontend sends the resulting token as `Authorization: Bearer <token>`; Shape validates it.

#### `audience` — v1.0 vs v2.0 tokens

What goes in `oidc.audience` depends on which token format your app issues:

- **v1.0 tokens** (endpoint `/oauth2/token`, default for legacy app registrations): `aud` is the **client ID GUID** — e.g. `11111111-2222-3333-4444-555555555555`.
- **v2.0 tokens** (endpoint `/oauth2/v2.0/token`, default for new app registrations and any frontend using MSAL.js with the modern authority URL): `aud` is the **App ID URI** — typically `api://<client-id-guid>` or a custom URI you set in the registration (e.g. `api://my-backend`).

When you scope your token request with `api://<api-id-uri>/.default` (the standard MSAL pattern), the IdP issues a **v2.0** token whose `aud` is the App ID URI, *not* the client GUID. Misconfiguring `oidc.audience` here is the most common Entra integration error — Shape's error envelope on a mismatch reports both the expected and the found `aud`, so the fix is one config line, but knowing which value to put there saves a debug round.

If you control both ends and want determinism, set the App ID URI explicitly in the app registration (Expose an API → Application ID URI) and reference that exact value in `oidc.audience`.

```php
'oidc' => [
    'jwks_uri'    => 'https://login.microsoftonline.com/' . TENANT_ID . '/discovery/v2.0/keys',
    'issuer'      => 'https://login.microsoftonline.com/' . TENANT_ID . '/v2.0',
    'audience'    => CLIENT_ID,
    'role_claim'  => 'roles',
    'id_claim'    => 'oid',
    'email_claim' => 'preferred_username',
    'role_mapping' => [
        'BackendAdmin' => 'admin',
        'BackendUser'  => 'user',
    ],
],
```

### Local user resolution — `oid` → `users.id`

The OIDC verifier resolves the IdP-issued `oid` to a local `users.id` after signature verification, so audit columns (`created_by` / `updated_by`) and `audit_log.actor_id` always reference `users.id` regardless of which auth path validated the request. FK from any audit surface to `users(id)` works for both local and OIDC-authenticated users.

**Required schema change** — add an `oid` column to your `users` table:

```sql
ALTER TABLE users ADD COLUMN oid VARCHAR(64) NULL;
CREATE UNIQUE INDEX uq_users_oid ON users(oid);
```

**Provisioning model — pick one:**

```php
'oidc' => [
    // …existing config…
    'users_table'         => 'users',     // default
    'users_lookup_column' => 'oid',       // default
    'auto_provision'      => false,       // default — admin pre-provisions

    // Or:
    'auto_provision'      => true,        // create the row on first login
    'auto_provision_role' => 'user',      // default role for auto-provisioned users
],
```

| Mode | What happens on first OIDC login |
|---|---|
| **Manual provisioning** (default) | Admin must `INSERT INTO users (email, password_hash, role, oid) VALUES (…)` before the user can call the API. Unprovisioned tokens get `403 AUTH_FORBIDDEN` with the hint *"Insert a row into 'users' with oid set to the token's oid, or set 'auth.oidc.auto_provision' => true to create rows on first login."* |
| **Auto-provision** (opt-in) | A row is inserted automatically: `email` from token's `preferred_username`, `role` from `auto_provision_role`, `password_hash` set to `'!locked-oidc-only!'` (a sentinel that can never validate via password_verify, so this row can't log in via the local password flow). |

After resolution, `authContext` carries:

```php
[
    'id'    => 1,                            // users.id (int) — canonical actor identifier
    'oid'   => 'entra-oid-abc-123',          // preserved from token for reference
    'email' => 'alice@contoso.com',          // from local users table (overrides token email)
    'role'  => 'admin',                      // from token's role_mapping (token-mapped is authoritative)
    '_oidc' => true,
    '_local_user' => ['id' => 1, 'email' => '…', 'role' => '…'],
    // … plus all original token claims
]
```

**Why `users.id` not `oid` in audit surfaces:**

- Cleaner FK story: `notes.created_by INT REFERENCES users(id)` works for everyone.
- Single column type (INT/BIGINT) instead of mixed INT/VARCHAR.
- `JOIN notes n LEFT JOIN users u ON u.id = n.created_by` returns the email/role for both local and OIDC users uniformly.
- Audit log entries from local and OIDC users sort and aggregate together by `actor_id`.

The `oid` is preserved on `authContext` for app code that needs the IdP identifier (e.g., calling Microsoft Graph as the user).

### What OIDC does NOT do

- **No federation flow.** Shape does not redirect to Microsoft for sign-in or run the OAuth dance. The frontend uses MSAL.js (or equivalent) to obtain tokens client-side; Shape is purely a verifier.
- **No refresh-token forwarding.** Microsoft tokens expire (~1 hour); the frontend uses MSAL's silent refresh against Microsoft, not against Shape's `auth.refresh`. Shape's refresh-token flow is for local users only.
- **No user record auto-creation.** OIDC-authenticated users do not need a row in `users` — the token is the source of truth. If your app needs per-user data ownership, key it on `oid` from the token claims.
- **No multi-provider routing in v1.** One OIDC provider per backend. Adding a second IdP requires extending `OidcVerifier` (one-week feature; ask if you need it).

### Security posture

The OIDC verifier defends against the standard JWT attack surface:

| Attack | Defense |
|---|---|
| `alg: none` (signature stripping) | Algorithm whitelist enforced before any verification work |
| HMAC-confusion (sending HS256 token signed with the public key) | `alg` whitelist + dispatch by alg means HS256 tokens don't reach the RSA verifier |
| Wrong-issuer token (e.g., another tenant's tokens) | `iss` validation when configured |
| Wrong-audience token (e.g., another app's tokens for the same tenant) | `aud` validation when configured; supports both string and array `aud` claims |
| Expired token | `exp` checked |
| Not-yet-active token | `nbf` checked |
| JWKS endpoint outage | Stale cache served while a fresh fetch retries |
| Modified token body | RSA signature verification fails |

Verified end-to-end with a smoke test covering all eight cases.

### Combining the two

Both paths can be active simultaneously:

- A frontend that signs in via username/password → gets an HS256 token from `auth.token` → calls API with that token.
- A frontend that signs in via Microsoft → gets an RS256 token from MSAL → calls API with that token.
- The same Shape backend handles both. Each request's auth context is built from whichever path matched.
- Mask role checks see the same `role` field in either case — the OIDC verifier maps IdP roles into Shape's role vocabulary via `role_mapping`.

This is the recommended deployment for an enterprise app with mixed workforce auth.
