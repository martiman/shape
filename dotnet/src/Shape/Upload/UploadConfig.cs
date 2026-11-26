namespace Shape.Upload;

/// <summary>
/// Per-endpoint upload configuration. Keyed by endpoint name (e.g. <c>"covers"</c>)
/// inside <see cref="Shape.Core.ShapeConfig.Uploads"/>. The wire route is
/// <c>POST /upload/&lt;name&gt;</c>.
/// </summary>
public sealed class UploadEntry
{
    /// <summary>Storage backend configuration.</summary>
    public StorageConfig Storage { get; set; } = new();

    /// <summary>Roles allowed to use this upload endpoint. Empty/null = public.</summary>
    public List<string>? Roles { get; set; }

    /// <summary>Maximum file size in bytes. <c>0</c> = no limit.</summary>
    public long MaxSize { get; set; }

    /// <summary>Allowed mime types (detected from file content, not the client header). Empty/null = any.</summary>
    public List<string>? MimeTypes { get; set; }

    /// <summary>Description shown in manifest output.</summary>
    public string? Description { get; set; }

    /// <summary>
    /// When set, uploads become private attachments: a row of an ordinary table, streamed only through
    /// <c>GET /files/&lt;name&gt;/&lt;id&gt;</c> after the table's roles and row scopes allow it. No public URL is exposed.
    /// </summary>
    public AttachmentsConfig? Attachments { get; set; }
}

/// <summary>
/// Private-attachment settings of an upload endpoint. The table needs the columns
/// <c>id, file_key, filename, mime_type, size_bytes, sha256</c> plus the <see cref="Fields"/>
/// (and any scope/audit columns the app already uses).
/// </summary>
public sealed class AttachmentsConfig
{
    /// <summary>Ordinary table that holds one row per file; needs an <c>Access</c> entry.</summary>
    public string Table { get; set; } = "";

    /// <summary>Multipart form fields copied into the row (e.g. <c>case_id</c>).</summary>
    public List<string> Fields { get; set; } = new();

    /// <summary>Optional: form field → parent table. The caller must be able to read <c>&lt;table&gt;.get(id)</c> before anything is stored.</summary>
    public Dictionary<string, string> Parents { get; set; } = new();
}

public sealed class StorageConfig
{
    /// <summary>Backend driver name. Currently only <c>"local"</c> is supported in v1.</summary>
    public string Driver { get; set; } = "local";

    /// <summary>Local-disk path (absolute or relative to cwd).</summary>
    public string Path { get; set; } = "";

    /// <summary>Public URL prefix the web server serves files from.</summary>
    public string PublicUrl { get; set; } = "/uploads";
}
