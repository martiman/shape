namespace Shape.Upload;

/// <summary>
/// Abstract over file-storage backends. <see cref="LocalStorage"/> today can
/// become S3Storage / GcsStorage / R2Storage later without changing
/// <see cref="UploadManager"/>. Each implementation handles only put/exists/
/// delete primitives — validation, naming, and metadata live in the manager.
/// </summary>
public interface IStorage
{
    /// <summary>
    /// Store the bytes at the given relative key. Returns a stable URL the
    /// client can use to retrieve the file later.
    /// </summary>
    /// <param name="key">Relative key (e.g. <c>"abc123.jpg"</c>).</param>
    /// <param name="bytes">The uploaded content.</param>
    /// <param name="mimeType">Content-type to record/serve.</param>
    string Put(string key, byte[] bytes, string mimeType);

    /// <summary>Whether a previously-stored object exists.</summary>
    bool Exists(string key);

    /// <summary>Remove the object. No-op if the key doesn't exist.</summary>
    void Delete(string key);

    /// <summary>Driver name (<c>local</c>, <c>s3</c>, …) for diagnostics and manifest output.</summary>
    string Driver { get; }
}
