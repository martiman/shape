using System.Text;
using System.Text.Json;
using Shape.Core;
using Shape.Pdf;
using Xunit;

namespace Shape.Tests;

/// <summary>PDF documents — mirror of the PHP Documents.phpt, including the golden file shared with the PHP port.</summary>
public sealed class DocumentsTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"shape-doc-{Guid.NewGuid():N}.db");
    private readonly Database _db;
    private readonly BatchExecutor _executor;
    private readonly DocumentService _svc;
    private const string Created = "20261126120000Z";

    private static readonly Dictionary<string, ScopeConfig> Scopes = new(StringComparer.Ordinal)
    {
        ["owner_id"] = new ScopeConfig { Self = true, UnscopedRoles = new() { "admin" } }
    };

    public DocumentsTests()
    {
        _db = new Database(new DatabaseConfig { Driver = "sqlite", ConnectionString = $"Data Source={_file}" });
        _db.Exec("CREATE TABLE invoices (id INTEGER PRIMARY KEY AUTOINCREMENT, owner_id INTEGER NOT NULL, number TEXT NOT NULL, customer TEXT NOT NULL)", new());
        _db.Exec("CREATE TABLE invoice_items (id INTEGER PRIMARY KEY AUTOINCREMENT, invoice_id INTEGER NOT NULL, owner_id INTEGER NOT NULL, name TEXT NOT NULL, total REAL NOT NULL)", new());
        _db.Exec("INSERT INTO invoices (owner_id, number, customer) VALUES (1, '2026/001', 'Žluťoučký s.r.o.'), (2, '2026/002', 'Other')", new());
        _db.Exec("INSERT INTO invoice_items (invoice_id, owner_id, name, total) VALUES (1, 1, 'Práce', 1000.5), (1, 1, 'Materiál', 249.5), (2, 2, 'Secret', 99), (1, 2, 'Leak', 5)", new());

        MaskConfig Mk(string sql, Dictionary<string, ParamSchema> p) => new()
        {
            Sql = sql + " AND owner_id = @__scope_owner_id", SqlUnscoped = sql, Params = p,
            Roles = new() { "admin", "user" }, RowScopes = new List<string> { "owner_id" }, Description = "x"
        };
        var masks = new Dictionary<string, MaskConfig>
        {
            ["invoices.get"] = Mk("SELECT id, number, customer FROM invoices WHERE id = @id", new() { ["id"] = new ParamSchema { Type = "int", Required = true } }),
            ["invoice_items.byInvoice"] = Mk("SELECT name, total FROM invoice_items WHERE invoice_id = @invoice_id", new() { ["invoice_id"] = new ParamSchema { Type = "int", Required = true } })
        };
        _executor = new BatchExecutor(_db, new MaskRegistry(masks), new ValidatorRegistry(), null, Scopes);
        _svc = new DocumentService(_executor, new()
        {
            ["invoice"] = new()
            {
                ["mask"] = "invoices.get", ["params"] = new List<object?> { "id" }, ["roles"] = new List<object?> { "admin", "user" },
                ["filename"] = "faktura {number}.pdf", ["title"] = "Faktura {number} – {customer}", ["footer"] = "{number} {page}/{pages}",
                ["blocks"] = new List<object?>
                {
                    new Dictionary<string, object?> { ["type"] = "heading", ["text"] = "Faktura {number}" },
                    new Dictionary<string, object?> { ["type"] = "fields", ["items"] = new List<object?> { new Dictionary<string, object?> { ["label"] = "Odběratel", ["value"] = "{customer}" } } },
                    new Dictionary<string, object?>
                    {
                        ["type"] = "table", ["mask"] = "invoice_items.byInvoice",
                        ["params"] = new Dictionary<string, object?> { ["invoice_id"] = "{id}" },
                        ["columns"] = new List<object?>
                        {
                            new Dictionary<string, object?> { ["field"] = "name", ["label"] = "Položka", ["width"] = 3 },
                            new Dictionary<string, object?> { ["field"] = "total", ["label"] = "Cena", ["format"] = "number:2", ["sum"] = true }
                        }
                    }
                }
            }
        });
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_file); } catch { }
    }

    // ── helpers ──────────────────────────────────────────────────────

    private static string GoldenDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var candidate = Path.Combine(d.FullName, "php", "tests", "golden");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("php/tests/golden not found above the test binaries (run from a repo checkout).");
    }

    private static object? Native(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Native(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => e.EnumerateArray().Select(Native).ToList(),
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) && !e.GetRawText().Contains('.') && !e.GetRawText().Contains('e') && !e.GetRawText().Contains('E') ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    private static void AssertValidPdf(byte[] bytes)
    {
        var s = Encoding.Latin1.GetString(bytes);
        Assert.StartsWith("%PDF-1.4", s);
        Assert.EndsWith("%%EOF\n", s);
        var m = System.Text.RegularExpressions.Regex.Match(s, @"startxref\n(\d+)\n%%EOF");
        Assert.True(m.Success);
        var xref = int.Parse(m.Groups[1].Value);
        Assert.Equal("xref", s.Substring(xref, 4));
        var c = System.Text.RegularExpressions.Regex.Match(s[xref..], @"^xref\n0 (\d+)\n");
        var n = int.Parse(c.Groups[1].Value);
        var table = s[(xref + c.Length)..];
        for (var i = 1; i < n; i++)
        {
            var offset = int.Parse(table.Substring(i * 20, 10));
            Assert.Equal($"{i} 0 obj", s.Substring(offset, $"{i} 0 obj".Length));
        }
    }

    private static string CodeOf(Action a)
    {
        try { a(); } catch (ApiException e) { return e.ApiCode; }
        return "(none)";
    }

    private static Dictionary<string, object?> Ctx(string role, int? userId) =>
        new() { ["id"] = userId ?? 9, ["role"] = role, ["owner_id"] = userId };

    private static IReadOnlyDictionary<string, string?> Q(string? id) => id == null ? new Dictionary<string, string?>() : new Dictionary<string, string?> { ["id"] = id };

    private static string Hex(TrueTypeFont font, string s)
    {
        var sb = new StringBuilder();
        foreach (var r in s.EnumerateRunes()) sb.Append(font.Glyph(r.Value).ToString("X4"));
        return sb.ToString();
    }

    // ── golden file shared with PHP ──────────────────────────────────

    [Fact]
    public void Golden_pdf_is_byte_identical_to_the_one_the_PHP_port_produced()
    {
        var dir = GoldenDir();
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "document-sample.json")));
        var root = doc.RootElement;
        var spec = (Dictionary<string, object?>)Native(root.GetProperty("spec"))!;
        var data = (Dictionary<string, object?>)Native(root.GetProperty("data"))!;
        var tables = new Dictionary<int, List<Dictionary<string, object?>>>();
        foreach (var p in root.GetProperty("tableRows").EnumerateObject())
            tables[int.Parse(p.Name)] = ((List<object?>)Native(p.Value)!).Cast<Dictionary<string, object?>>().ToList();
        var created = root.GetProperty("created").GetString()!;

        var golden = File.ReadAllBytes(Path.Combine(dir, "document-sample.pdf"));
        var plain = new DocumentRenderer().Render(spec, data, tables, created, compress: false);
        Assert.True(golden.AsSpan().SequenceEqual(plain), "PDF differs from php/tests/golden/document-sample.pdf");
        AssertValidPdf(plain);
        Assert.Contains("/Count 2", Encoding.Latin1.GetString(plain));

        var packed = new DocumentRenderer().Render(spec, data, tables, created, compress: true);
        AssertValidPdf(packed);
        Assert.True(packed.AsSpan().SequenceEqual(new DocumentRenderer().Render(spec, data, tables, created, compress: true)));
        Assert.True(packed.Length < plain.Length / 2);
    }

    // ── formats ──────────────────────────────────────────────────────

    [Fact]
    public void Placeholders_and_formats_follow_the_locale()
    {
        var r = new DocumentRenderer();
        var d = new Dictionary<string, object?> { ["total"] = 1234567.891, ["neg"] = -0.004, ["day"] = "2026-11-26 14:05:00", ["ok"] = true, ["none"] = null };
        Assert.Equal("1 234 567,89", r.Fill("{total|number:2}", d));
        Assert.Equal("1 234 568", r.Fill("{total|int}", d));
        Assert.Equal("1 234 568", r.Fill("{total|number:0}", d));
        Assert.Equal("0,00", r.Fill("{neg|number:2}", d));
        Assert.Equal("26. 11. 2026", r.Fill("{day|date}", d));
        Assert.Equal("26. 11. 2026 14:05", r.Fill("{day|datetime}", d));
        Assert.Equal("ano||", r.Fill("{ok}|{none}|{missing}", d));

        var en = new DocumentRenderer();
        en.Render(new() { ["locale"] = "en", ["blocks"] = new List<object?>() }, new(), new(), "20260101000000Z");
        Assert.Equal("1,234,567.89", en.Fill("{total|number:2}", d));
        Assert.Equal("2026-11-26", en.Fill("{day|date}", d));
    }

    [Fact]
    public void Layout_errors_are_explicit()
    {
        Assert.Throws<ArgumentException>(() => new DocumentRenderer().Render(
            new() { ["blocks"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "nope" } } }, new(), new(), Created));
        Assert.Throws<ArgumentException>(() => new DocumentRenderer().Render(
            new() { ["blocks"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "table" } } }, new(), new(), Created));
    }

    // ── service: data from masks in the caller's context ─────────────

    [Fact]
    public void Document_shows_only_what_the_caller_may_see()
    {
        var u1 = Ctx("user", 1);
        var u2 = Ctx("user", 2);
        var admin = Ctx("admin", null);

        var o = _svc.Render("invoice", Q("1"), u1, Created);
        AssertValidPdf(o.Pdf);
        Assert.Equal("faktura_2026_001.pdf", o.Filename);                      // placeholder filled, unsafe characters replaced
        Assert.Contains("/Title <FEFF", Encoding.Latin1.GetString(o.Pdf));      // non-ASCII title is UTF-16 encoded

        var font = TrueTypeFont.Bundled("DejaVuSans-Subset.ttf");
        var plain = Encoding.Latin1.GetString(_svc.Render("invoice", Q("1"), u1, Created, compress: false).Pdf);
        Assert.Contains(Hex(font, "Práce"), plain);                             // own item is printed ...
        Assert.DoesNotContain(Hex(font, "Leak"), plain);                        // ... a row of another owner on the SAME invoice is not
        Assert.Contains(Hex(font, "Leak"), Encoding.Latin1.GetString(_svc.Render("invoice", Q("1"), admin, Created, compress: false).Pdf));
        Assert.Contains(Hex(font, "Secret"), Encoding.Latin1.GetString(_svc.Render("invoice", Q("2"), admin, Created, compress: false).Pdf));

        Assert.Equal("NOT_FOUND", CodeOf(() => _svc.Render("invoice", Q("2"), u1)));   // someone else's invoice == missing
        Assert.Equal("NOT_FOUND", CodeOf(() => _svc.Render("invoice", Q("999"), u1)));
        Assert.Equal("NOT_FOUND", CodeOf(() => _svc.Render("nope", Q("1"), u1)));
        Assert.Equal("DOCUMENT_VALIDATION_ERROR", CodeOf(() => _svc.Render("invoice", Q(null), u1)));
        Assert.Equal("AUTH_FORBIDDEN", CodeOf(() => _svc.Render("invoice", Q("1"), new())));
        Assert.Equal("NOT_FOUND", CodeOf(() => _svc.Render("invoice", Q("1"), u2)));
    }

    [Fact]
    public void Huge_tables_are_refused_and_bad_config_is_a_config_error()
    {
        _db.Exec($"WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c WHERE x < {DocumentService.MaxTableRows + 1}) " +
                 "INSERT INTO invoice_items (invoice_id, owner_id, name, total) SELECT 1, 1, 'x' || x, 1 FROM c", new());
        Assert.Equal("DOCUMENT_TOO_LARGE", CodeOf(() => _svc.Render("invoice", Q("1"), Ctx("user", 1))));

        var bad = new DocumentService(_executor, new()
        {
            ["x"] = new() { ["mask"] = "invoices.get", ["params"] = new List<object?> { "id" },
                ["blocks"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "nope" } } }
        });
        Assert.Equal("DOCUMENT_CONFIG_ERROR", CodeOf(() => bad.Render("x", Q("1"), Ctx("user", 1))));
    }
}
