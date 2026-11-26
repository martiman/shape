# Shape Security Review — .NET + PHP

**Date:** 2026-07-07
**Scope:** `dotnet/src/Shape` (~14.7k LOC) and `php/packages/{shape,shaperuntime}` (~15.0k LOC).
**Method:** Manual read of the security-critical paths, followed by a five-track
independent multi-agent pass that (a) adversarially re-verified each initial finding and
(b) audited the files the first pass had not deeply read (ParamResolver, Database internals,
DsnTranslator, introspectors, JwtIssuer, config/bootstrap/secrets, MCP server + transports,
generators, audit logger, tenancy planner, and the HTTP routing/request-envelope layer).
Every finding below carries a verification status.

## Overall assessment

The framework's security **fundamentals are strong** and were confirmed clean under
independent re-audit:

- **Parameterization / identifier quoting is sound.** ParamResolver, `Database`,
  `SqlDialect`, and the non-SQLite introspectors were re-read in full: client values always
  reach SQL as bound parameters, cross-query `{ref,value}` references resolve to *data*
  (never identifiers), and every interpolated identifier passes a driver-aware
  delimiter-doubling `QuoteIdentifier`. The OData `$filter` *compiler* whitelists columns
  and parameterizes values.
- **JWT primitives are correct.** `JwtIssuer` hardcodes `alg`, always sets `exp`, and does
  not let caller claims override lifetime claims; the HS256 secret is separate from the RSA
  keys, so HS/RS **algorithm confusion is genuinely closed**; `alg:none` is rejected.
- **Passwords** use BCrypt; **refresh tokens** are SHA-256-hashed at rest, rotated with
  reuse rejection and revoke-on-password-change. **No default admin credentials or committed
  secrets** exist anywhere; the `.env` parser and env overlay are clean; secrets are not
  logged.
- **`AuditLogger`** logs only column *names* (never values/secrets/hashes) and binds all
  parameters. **`TenantExposurePlanner`** requires a *verified FK* to the tenant registry,
  not a name coincidence. Generators emit JSON/Markdown (no HTML sink → no XSS) from
  operator/DBA-controlled metadata.

However, the deeper pass found **two high-impact issues the first read missed** — a
client-reachable SQL injection on the .NET query path (C1) and a JWKS-cache key-substitution
auth bypass (H2) — plus a design-level authorization gap (H1) whose blast radius is larger
than first assessed. The two implementations are near-mirror; unless a finding names one
language it applies to **both**.

---

## Remediation status (2026-07-08)

Fixes were implemented across both runtimes; .NET (493 tests) and PHP (10 .phpt + PHPStan)
suites pass. Fixed in this pass:

| ID | Fix | Langs |
|----|-----|-------|
| **C1** | Compiled OData `$filter` moved to a trusted out-of-band `precompiledFilters` arg; client `__`-prefixed query options now rejected. Regression tests added. | .NET |
| **H1** | `writable_columns` allowlist on writable masks, enforced against client data pre-augmentation; audit/PK/tenant-managed/sensitive/privilege columns excluded via shared `SensitiveColumns`. Tests added. | both |
| **H2** | JWKS cache dir forced 0700, group/world-writable dir + file refused, cache files trusted only when not group/other-writable, stale-serve window bounded. | both |
| **M1** | `exp` now required (HS256 + OIDC). | both |
| **M2** | OIDC `audience` mandatory — boot fails without it; issuer+audience enforced at verify time. | both |
| **M4** | Login not-found branch runs a dummy hash verify (timing-oracle closed). | both |
| **M5** | Batch op-count cap (100); upload streamed with a hard byte cap + default max size. | both |
| **M6** | Stored upload extension derived from detected MIME, not client filename. | both |
| **M7** | Internal/command errors return a generic message + correlation id; detail logged server-side. | both |
| **M8** | MCP `resources/read` (`shape://tools`/`manifest`/`entities`) role-filtered. | both |
| **M9** | `command_roles` policy enforced in `CommandExecutor` (covers MCP + main API) and MCP tool visibility. | both |
| **M10** | `.NET` manifest no longer emits `sql_unscoped`. | .NET |
| **L4** | `.shape/` dir 0700; secret file created with 0600 before write (no TOCTOU window). | .NET |
| **L6** | SQLite introspector `PRAGMA` table name now backtick-escaped. | PHP |
| **L8** | Docs UI serves `X-Content-Type-Options`/`X-Frame-Options`/`Referrer-Policy`. | both |
| **L9** | DB-less OIDC path drops any token-supplied `tenant_id` (fails closed). | both |
| **L10** | 60s clock-skew leeway on exp/nbf. | both |

Deferred (with rationale), tracked for a follow-up pass:

- **L1** (MCP-over-HTTP admin-on-`driver:none` + wildcard CORS): needs a config surface
  (`mcp.allow_anonymous`, CORS origin allowlist) — a policy/design change, not a code bug.
- **L2** (auto-secret production warning): needs an environment/profile signal to detect
  "production"; low risk (the secret is strong, just operationally fragile at scale).
- **L3** (`.shape/` under webroot): a deployment/documentation control, not a code change.
- **L5** (.NET `DsnTranslator` → `SqlConnectionStringBuilder`): a refactor with regression
  surface across four drivers; value is Low (operator-config trust).
- **L7** (OpenAPI advertises admin routes): overlaps M8's disclosure fix; gating `?openapi`
  by default is a policy change to weigh separately.
- **L11** (`AdminMaskGenerator` name-heuristic): `SensitiveColumns` now centralizes the
  policy; extending the read-strip blocklist (api_key/ssn/…) is a tuning follow-up.
- **M3** (login rate limiting): out of scope for this pass; best delivered as an upstream
  gateway control or an injectable throttle — noted as a required deployment control.

---

## Findings (ranked, with verification status)

### C1 — SQL injection via client-supplied `__filterSql` query option — **VERIFIED**
**Severity: Critical · .NET only** *(PHP not affected — see below)*
On the POST `query` op, the per-query object flows unmodified from client JSON.
`BatchExecutor.ReadOptions` (`Core/BatchExecutor.cs:1687`) *skips* `__`-prefixed option
keys from its whitelist (rather than rejecting them) and returns the dict intact; `Execute`
then reads `options["__filterSql"]` (`:1023`) and splices it **as raw SQL** via
`WrapMaskWithFilter` → `CloneMask(mask, $"SELECT * FROM ({mask.Sql}) AS __filt WHERE
({filterSql})")` (`:1068`), which is executed by `_database.Run`. Nothing strips client
`__` keys: `RequestEnvelope.ReadAsync` (`Http/RequestEnvelope.cs`) only parses `op`/`payload`
and does **not** reject underscored keys — the in-code comment at `:1683` claiming it does is
false. `__filterSql` is meant to be injected server-side only, by the OData GET path
(`ShapeRouter.cs:264`).

*Exploit* — any caller authorized for even one mask:
```json
{"op":"query","payload":{"queries":[{"ref":"q1","sql":"<any mask>",
  "options":{"__filterSql":"1=1 UNION SELECT password_hash,email,role FROM users--"}}]}}
```
The injected fragment lands in the `WHERE` of the wrapped query → arbitrary read/exfiltration
of any table (bypassing tenancy scoping and mask column projection), blind/boolean/UNION
extraction, and stacked statements on drivers that permit them.

*Why PHP is safe:* PHP applies `$filter` as an in-memory PHP closure
(`ShapeRouter.php:778 parseODataFilter`), never as spliced SQL, and has no `__filterSql`
channel at all (confirmed by grep).

*Update:* the in-memory closure ran after `LIMIT` (so only the first rows were searched and
`$count` ignored the filter). PHP now compiles `$filter` to a parameterised SQL condition
(`ShapeRuntime\Core\ODataFilter`): columns are plain identifiers quoted with `quoteIdentifier`,
every value is a bound `:__fN` parameter, and the router hands the expression to
`BatchExecutor::execute()` through a trusted third argument (client `options` still cannot
carry a filter). Covered by `tests/ODataFilter.phpt`.

*Fix:* in `ReadOptions`, **reject** any client `__`-prefixed option key
(`QUERY_VALIDATION_ERROR`) instead of skipping it; deliver the compiled OData filter to
`Execute` through a trusted out-of-band parameter (or set it on the options dict *after*
`ReadOptions` has sanitized client input), never through the client-controllable options
map. Add a regression test that a client-supplied `__filterSql` is refused.

### H1 — No field-level write authorization (mass assignment) — **VERIFIED (both langs)**
**Severity: High → Critical in the escalation path**
`BatchExecutor` builds insert/update/where column lists directly from client `data`/`filter`
keys with no per-column write allowlist (.NET `Core/BatchExecutor.cs:719,754,812`; PHP
`Core/BatchExecutor.php:609,658`). `Mask` exposes only a table-level `Writable` bool and
`WritableMode`; per-action `RolesByAction` gates the *operation*, not columns. The only
protected columns are the tenant column (`ApplyTenantColumn`) and audit columns
(`ApplyAuditColumns`, which merely *fills* absent ones). `AssignableRoles` is consulted only
in `LoginService.CreateUser`, never on the generic `mutation` path.

*Escalation (worse than first assessed):* on a writable `users`-like entity, an ordinary
scoped user updates their own row with `data:{role:"<unscoped-role>"}`. Because
`BuildTokenResponse` issues `tenant_id = null` for unscoped roles
(`LoginService.cs:614` / PHP `:590`), the next `auth.token` login yields **cross-tenant
god-mode from an ordinary scoped account**. `password_hash` / `is_active` are writable the
same way. Rank as High for the general case; **Critical** where any writable entity carries a
privilege- or tenant-bearing column.

*Fix:* add an optional `writable_columns` allowlist to `MaskConfig`/`Mask`; populate it in the
generators from non-sensitive entity fields; reject any insert/update column not on the list
(`WRITE_COLUMN_FORBIDDEN`). Default PK, tenant column, audit columns, `password_hash`,
`role`, and `is_active` to non-writable unless explicitly opted in. The generator should
always emit the list so real deployments are restricted (null = legacy unrestricted).

### H2 — JWKS cache poisoning → OIDC auth bypass — **VERIFIED (both langs)**
**Severity: High**
The OIDC JWKS disk cache is stored at a predictable path in a world-writable temp dir
(`Auth/OidcVerifier.cs:154`; PHP `OidcVerifier.php:239`, `@mkdir($dir,0777,true)`), filename
`sha256(jwks_uri).json` (derivable from the public JWKS URL). Cache reads are trusted and a
valid stale entry short-circuits the network fetch. On a shared/multi-tenant host, a local
user who can write the temp dir can pre-plant a cache file containing an **attacker-controlled
RSA public key** before Shape's first fetch. The verifier then accepts forged OIDC tokens
(attacker holds the private key) with any `role`/`email` claims → full auth bypass /
privilege escalation. (Also subsumes the earlier "stale JWKS served with no max-staleness
bound" note: a revoked key keeps validating during an IdP outage.)
*Fix:* create the cache dir `0700` under the app-private `.shape/` tree; verify
ownership/mode before trusting a cache file; cap the stale-serve window to a small multiple
of the TTL.

### M1 — JWT `exp` optional → non-expiring tokens accepted — **VERIFIED (both)**
Expiry is checked only when `exp` is present (HS256 `JwtGuard.cs:68` / PHP `:84`; OIDC
`OidcVerifier.cs:287` / PHP `:360`). Local HS256 tokens always carry `exp` (so forging one
still needs the secret); the realistic exposure is an IdP/mis-scoped OIDC token with no
`exp`, accepted forever. *Fix:* require `exp` on the local path; make it mandatory for OIDC
(configurable only if a provider legitimately omits it).

### M2 — OIDC `audience`/`issuer` optional; app boots without `audience` — **VERIFIED (both)**
`ValidateClaims` checks `iss`/`aud` only when configured (`OidcVerifier.cs:293,300`; PHP
`:367,372`), and **no bootstrap requires `audience`** (`ConfigLoader.cs:193`,
`ShapeApp.cs:407 ValidateOidcTenancy`). The IdP presets fill `issuer` but never `audience`
(`IdpPresets` / `OidcVerifier.php:99`), so the *default* preset config validates issuer yet
accepts **any** token the IdP signed for **any** application — the Entra/Google/Cognito
common-endpoint confused-deputy. Elevated to **Medium-High** because it is a likely-default
misconfiguration, not a corner case. *Fix:* fail closed at boot (`CONFIG_INVALID`) when
`auth.oidc` is enabled and `audience` is empty.

### M3 — No rate limiting / account lockout on auth — **VERIFIED (absent)**
`auth.token`/`login`/`refresh`/`exchange` run with no throttle, lockout, or backoff anywhere
(`OpDispatcher.cs:101`). Enables online brute-force and token guessing. *Fix:* per-IP +
per-account throttle and progressive lockout on failed login; cap `exchange`. If delegated to
an upstream gateway, document it as a required control.

### M4 — Login user-enumeration timing oracle — **VERIFIED (both)**
Login returns before the BCrypt/`password_verify` call when the email is unknown
(`LoginService.cs:67`; PHP `LoginService.php:42`), so "unknown email" is measurably faster
than "known email, wrong password." *Fix:* dummy-verify against a fixed hash on the
not-found branch.

### M5 — Upload buffers whole file before size check; unbounded batch/body — **VERIFIED**
`ReadAllBytesAsync` reads the entire upload into memory (`Upload/UploadManager.cs:128`) and
the `MaxSize` check runs afterward (`:141`); `MaxSize` is optional. The `queries`/`mutations`
arrays have no item-count cap (`OpDispatcher.cs:133,144`); `top` bounds rows-per-query
(`MaxTop=200`) but not ops-per-batch. *Fix:* stream with a hard byte ceiling; ship a default
`MaxSize`; cap batch item count; set an app-level request-body limit.

### M6 — Stored-file extension from client filename; files public, no access control — **VERIFIED**
`GenerateKey` takes the extension from the original filename, not the detected MIME
(`Upload/UploadManager.cs:185`); `LocalStorage` files are served directly by the web server
with no authz (`Upload/LocalStorage.cs:11`). A PNG-content file named `x.html` stores as
`<random>.html` → stored XSS when served; uploads are public-by-URL with no per-tenant read
isolation. *Fix:* derive the extension from the detected MIME; serve with
`X-Content-Type-Options: nosniff` + `Content-Disposition: attachment`; proxy private-file
downloads through an authorized route.

### M7 — Error messages leak internal detail — **VERIFIED (both)**
`FatalHandler` returns `"Server error: " + message` with only file paths stripped
(`Http/FatalHandler.cs:129`; PHP `FatalHandler.php:65`); `CommandExecutor` returns
`"Command execution failed: " + e.Message` (`Core/CommandExecutor.cs:63`); raw DB error text
surfaces. Leaks schema/column/SQL detail. *Fix:* generic message + server-side correlation
id; log detail server-side only; keep the `hint` taxonomy, drop the raw `message`.

### M8 — MCP `resources/read` is not role-filtered — **VERIFIED (both)**
`tools/list` correctly hides admin tools, but `resources/read` serves the same data
unfiltered: `shape://tools` (`Mcp/McpServer.cs:318`; PHP `:333`), `shape://manifest`, and
`shape://entities/*` skip the `IsToolVisible` / `ManifestRoles` gate that the HTTP
`?manifest` path applies (`ShapeRouter.cs:176` vs `:186`). A low-priv (or, on `driver:none`,
anonymous) MCP caller reads the full admin/secret-table surface map and defeats
`manifest_roles`. Disclosure only (invocation stays role-checked). *Fix:* apply the
`IsToolVisible`/role filter to the resource surface too.

### M9 — MCP command tools never role-filtered — **VERIFIED (both)**
`IsToolVisible` returns true unconditionally for commands (`McpServer.cs:412`; PHP `:457`)
and `CommandExecutor` performs no role check of its own. Every command is listed to — and
invocable by — any caller reaching MCP, including anonymous; only mutations the command
itself issues are re-gated. *Fix:* gate command visibility and invocation by a configured
role policy; make `CommandExecutor` default-deny.

### M10 — .NET manifest leaks raw `sql_unscoped` — **VERIFIED (.NET only)**
`ManifestGenerator.cs:158` emits `sql_unscoped` — the predicate-free cross-tenant query text
and exact column list for every scoped/admin mask — into the manifest (reachable via M8). PHP
never emits SQL in its manifest (divergence). *Fix:* drop `sql_unscoped` from the manifest to
match PHP.

### Lows — **VERIFIED**
- **L1 · MCP-over-HTTP fails open to `admin` on non-jwt drivers** with wildcard CORS
  (`ShapeRouter.cs:179` / PHP `HttpTransport.php:105`): on `driver:none`, all callers get
  `role:admin` unauthenticated over the network. By design for `driver:none`, but the silent
  admin default over *HTTP* (vs a local stdio pipe) warrants an explicit `mcp.allow_anonymous`
  opt-in.
- **L2 · Auto-generated JWT secret has no prod guardrail** (`JwtSecretMaterializer.cs`; PHP
  `ShapeApp.php:621`): strong value, but no warning when `driver=jwt` boots on an
  auto-materialized secret, and multi-instance deployments diverge (intermittent 401s). .NET
  `File.WriteAllText` lacks the `LOCK_EX` PHP uses → cold-boot write race.
- **L3 · `.shape/` under the config/project dir** (`.shape/jwt-secret`, auto `data.sqlite`):
  fetchable over HTTP if the docroot is the project root instead of `public/`. Document a
  "keep `.shape/` outside webroot" requirement / ship a deny rule.
- **L4 · .NET `.shape/` created world-readable (0755) vs PHP 0700**, with a write-then-chmod
  TOCTOU window on the secret file (`JwtSecretMaterializer.cs:48-63`). *Fix:* create 0700;
  open the file with a create-time 0600 mode.
- **L5 · .NET `DsnTranslator` builds connection strings by manual `Key=value` joins** — a
  `user`/`password` containing `;` can smuggle keywords (`TrustServerCertificate=true`,
  `Integrated Security`). Operator config today → Low. *Fix:* use the ADO.NET
  `*ConnectionStringBuilder` types.
- **L6 · PHP SQLite introspector interpolates the table name into `PRAGMA` without doubling
  backticks** (`DatabaseIntrospector.php:426,463,524,685`) — inconsistent with the escaping
  done one line later and with the .NET side. Schema/config-trusted today. *Fix:* route
  through `SqlDialect::quoteIdentifier`.
- **L7 · OpenAPI documents admin/secret-table routes** when `manifest_roles` is unset
  (`OpenApiGenerator.cs:124`). Disclosure only. *Fix:* exclude `admin*` masks or gate
  `?openapi` by default.
- **L8 · No security headers on the HTML docs UI** (`DocsUiPage`). *Fix:* add
  `X-Content-Type-Options` / `X-Frame-Options` / CSP.
- **L9 · DB-less OIDC would trust the token's `tenant_id`** — when no `Database` is wired,
  `Verify` skips `ResolveLocalUserId` and `MapClaims` copies the whole payload
  (`OidcVerifier.cs:359`; PHP `:426`), so a token `tenant_id` flows through `NormalizeTenancy`.
  Only reachable in a DB-less OIDC config. *Fix:* guard explicitly.
- **L10 · No clock-skew leeway** on `exp`/`nbf` in either verifier (robustness, not
  exploitable).
- **L11 · `AdminMaskGenerator` sensitive-column blocklist is name-heuristic** — a secret
  column named `api_key`/`ssn`/`pin` passes `IsSafeColumn` and is exposed in admin masks.
  *Fix:* allowlist or config-level column redaction.

### Note — auth is disabled by default; NullGuard fail-open
Default `driver = "none"` (`JwtGuard.cs:186`; PHP `ShapeApp.php:566`) means no auth, and masks
without explicit `Roles` are world-accessible; boot only hard-fails under `security:"strict"`.
Consider defaulting to `strict`, or warning when `driver=none` combines with any writable /
non-role-gated mask.

### Confirmed CLEAN under independent re-audit
ParamResolver, `Database`/`SqlDialect` (all inserts/PK-allocation quote identifiers + bind
values), `DsnTranslator` parsing, non-SQLite introspectors (bound table names), `JwtIssuer`
(hardcoded alg, always-set exp, caller can't override lifetime claims — HS/RS confusion
closed), `AuditLogger` (names only, parameterized), `TenantExposurePlanner` (verified-FK
requirement), all generators (JSON/Markdown, no XSS sink; identifiers dialect-quoted), the
`.env` parser and `EnvLoader` (key validation, no shell expansion, no `getenv` export), OIDC
preset construction (user config always wins — no attacker-forced weakening preset). No
default admin credentials or committed secrets; secrets never logged.

---

## Recommendation

Fix in this order:

1. **C1 (Critical, .NET) — now.** It is a live, client-reachable SQL injection reachable by
   any authenticated (or, under `driver:none`, anonymous) caller. The fix is small and
   local; ship it first with a regression test.
2. **H1 (mass assignment) and H2 (JWKS poisoning).** H1 is the only structural authorization
   gap; until `writable_columns` exists, treat every writable entity as if all its columns
   are client-writable and keep privilege/tenant columns off writable entities. H2 is a
   config/permissions fix (0700 cache dir, ownership check).
3. **M1/M2/M4 (auth hardening)** — small, high-value; land together, with the M2 boot check
   converting "operator forgot `audience`" into "cannot boot".
4. **M8/M9/M10 (MCP + manifest disclosure)** — one workstream: filter the resource surface,
   role-gate commands, drop `sql_unscoped`.
5. **M3/M5 (DoS/abuse limits)**, then **M6/M7 (upload + error hygiene)**.
6. **Lows** — batch them; prioritize L1 (MCP admin fail-open) and L6/L5 (the two quoting
   inconsistencies) since they touch the SQL/authz surface.

Because the codebases mirror each other, implement each shared fix once, port verbatim, and
add a parity test with identical error codes so the runtimes stay behaviorally equivalent.

---

## Implementation plan — .NET

**C1 · `__filterSql` injection (do first)**
- `Core/BatchExecutor.cs ReadOptions`: replace the `if (key.StartsWith("__")) continue;`
  skip with an explicit rejection of any client `__`-prefixed option key
  (`QUERY_VALIDATION_ERROR`).
- Deliver the compiled OData filter out-of-band: pass `filterSql`/`filterParams` as explicit
  arguments to `Execute` (or set them on the options dict *after* `ReadOptions` sanitizes
  client input) rather than through the client-visible `options` map. `ShapeRouter.cs:264`
  becomes the only writer.
- Tests: a POST `query` with `options.__filterSql` is refused; the OData GET path still
  filters correctly.

**H1 · writable-column allowlist**
- Add `List<string>? WritableColumns` to `MaskConfig` + `Mask` (`Core/MaskRegistry.cs`);
  populate in `Generator/MaskGenerator.cs` from non-sensitive `EntityMeta` fields (exclude
  PK, tenant column, audit columns, `password_hash`, `role`, `is_active`; add a `Sensitive`
  tag or reuse `AccessAttribute`).
- Enforce in `PerformInsert`/`PerformUpdate`: reject any `data` key not in the set with
  `WRITE_COLUMN_FORBIDDEN`. Generator always emits the list; `null` = legacy unrestricted.
- Tests: overposting `role`/`is_active`/unscoped-role on a generated `users` mask → 403.

**H2 · JWKS cache** — in `Auth/OidcVerifier.LoadJwks`: create the cache dir 0700 under the
app-private `.shape/` tree; verify ownership/mode before trusting a cache file; cap
stale-serve age.

**M1/M2** — require `exp` on the HS256 path (`JwtGuard`); mandatory `exp` for OIDC; validate
`audience`/`issuer` unconditionally when OIDC enabled and fail at boot
(`ConfigLoader`/`ShapeApp.ValidateOidcTenancy`) when `Audience` is empty.

**M3** — pluggable `ILoginThrottle` (in-memory default keyed by IP+email; Redis-backable)
invoked in `LoginService.Login`/`Exchange`.

**M4** — dummy `BCrypt.Verify` against a constant hash on the not-found branch.

**M5** — stream-and-count in `ReadAllBytesAsync` with early abort at `entry.MaxSize` (default
it); cap `queries`/`mutations` length in `OpDispatcher`; set
`KestrelServerLimits.MaxRequestBodySize`.

**M6** — extension from `DetectMimeType`; add serve-time headers; document private-file
proxying.

**M7** — generic message + `correlation_id` in `FatalHandler.RunSafelyAsync` and
`CommandExecutor`; full detail via the `Register` logger hook.

**M8/M9/M10** — filter `shape://tools`/`shape://manifest`/`shape://entities` by role in
`Mcp/McpServer.cs`; gate command visibility+invocation by role and default-deny
`CommandExecutor`; drop `sql_unscoped` from `ManifestGenerator.cs`.

**Lows** — `mcp.allow_anonymous` opt-in + origin allowlist (`Mcp/HttpTransport`,
`ShapeRouter`); prod-secret warning + file lock (`JwtSecretMaterializer`); create `.shape/`
0700 with create-time 0600 secret; `SqlConnectionStringBuilder` in `DsnTranslator`; exclude
`admin*` from OpenAPI / gate `?openapi`; security headers in `DocsUiPage`; DB-less-OIDC
`tenant_id` guard; clock-skew leeway; allowlist/redaction in `AdminMaskGenerator`.

## Implementation plan — PHP

Mirror each shared change 1:1 (parallel file layout). **C1 does not apply** (PHP filters via
an in-memory closure, `ShapeRouter.php:778`), but add a test asserting client input cannot
reach spliced SQL, to lock the divergence.

- **H1** — `writable_columns` on the mask array (`Core/MaskRegistry.php`); populate in
  `Generator/MaskGenerator.php`; enforce in `Core/BatchExecutor.php` insert/update with the
  same `WRITE_COLUMN_FORBIDDEN`.
- **H2** — cache dir 0700 + ownership check in `Auth/OidcVerifier.php::loadJwks`; keep the
  existing `LOCK_EX`.
- **M1/M2** — require `exp` in `Auth/JwtGuard.php`; mandatory `exp` + unconditional
  `audience`/`issuer` in `Auth/OidcVerifier.php`; boot check in `ShapeApp.php`.
- **M3** — APCu/Redis login throttle in `Auth/LoginService.php`.
- **M4** — dummy `password_verify` against a fixed hash on the not-found branch.
- **M5** — stream cap in `Upload/UploadManager.php`; batch-length cap in
  `Http/AppRouter.php`/`ShapeRouter.php`; treat `post_max_size` as a backstop only.
- **M6** — extension from detected MIME in `Upload/UploadManager.php`; serve headers/proxy
  guidance in `Upload/LocalStorage.php`.
- **M7** — redact in `Http/FatalHandler.php` + `Core/CommandExecutor.php`.
- **M8/M9** — role-filter the resource surface + command visibility/invocation in
  `Mcp/McpServer.php`. (M10 is .NET-only — PHP already omits SQL from its manifest.)
- **Lows** — `mcp.allow_anonymous` + origin allowlist (`Mcp/HttpTransport.php`); prod-secret
  warning in `ShapeApp::resolveAutoJwtSecret`; **L6** fix the SQLite PRAGMA backtick escaping
  in `Metadata/DatabaseIntrospector.php`; security headers in `DocsUiPage.php`;
  DB-less-OIDC `tenant_id` guard; clock-skew leeway; column redaction in
  `Generator/AdminMaskGenerator.php`.
- **Robustness** — unit-test the hand-rolled DER encoder in `Auth/OidcVerifier.php::jwkToPem`
  against `.NET` `RSA.ImportParameters` output across real JWKS keys (odd modulus lengths,
  leading-zero bytes).

**Parity discipline:** every fix gets matching tests on both sides with identical error
codes/messages so the two runtimes stay behaviorally equivalent.

---

## Appendix — verification method
Findings were produced by an initial manual read and then re-derived by five independent
audit tracks: (1) adversarial re-verification of the auth findings + `JwtIssuer`;
(2) data-layer injection audit (ParamResolver / Database / DsnTranslator / introspectors);
(3) config / secrets / bootstrap; (4) MCP / generators / audit logger / tenancy planner;
(5) mass-assignment + routing (the C1 injection was found while closing this track's final
open question on `__`-key handling). Each "VERIFIED" finding was reproduced from source at
the cited file:line by at least one track independent of the initial read.
