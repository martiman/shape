using System.Security.Cryptography;

namespace Shape.Auth;

/// <summary>
/// When <c>AuthConfig.Driver = "jwt"</c> and <c>AuthConfig.JwtSecret</c> is
/// unset, materialize a 32-byte hex secret to <c>&lt;baseDir&gt;/.shape/jwt-secret</c>
/// (mode 0600 on Unix) and re-read it on subsequent boots. Mirrors the PHP
/// <c>ShapeApp::buildDatabaseConfig</c> behaviour — local-dev tokens stay
/// valid across restarts, prod operators set <c>JwtSecret</c> explicitly to
/// opt out.
/// </summary>
public static class JwtSecretMaterializer
{
    /// <summary>
    /// Idempotent. No-ops when:
    /// <list type="bullet">
    ///   <item><c>auth.Driver != "jwt"</c> (no local-JWT login configured)</item>
    ///   <item><c>auth.JwtSecret</c> is already non-empty (explicit override)</item>
    /// </list>
    /// Otherwise reads <c>&lt;baseDir&gt;/.shape/jwt-secret</c> when present,
    /// or generates and writes a new 32-byte hex secret. Sets
    /// <c>auth.JwtSecret</c> in place.
    /// </summary>
    public static void Ensure(AuthConfig auth, string baseDir)
    {
        if (!string.Equals(auth.Driver, "jwt", StringComparison.OrdinalIgnoreCase))
            return;
        if (!string.IsNullOrEmpty(auth.JwtSecret))
            return;

        if (string.IsNullOrWhiteSpace(baseDir))
            baseDir = AppContext.BaseDirectory;

        var dir = Path.Combine(baseDir, ".shape");
        var file = Path.Combine(dir, "jwt-secret");

        if (File.Exists(file))
        {
            var existing = File.ReadAllText(file).Trim();
            if (existing.Length > 0)
            {
                auth.JwtSecret = existing;
                return;
            }
        }

        Directory.CreateDirectory(dir);
        // SECURITY-REVIEW L4: lock the .shape dir down to the owner (0700) — the
        // secret lives here and a world-readable dir leaks its existence/metadata.
        if (!OperatingSystem.IsWindows())
        {
            try { File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
            catch (PlatformNotSupportedException) { /* best-effort */ }
        }

        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();

        // SECURITY-REVIEW L4: create the file with a restrictive mode BEFORE
        // writing the secret, closing the write-then-chmod window where the
        // freshly-written secret sat at the umask default. NOOP on Windows.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                using var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 4096, options: FileOptions.None);
                File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                using var writer = new StreamWriter(fs);
                writer.Write(hex);
            }
            catch (PlatformNotSupportedException)
            {
                File.WriteAllText(file, hex);
                try { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
            }
        }
        else
        {
            File.WriteAllText(file, hex);
        }

        auth.JwtSecret = hex;
    }
}
