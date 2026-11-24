using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Shape.Core;

namespace Shape.Upload;

/// <summary>
/// Resolves the configured upload entry, validates the incoming file
/// (size + mime), generates a stable storage key, dispatches to the
/// configured backend, and shapes the JSON response.
///
/// Wire route: <c>POST /upload/&lt;name&gt;</c> with a multipart body
/// containing a single <c>file</c> field.
/// </summary>
public sealed class UploadManager
{
    private readonly Dictionary<string, UploadEntry> _uploads;

    /// <summary>Default upload cap (bytes) when an endpoint sets no explicit MaxSize.</summary>
    private const long DefaultMaxUploadBytes = 10L * 1024 * 1024;

    public UploadManager(Dictionary<string, UploadEntry>? uploads)
    {
        _uploads = uploads ?? new Dictionary<string, UploadEntry>();
    }

    /// <summary>Names of configured upload endpoints (for manifest output).</summary>
    public IReadOnlyList<string> Names() => _uploads.Keys.ToList();

    /// <summary>
    /// Handle a single upload request. Returns the response payload:
    /// <c>{ id, filename, size, mime_type, url }</c>.
    /// </summary>
    public async Task<Dictionary<string, object?>> HandleAsync(
        string name,
        IFormFileCollection files,
        Dictionary<string, object?> authContext)
    {
        if (!_uploads.TryGetValue(name, out var entry))
        {
            throw new ApiException(
                "NOT_FOUND",
                $"Upload endpoint '{name}' is not configured.",
                404,
                null,
                new Dictionary<string, object?>
                {
                    ["expected"] = _uploads.Count == 0
                        ? "No upload endpoints are configured for this app."
                        : "one of: " + string.Join(", ", _uploads.Keys),
                    ["hint"] = "Upload endpoints are declared under ShapeConfig.Uploads[\"<name>\"]."
                });
        }

        AuthorizeRole(entry, authContext);

        var file = ExtractFile(files);
        // SECURITY-REVIEW M5: bound the read so a huge upload can't exhaust
        // memory before the size check. Default cap applies when the endpoint
        // sets no explicit MaxSize.
        var maxBytes = entry.MaxSize > 0 ? entry.MaxSize : DefaultMaxUploadBytes;
        var bytes = await ReadAllBytesAsync(file, maxBytes);
        var detectedMime = ValidateFile(file, bytes, entry);

        // SECURITY-REVIEW M6: the stored extension is derived from the detected
        // MIME, not the client filename — a PNG-content file named "x.html"
        // must not be stored (and later served) as .html.
        var key = GenerateKey(detectedMime);
        var storage = BuildStorage(entry);
        var url = storage.Put(key, bytes, detectedMime);

        return new Dictionary<string, object?>
        {
            ["id"] = key,
            ["filename"] = file.FileName,
            ["size"] = bytes.LongLength,
            ["mime_type"] = detectedMime,
            ["url"] = url
        };
    }

    // ── Internals ─────────────────────────────────────────────────────

    private static void AuthorizeRole(UploadEntry entry, Dictionary<string, object?> authContext)
    {
        var allowed = entry.Roles;
        if (allowed == null || allowed.Count == 0)
            return; // public upload endpoint

        var role = authContext.TryGetValue("role", out var r) ? r?.ToString() ?? "" : "";
        if (string.IsNullOrEmpty(role) || !allowed.Contains(role))
        {
            throw new ApiException(
                "AUTH_FORBIDDEN",
                "Current role is not allowed for this upload endpoint.",
                403,
                null,
                new Dictionary<string, object?>
                {
                    ["expected"] = new Dictionary<string, object?> { ["role"] = "one of: " + string.Join(", ", allowed) },
                    ["hint"] = role.Length == 0
                        ? "Authenticate first via POST {op:'auth.token'} and pass the access token as Authorization: Bearer <token>."
                        : $"Your role '{role}' cannot use this upload endpoint."
                });
        }
    }

    private static IFormFile ExtractFile(IFormFileCollection files)
    {
        if (files.Count == 0)
        {
            throw new ApiException(
                "UPLOAD_VALIDATION_ERROR",
                "No file uploaded. Send a multipart/form-data request with a 'file' field.",
                400,
                null,
                new Dictionary<string, object?>
                {
                    ["expected"] = "multipart/form-data body with a single 'file' field",
                    ["hint"] = "Use Content-Type: multipart/form-data and attach the file under the 'file' form name."
                });
        }

        // Documented convention: 'file' field. Fall back to the only file when
        // a single field is present (common with <input name="cover">).
        var picked = files.GetFile("file") ?? (files.Count == 1 ? files[0] : null);
        if (picked == null)
        {
            throw new ApiException(
                "UPLOAD_VALIDATION_ERROR",
                "Multi-file uploads are not supported in v1; send a single file under the 'file' form name.",
                400);
        }
        return picked;
    }

    private static async Task<byte[]> ReadAllBytesAsync(IFormFile file, long maxBytes)
    {
        await using var stream = file.OpenReadStream();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            if (ms.Length + read > maxBytes)
            {
                var maxKb = (maxBytes + 1023) / 1024;
                throw new ApiException(
                    "UPLOAD_VALIDATION_ERROR",
                    $"File too large: exceeds limit of {maxKb} KB.",
                    400,
                    null,
                    new Dictionary<string, object?>
                    {
                        ["expected"] = new Dictionary<string, object?> { ["max_size"] = maxBytes + " bytes" },
                        ["hint"] = "Compress or resize the file, or ask the admin to raise the limit on this endpoint."
                    });
            }
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    private static string ValidateFile(IFormFile file, byte[] bytes, UploadEntry entry)
    {
        if (bytes.Length == 0)
            throw new ApiException("UPLOAD_VALIDATION_ERROR", "Uploaded file is empty.", 400);

        if (entry.MaxSize > 0 && bytes.LongLength > entry.MaxSize)
        {
            var sizeKb = (bytes.LongLength + 1023) / 1024;
            var maxKb = (entry.MaxSize + 1023) / 1024;
            throw new ApiException(
                "UPLOAD_VALIDATION_ERROR",
                $"File too large: {sizeKb} KB exceeds limit of {maxKb} KB.",
                400,
                null,
                new Dictionary<string, object?>
                {
                    ["expected"] = new Dictionary<string, object?> { ["max_size"] = entry.MaxSize + " bytes" },
                    ["hint"] = "Compress or resize the file, or ask the admin to raise the limit on this endpoint."
                });
        }

        // Detect from magic bytes, not the client-supplied Content-Type
        // (trivially spoofable). The detected value is what gets returned.
        var detected = DetectMimeType(bytes, file.ContentType);

        var allowed = entry.MimeTypes;
        if (allowed != null && allowed.Count > 0 && !allowed.Contains(detected))
        {
            throw new ApiException(
                "UPLOAD_VALIDATION_ERROR",
                $"Disallowed mime type: '{detected}'.",
                400,
                null,
                new Dictionary<string, object?>
                {
                    ["expected"] = new Dictionary<string, object?> { ["mime_type"] = "one of: " + string.Join(", ", allowed) },
                    ["hint"] = "The mime type is detected from file contents, not the client header — relabeling the client request will not help."
                });
        }

        return detected;
    }

    /// <summary>
    /// Stable storage key: 16 random hex chars + an extension derived from the
    /// <b>detected</b> MIME type (SECURITY-REVIEW M6). The client filename never
    /// influences the stored extension, so a mislabeled file (PNG bytes named
    /// "x.html") cannot be stored — and later served — as an executable/renderable
    /// type. Unknown MIME → no extension.
    /// </summary>
    internal static string GenerateKey(string detectedMime)
    {
        var ext = MimeToExtension(detectedMime);
        var bytes = RandomNumberGenerator.GetBytes(8);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return hex + ext;
    }

    private static string MimeToExtension(string mime) => mime switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "application/pdf" => ".pdf",
        "application/zip" => ".zip",
        _ => ""
    };

    private static IStorage BuildStorage(UploadEntry entry)
    {
        var driver = (entry.Storage.Driver ?? "local").ToLowerInvariant();
        return driver switch
        {
            "local" => new LocalStorage(entry.Storage),
            _ => throw new ApiException(
                "UPLOAD_CONFIG_ERROR",
                $"Unsupported storage driver '{driver}'. Only 'local' is supported in v1.",
                500)
        };
    }

    /// <summary>
    /// Magic-byte sniffer covering the common image / PDF / zip families.
    /// Falls back to the client-supplied <c>Content-Type</c> when nothing
    /// matches, finally to <c>application/octet-stream</c>.
    /// </summary>
    internal static string DetectMimeType(byte[] bytes, string clientHint)
    {
        if (bytes.Length >= 4)
        {
            // PNG: 89 50 4E 47
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
                return "image/png";
            // JPEG: FF D8 FF
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
                return "image/jpeg";
            // GIF87a / GIF89a
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
                return "image/gif";
            // PDF: 25 50 44 46
            if (bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46)
                return "application/pdf";
            // ZIP / DOCX / XLSX / PPTX: 50 4B 03 04
            if (bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04)
                return "application/zip";
        }
        if (bytes.Length >= 12)
        {
            // WebP: RIFF....WEBP
            if (bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
                return "image/webp";
        }

        return !string.IsNullOrEmpty(clientHint) ? clientHint : "application/octet-stream";
    }
}
