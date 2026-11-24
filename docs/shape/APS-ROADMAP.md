# Shape for advanced planning & scheduling — feature roadmap

Shape today carries roughly half of what an APS (Advanced Planning & Scheduling) system needs at the data-tier layer — auto-CRUD over master data (resources, work centers, BOMs), audit trail, dimensions via OData, MCP server for planner-AI assistants, bulk-nested-create for work-order trees, ETag-cacheable reads for Gantt UIs. This document lists the framework-level features that would close the gap to "credible APS foundation," and the seven APS-specific patterns that complement the IFRS-ERP roadmap (`IFRS-ERP-ROADMAP.md`) for scheduling-heavy systems.

The framework's design contract — small, declarative, schema-driven, opt-in via mask flag or detected schema convention — applies unchanged. None of these features require breaking changes; verbose / explicit forms keep working.

---

## What an APS system needs

Every APS system, regardless of industry (discrete manufacturing, process, services, projects), shares a core data model:

- **Resources** — machines, work centers, people — with calendars and finite capacity per time bucket.
- **Operations / tasks** — units of work with duration, precedence, and resource demand.
- **Routings / BOMs** — how products are made (sequence of operations + materials).
- **Demand** — sales orders, forecasts — with due dates.
- **Schedules** — versioned solutions binding operations to (resource, time-window) tuples.
- **Constraints** — material availability, calendar windows, alternative routings, precedence relations.
- **What-if scenarios** — evaluate alternatives before committing.
- **Re-planning** — rolling-horizon updates as reality drifts from the plan.
- **Visibility** — Gantt charts, capacity-loading reports, bottleneck analysis.

Roughly 60–70% of an APS implementation is the *scheduling algorithm itself* (finite-capacity, theory-of-constraints, MILP / CP-SAT solvers, dispatching rules). The remaining 30–40% is the data tier — exactly where Shape lives.

---

## What Shape already gives an APS team for free

| Shape feature | APS use |
|---|---|
| Auto-CRUD from schema | Resources, work centers, calendars, BOMs, routings, products — generated in hours, not weeks |
| `audit_log` convention | "Why did order #1234 move to next week?" — answerable from one query against `audit_log` |
| Bulk nested create (`relations`) | `work_order` → `operations` → `resource_allocations` is the canonical APS shape; one mutation inserts the whole tree atomically |
| Soft-delete (`deleted_at`) | Rejected scenario versions marked deleted; reads automatically filter |
| OData filters (`?filter=...`) | Capacity queries: `resource_id eq 5 and start_date ge '2026-05-01'` |
| ETag + Cache-Control | Gantt-chart UIs hit reads constantly; cacheable URLs cut DB load 10–20× |
| Pagination push-down | Schedules can have 100k+ operations; SQL-pushed `?$top` keeps Gantt windows fast |
| MCP server + LLM tool definitions | "Why is order #1234 late?" / "What's the bottleneck this week?" — APS planners are early-adopter heavy on AI assistants. Big domain-specific win |
| Streaming JSON output (proposed) | Schedule export endpoints (10k+ rows for week-long Gantt) are exactly where streaming pays |

---

## IFRS-ERP roadmap features that map directly onto APS

From `IFRS-ERP-ROADMAP.md`, several features framed in accounting terms are equally useful for scheduling:

| Roadmap feature | APS framing |
|---|---|
| `'immutable'` flag + reversal | Released schedules become immutable; revisions are new versions linked to predecessors |
| Period locking by convention | "Frozen weeks" — production within the next 5 days isn't reschedulable without override |
| Gap-free sequence numbering | Work-order numbers, job numbers — same audit-traceability invariant |
| Cross-row validators (`sum_max`) | Capacity invariant: *"sum of resource-hours per day per work center ≤ available capacity"* — declarative one-liner |
| Approval workflows | Schedule-version state machine: `draft → review → released` |
| Dimensions (cost_center, work_center, shift) | Auto-scoped queries across the typical APS dimensions |
| As-of-date queries | *"What was the BOM for product X on 2026-04-15?"* / *"What was Monday's schedule snapshot?"* — temporal queries are central to APS |
| Idempotency keys | APS scheduling jobs retry after timeouts constantly; without idempotency you get phantom duplicate operations |

These are not duplicated below — they live in the IFRS-ERP roadmap and serve both domains. The seven items below are the **APS-specific** additions on top.

---

## APS-specific feature roadmap

### 1. Calendar / shift integration

```php
'calendars' => [
    'table'            => 'work_calendars',
    'shift_table'      => 'shifts',
    'holiday_table'    => 'holidays',
    'default_calendar' => 'plant_a',
],
```

When a mask query asks for "next 3 working days from Friday" or "operations within shift A", the framework consults the `calendars` table and skips weekends / configured holidays / non-shift hours. Detected at boot via the table's existence — same convention pattern as `audit_log`.

**Why framework, not app:** every APS team rolls their own calendar math; the result is a recurring source of off-by-one date bugs around DST transitions, leap years, and holiday calendars. One framework-level implementation eliminates the class.

**Effort:** ~120 LOC + driver-aware date arithmetic. **Single highest-leverage APS feature** because every system in every industry shares this pattern.

### 2. Capacity-aware aggregations

```php
'masks' => [
    'capacity.daily' => [
        'sql' => 'SELECT work_center_id, planned_minutes FROM operations',
        'capacity' => [
            'by'           => 'work_center_id',
            'period'       => 'day',
            'limit_table'  => 'work_center_capacity',
            'limit_column' => 'minutes_per_day',
        ],
    ],
],
```

Mask wraps the SQL with a per-period aggregation and joins against the capacity table to compute *planned vs available* per resource per time bucket. Returns rows like `{ work_center_id, day, planned_minutes, available_minutes, utilization_pct }`. A single declaration replaces ~200 LOC of bespoke aggregation in every APS system.

**Why framework, not app:** the capacity-loading report is the most common APS dashboard. Standardizing the shape and computation reduces every team's effort and makes the manifest self-describing for AI agents asking *"how loaded is work center 5 next week?"*

**Effort:** ~150 LOC.

### 3. Versioning by convention

```php
'masks' => [
    'schedule_operations' => [
        'writable' => true,
        'version'  => [
            'column'     => 'schedule_version_id',
            'default_to' => 'latest_released',  // or 'latest_any', or specific id
        ],
    ],
],
```

Reads default to the latest released version; writes go to the current draft version; `?version=42` query param overrides. Lets the APS planner work in a draft scenario without touching production data — and lets dashboards always read the released schedule without filtering manually.

**Why framework, not app:** the live-vs-draft confusion is the single biggest production bug pattern in APS deployments. A framework-level convention removes the class of bug entirely; the alternative is per-team discipline that breaks under pressure.

**Effort:** ~80 LOC.

### 4. Precedence / no-overlap validators

```php
'masks' => [
    'operations' => [
        'writable' => true,
        'batch_validators' => [
            'precedence' => ['after_column' => 'predecessor_op_id', 'time_column' => 'start_at'],
            'no_overlap' => ['by' => 'resource_id', 'start' => 'start_at', 'end' => 'end_at'],
        ],
    ],
],
```

Extension of the IFRS-ERP cross-row validator framework: `precedence` enforces *"B starts after A finishes"*; `no_overlap` enforces resource exclusivity (*"two operations can't occupy the same machine at the same time"*). Replaces hand-rolled validation in every scheduling command.

**Why framework, not app:** these two invariants are the floor of any feasible schedule. Every APS validates them; standardizing the declaration makes the constraint visible in the manifest, queryable by AI agents (*"what precedence relations exist on operation 5?"*), and uniformly enforced across all mutation paths.

**Effort:** ~80 LOC, riding on top of the IFRS-ERP cross-row validator framework.

### 5. Optimistic locking for planner sessions

```php
'masks' => [
    'schedule_operations' => [
        'writable' => true,
        'lock' => [
            'column' => 'lock_token',
            'ttl'    => 600,  // seconds
        ],
    ],
],
```

Before updating an operation, the planner UI grabs a lock token (TTL 10 minutes). A second planner trying to move the same op gets a friendly `LOCK_HELD` error with the holder's name. Lock auto-releases on TTL expiry or explicit unlock. Prevents the classic "two planners both moved this job" race.

**Why framework, not app:** multi-planner shops hit this conflict daily. Without framework support, every team builds either nothing (silent overwrites) or a bespoke locking mechanism with subtle TTL bugs. The standard implementation matters.

**Effort:** ~60 LOC.

### 6. What-if scenario isolation

```php
'scenarios' => [
    'scenario_dim' => 'scenario_id',
    'live_value'   => 0,           // scenario_id = 0 means "live schedule"
    'auto_filter'  => true,
],
```

The `scenario_id` dimension auto-applies to every read and write. A planner runs `?scenario_id=42` to work in scenario 42 without touching live data. Promoting a scenario to live is a built-in command that copies its rows back to scenario 0 with audit-log entries. Built on top of the IFRS-ERP dimensions feature.

**Why framework, not app:** scenario branching is what APS planners actually want — *"try this and tell me the cost vs the current schedule."* Without framework support, teams either skip the feature (and planners can't experiment) or build a fork-DB sandbox that desyncs from production.

**Effort:** ~50 LOC if dimensions ships first; ~150 LOC standalone.

### 7. Cascading reschedule

```php
'mutations' => [[
    'table'   => 'operations',
    'action'  => 'reschedule',     // new built-in action
    'filter'  => ['id' => 4521],
    'data'    => ['start_at' => '2026-05-15 08:00'],
    'cascade' => ['follow_predecessors' => true],
]],
```

New `reschedule` action moves the target operation and cascades to all dependent operations following the precedence graph. Built on the FK-walking pattern from the existing `relations` block. Validates the cascaded changes pass capacity / precedence invariants in one transaction. Atomic: either the whole cascade succeeds or none of it does.

**Why framework, not app:** moving one operation in a typical schedule cascades to 20–50 downstream ops. Doing it manually (or with a custom command per app) is the most tedious bit of APS plumbing. A framework primitive that walks the precedence graph turns 200 LOC of cascade logic into one declarative line.

**Effort:** ~150 LOC, riding on top of the existing `relations` FK-walker.

---

## What stays out of Shape

The actual scheduling algorithms remain domain logic. APS systems are 60–70% algorithm, 30–40% data tier. Shape can't help with:

- **Finite-capacity scheduling solvers** — CP-SAT (Google OR-Tools), MILP, heuristics like forward/backward scheduling, theory of constraints. Pure algorithm work.
- **Real-time event handling** — a machine breaks down at 14:32 → schedule reacts in seconds. Event-driven architectures (Kafka, NATS) layer on top of Shape.
- **Optimization** — minimize makespan, maximize throughput, level-load resources. External solvers called from commands.
- **Forecasting** — time-series ML for demand prediction (Prophet, ARIMA, deep learning). External services.
- **Gantt UIs** — frontend concern (DHTMLX, Bryntum, custom React).
- **MES integration protocols** — OPC UA, SECS/GEM, MQTT. Per-vendor adapters live in commands or dedicated microservices.
- **Constraint-programming declarations** — Shape's validators express simple invariants; expressing *"job A and job B can't both run during the day shift unless operator C is on"* needs a real CP solver.

---

## Recommended sequence

If we wanted to ship the APS-specific deltas in priority order:

| # | Feature | Why this order |
|---|---|---|
| 1 | Calendar / shift integration | Highest-leverage single feature; every APS in every industry needs it |
| 2 | Versioning by convention | Removes the biggest source of production bugs (live-vs-draft mixing) |
| 3 | Precedence / no-overlap validators | Floor of a feasible schedule; trivial declaration once cross-row validators exist |
| 4 | Capacity-aware aggregations | Enables the canonical APS dashboard |
| 5 | Cascading reschedule | Removes the most tedious bit of plumbing |
| 6 | Optimistic locking | Multi-planner conflict prevention |
| 7 | What-if scenarios | Quality-of-life on top of the above; depends on dimensions feature shipping first |

Items 1–3 are roughly **a week of focused work** and cover ~50% of the APS-specific deltas. Items 4–7 are another two weeks. Combined with the IFRS-ERP roadmap features that double-duty (immutability, period locking, sequences, cross-row validators, dimensions, as-of-date, idempotency keys), the **complete APS-credible Shape posture** is roughly four weeks of framework work — versus six to nine months of bespoke development on a generic framework.

---

## Summary

| Category | Coverage |
|---|---|
| **Shape today** | ~50% of APS data tier (master data, audit, dimensions via OData, MCP for planner-AI, bulk-nested create, ETag-cacheable reads) |
| **IFRS-ERP roadmap features** that double-duty | Adds another ~15% — they generalize cleanly across both ERP and APS |
| **APS-specific roadmap** (this doc) — calendars, capacity aggregations, versioning, precedence validators, locking, scenarios, cascade | Adds another ~10% |
| **Your domain logic** — scheduling algorithms, optimization, forecasting, MES adapters | The remaining ~25%, irreducible |

Combined, an APS implementation on a fully-built-out Shape would land around **75% framework, 25% domain logic** — inverting the typical 30/70 split on a generic framework. That's the quantitative argument for treating Shape as the APS foundation rather than as a generic framework.

---

📖 Related: [`IFRS-ERP-ROADMAP.md`](IFRS-ERP-ROADMAP.md) for the parallel ERP feature roadmap (many features serve both domains), [`DB-FIRST.md`](DB-FIRST.md) for the schema-driven model the APS conventions extend, [`AGENTS.md`](../../AGENTS.md) for the agent-friendly entry point.
