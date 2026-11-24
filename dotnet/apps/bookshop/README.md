# Bookshop — Shape example app

Entity-first showcase running on SQLite. No external services needed: schema, sample users, and a starter row are seeded into `bookshop.db` on first boot.

## Run

```bash
dotnet run --project apps/bookshop
# server listens on http://localhost:5060
```

## What's wired

| Feature | Where |
|---|---|
| Three entities (`authors`, `books`, `reviews`) with FK relations | `Entities/*.cs` |
| Soft-delete on `books` (column `deleted_at`) | auto-detected |
| Audit columns (`created_at`, `updated_at`, `created_by`, `updated_by`) | auto-detected on `books`, `reviews` |
| Audit log writer — every mutation appends to `audit_log` | auto-wired (`audit_log` table detected) |
| JWT auth with `user` / `admin` roles | `AuthConfig.Driver = "jwt"` |
| Custom mask: `reviews.topForBook` (joined `reviews + books`) | `Masks` dictionary in `Program.cs` |
| Custom command: `book.publish` | `Commands` dictionary in `Program.cs` |
| Upload endpoint at `/upload/bookcovers` (admin only, ≤ 2 MB, PNG/JPEG/WebP) | `Uploads` dictionary in `Program.cs` |
| MCP HTTP transport at `/?mcp` | wired by `MapShape` |
| `?manifest`, `?openapi`, OData GET, generic `/_/<mask>?...` | wired by `MapShape` |

## Sample credentials

| Email | Password | Role |
|---|---|---|
| `admin@bookshop.local` | `admin12345` | `admin` |
| `user@bookshop.local` | `user12345` | `user` |

## Curl recipes

### 0. See what the backend exposes

```bash
curl -s http://localhost:5060/?manifest | jq
curl -s http://localhost:5060/?openapi | jq '.paths | keys'
```

### 1. Login and capture the token

```bash
TOKEN=$(curl -s -X POST http://localhost:5060/ \
  -H 'Content-Type: application/json' \
  -d '{"op":"auth.token","payload":{"email":"admin@bookshop.local","password":"admin12345"}}' \
  | jq -r .access_token)
echo $TOKEN
```

### 2. Read books (REST/OData GET)

```bash
curl -sH "Authorization: Bearer $TOKEN" \
  'http://localhost:5060/books?$top=20&$orderby=title%20asc' | jq
```

### 3. Read a single book by id

```bash
curl -sH "Authorization: Bearer $TOKEN" http://localhost:5060/books/1 | jq
```

### 4. Insert a new author + book in one transaction

```bash
curl -s -X POST http://localhost:5060/ \
  -H 'Content-Type: application/json' \
  -H "Authorization: Bearer $TOKEN" \
  -d '{
    "op":"mutation",
    "payload":{
      "transaction": true,
      "mutations":[
        {"ref":"a","table":"authors","action":"insert",
         "data":{"name":"Octavia Butler"}},
        {"ref":"b","table":"books","action":"insert",
         "data":{"author_id":{"ref":"a","value":"id"},"title":"Kindred","price":14.00}}
      ]
    }
  }' | jq
```

Note `{"ref":"a","value":"id"}` — Shape resolves the new author's id from the
previous mutation's result. The audit columns (`created_at`, `created_by`)
are stamped automatically.

### 5. Run the custom mask

```bash
curl -s -X POST http://localhost:5060/ \
  -H 'Content-Type: application/json' \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"op":"query","payload":{"queries":[
    {"ref":"top","sql":"reviews.topForBook","params":{"book_id":1}}
  ]}}' | jq
```

Or as a GET via the generic-mask path:

```bash
curl -sH "Authorization: Bearer $TOKEN" 'http://localhost:5060/_/reviews.topForBook?book_id=1' | jq
```

### 6. Soft-delete a book (DELETE → UPDATE)

```bash
curl -s -X POST http://localhost:5060/ \
  -H 'Content-Type: application/json' \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"op":"mutation","payload":{"mutations":[
    {"table":"books","action":"delete","filter":{"id":2}}
  ]}}' | jq
```

The row stays in the table; `deleted_at` is set to `CURRENT_TIMESTAMP`.
Subsequent reads filter it out automatically. To restore:

```bash
curl -s -X POST http://localhost:5060/ \
  -H 'Content-Type: application/json' \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"op":"mutation","payload":{"mutations":[
    {"table":"books","action":"update","filter":{"id":2},"data":{"deleted_at":null}}
  ]}}' | jq
```

### 7. Run the custom command

```bash
curl -s -X POST http://localhost:5060/ \
  -H 'Content-Type: application/json' \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"op":"command","payload":{"name":"book.publish","params":{"book_id":1}}}' | jq
```

### 8. Upload a book cover (admin only)

```bash
curl -s -X POST http://localhost:5060/upload/bookcovers \
  -H "Authorization: Bearer $TOKEN" \
  -F file=@cover.png | jq
# →  { "id": "abc123ef.png", "filename": "cover.png", "size": 102400,
#      "mime_type": "image/png", "url": "/uploads/covers/abc123ef.png" }
```

Then attach the URL to the book:

```bash
curl -s -X POST http://localhost:5060/ \
  -H 'Content-Type: application/json' \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"op":"mutation","payload":{"mutations":[
    {"table":"books","action":"update","filter":{"id":1},
     "data":{"cover_url":"/uploads/covers/abc123ef.png"}}
  ]}}' | jq
```

### 9. Self-documenting errors

Try posting a bad mutation. The response tells you exactly what was wrong
and what a corrected request looks like:

```bash
curl -s -X POST http://localhost:5060/ \
  -H 'Content-Type: application/json' \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"op":"mutation","payload":{"mutations":[
    {"table":"books","action":"insert","data":{"title":""}}
  ]}}' | jq
# →  {
#      "error": {
#        "code": "VALIDATION_ERROR",
#        "message": "title is required",
#        ...
#      }
#    }
```

### 10. MCP from the command line

```bash
# Capability hint
curl -sH "Authorization: Bearer $TOKEN" 'http://localhost:5060/?mcp' | jq

# tools/list (role-filtered — admin sees more than user)
curl -s -X POST 'http://localhost:5060/?mcp' \
  -H 'Content-Type: application/json' \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | jq

# resources/read shape://README.md  (the narrated backend overview)
curl -s -X POST 'http://localhost:5060/?mcp' \
  -H 'Content-Type: application/json' \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"jsonrpc":"2.0","id":2,"method":"resources/read","params":{"uri":"shape://README.md"}}' \
  | jq -r '.result.contents[0].text'
```

## Wiring this up to Claude Desktop / Cursor

After `dotnet tool install --global Shape.Mcp.Cli`, point your MCP client at
`shape-mcp` with the bookshop database:

```json
{
  "mcpServers": {
    "bookshop": {
      "command": "shape-mcp",
      "env": {
        "SHAPE__Db__Driver": "sqlite",
        "SHAPE__Db__ConnectionString": "Data Source=/absolute/path/to/bookshop.db",
        "SHAPE__ServiceName": "bookshop",
        "SHAPE_MCP_ROLE": "admin"
      }
    }
  }
}
```

The agent then sees every entity, every mask, every command, plus the
narrated `shape://README.md` describing exactly what's in the backend.

## Reset

```bash
rm apps/bookshop/bin/Debug/net8.0/bookshop.db
# next run re-seeds the schema and sample data
```
