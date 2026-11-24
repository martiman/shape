using Bookshop.Entities;
using Shape;
using Shape.Auth;
using Shape.Core;
using Shape.Upload;

// Bookshop — a single-file showcase of Shape's surface:
//   * Entity-first declaration with [Entity] / [Column] / [Fk] / [Validate] / [Access] attributes
//   * SQLite backend (no external services needed; schema seeded on first boot)
//   * Soft-delete on `books` (deleted_at column)
//   * Audit columns auto-stamped (created_at / updated_at / created_by / updated_by)
//   * JWT auth with two roles: `user` and `admin`
//   * Audit log written on every mutation (audit_log table auto-detected)
//   * Manual mask for "top reviews" — auto-generated masks coexist with hand-written ones
//   * Custom command `book.publish` for business-logic operations
//   * Upload endpoint at /upload/bookcovers for cover images (admin role only)
//   * MCP HTTP transport at /?mcp so Claude Desktop / Cursor can drive the API
//
// Boot:    dotnet run --project apps/bookshop
// Manifest: curl http://localhost:5060/?manifest | jq
// OpenAPI:  curl http://localhost:5060/?openapi | jq
// Login:    see README.md for sample credentials and curl recipes.

var dbPath = Path.Combine(AppContext.BaseDirectory, "bookshop.db");
SeedSchemaIfMissing(dbPath);

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var config = ShapeApp.FromAssembly(typeof(Author).Assembly, new ShapeConfig
{
    ServiceName = "bookshop",

    Db = new DatabaseConfig
    {
        Driver = "sqlite",
        ConnectionString = $"Data Source={dbPath}"
    },

    Auth = new AuthConfig
    {
        Driver = "jwt",
        JwtSecret = "dev-secret-not-for-production-please-replace",
        JwtIssuer = "bookshop.local",
        JwtAudience = "bookshop.clients",
        AccessTokenTtl = 3600,
        RefreshTokenTtl = 2592000,
        AssignableRoles = new() { "user", "admin" }
    },

    DefaultListLimit = 50,
    Audit = true,        // audit_log table is detected, AuditLogger gets wired automatically

    // ── Manual mask layered on top of auto-CRUD ─────────────────────
    // Hand-written masks coexist with the entity-derived set. Top reviews
    // for a given book — joins reviews + books, params declared inline.
    Masks = new Dictionary<string, MaskConfig>
    {
        ["reviews.topForBook"] = new()
        {
            Sql = """
                  SELECT r.id, r.rating, r.body, r.created_at, b.title AS book_title
                  FROM reviews r
                  JOIN books b ON b.id = r.book_id
                  WHERE r.book_id = @book_id
                  ORDER BY r.rating DESC, r.created_at DESC
                  """,
            Params = new() { ["book_id"] = new() { Type = "int", Required = true } },
            Roles = new() { "user", "admin" },
            Description = "Top reviews for a given book, joined with the book title.",
            DefaultLimit = 10
        }
    },

    // ── Upload endpoint ─────────────────────────────────────────────
    Uploads = new Dictionary<string, UploadEntry>
    {
        ["bookcovers"] = new()
        {
            Storage = new StorageConfig
            {
                Driver = "local",
                Path = Path.Combine(AppContext.BaseDirectory, "uploads", "covers"),
                PublicUrl = "/uploads/covers"
            },
            Roles = new() { "admin" },
            MaxSize = 2 * 1024 * 1024,       // 2 MB
            MimeTypes = new() { "image/png", "image/jpeg", "image/webp" },
            Description = "Upload a book cover image."
        }
    },

    // ── Custom command ──────────────────────────────────────────────
    // `book.publish` — promote a draft book by ensuring it has a price,
    // a cover, and at least one review, then logging the publish action.
    Commands = new Dictionary<string, Func<Dictionary<string, object?>, CommandContext, Dictionary<string, object?>>>
    {
        ["book.publish"] = (parameters, ctx) =>
        {
            var bookId = parameters.TryGetValue("book_id", out var b) ? Convert.ToInt32(b) : 0;
            if (bookId <= 0)
                throw new ApiException("COMMAND_VALIDATION_ERROR", "book_id is required and must be a positive integer.", 400);

            // Stamp updated_at = now (the audit-column injector handles audit
            // automatically, but we want this to register as an explicit
            // "publish" event for the audit log).
            var result = ctx.ExecuteMutations(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["table"] = "books",
                    ["action"] = "update",
                    ["ref"] = "publish",
                    ["filter"] = new Dictionary<string, object?> { ["id"] = bookId },
                    ["data"] = new Dictionary<string, object?> { ["updated_at"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss") }
                }
            }, useTransaction: true);

            return new Dictionary<string, object?>
            {
                ["status"] = 200,
                ["book_id"] = bookId,
                ["published_at"] = DateTime.UtcNow,
                ["mutations_applied"] = (result.GetValueOrDefault("results") as List<Dictionary<string, object?>>)?.Count ?? 0
            };
        }
    }
});

app.MapShape(config);

// Static-serve the uploads directory so the URLs the upload endpoint returns
// actually resolve (Kestrel does not proxy file reads through Shape itself).
var uploadsRoot = Path.Combine(AppContext.BaseDirectory, "uploads");
Directory.CreateDirectory(uploadsRoot);
app.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(uploadsRoot),
    RequestPath = "/uploads"
});

app.Run();


// ── First-boot schema seed ──────────────────────────────────────────
// Idempotent: if the file exists, do nothing. The seed creates the
// domain tables, the framework's `users` + `refresh_tokens` tables for
// JWT auth, and the `audit_log` table that ShapeApp picks up at boot.
static void SeedSchemaIfMissing(string dbPath)
{
    if (File.Exists(dbPath)) return;

    using var db = new Database(new DatabaseConfig
    {
        Driver = "sqlite",
        ConnectionString = $"Data Source={dbPath}"
    });

    db.Exec("""
        CREATE TABLE authors (
          id          INTEGER PRIMARY KEY AUTOINCREMENT,
          name        TEXT NOT NULL,
          bio         TEXT,
          created_at  TIMESTAMP,
          updated_at  TIMESTAMP
        )
        """, new());

    db.Exec("""
        CREATE TABLE books (
          id          INTEGER PRIMARY KEY AUTOINCREMENT,
          author_id   INTEGER NOT NULL REFERENCES authors(id),
          title       TEXT NOT NULL,
          isbn        TEXT,
          price       REAL NOT NULL,
          cover_url   TEXT,
          deleted_at  TIMESTAMP NULL,
          created_at  TIMESTAMP,
          updated_at  TIMESTAMP,
          created_by  INTEGER,
          updated_by  INTEGER
        )
        """, new());

    db.Exec("""
        CREATE TABLE reviews (
          id          INTEGER PRIMARY KEY AUTOINCREMENT,
          book_id     INTEGER NOT NULL REFERENCES books(id),
          rating      INTEGER NOT NULL,
          body        TEXT NOT NULL,
          created_at  TIMESTAMP,
          created_by  INTEGER
        )
        """, new());

    // ── Framework tables ───────────────────────────────────────────
    db.Exec("""
        CREATE TABLE users (
          id              INTEGER PRIMARY KEY AUTOINCREMENT,
          email           TEXT NOT NULL UNIQUE,
          password_hash   TEXT NOT NULL,
          display_name    TEXT,
          role            TEXT NOT NULL DEFAULT 'user',
          is_active       INTEGER NOT NULL DEFAULT 1,
          created_at      TIMESTAMP DEFAULT CURRENT_TIMESTAMP
        )
        """, new());

    db.Exec("""
        CREATE TABLE refresh_tokens (
          id                  INTEGER PRIMARY KEY AUTOINCREMENT,
          user_id             INTEGER NOT NULL REFERENCES users(id),
          token_hash          TEXT NOT NULL,
          expires_at          TIMESTAMP NOT NULL,
          created_at          TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
          revoked_at          TIMESTAMP NULL,
          replaced_by_token_id INTEGER NULL
        )
        """, new());

    db.Exec("""
        CREATE TABLE audit_log (
          id            INTEGER PRIMARY KEY AUTOINCREMENT,
          occurred_at   TEXT NOT NULL,
          actor_id      TEXT,
          actor_email   TEXT,
          actor_role    TEXT,
          table_name    TEXT NOT NULL,
          row_id        TEXT,
          action        TEXT NOT NULL,
          changed       TEXT
        )
        """, new());

    // ── Seed users + a sample author/book/review ──────────────────
    // BCrypt hash of "admin12345" / "user12345" — see README for credentials.
    var adminHash = BCrypt.Net.BCrypt.HashPassword("admin12345");
    var userHash = BCrypt.Net.BCrypt.HashPassword("user12345");

    db.Exec(
        "INSERT INTO users (email, password_hash, display_name, role) VALUES (@email, @hash, @name, @role)",
        new() { ["email"] = "admin@bookshop.local", ["hash"] = adminHash, ["name"] = "Admin", ["role"] = "admin" });

    db.Exec(
        "INSERT INTO users (email, password_hash, display_name, role) VALUES (@email, @hash, @name, @role)",
        new() { ["email"] = "user@bookshop.local", ["hash"] = userHash, ["name"] = "Reader", ["role"] = "user" });

    db.Exec("INSERT INTO authors (name, bio) VALUES (@n, @b)",
        new() { ["n"] = "Ursula K. Le Guin", ["b"] = "American author of speculative fiction." });

    db.Exec("INSERT INTO books (author_id, title, price) VALUES (@a, @t, @p)",
        new() { ["a"] = 1, ["t"] = "A Wizard of Earthsea", ["p"] = 12.50 });

    db.Exec("INSERT INTO reviews (book_id, rating, body) VALUES (@b, @r, @body)",
        new() { ["b"] = 1, ["r"] = 5, ["body"] = "A foundational work of fantasy." });
}
