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
