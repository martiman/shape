using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Shape;
using Shape.Attributes;
using Shape.Auth;
using Shape.Core;
using Shape.Pdf;
using Shape.Upload;
using Xunit;

namespace Shape.Tests;

[Entity("cases", Writable = true)]
[Access(Roles = new[] { "user", "admin" })]
public sealed class AttCase
{
    [Id][AutoIncrement] public int Id { get; set; }
    public int OwnerId { get; set; }
    [Column(Length = 50)] public string Title { get; set; } = "";
}

[Entity("case_files", Writable = true)]
[Access(Roles = new[] { "user", "admin" })]
public sealed class AttCaseFile
{
    [Id][AutoIncrement] public int Id { get; set; }
    public int CaseId { get; set; }
    public int OwnerId { get; set; }
    [Column(Length = 64)] public string FileKey { get; set; } = "";
    [Column(Length = 255)] public string Filename { get; set; } = "";
    [Column(Length = 100)] public string MimeType { get; set; } = "";
    public long SizeBytes { get; set; }
    [Column(Length = 64)] public string Sha256 { get; set; } = "";
}

/// <summary>Private attachments — mirror of the PHP Attachments.phpt.</summary>
public sealed class AttachmentTests : IDisposable
{
    private static readonly Dictionary<string, ScopeConfig> Scopes = new(StringComparer.Ordinal)
    {
        ["owner_id"] = new ScopeConfig { Self = true, UnscopedRoles = new() { "admin" } }
    };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"shape-att-{Guid.NewGuid():N}");
    private readonly string _dbFile = Path.Combine(Path.GetTempPath(), $"shape-att-{Guid.NewGuid():N}.db");
    private readonly Database _db;
    private readonly AttachmentService _svc;
    private readonly DocumentService _docs;

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj<<>>endobj\ntrailer<<>>\n%%EOF\n");

    public AttachmentTests()
    {
        Directory.CreateDirectory(_dir);
        var dbConfig = new DatabaseConfig { Driver = "sqlite", ConnectionString = $"Data Source={_dbFile}" };
        _db = new Database(dbConfig);
        _db.Exec("CREATE TABLE cases (id INTEGER PRIMARY KEY AUTOINCREMENT, owner_id INTEGER NOT NULL, title TEXT NOT NULL)", new());
        _db.Exec("CREATE TABLE case_files (id INTEGER PRIMARY KEY AUTOINCREMENT, case_id INTEGER NOT NULL, owner_id INTEGER NOT NULL, " +
                 "file_key TEXT NOT NULL, filename TEXT NOT NULL, mime_type TEXT NOT NULL, size_bytes INTEGER NOT NULL, sha256 TEXT NOT NULL)", new());
        _db.Exec("INSERT INTO cases (owner_id, title) VALUES (1, 'mine'), (2, 'theirs')", new());

        var config = ShapeApp.FromTypes(new[] { typeof(AttCase), typeof(AttCaseFile) }, new ShapeConfig
        {
            Db = dbConfig,
            Scopes = Scopes,
            Access = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["cases"] = new() { "user", "admin" },
                ["case_files"] = new() { "user", "admin" }
            }
        });
        var executor = new BatchExecutor(_db, new MaskRegistry(config.Masks), new ValidatorRegistry(), null, Scopes);
        var uploads = new UploadManager(new()
        {
            ["case_files"] = new UploadEntry
            {
                Storage = new() { Driver = "local", Path = _dir },
                Roles = new() { "user", "admin" },
                MaxSize = 1024 * 1024,
                MimeTypes = new() { "application/pdf" },
                Attachments = new AttachmentsConfig
                {
                    Table = "case_files",
                    Fields = new() { "case_id" },
                    Parents = new() { ["case_id"] = "cases" }
                }
            }
        });
        _svc = new AttachmentService(uploads, executor);
        _docs = new DocumentService(executor, new()
        {
            ["case_sheet"] = new()
            {
                ["mask"] = "cases.get", ["params"] = new List<object?> { "id" }, ["filename"] = "case-{id}.pdf",
                ["blocks"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "heading", ["text"] = "Case {title}" } },
                ["archive"] = new Dictionary<string, object?>
                {
                    ["upload"] = "case_files",
                    ["fields"] = new Dictionary<string, object?> { ["case_id"] = "{id}" }
                }
            },
            ["plain_sheet"] = new()
            {
                ["mask"] = "cases.get", ["params"] = new List<object?> { "id" },
                ["blocks"] = new List<object?> { new Dictionary<string, object?> { ["type"] = "heading", ["text"] = "x" } }
            }
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        try { File.Delete(_dbFile); } catch { }
    }

    private static Dictionary<string, object?> Ctx(string role, int? userId) =>
        new() { ["id"] = userId ?? 9, ["role"] = role, ["user_id"] = userId, ["owner_id"] = userId };

    private static FormFileCollection Files(string name, byte[] bytes)
    {
        var col = new FormFileCollection();
        col.Add(new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = "application/pdf"
        });
        return col;
    }

    private static Dictionary<string, string> Form(string? caseId) =>
        caseId == null ? new() : new() { ["case_id"] = caseId };

    private int StoredCount() => Directory.GetFiles(_dir, "*.pdf").Length;

    private static string Code(Action a)
    {
        try { a(); } catch (ApiException e) { return e.ApiCode; }
        return "(none)";
    }

    private static async Task<string> CodeAsync(Func<Task> a)
    {
        try { await a(); } catch (ApiException e) { return e.ApiCode; }
        return "(none)";
    }

    [Fact]
    public async Task Upload_creates_row_without_url_and_owner_reads_bytes_back()
    {
        var r = await _svc.UploadAsync("case_files", Files("../../etc/passwd\".pdf", Pdf), Form("1"), Ctx("user", 1));
        Assert.NotNull(r["id"]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Pdf)).ToLowerInvariant(), r["sha256"]);
        Assert.Equal("application/pdf", r["mime_type"]);
        Assert.False(r.ContainsKey("url"));
        var fn = (string)r["filename"]!;
        Assert.DoesNotContain('/', fn);
        Assert.DoesNotContain('"', fn);
        Assert.Equal(1, StoredCount());

        var id = Convert.ToInt64(r["id"]);
        using (var o = _svc.Open("case_files", id, Ctx("user", 1)).Stream)
        {
            using var ms = new MemoryStream();
            o.CopyTo(ms);
            Assert.Equal(Pdf, ms.ToArray());
        }
        _svc.Open("case_files", id, Ctx("admin", null)).Stream.Dispose(); // admin sees everything
    }

    [Fact]
    public async Task Other_user_gets_NOT_FOUND_for_open_and_delete_and_missing_is_the_same()
    {
        var id = Convert.ToInt64((await _svc.UploadAsync("case_files", Files("a.pdf", Pdf), Form("1"), Ctx("user", 1)))["id"]);
        Assert.Equal("NOT_FOUND", Code(() => _svc.Open("case_files", id, Ctx("user", 2))));
        Assert.Equal("NOT_FOUND", Code(() => _svc.Delete("case_files", id, Ctx("user", 2))));
        Assert.Equal("NOT_FOUND", Code(() => _svc.Open("case_files", 9999L, Ctx("user", 1))));
    }

    [Fact]
    public async Task Upload_to_someone_elses_case_is_refused_before_anything_is_stored()
    {
        Assert.Equal("NOT_FOUND", await CodeAsync(() => _svc.UploadAsync("case_files", Files("x.pdf", Pdf), Form("2"), Ctx("user", 1))));
        Assert.Equal(0, StoredCount());
    }

    [Fact]
    public async Task Missing_parent_wrong_mime_and_oversize_are_rejected_with_nothing_left_on_disk()
    {
        Assert.Equal("UPLOAD_VALIDATION_ERROR", await CodeAsync(() => _svc.UploadAsync("case_files", Files("x.pdf", Pdf), Form(null), Ctx("user", 1))));
        Assert.Equal("UPLOAD_VALIDATION_ERROR", await CodeAsync(() => _svc.UploadAsync("case_files", Files("x.pdf", Encoding.ASCII.GetBytes("just text\n")), Form("1"), Ctx("user", 1))));
        var big = new byte[Pdf.Length + 1024 * 1024];
        Pdf.CopyTo(big, 0);
        Assert.Equal("UPLOAD_VALIDATION_ERROR", await CodeAsync(() => _svc.UploadAsync("case_files", Files("big.pdf", big), Form("1"), Ctx("user", 1))));
        Assert.Equal(0, StoredCount());
    }

    [Fact]
    public async Task Anonymous_caller_is_rejected_by_endpoint_roles()
    {
        var id = Convert.ToInt64((await _svc.UploadAsync("case_files", Files("a.pdf", Pdf), Form("1"), Ctx("user", 1)))["id"]);
        Assert.Equal("AUTH_FORBIDDEN", Code(() => _svc.Open("case_files", id, new())));
    }

    [Fact]
    public async Task Delete_removes_row_and_file()
    {
        var id = Convert.ToInt64((await _svc.UploadAsync("case_files", Files("a.pdf", Pdf), Form("1"), Ctx("user", 1)))["id"]);
        _svc.Delete("case_files", id, Ctx("user", 1));
        Assert.Equal("NOT_FOUND", Code(() => _svc.Open("case_files", id, Ctx("user", 1))));
        Assert.Equal(0, StoredCount());
    }

    [Fact]
    public void Entry_without_attachments_is_not_an_attachment_entry()
    {
        var plain = new AttachmentService(new UploadManager(new() { ["covers"] = new UploadEntry { Storage = new() { Path = _dir } } }),
            new BatchExecutor(_db, new MaskRegistry(new()), new ValidatorRegistry()));
        Assert.False(plain.IsAttachmentEntry("covers"));
    }

    [Fact]
    public async Task Archive_stores_the_generated_pdf_as_attachment()
    {
        var q = new Dictionary<string, string?> { ["id"] = "1" };
        var a = await _docs.ArchiveAsync("case_sheet", q, Ctx("user", 1), _svc, "20261126120000Z");
        Assert.Equal("case-1.pdf", a["filename"]);
        Assert.Equal("application/pdf", a["mime_type"]);
        Assert.Equal(1L, Convert.ToInt64(a["case_id"]));
        Assert.Equal(1, StoredCount());

        var id = Convert.ToInt64(a["id"]);
        var opened = _svc.Open("case_files", id, Ctx("user", 1));
        using var ms = new MemoryStream();
        await using (var st = opened.Stream) await st.CopyToAsync(ms);
        var bytes = ms.ToArray();
        Assert.Equal("%PDF-1.4", Encoding.ASCII.GetString(bytes, 0, 8));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), a["sha256"]);
        Assert.Equal((long)bytes.Length, Convert.ToInt64(a["size"]));

        // the archived copy follows the case's scope
        Assert.Equal("NOT_FOUND", Code(() => _svc.Open("case_files", id, Ctx("user", 2))));

        // every call stores a NEW copy
        var a2 = await _docs.ArchiveAsync("case_sheet", q, Ctx("user", 1), _svc, "20261126130000Z");
        Assert.NotEqual(a["id"], a2["id"]);
        Assert.Equal(2, StoredCount());
    }

    [Fact]
    public async Task Archive_refuses_what_the_caller_cannot_see_and_stores_nothing()
    {
        var q = new Dictionary<string, string?> { ["id"] = "2" };   // someone else's case
        Assert.Equal("NOT_FOUND", await CodeAsync(() => _docs.ArchiveAsync("case_sheet", q, Ctx("user", 1), _svc)));
        Assert.Equal("NOT_FOUND", await CodeAsync(() => _docs.ArchiveAsync("plain_sheet", new Dictionary<string, string?> { ["id"] = "1" }, Ctx("user", 1), _svc)));
        Assert.Equal("NOT_FOUND", await CodeAsync(() => _docs.ArchiveAsync("nope", q, Ctx("user", 1), _svc)));
        Assert.NotEqual("(none)", await CodeAsync(() => _docs.ArchiveAsync("case_sheet", new Dictionary<string, string?> { ["id"] = "1" }, new(), _svc)));
        Assert.Equal(0, StoredCount());
    }
}
