using Shape.Auth;
using Shape.Core;
using Xunit;

namespace Shape.Tests;

/// <summary>Mirror of the PHP ChangePassword.phpt: self-service change + login_format any.</summary>
public sealed class ChangePasswordTests : IDisposable
{
    private readonly string _dbFile = Path.Combine(Path.GetTempPath(), $"shape-pw-{Guid.NewGuid():N}.db");
    private readonly DatabaseConfig _dbConfig;
    private readonly AuthConfig _auth;
    private readonly LoginService _service;
    private readonly JwtGuard _guard;

    public ChangePasswordTests()
    {
        _dbConfig = new DatabaseConfig { Driver = "sqlite", ConnectionString = $"Data Source={_dbFile}" };
        using (var db = new Database(_dbConfig))
        {
            db.Exec("CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT, email TEXT NOT NULL, password_hash TEXT NOT NULL, display_name TEXT, role TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1)", new());
            db.Exec("CREATE TABLE refresh_tokens (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL, token_hash TEXT NOT NULL, expires_at TEXT NOT NULL, revoked_at TEXT, replaced_by_token_id INTEGER)", new());
            var hash = BCrypt.Net.BCrypt.HashPassword("oldpass123");
            db.Exec("INSERT INTO users (email, password_hash, role) VALUES ('jan.novak', @h, 'user'), ('eva', @h, 'user')", new() { ["h"] = hash });
        }
        _auth = new AuthConfig { Driver = "jwt", JwtSecret = "change-password-test-secret-long-enough", LoginFormat = "any" };
        _service = new LoginService(_dbConfig, _auth, new JwtIssuer(_auth));
        _guard = new JwtGuard(_auth);
    }

    private Dictionary<string, object?> Claims(Dictionary<string, object?> tok) =>
        _guard.Authorize(new Dictionary<string, string> { ["authorization"] = "Bearer " + tok["access_token"] });

    private Dictionary<string, object?> Login(string user, string pass) =>
        _service.Login(new() { ["email"] = user, ["password"] = pass });

    [Fact]
    public void Self_change_validates_verifies_current_and_revokes_tokens()
    {
        var tok = Login("jan.novak", "oldpass123");
        var ctx = Claims(tok);

        Assert.Equal("AUTH_VALIDATION_ERROR", Assert.Throws<ApiException>(() =>
            _service.ChangeOwnPassword(new() { ["new_password"] = "newpass456" }, ctx)).ApiCode);
        Assert.Equal("AUTH_VALIDATION_ERROR", Assert.Throws<ApiException>(() =>
            _service.ChangeOwnPassword(new() { ["current_password"] = "oldpass123", ["new_password"] = "short" }, ctx)).ApiCode);
        Assert.Equal("AUTH_INVALID_CREDENTIALS", Assert.Throws<ApiException>(() =>
            _service.ChangeOwnPassword(new() { ["current_password"] = "nope", ["new_password"] = "newpass456" }, ctx)).ApiCode);
        Assert.Equal("AUTH_INVALID", Assert.Throws<ApiException>(() =>
            _service.ChangeOwnPassword(new() { ["current_password"] = "oldpass123", ["new_password"] = "newpass456" }, new() { ["role"] = "user" })).ApiCode);

        var res = _service.ChangeOwnPassword(new() { ["current_password"] = "oldpass123", ["new_password"] = "newpass456" }, ctx);
        Assert.Equal(true, res["updated"]);
        Assert.Equal(1, Convert.ToInt32(res["revoked_refresh_tokens"]));

        Assert.Equal("AUTH_INVALID_CREDENTIALS", Assert.Throws<ApiException>(() => Login("jan.novak", "oldpass123")).ApiCode);
        Assert.NotEmpty(Login("jan.novak", "newpass456")["access_token"]!.ToString()!);
        Assert.Throws<ApiException>(() => _service.Refresh(new() { ["refresh_token"] = tok["refresh_token"] }));

        // Only the caller's password changed.
        Assert.NotEmpty(Login("eva", "oldpass123")["access_token"]!.ToString()!);
    }

    [Fact]
    public void Admin_reset_accepts_non_email_login_only_when_login_format_is_any()
    {
        Assert.Equal(true, _service.ChangePassword(new() { ["email"] = "eva", ["new_password"] = "reset-pass-1" })["updated"]);
        Assert.NotEmpty(Login("eva", "reset-pass-1")["access_token"]!.ToString()!);

        var strict = new LoginService(_dbConfig, new AuthConfig { Driver = "jwt", JwtSecret = _auth.JwtSecret, LoginFormat = "email" }, new JwtIssuer(_auth));
        Assert.Equal("AUTH_VALIDATION_ERROR", Assert.Throws<ApiException>(() =>
            strict.ChangePassword(new() { ["email"] = "eva", ["new_password"] = "reset-pass-2" })).ApiCode);
    }

    public void Dispose()
    {
        foreach (var f in new[] { _dbFile, _dbFile + "-wal", _dbFile + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }
}
