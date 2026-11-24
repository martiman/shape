# Token exchange + secure preset — PHP implementation plan

> **Status: IMPLEMENTED (2026-07-07).** All items X1–X7 done; `php/tests/TokenExchange.phpt`
> mirrors `TokenExchangeTests.cs`; full PHP suite green (8 tests). One naming note:
> PHP's manifest-roles config key is the pre-existing `'manifest'` (not
> `manifest_roles`) — the strict preset checks that native key.
>
> **For the implementing agent.** The .NET side is DONE and fixes the
> cross-stack contract — do not redesign, port. Reference implementation:
> `dotnet/src/Shape/Auth/ITokenExchangeSource.cs`,
> `LoginService.Exchange` + `ApplyClaimMapping` + the refresh-optional
> `BuildTokenResponse` in `dotnet/src/Shape/Auth/LoginService.cs`,
> the `auth.exchange` case in `dotnet/src/Shape/Core/OpDispatcher.cs`,
> `ValidateStrictSecurity` in `dotnet/src/Shape/ShapeApp.cs`,
> `dotnet/src/Shape/Metadata/TenantExposurePlanner.cs`, and the tests in
> `dotnet/tests/Shape.Tests/TokenExchangeTests.cs`. Feature docs:
> [TOKEN-EXCHANGE.md](TOKEN-EXCHANGE.md),
> [BROWNFIELD-ADAPTER-GUIDE.md](BROWNFIELD-ADAPTER-GUIDE.md).
> Scope: `php/packages/shape` + `php/packages/shaperuntime` only —
> **never edit `php/.deploy/**`** (deployed copy).
>
> House rules: compact code style (no `withPreparedSets` in ecs.php), typed
> `ApiException` errors with `expected`/`hint` details, `php -l` + the Nette
> Tester suite (`php vendor/bin/tester -C tests/`) green before done.

## 1. Contract (fixed — must match .NET byte-for-byte where visible)

### Op

`POST {op: "auth.exchange", payload: {token: "<external>"}}` — anonymous like
`auth.token` (the external token IS the credential).

### Config (PHP shape — callable instead of the C# interface)

```php
'auth' => [
    'driver' => 'jwt',
    'exchange' => [
        // fn(string $externalToken, Database $db): ?array
        // Returns the verified identity or null (→ AUTH_EXCHANGE_INVALID 401).
        // Keys (pre-mapping): id (required), email, role, tenant_id, display_name.
        // May throw ApiException for app-specific denials — passes through.
        'source'              => $callable,
        'claim_mapping'       => ['OrganizationId' => 'tenant_id'],  // rename only; never overwrites a present canonical key
        'role_mapping'        => ['BackendAdmin' => 'admin'],        // unmapped roles pass through verbatim
        'issue_refresh_token' => false,                              // default: stateless
    ],
],
'security' => 'strict',   // top-level, like 'tenancy'
```

### Behavior table (mirror of TOKEN-EXCHANGE.md — test each)

| Case | Result |
|---|---|
| source returns null | `AUTH_EXCHANGE_INVALID`, 401, hint "Obtain a fresh token from the application's own login flow, then retry the exchange." |
| payload missing/empty `token` | `AUTH_VALIDATION_ERROR`, 400, `expected: {token: 'string (the external token to exchange)'}` |
| `auth.exchange` config absent | `AUTH_NOT_CONFIGURED`, 501, hint pointing at the config key |
| identity lacks `id` (after mapping) | `AUTH_CONFIG_ERROR`, 500 |
| tenancy on + scoped role | `assertTenantActive` gate (existing) → `TENANT_SUSPENDED` 403 when suspended/missing |
| tenancy on + unscoped role | exchanges even with suspended home tenant; claim `tenant_id = null` |
| non-numeric ids (Guid/uuid strings) | preserved **verbatim** in the `sub` claim and the response `user.id` — no int cast (`normalizeTenantId`-style care; see the .NET `idText` handling) |
| extra identity keys (e.g. `organization_name`) | pass through to the response `user` object; reserved set never echoed: `id, email, role, tenant_id, display_name, password_hash, password` (exchange path only — local login keeps the fixed key set) |

### Stateless response (default)

```jsonc
{ "access_token": "…", "token_type": "Bearer", "expires_in": 3600,
  "user": { "id": …, "email": "…", "display_name": "…", "role": "…", "tenant_id": … } }
// NO refresh_token / refresh_token_expires_in keys.
// issue_refresh_token: true → full pair via the existing issueTokenPair()
// (requires refresh_tokens table + ids insertable there).
```

### `security: strict` (boot-time, CONFIG_INVALID + hint each)

1. `auth.driver` is `none`/absent → "requires an auth driver".
2. Any mask whose `roles` is empty/absent → message lists the mask names
   (sorted), hint explains empty roles = any authenticated caller.
3. `manifest` roles not set (null/absent; PHP's native key) → "requires manifest roles" ( `[]` is
   *set* and passes — it means any authenticated caller).

## 2. Work items

- [x] **X1 `LoginService::exchange(array $payload): array`** in
  `php/packages/shaperuntime/src/ShapeRuntime/Auth/LoginService.php`.
  Follow the .NET method line-by-line: config guard → token guard → create
  PDO/Database → `$identity = ($cfg['source'])($token, $db)` (`Database`
  instance — construct one from `$this->dbConfig`; note LoginService today
  uses raw PDO — either build a `Database` for the source call or accept the
  PDO-vs-Database difference by passing a `Database`; **pass `Database`**, it
  is the documented contract) → null → `AUTH_EXCHANGE_INVALID` → claim
  mapping (private `applyClaimMapping`) → role mapping → id presence check →
  tenancy gate via existing `isUnscopedRole` / `assertTenantActive` → response
  via `buildTokenResponse` with **nullable refresh** (refactor: make
  `$refreshToken` param nullable; omit the two refresh keys when null — the
  existing callers pass non-null, zero behavior change) or `issueTokenPair`
  when `issue_refresh_token`.
  ⚠ `buildTokenResponse` builds `sub` from `(string)((int)($user['id']))` —
  generalize exactly like .NET: numeric → int string, non-numeric → verbatim
  string; same for the response `user.id`.
- [x] **X2 Op wiring** — add `case 'auth.exchange':` next to `auth.refresh`
  in **both** routers: `php/packages/shape/src/Shape/ShapeRouter.php` (~line
  249) and `php/packages/shaperuntime/src/ShapeRuntime/Http/AppRouter.php`
  (~line 119). No guard call (anonymous op). Status 200.
- [x] **X3 Config plumbing** — the exchange block rides `auth` config
  untouched (the source is a callable in `app.php`, closures are fine — the
  config is required-in, never serialized). Validate shape lazily in
  `exchange()` (matches .NET; no build-time check beyond `security`).
- [x] **X4 `security` preset** — `ShapeApp`: read top-level
  `$config['security']`; after `validateMaskContracts` in `buildFromEntities`
  **and** after the admin/audit-mask merge in `buildDatabaseConfig` (same two
  call sites the contract validator uses — admin masks join late), run
  `validateStrictSecurity($config)` implementing the three checks. Note PHP
  semantics: `manifest_roles` unset = null = open (mirror `authorizeManifest`,
  `ShapeRouter.php:1051`).
- [x] **X5 Exposure planner** — `php/packages/shape/src/Shape/Metadata/TenantExposurePlanner.php`:
  static `planTables(DatabaseIntrospector $introspector, array $tenancy, array $excludeTables = []): list<string>`.
  Port `TenantExposurePlanner.cs`: skip registry + excluded (case-insensitive),
  require the tenancy column present **and** a verified FK to the registry
  PK (`Id`/`id`). Prerequisite: `DatabaseIntrospector::getForeignKeys()` is
  private — add a public `describeForeignKeys(string $table): array` wrapper
  (mirror of the existing public `describeColumns`, `DatabaseIntrospector.php:146`).
- [x] **X6 Tests** — `php/tests/TokenExchange.phpt` (SQLite, mirror
  `TokenExchangeTests.cs` 1:1): stateless happy path incl. Guid-string `sub`
  + claim/role mapping; unknown token → `AUTH_EXCHANGE_INVALID`; suspended
  tenant blocks scoped role but not unscoped; missing config →
  `AUTH_NOT_CONFIGURED`; missing token field; the three strict-preset boot
  failures + one passing strict config; planner classification
  (FK-verified exposed / name-only column skipped / excluded skipped).
- [x] **X7 Docs** — flip the status line in
  [TOKEN-EXCHANGE.md](TOKEN-EXCHANGE.md) ("implemented in .NET **and PHP**"),
  add the PHP config example there (callable source), note `security` +
  `exchange` keys in SPEC.md's config section, and check off this plan.

## 3. Definition of done

`php -l` clean on every touched file; `php vendor/bin/tester -C tests/` fully
green (8 tests incl. the new one); no edits under `php/.deploy/`; response
keys and error codes byte-identical to the .NET tests' assertions.
