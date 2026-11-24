using Shape.Core;

namespace Shape.Upload;

/// <summary>
/// Local-disk storage backend. Saves uploaded files under a configured
/// directory and serves them via a configured public-URL prefix. The web
/// server (Kestrel static files / Nginx / Apache) serves files at
/// <see cref="StorageConfig.PublicUrl"/> from the directory at
/// <see cref="StorageConfig.Path"/>. Shape doesn't proxy file reads — the
/// upload route is for write only.
/// </summary>
public sealed class LocalStorage : IStorage
{
    private readonly string _path;
    private readonly string _publicUrl;

    public LocalStorage(StorageConfig config)
    {
        if (string.IsNullOrEmpty(config.Path))
            throw new ApiException("UPLOAD_CONFIG_ERROR", "Local storage requires a non-empty 'Path'.", 500);

        _path = config.Path.TrimEnd('/', '\\');
        _publicUrl = (config.PublicUrl ?? "/uploads").TrimEnd('/');

        Directory.CreateDirectory(_path);

        if (!Directory.Exists(_path))
        {
            throw new ApiException(
                "UPLOAD_CONFIG_ERROR",
                $"Local storage directory '{_path}' is missing or not writable.",
                500);
        }
    }

    public string Driver => "local";

    public string Put(string key, byte[] bytes, string mimeType)
    {
        var safeKey = SanitizeKey(key);
        var target = Path.Combine(_path, safeKey);

        var targetDir = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(targetDir))
            Directory.CreateDirectory(targetDir);

        try
        {
            File.WriteAllBytes(target, bytes);
        }
        catch (Exception ex)
        {
            throw new ApiException(
                "UPLOAD_STORAGE_ERROR",
                $"Failed to write upload to '{target}': {ex.Message}",
                500,
                null,
                null,
                ex);
        }

        return _publicUrl + "/" + safeKey;
    }

    public bool Exists(string key) => File.Exists(Path.Combine(_path, SanitizeKey(key)));

    public void Delete(string key)
    {
        var target = Path.Combine(_path, SanitizeKey(key));
        if (File.Exists(target))
        {
            try { File.Delete(target); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Reject path-traversal attempts. The upload manager generates keys, so
    /// this is defense-in-depth, but a future caller passing untrusted input
    /// deserves the safety net.
    /// </summary>
    private static string SanitizeKey(string key)
    {
        var trimmed = key.TrimStart('/', '\\');
        if (trimmed.Length == 0 || trimmed.Contains(".."))
            throw new ApiException("UPLOAD_STORAGE_ERROR", $"Invalid storage key '{key}'.", 400);
        return trimmed;
    }
}
