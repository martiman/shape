using System.Text;
using Microsoft.AspNetCore.Http;
using Shape.Core;
using Shape.Upload;
using Xunit;

namespace Shape.Tests;

public sealed class UploadManagerTests
{
    private static FormFile MakeFile(string name, byte[] bytes, string contentType)
    {
        var stream = new MemoryStream(bytes);
        return new FormFile(stream, 0, bytes.Length, name: "file", fileName: name)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };
    }

    [Fact]
    public async Task Unknown_endpoint_throws_NOT_FOUND()
    {
        var manager = new UploadManager(new());
        var files = new FormFileCollection();

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            manager.HandleAsync("missing", files, new()));

        Assert.Equal("NOT_FOUND", ex.ApiCode);
        Assert.NotNull(ex.Details);
    }

    [Fact]
    public async Task Empty_form_throws_UPLOAD_VALIDATION_ERROR()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "shape_upload_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new UploadManager(new()
            {
                ["covers"] = new UploadEntry { Storage = new() { Driver = "local", Path = tempDir, PublicUrl = "/uploads/covers" } }
            });

            var ex = await Assert.ThrowsAsync<ApiException>(() =>
                manager.HandleAsync("covers", new FormFileCollection(), new()));

            Assert.Equal("UPLOAD_VALIDATION_ERROR", ex.ApiCode);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [Fact]
    public async Task Role_gate_blocks_unauthorized_caller()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "shape_upload_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new UploadManager(new()
            {
                ["covers"] = new UploadEntry
                {
                    Storage = new() { Driver = "local", Path = tempDir, PublicUrl = "/uploads/covers" },
                    Roles = new() { "admin" }
                }
            });

            var files = new FormFileCollection { MakeFile("x.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg") };

            var ex = await Assert.ThrowsAsync<ApiException>(() =>
                manager.HandleAsync("covers", files, new Dictionary<string, object?> { ["role"] = "user" }));

            Assert.Equal("AUTH_FORBIDDEN", ex.ApiCode);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [Fact]
    public async Task Disallowed_mime_throws()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "shape_upload_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new UploadManager(new()
            {
                ["covers"] = new UploadEntry
                {
                    Storage = new() { Driver = "local", Path = tempDir, PublicUrl = "/uploads/covers" },
                    MimeTypes = new() { "image/png" }
                }
            });

            // JPEG magic bytes — detected as image/jpeg, not in allow list.
            var files = new FormFileCollection { MakeFile("x.jpg", new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg") };

            var ex = await Assert.ThrowsAsync<ApiException>(() =>
                manager.HandleAsync("covers", files, new()));

            Assert.Equal("UPLOAD_VALIDATION_ERROR", ex.ApiCode);
            Assert.Contains("Disallowed mime type", ex.Message);
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [Fact]
    public async Task Successful_upload_writes_file_and_returns_metadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "shape_upload_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            var manager = new UploadManager(new()
            {
                ["covers"] = new UploadEntry
                {
                    Storage = new() { Driver = "local", Path = tempDir, PublicUrl = "/uploads/covers" },
                    MimeTypes = new() { "image/png" }
                }
            });

            // PNG magic bytes
            var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
            var files = new FormFileCollection { MakeFile("hero.png", pngBytes, "image/png") };

            var result = await manager.HandleAsync("covers", files, new());

            Assert.Equal("hero.png", result["filename"]);
            Assert.Equal("image/png", result["mime_type"]);
            Assert.Equal(8L, result["size"]);
            Assert.NotNull(result["id"]);
            var url = (string)result["url"]!;
            Assert.StartsWith("/uploads/covers/", url);
            Assert.True(File.Exists(Path.Combine(tempDir, ((string)result["id"]!))));
        }
        finally { try { Directory.Delete(tempDir, true); } catch { } }
    }

    [Fact]
    public void DetectMimeType_recognizes_common_magic_bytes()
    {
        Assert.Equal("image/png", UploadManager.DetectMimeType(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0, 0, 0, 0 }, ""));
        Assert.Equal("image/jpeg", UploadManager.DetectMimeType(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, ""));
        Assert.Equal("image/gif", UploadManager.DetectMimeType(new byte[] { 0x47, 0x49, 0x46, 0x38, 0, 0 }, ""));
        Assert.Equal("application/pdf", UploadManager.DetectMimeType(new byte[] { 0x25, 0x50, 0x44, 0x46, 0 }, ""));
    }

    [Fact]
    public void DetectMimeType_falls_back_to_client_hint_then_octet_stream()
    {
        Assert.Equal("application/x-custom",
            UploadManager.DetectMimeType(Encoding.UTF8.GetBytes("hello"), "application/x-custom"));
        Assert.Equal("application/octet-stream",
            UploadManager.DetectMimeType(Encoding.UTF8.GetBytes("hello"), ""));
    }

    [Fact]
    public void GenerateKey_derives_extension_from_detected_mime()
    {
        // SECURITY-REVIEW M6: the extension comes from the detected MIME, never
        // from a client filename.
        Assert.EndsWith(".png", UploadManager.GenerateKey("image/png"));
        Assert.EndsWith(".pdf", UploadManager.GenerateKey("application/pdf"));

        // A renderable/executable type the sniffer doesn't recognize gets no
        // extension — it cannot be stored as .html/.svg/.php from a filename.
        var key = UploadManager.GenerateKey("text/html");
        Assert.DoesNotContain(".", key);
        Assert.DoesNotContain("/", key);
    }
}
