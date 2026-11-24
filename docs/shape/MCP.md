# Shape MCP Server

> **AI agents:** read [`AGENTS.md`](../../AGENTS.md) first for the framework-level overview. This file is the deep MCP reference.

## Overview

Shape ships a built-in [Model Context Protocol](https://modelcontextprotocol.io/) server, so any Shape backend can be mounted directly into Claude Desktop, Cursor, Cline, Continue, or any other MCP-aware agent. The agent then talks to the database through the same masks, role checks, and self-documenting error envelopes the HTTP API enforces — no separate authorization layer, no client-side translation step.

## What makes Shape's MCP different

Most MCP servers expose tools and let the LLM guess at when to use them. Shape exposes a **narrated guide to the entire backend** as a first-class MCP resource:

- **`shape://README.md`** — an auto-generated, human-readable overview of every entity, mask, command, and auth flow. Attach this resource once per Claude Desktop session and the agent has the entire backend's surface area as context before its first tool call.
- **`shape://manifest`** — the same machine-readable manifest you get from `?manifest`, served as a JSON resource.
- **`shape://entities/{table}`** — per-entity drill-down with field-level detail.
- **`shape://tools`** — the tools-list as JSON, useful when the agent needs to inspect tool schemas as data.

No other MCP server today gives the agent the whole backend spec as a click-attachable resource. That's the wedge.

## Quick start (Claude Desktop, local stdio)

Add this block to `~/Library/Application Support/Claude/claude_desktop_config.json` (Mac) or the equivalent on Windows / Linux:

```json
{
  "mcpServers": {
    "myapp": {
      "command": "php",
      "args": [
        "/path/to/myapp/vendor/shape/shape/bin/shape-mcp",
        "/path/to/myapp/.env"
      ]
    }
  }
}
```

Restart Claude Desktop. The Shape backend now appears as a connected MCP server. The tools (`books_list`, `books_get`, `books_mutate`, etc.) and resources (`shape://README.md`, `shape://manifest`, …) are available immediately.

The script boots a Shape app via `ShapeApp::quickStart($envPath)`, so all the same defaults apply: wildcard access, admin masks, soft-delete detection, schema-driven validators.

## Tools

Tools are auto-derived from the manifest by `LlmToolGenerator` — same source of truth as `?manifest&format=anthropic-tools`:

| Mask | MCP tool | Action |
|---|---|---|
| `books.list` | `books_list` | Read |
| `books.get` | `books_get` | Read |
| `books.byAuthorId` | `books_byAuthorId` | Read |
| `books` (writable) | `books_mutate` | `action: insert/update/delete` |
| `users.adminList` | `users_adminList` | Read (admin role only) |
| Custom command `order.process` | `order_process` | Command |

### Auth-aware tool list

The MCP server runs the user's role through Shape's standard role check and **filters `tools/list` to only what the role can actually call**. An agent connected as `user` never sees `users_adminList`; an agent connected as `admin` does.

Default role for stdio is `admin` (the user invoked the script on their own machine). Override via `SHAPE_MCP_ROLE=user` in the environment.

### Self-documenting errors flow through

A tool call that fails returns an MCP `isError: true` content object whose body is Shape's standard error envelope:

```json
{
  "code": "PARAM_VALIDATION_ERROR",
  "message": "Missing required parameter 'id'.",
  "expected": { "id": "int (required)" },
  "example": { "id": 42 }
}
```

The agent corrects the call without re-fetching the manifest. This is the same retry-loop saving the HTTP API gives, applied to MCP.

## Resources

`resources/list` returns:

- `shape://README.md` (text/markdown) — the narrated overview
- `shape://manifest` (application/json) — full machine-readable manifest
- `shape://tools` (application/json) — the tools list as JSON
- `shape://entities/<table>` (application/json) — one per introspected entity

`resources/read` serves the content. The README is regenerated each time it's read, so it always reflects the current schema.

`resources/templates/list` exposes `shape://entities/{table}` as a URI template, so MCP clients that want to drive resource discovery via templates work too.

## Soft-delete & secret tables

The MCP layer respects every Shape convention:

- Reads on a soft-deletable table auto-filter `deleted_at IS NULL`.
- `<table>_mutate` with `action: 'delete'` on a soft-deletable table sets `deleted_at` instead of removing the row.
- Secret tables (`users`, `refresh_tokens`, plus anything in `secret_tables` config) are **never** exposed as auto-CRUD tools; only the explicitly opted-in `*_adminList` / `*_adminGet` admin masks appear, and those are filtered to admin role.

The agent gets the same security posture through MCP as through HTTP. There is no separate "MCP authorization" to maintain.

## CLI usage

The standalone script:

```
php /path/to/shape/bin/shape-mcp /path/to/.env
```

Reads the `.env`, boots Shape via `quickStart()`, then enters a JSON-RPC over stdio loop. One newline-delimited JSON message per stdin line; one response per stdout line.

Environment variables:

| Variable | Effect |
|---|---|
| `SHAPE_MCP_ROLE` | Default role for the MCP session (default `admin`) |

## Hosted backend (HTTP transport)

A Shape backend running over HTTP also serves MCP at `/api.php?mcp` — same domain, same auth, no extra port:

```json
{
  "mcpServers": {
    "myapp": {
      "url": "https://api.myapp.com/api.php?mcp",
      "headers": {
        "Authorization": "Bearer eyJhbGc..."
      }
    }
  }
}
```

The endpoint speaks **streamable-HTTP**: `POST` with a JSON-RPC message returns a JSON-RPC response (or `202 Accepted` with no body for notifications). A `GET` on the same URL returns a small discovery document so probing clients see they've got the right URL.

**Auth flow:**

- If `auth.driver` is `jwt`, the `Authorization: Bearer <token>` header is validated by the same `JwtGuard` the rest of the API uses. The role from the JWT claims drives `tools/list` filtering.
- If `auth.driver` is `none` (the `quickStart()` default), the caller is treated as **admin** — same as the stdio default. The intent of "no auth driver" is "this backend doesn't gate access," so silently 403'ing every MCP tool would be a worse UX than just letting the agent through. Configure `auth.driver => 'jwt'` to gate.
- Bad or missing token under `jwt`: the call passes through with no role; tool calls return `AUTH_FORBIDDEN` through the standard self-documenting envelope. Clients can correct without re-fetching the manifest.

**CORS** is enabled (`*`) for browser-based MCP clients with a 600 s preflight cache.

## Architecture

```
                       Shape\Mcp\McpServer
                  (transport-agnostic protocol)
                            │
                ┌───────────┴───────────┐
                ▼                       ▼
       Shape\Mcp\StdioTransport   Shape\Mcp\HttpTransport
       (Claude Desktop, local)    (hosted backend, streamable-HTTP)
                │                       │
                ▼                       ▼
            stdin/stdout         /api.php?mcp endpoint
```

The `McpServer` class is transport-agnostic: it takes a JSON-RPC message and returns a response. `StdioTransport` wraps it in a stdin/stdout loop for local Claude Desktop integration. `HttpTransport` wraps it behind `/api.php?mcp` for hosted backends, reusing the same `JwtGuard` and the same `Database` / `MaskRegistry` / `BatchExecutor` chain as the rest of the HTTP API.

Tool calls dispatch through `BatchExecutor` and `CommandExecutor` — the exact same code paths the HTTP API uses. There is no parallel "MCP execution layer" to maintain.

## Security notes

- The stdio server has full access to whatever the `.env` DSN points at. Treat it the same as any local dev tool: don't point production credentials at a Claude Desktop session.
- Tool filtering is **role-based, not capability-based**: a tool is hidden if the role can't call its mask, but a malicious client that bypasses `tools/list` and calls `tools/call` directly still hits the same auth check at runtime — the `MaskRegistry::resolve()` returns `AUTH_FORBIDDEN`.
- Secret tables remain hidden through any path. There is no way to call `users_list` (it doesn't exist as a mask).
- Soft-delete is enforced at the SQL level inside generated masks; an agent cannot bypass it through MCP.

## Roadmap

Already shipped (v1):

- Stdio transport, `bin/shape-mcp` CLI
- HTTP transport at `/api.php?mcp` (streamable-HTTP)
- Auth-aware `tools/list` filtering (per-role visibility)
- Self-documenting errors flowing through MCP verbatim
- `shape://README.md` differentiator resource
- `shape://manifest`, `shape://entities/{table}`, `shape://tools` resources
- CORS preflight on the HTTP endpoint
- All Shape conventions respected (soft-delete, secret tables, admin masks, schema-driven validators)

Next:

- SSE streaming for paginated reads
- Tool-call telemetry for measured token-savings data
- Optional read-only mode (`SHAPE_MCP_READONLY=1`)
- Server-initiated notifications (`notifications/tools/list_changed`) when masks change at runtime
