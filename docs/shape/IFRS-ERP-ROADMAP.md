# Shape for IFRS ERP — feature roadmap

Shape today is a strong foundation for the data and API tier of an ERP — auto-CRUD, audit log, OIDC, role-based access, MCP, OpenAPI — but it stops short of the accounting-engine concepts an IFRS-compliant system needs (immutability, period locking, gap-free numbering, cross-row invariants like double-entry). This document lists the framework-level features that would meaningfully reduce that gap, grouped by how general-purpose they are. Tier 1 features earn their place in Shape itself; Tier 2 are accounting-flavored but still framework-fit; Tier 3 stays out — too domain-specific, build as application commands.

The framework's design contract: small, declarative, schema-driven, auditable by default. These additions follow that contract. None require breaking changes — every new behavior is opt-in via a mask flag, a top-level config key, or a detected schema convention.

---

## Three-layer architecture — `ShapeRuntime`, `Shape`, `ShapeLedger`

The features described below split cleanly across **three packages** with a strict dependency direction. The third package — proposed name **`ShapeLedger`** — is what turns Shape into ERP-credible territory. Each layer adds opinions; teams that don't need a higher layer don't pay for it.

```
ShapeRuntime  ←  Shape  ←  ShapeLedger
   (runtime)     (framework)   (audit-grade conventions)
```

### Per-layer contract

| Layer | Contract | What's in it |
|---|---|---|
| **`ShapeRuntime`** | Given configured masks + a JSON request, execute it. Pure runtime. | PDO gateway, mask registry, batch executor, JWT/OIDC auth, fatal handler, validators. |
| **`Shape`** | Given a database schema, generate masks / commands / manifest / OpenAPI / MCP from it. | DB introspector, mask generator, manifest, OpenAPI, MCP server, file uploads, audit-log convention, audit-columns convention, soft-delete convention. |
| **`ShapeLedger`** | Given a schema **plus compliance primitives in mask config**, enforce immutability / period locking / sequencing / approvals / cross-row invariants. | Period-lock validator, sequence allocator, FX revaluation engine, approval state machine, double-entry invariant, dimension scoping, as-of-date temporal queries. |

### Where each Tier 1 / Tier 2 feature lives

Tier numbers (below) describe how *general-purpose* the feature is. Layer assignment (below) describes which *package* it ships in. The two lenses cross — some Tier 1 features land in `Shape`, others in `ShapeLedger`, depending on how accounting-flavored the concept is.

| Feature | Tier | Lives in |
|---|---|---|
| `'immutable'` flag + reversal action (#1) | 1 | **Shape** — pattern is general for any audit-sensitive table |
| Idempotency keys (#5) | 1 | **Shape** — protocol-layer concern, every backend benefits |
| Before/after diffs in `audit_log` (#4) | 1 | **Shape** — small extension of an existing Shape primitive |
| Period locking by convention (#2) | 1 | **ShapeLedger** — finance-flavored; specific concept of "fiscal period" |
| Gap-free sequence numbering (#3) | 1 | **ShapeLedger** — invoice / journal flavor; not every app needs gap-free guarantees |
| Multi-currency convention (#6) | 2 | **ShapeLedger** — pure accounting (IAS 21) |
| Cross-row validators (`sum_zero`) (#7) | 2 | **ShapeLedger** — double-entry is the canonical use |
| Approval workflows (#8) | 2 | **ShapeLedger** — accounting pattern (could equally live in Shape; argument either way) |
| Dimension scoping (#9) | 2 | **ShapeLedger** — chart-of-accounts dimensions |
| As-of-date queries (#10) | 2 | **ShapeLedger** — temporal accounting / SCDs |

This split puts the **general-purpose audit primitives** in `Shape` proper (so every Shape backend benefits), and the **accounting-flavored conventions** in `ShapeLedger` (so teams that don't want them aren't paying for them). Apps that need Tier 3 features (financial statement assembly, IFRS 16 schedules, XBRL output, consolidation) build them as commands in their own application layer — that's `Tier 3 = your app`.

### Distribution

Ship `ShapeLedger` as a **separate Composer package** (`shape/shapeledger`), parallel to how `shape/shape` already sits on `shape/shaperuntime`. Operators add it explicitly when they need it:

```bash
composer require shape/shape         # Shape framework (today)
composer require shape/shapeledger    # opts into the audit-grade conventions
```

Marketing-wise this is also clean: *"Building an ERP, regulated SaaS, or any audit-sensitive system? `composer require shape/shapeledger`."* The boundary between Shape and ShapeLedger forces good API design — what's the contract between them? — which keeps both layers cleaner than they would be as one bigger framework.

### Why this is the right split (and not "all in Shape")

A team building a B2C SaaS doesn't need period locking, gap-free sequences, FX revaluation, or approval workflows. Bundling those into `Shape` itself would inflate its surface, complicate its mask schema, and make every adopter pay for ERP-shaped features they don't use. Splitting them out keeps `Shape` minimal and makes `ShapeLedger`'s value proposition explicit: *"this is the layer you reach for when auditors will read your code."*

The dependency direction also stays one-way: `ShapeLedger` can extend `Shape`'s mask config (adding new keys like `'immutable'`, `'sequence'`, `'approval'`); `Shape` doesn't need to know `ShapeLedger` exists. That's the same relationship `Shape` already has with `ShapeRuntime`.

---

## Tier 1 — framework primitives every compliance-heavy app needs

These aren't IFRS-specific. They benefit any audit-sensitive system (medical records, legal documents, insurance claims) and earn their place in Shape itself.

### 1. `'immutable'` mask flag — append-only with reversal

```php
'masks' => [
    'journal_entries' => [
        'writable'   => true,
        'immutable'  => true,         // INSERT-only; UPDATE / DELETE rejected at framework layer
        'reversible' => 'reverse_of', // FK column for reversal entries
    ],
],
```

Framework rejects UPDATE/DELETE on tables flagged immutable. Adds a built-in `reverse` action that creates an offsetting INSERT pointing at the original via the configured FK. Audit log records the reversal as `action: reverse` with both row IDs.

**Why framework, not app:** the rule is invariant across every audit-sensitive table; baking it into `BatchExecutor` and `MaskGenerator` means every mask gets it correctly without operator discipline. The alternative — relying on every developer to remember to disable UPDATE on every audit-relevant mask — is the failure mode auditors see most often.

**Effort:** ~80 LOC in `BatchExecutor` + `MaskGenerator`. Single biggest IFRS-relevance win.

### 2. Period locking by convention

When a table named `accounting_periods` (or configurable) exists with a `status` column, the framework auto-installs a validator that rejects mutations to any table with a `period_id` (or `posting_date`) when the relevant period isn't `open`.

```php
'period_locking' => [
    'period_table'  => 'accounting_periods',  // default
    'date_column'   => 'posting_date',         // on the protected table
    'status_field'  => 'status',                // on accounting_periods
    'open_value'    => 'open',
    'override_role' => 'cfo',                   // allowed to back-date if needed
],
```

**Why framework, not app:** "no posting to closed periods" is the single most common audit finding; making it a framework-detected convention means the moment an `accounting_periods` table appears in the schema, the rule is on. Override role is the audit-trail-recorded escape hatch for legitimate back-dating.

**Effort:** ~60 LOC. Detected at boot via the same column-existence check the cache bundle already does for `password_hash` and `users_lookup_column`.

### 3. Gap-free sequence numbering

```php
'masks' => [
    'invoices' => [
        'writable' => true,
        'sequence' => [
            'column' => 'invoice_number',
            'format' => 'INV-{YYYY}-{NNNNN}',  // NNNNN zero-padded, 5 digits
            'reset'  => 'yearly',                // | 'never' | 'monthly'
        ],
    ],
],
```

Framework allocates the next number atomically inside the same transaction as the INSERT, using a dedicated `_shape_sequences` table with row-level locking (gap-free, unlike PostgreSQL's `SEQUENCE` objects which lose values on rollback). Year boundary triggered by row's `created_at`.

**Why framework, not app:** every team rolls their own; most of those implementations have race conditions or leave gaps under contention. VAT auditors care about gap-free numbering specifically; a one-line declaration is the right ergonomics.

**Effort:** ~120 LOC across drivers.

### 4. Before/after diffs in audit log

Today Shape's `audit_log` records `changed` (a list of column names). Add a `before` / `after` JSON column convention so each audit row carries the actual values that changed.

```sql
audit_log (
    ...,
    changed       JSON,        -- existing: ["name","price"]
    before_values JSON,        -- new:      {"name":"Acme","price":100}
    after_values  JSON         -- new:      {"name":"Acme Corp","price":120}
)
```

If the columns exist, Shape populates them; if not, falls back to existing behavior. Auditor-friendly: *"what was customer 42's credit limit on 2025-12-31?"* answerable from one query.

**Why framework, not app:** the audit-log pipeline is in the framework; this is a 30-line extension of existing behavior, gated on column existence so adopters opt in by adding columns.

**Effort:** ~30 LOC in `BatchExecutor::executeUpdate`.

### 5. Idempotency keys

```http
POST /api.php
Idempotency-Key: 7e2f...
{op: 'mutation', payload: {...}}
```

First call commits and stashes the result keyed by header + body hash; replays of the same key within TTL return the cached result without re-posting. **Critical for ERP** — every double-post of a $50k journal is an audit incident.

**Why framework, not app:** retry semantics belong at the protocol layer, not in every command's body. Already a candidate on the main roadmap; ERP use cases promote it to "must-have."

**Effort:** ~60 LOC.

---

## Tier 2 — accounting-flavored conventions, still framework-fit

These are more domain-leaning but the concepts repeat across enough finance / HR / insurance schemas to justify shipping them.

### 6. Multi-currency convention

```php
'masks' => [
    'invoices' => [
        'writable' => true,
        'currency' => [
            'currency_column' => 'currency',
            'amount_columns'  => ['subtotal', 'tax', 'total'],
            'base_columns'    => ['subtotal_base', 'tax_base', 'total_base'],
            'rate_table'      => 'fx_rates',
            'rate_date'       => 'invoice_date',
        ],
    ],
],
```

On INSERT, framework looks up the FX rate from `fx_rates` and populates the `*_base` columns. Built-in `accounting.fx_revalue` command reprices open balances at period-end rates. The `fx_rates` schema is detected conventionally — table existence is enough.

**Why framework, not app:** IAS 21 logic is identical across every multi-currency ERP. Doing it once in the framework saves every adopter 200+ lines of conversion code per writable mask.

**Effort:** ~150 LOC + a built-in command.

### 7. Cross-row validators

Today Shape validates one row at a time. Add a mask-level validator that runs against the *batch* of rows being inserted in one mutation.

```php
'masks' => [
    'journal_entries' => [
        'writable' => true,
        'batch_validators' => [
            'sum_zero' => ['group_by' => 'journal_id', 'sum_column' => 'amount'],
        ],
    ],
],
```

Enforces double-entry (debits − credits = 0 per journal) at the framework layer. Generalizable: same mechanism handles "sum of allocation percentages = 100" for cost-allocation tables, "sum of line totals = invoice total" for invoice integrity, etc.

**Why framework, not app:** double-entry is the canonical example, but the abstraction (group-by + sum check) covers many domains. The alternative is a custom validator in every command, which gets forgotten.

**Effort:** ~50 LOC in `BatchExecutor`.

### 8. Approval workflow primitive

```php
'masks' => [
    'payments' => [
        'writable' => true,
        'approval' => [
            'state_column' => 'approval_status',
            'levels'       => [
                ['role' => 'accountant', 'min_amount' => 0],
                ['role' => 'manager',    'min_amount' => 5000],
                ['role' => 'cfo',        'min_amount' => 50000],
            ],
        ],
    ],
],
```

Framework gates downstream operations (e.g. a payment-execution command) until `approval_status === 'approved'`. Audit log records every state transition with actor. Two new built-in actions: `approve` and `reject`.

**Why framework, not app:** approval routing is the most-reinvented wheel in any ERP. The state-machine concept is small; the value of having one canonical implementation that audit logs correctly is large.

**Effort:** ~100 LOC.

### 9. Dimension scoping

Many accounting tables carry the same dimension columns: `cost_center_id`, `department_id`, `project_id`, `product_id`. Top-level config:

```php
'dimensions' => ['cost_center_id', 'department_id', 'project_id'],
```

When set, every read mask gets auto-extended with `?cost_center_id=42&project_id=17` query params, and any mutation against a table with these columns auto-stamps them from the auth context if present (e.g. user belongs to a department → all their inserts default to that department).

**Why framework, not app:** the "filter by every dimension everywhere" boilerplate explodes for any non-trivial ledger. Treating dimensions as a first-class convention is generic enough to belong in Shape.

**Effort:** ~80 LOC.

### 10. As-of-date queries

For type-2 slowly-changing dimensions — common in HR for employee history, in accounting for chart-of-accounts versions, in pricing for historical price lists:

```php
'masks' => [
    'employees.list' => [
        'sql'      => 'SELECT * FROM employees',
        'temporal' => ['valid_from' => 'valid_from', 'valid_to' => 'valid_to'],
    ],
],
```

Adds `?as_of=2025-12-31` query param that automatically wraps the SQL in `WHERE valid_from <= :as_of AND (valid_to IS NULL OR valid_to > :as_of)`. *"What was the org chart on year-end?"* is one URL.

**Why framework, not app:** the SCD pattern is identical across every system that has it; useful well beyond accounting.

**Effort:** ~40 LOC.

---

## Tier 3 — stays out of the framework

Too domain-specific or too implementation-varied. Build in your application layer as `commands`; the framework features above give you the right plumbing.

- **Financial statement assembly** (Balance Sheet, P&L, Cash Flow). Each company has its own chart of accounts; the mapping from accounts to statement lines is per-tenant. Build as a `report.balanceSheet` command that reads from a `statement_line_mapping` table you own.
- **IFRS 16 lease calculations**, **IFRS 15 revenue recognition**, **IAS 36 impairment tests**. All highly parameterized — right-of-use asset schedules, performance-obligation allocations — and the inputs (lease terms, revenue contracts) are application data, not framework concepts. Package as commands.
- **XBRL output**. Filing format with country-specific taxonomies (US GAAP vs UK FRS vs Czech accounting standard). Build as an exporter command per jurisdiction; framework stays format-agnostic.
- **Consolidation under IFRS 10 / 11 / 12**. Eliminations between entities depend on entity hierarchies and chart-of-accounts mapping per group — too custom for one framework feature. Multi-tenant scoping (Tier 2 item from the main roadmap) gives the data-shape; the consolidation engine itself is your domain.
- **Tax engines** (US sales tax, EU VAT, Indian GST). Rule complexity varies by jurisdiction and changes annually; better delegated to specialized libraries (Avalara, Vertex, TaxJar) called from commands.

---

## Recommended sequence

If we wanted to ship the framework deltas in priority order:

| # | Feature | Why this order |
|---|---|---|
| 1 | `'immutable'` flag + reversal action | Single biggest IFRS audit win; ~half a day |
| 2 | Idempotency keys | Protects every mutation, not just journals |
| 3 | Period locking by convention | No IFRS shop ships without this |
| 4 | Cross-row validators | Double-entry invariant becomes a one-line declaration |
| 5 | Sequence numbering | VAT-audit insurance |
| 6 | Before/after diffs in audit log | Cheap; satisfies the most common auditor request |
| 7 | Multi-currency | Bigger, but unlocks IAS 21 territory |
| 8 | Approval workflows | High value but more design surface to settle |
| 9 | Dimensions + as-of-date | Quality-of-life on top of the above |

Items 1–6 are roughly **two weeks of focused work** and cover ~70% of the framework deltas an IFRS team would notice on day one. Items 7–9 are another two weeks.

**Suggested release packaging:**

- **"Audit-grade" release** — items 1, 2, 4, 5, plus the existing-roadmap idempotency-keys candidate. Minimum viable framework story for *"yes, you can build SOX/IFRS-compliant systems on this."*
- **"Finance edition" release** — items 3, 6, 7. Completes the accounting-specific layer.
- **"Quality of life" release** — items 8, 9, 10. Quality and ergonomics on top.

---

## What stays out of scope (for clarity)

To keep expectations grounded: even with all twelve features above shipped, an IFRS ERP built on Shape is roughly **30% framework, 70% domain logic**. That ratio is the same on any framework — accounting is a deep specialty. What Shape buys you is that the 30% is *finished* on day one. Where Shape stops:

- It is not a workflow engine. State machines beyond the single approval primitive (item 8) are domain logic.
- It does not know what a chart of accounts is, what a fiscal calendar is, how to consolidate entities, or how to file a tax return. Those are commands and reports you write.
- It is not a UI framework. Frontend is your stack of choice (React, Vue, etc.) consuming the API.
- It does not migrate accounting data from your existing system. Shape sits over your data; data migration is a one-time project on top.

The win is that the *plumbing* — auth, audit, immutability, period gates, sequences, double-entry invariants, multi-currency conversion, approval routing — is declarative one-liners instead of weeks of bespoke code. That's where six months of the typical greenfield-ERP project lives, and it's where Shape closes the gap.

---

📖 Related: [`AUTH.md`](AUTH.md) for the OIDC + JWT story (essential for enterprise ERP), [`DB-FIRST.md`](DB-FIRST.md) for the schema-driven model that the audit-log conventions extend, [`ROADMAP.md`](../../ROADMAP.md) for the broader framework direction.
