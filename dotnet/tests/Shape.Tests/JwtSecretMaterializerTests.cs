using Shape.Auth;
using Xunit;

namespace Shape.Tests;

public sealed class JwtSecretMaterializerTests
{
    private static string TempBaseDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shape-jwt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Ensure_writes_secret_when_unset()
    {
        var dir = TempBaseDir();
        try
        {
            var auth = new AuthConfig { Driver = "jwt" };
            JwtSecretMaterializer.Ensure(auth, dir);

            var path = Path.Combine(dir, ".shape", "jwt-secret");
            Assert.True(File.Exists(path));
            Assert.False(string.IsNullOrEmpty(auth.JwtSecret));
            Assert.Equal(64, auth.JwtSecret!.Length); // 32 bytes hex = 64 chars
            Assert.Equal(File.ReadAllText(path).Trim(), auth.JwtSecret);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Ensure_reads_existing_secret_on_subsequent_boots()
    {
        var dir = TempBaseDir();
        try
        {
            var auth1 = new AuthConfig { Driver = "jwt" };
            JwtSecretMaterializer.Ensure(auth1, dir);
            var first = auth1.JwtSecret!;

            var auth2 = new AuthConfig { Driver = "jwt" };
            JwtSecretMaterializer.Ensure(auth2, dir);

            Assert.Equal(first, auth2.JwtSecret);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Ensure_noops_when_secret_already_set()
    {
        var dir = TempBaseDir();
        try
        {
            var auth = new AuthConfig { Driver = "jwt", JwtSecret = "explicit-operator-supplied-secret" };
            JwtSecretMaterializer.Ensure(auth, dir);

            Assert.Equal("explicit-operator-supplied-secret", auth.JwtSecret);
            Assert.False(File.Exists(Path.Combine(dir, ".shape", "jwt-secret")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Ensure_noops_when_driver_is_not_jwt()
    {
        var dir = TempBaseDir();
        try
        {
            var auth = new AuthConfig { Driver = "none" };
            JwtSecretMaterializer.Ensure(auth, dir);

            Assert.Null(auth.JwtSecret);
            Assert.False(Directory.Exists(Path.Combine(dir, ".shape")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Ensure_sets_owner_only_permissions_on_unix()
    {
        if (OperatingSystem.IsWindows()) return; // 0600 is a no-op on Windows

        var dir = TempBaseDir();
        try
        {
            var auth = new AuthConfig { Driver = "jwt" };
            JwtSecretMaterializer.Ensure(auth, dir);

            var path = Path.Combine(dir, ".shape", "jwt-secret");
            var mode = File.GetUnixFileMode(path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AuthConfig_Jwt_factory_returns_jwt_driver()
    {
        var auth = AuthConfig.Jwt();
        Assert.Equal("jwt", auth.Driver);
        Assert.Null(auth.JwtSecret);
    }
}
