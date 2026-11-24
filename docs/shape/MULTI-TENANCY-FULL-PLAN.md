# Multi-tenancy FULL tier — membership, per-tenant roles, field-level access

> **Scope contract:** everything the simple tier deliberately excludes — one user in
> **N tenants** with a **different role per tenant**, **tenant-defined roles** backed by
> permission tables, **field-level** read/write permissions, and **row ownership**
> scoping. Prerequisite: [MULTI-TENANCY-SIMPLE-PLAN.md](MULTI-TENANCY-SIMPLE-PLAN.md)
> complete (especially refactor **R1** — `(column, context-key)` parameterization — and
> the S1 privilege-escalation guard, which every phase here assumes). Base design:
> [MULTI-TENANCY-PLAN.md](MULTI-TENANCY-PLAN.md); this plan implements its §2.12
> deferred tier plus the RBAC layers common CRMs/ERPs need.
>
> Applies to both stacks; PHP reference, .NET port with byte-identical manifest keys.

## 1. What changes vs. the simple tier

| Concern | Simple tier | Full tier |
|---|---|---|
| Membership | 1 user : 1 tenant (`users.<col>`) | 1 user : N tenants via `user_tenants` |
| Role source | Global `users.role` | Per-membership `user_tenants.role` |
| Role vocabulary | Static config strings | + tenant-defined roles in a `roles` table |
| Permissions | Per-mask role lists (+ per-action split) | + DB-driven `(role, resource, action)` rows |
| Field access | All columns of an allowed mask | Per-role column visibility + writable subsets |
| Row scope | Tenant only | Tenant AND optional `user_id` ownership (§2.11 of base plan) |
| Login identity | Email globally unique | Per-tenant emails possible (tenant hint at login) |

Design invariants that do **not** change: the tenant and role ride in the JWT as
server-issued claims; scoping SQL is compiled at build time, never runtime string
surgery; role/permission checks stay at the `MaskRegistry::resolve()` /
`BatchExecutor` chokepoints; every denial uses the standard error envelope.

## 2. Phase F1 — multi-tenant membership + role-per-tenant

The highest-value piece: the same person is `admin` in tenant A and `user` in tenant B.

### 2.1 Schema

```sql
CREATE TABLE user_tenants (
  user_id    INT NOT NULL REFERENCES users(id),
  tenant_id  INT NOT NULL REFERENCES tenants(id),
  role       VARCHAR(32) NOT NULL,
  is_default TINYINT NOT NULL DEFAULT 0,
  created_at DATETIME,        -- audit conventions apply
  PRIMARY KEY (user_id, tenant_id)
);
```

- Hardcoded **secret table** (like `users` / `refresh_tokens`) — never auto-CRUDed;
  admin masks generated with the same dual-variant scoping.
- **Backward compatible**: no `user_tenants` table (or no rows for a user) → the
  simple-tier `users.role` + `users.<tenant col>` pair acts as the single implicit
  membership. Existing apps migrate by *adding* the table, not altering `users`.
- Config: `'tenancy' => ['membership' => true, …]` — off by default; enabling with a
  missing table → build-time `CONFIG_INVALID` with the CREATE TABLE hint.

### 2.2 Auth flow

- **Login**: resolve memberships. One membership → exactly today's behavior. Several →
  issue the token pair for the `is_default` (else first) tenant and return
  `memberships: [{tenant_id, tenant_name, role}]` in the login response so a client can
  render the switcher without an extra call.
- **New endpoint `auth.switch-tenant`** `{tenant_id}`: verify an active membership +
  active tenant, issue a **fresh token pair** with that membership's `tenant_id` and
  `role`; revoke the presented refresh token (rotation chain records the switch).
  Nothing downstream changes — the executor, mask variants, and `__tenant` binding
  already trust the claims.
- **Refresh** re-reads the *membership* row (not `users.role`) so a demoted/removed
  membership propagates at most one TTL later, same as suspension.
- **Unscoped roles**: a membership row with a role in `unscoped_roles` is a build-time
  `CONFIG_INVALID` — unscoped status stays global (`users.role`), never per-tenant, or
  a tenant admin could mint a local "superadmin".
- `tenant.provision` gains an optional `existing_user_id` — provisioning can attach an
  existing user as the new tenant's first admin (creates the membership row).

### 2.3 Per-tenant unique emails (base plan §2.12)

With membership in place, login needs a tenant hint only when the same email exists in
several tenants: accept optional `tenant` (registry slug) in `auth.token`; ambiguous
email without hint → `VALIDATION_ERROR` listing nothing (no tenant-name oracle), hint
"supply tenant". Unique index moves from `users(email)` to `users(email, tenant_id)`
only for apps that opt in (`'tenancy' => ['per_tenant_emails' => true]`).

## 3. Phase F2 — tenant-defined roles (DB-driven RBAC)

For tenants needing custom vocabularies ("warehouse-clerk", "accountant") without
redeploying config.

### 3.1 Schema

```sql
CREATE TABLE roles (
  id INT PRIMARY KEY, tenant_id INT NULL REFERENCES tenants(id),  -- NULL = global role
  name VARCHAR(32) NOT NULL, UNIQUE (tenant_id, name)
);
CREATE TABLE role_permissions (
  role_id INT NOT NULL REFERENCES roles(id),
  resource VARCHAR(64) NOT NULL,   -- table name or mask name ('books', 'orders.summary')
  action VARCHAR(16) NOT NULL,     -- read | create | update | delete | *
  PRIMARY KEY (role_id, resource, action)
);
```

Both are registry-adjacent global tables managed through generated masks scoped so a
tenant admin edits only `tenant_id = own` rows (the existing machinery — `roles` carries
the discriminator, so it scopes for free) and cannot touch global roles.

### 3.2 PermissionResolver

One new component per stack, sitting **behind** the existing chokepoint checks:

```
allowed(authContext, resource, action):
  role in mask.roles (static config)          → allow   # config roles keep working
  role is a DB role for authContext.tenant_id → lookup (role, resource, action|*)
  else                                        → deny
```

- A static config role is a pre-resolved permission set — precedence: explicit mask
  `roles` grant wins, then DB permissions. No behavior change for apps that never
  create a `roles` row.
- **Caching**: permission set loaded once per request (single indexed SELECT), plus an
  in-process TTL cache keyed `(tenant_id, role)`; invalidation piggybacks on writes
  through the `role_permissions` writable mask. Never cache across the access-token TTL.
- Manifest / OpenAPI / MCP `tools/list` filtering calls the same resolver, so a custom
  role sees exactly its endpoints. Parity test: resolver decisions byte-compare across
  stacks for a fixture permission set.
- JWT carries the role **name** only; permissions are always resolved server-side
  (revocation must not wait for token expiry).

## 4. Phase F3 — row ownership + field-level access

### 4.1 Row ownership (`user_id` auto-scoping — base plan §2.11)

Ships as the second convention of the R1-parameterized machinery: column `user_id`,
context key `authContext.id`. Composes with tenancy (predicates AND together). Paired
with F2, `read` splits into `read` (all tenant rows) vs `read_own` (owned rows only) —
resolver picks the mask variant. That is the CRM "reps see own accounts, managers see
the team's" story.

> The R1 machinery and the generic dimension case (department / cost center /
> branch — "row-level security by dimension") are now planned in detail in
> [ROW-SCOPES-PHP-PLAN.md](ROW-SCOPES-PHP-PLAN.md) and
> [ROW-SCOPES-DOTNET-PLAN.md](ROW-SCOPES-DOTNET-PLAN.md); `user_id` ownership
> is one `scopes` entry (`'user_id' => ['claim' => 'sub']`) on top of them.

### 4.2 Field-level read

Per-role column visibility, compiled at build time — the `sql`/`sql_unscoped` dual-variant
pattern generalized to a variant per **column set**, never per role name (roles sharing a
set share a variant; typical entity has 1–3 sets). Config:

```php
'access' => ['employees' => [
    'roles' => ['admin', 'hr', 'user'],
    'columns' => ['salary' => ['admin', 'hr'], 'ssn' => ['hr']],  // omit = visible to all
]],
```

Manifest lists per-field `roles`; OpenAPI renders per-role response schemas only when a
flag asks for it (schema explosion is real). COUNT/OData wrap the chosen variant —
same rule as tenancy.

### 4.3 Field-level write

Writable-field subsets per role enforced in `AutoValidators` (the audit-column strip
mechanism reused): a client writing a field its role may not touch →
`VALIDATION_ERROR` detail `FIELD_FORBIDDEN` (explicit beats silent-strip — agents
must learn the boundary from the error).

## 5. Testing strategy (extends base §6)

- **Membership matrix**: user in A(admin)+B(user); token for A cannot read B even with
  membership; switch-tenant rotates claims; removed membership blocks switch and next
  refresh; default-tenant selection; single-membership user sees zero behavior change.
- **Escalation suite**: per-tenant role named in `unscoped_roles` fails build; tenant
  admin cannot grant permissions on another tenant's resources nor edit global roles;
  switch-tenant to a non-membership tenant → `AUTH_FORBIDDEN`; suspended tenant blocks
  switch-tenant.
- **Resolver**: config-role precedence; `*` action; cache invalidation on permission
  write; cross-stack parity fixture.
- **Field-level**: hidden column absent from every surface (list/get/OData/`$select`/
  manifest); `FIELD_FORBIDDEN` on forbidden write; variant/COUNT consistency.
- **Ownership**: own-vs-all matrix composed with two tenants (4-quadrant leak test).

## 6. Risks & edge cases

| Risk | Mitigation |
|---|---|
| Per-tenant "superadmin" minted via membership/roles rows | Build-time + write-time guards: unscoped status is global-only (§2.2, F2 masks) |
| Stale permissions after revocation | Server-side resolution per request; TTL cache bounded by token TTL |
| Variant explosion (field-level × tenancy × ownership) | Variants keyed by column-set, not role; build-time report lists per-mask variant count; hard cap with `CONFIG_INVALID` |
| Membership table drifts from `users.role` fallback | Build-time warning when a user has both differing rows; migration recipe backfills `user_tenants` from `users` |
| Switch-tenant token replay | Fresh pair + old refresh revoked in one transaction (existing rotation chain) |
| OData `$select` bypasses column ACL | `$select` validated against the caller's visible column set, not the table's |

## 7. Suggested phasing

1. **F1** membership + switch-tenant (+ optional per-tenant emails) — delivers
   "role per tenant" with zero executor changes. *~2–3 days/stack.*
2. **F2** DB-driven roles + PermissionResolver. *~3–4 days/stack.*
3. **F3** ownership convention, then field-level read, then field-level write —
   independently shippable slices. *~2 days/stack combined.*

Stop where demand stops: F1 alone covers the consultancy-CRM / multi-org-ERP shape;
F2/F3 only pay off for platform-style deployments.

## 8. STATUS.md updates on completion

- New matrix rows: **Multi-tenant membership**, **DB-driven RBAC**, **Field-level
  access**, flipped per stack as each phase lands, linking here.
- Flip **Row-level auto-scoping (`user_id`)** when F3.1 ships (it is base-plan §2.11).
- SPEC.md gains `user_tenants` / `roles` / `role_permissions` schemas, the
  `auth.switch-tenant` op, `roles_by_action` + column-ACL mask keys, and error details
  `FIELD_FORBIDDEN`.
