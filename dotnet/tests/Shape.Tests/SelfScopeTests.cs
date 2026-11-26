using Shape;
using Shape.Attributes;
using Shape.Auth;
using Shape.Core;
using Xunit;

namespace Shape.Tests;

[Entity("sw_logs", Writable = true)]
[Access(Roles = new[] { "user", "admin" })]
public sealed class SwLog
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    public int UserId { get; set; }

    [Column(Length = 50)]
    public string Note { get; set; } = "";
}

/// <summary>
/// Self scope (<c>self: true</c>): the scope value is the caller's own users.id — a user
/// sees and writes only own rows, unscoped roles see all. Mirror of the PHP SelfScope.phpt.
/// </summary>
public sealed class SelfScopeTests : IDisposable
{
    private static readonly Dictionary<string, ScopeConfig> Scopes = new(StringComparer.Ordinal)
    {
        ["user_id"] = new ScopeConfig { Self = true, UnscopedRoles = new() { "admin" } }
    };

    private readonly string _dbFile = Path.Combine(Path.GetTempPath(), $"shape-self-{Guid.NewGuid():N}.db");
    private readonly DatabaseConfig _dbConfig;
    private readonly Database _db;
    private readonly BatchExecutor _executor;

    public SelfScopeTests()
    {
        _dbConfig = new DatabaseConfig { Driver = "sqlite", ConnectionString = $"Data Source={_dbFile}" };
        _db = new Database(_dbConfig);
        _db.Exec("CREATE TABLE sw_logs (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL, note TEXT NOT NULL)", new());
        _db.Exec("INSERT INTO sw_logs (user_id, note) VALUES (1, 'a1'), (1, 'a2'), (2, 'b1')", new());
        _db.Exec("CREATE TABLE users (id INTEGER PRIMARY KEY AUTOINCREMENT, email TEXT NOT NULL, password_hash TEXT NOT NULL, display_name TEXT, role TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1)", new());
        _db.Exec("CREATE TABLE refresh_tokens (id INTEGER PRIMARY KEY AUTOINCREMENT, user_id INTEGER NOT NULL, token_hash TEXT NOT NULL, expires_at TEXT NOT NULL, revoked_at TEXT, replaced_by_token_id INTEGER)", new());
        var hash = BCrypt.Net.BCrypt.HashPassword("password123");
        _db.Exec("INSERT INTO users (email, password_hash, role) VALUES ('a', @h, 'admin'), ('u', @h, 'user')", new() { ["h"] = hash });

        var config = ShapeApp.FromTypes(new[] { typeof(SwLog) }, new ShapeConfig
        {
            Db = _dbConfig,
            Scopes = Scopes,
            Access = new Dictionary<string, List<string>>(StringComparer.Ordinal) { ["sw_logs"] = new() { "user", "admin" } }
        });
        _executor = new BatchExecutor(_db, new MaskRegistry(config.Masks), new ValidatorRegistry(), null, Scopes);
    }

    private static Dictionary<string, object?> Ctx(string role, object? userId) =>
        new() { ["id"] = userId ?? 9, ["role"] = role, ["user_id"] = userId };

    private int Count(Dictionary<string, object?> ctx)
    {
        var res = _executor.Execute(new List<Dictionary<string, object?>>
            { new() { ["ref"] = "r", ["sql"] = "sw_logs.list", ["params"] = new Dictionary<string, object?>() } }, ctx);
        return Convert.ToInt32(((List<Dictionary<string, object?>>)res["results"]!)[0]["count"]);
    }

    private Dictionary<string, object?> Insert(Dictionary<string, object?> ctx, Dictionary<string, object?> data)
    {
        var res = _executor.ExecuteMutations(new List<Dictionary<string, object?>>
            { new() { ["table"] = "sw_logs", ["action"] = "insert", ["data"] = data } }, ctx);
        return ((List<Dictionary<string, object?>>)res["results"]!)[0];
    }

    [Fact]
    public void User_sees_only_own_rows_admin_sees_all()
    {
        Assert.Equal(2, Count(Ctx("user", 1)));
        Assert.Equal(1, Count(Ctx("user", 2)));
        Assert.Equal(3, Count(Ctx("admin", null)));
    }

    [Fact]
    public void Writing_for_someone_else_is_rejected_and_own_id_is_stamped()
    {
        var u1 = Ctx("user", 1);
        var ex = Assert.Throws<ApiException>(() => Insert(u1, new() { ["user_id"] = 2, ["note"] = "forged" }));
        Assert.Equal("SCOPE_MISMATCH", ex.Details?.GetValueOrDefault("detail"));

        Insert(u1, new() { ["note"] = "stamped" });
        Assert.Equal(1, Convert.ToInt32(_db.Run("SELECT COUNT(*) FROM sw_logs WHERE note = 'stamped' AND user_id = 1", new()).Rows[0][0]));
    }

    [Fact]
    public void Cannot_update_another_users_row()
    {
        var res = _executor.ExecuteMutations(new List<Dictionary<string, object?>>
        {
            new() { ["table"] = "sw_logs", ["action"] = "update", ["filter"] = new Dictionary<string, object?> { ["id"] = 3 },
                    ["data"] = new Dictionary<string, object?> { ["note"] = "hack" } }
        }, Ctx("user", 1));
        Assert.Equal(0, Convert.ToInt32(((List<Dictionary<string, object?>>)res["results"]!)[0]["affected"]));
    }

    [Fact]
    public void Claim_is_users_id_on_login_and_refresh_null_for_unscoped()
    {
        var auth = new AuthConfig { Driver = "jwt", JwtSecret = "self-scope-test-secret-long-enough-xx", Scopes = Scopes };
        var service = new LoginService(_dbConfig, auth, new JwtIssuer(auth));
        var guard = new JwtGuard(auth);
        Dictionary<string, object?> Ctx2(string t) =>
            guard.Authorize(new Dictionary<string, string> { ["authorization"] = "Bearer " + t });

        var login = service.Login(new() { ["email"] = "u", ["password"] = "password123" });
        Assert.Equal(2L, Convert.ToInt64(Ctx2(login["access_token"]!.ToString()!)["user_id"]));

        var refreshed = service.Refresh(new() { ["refresh_token"] = login["refresh_token"] });
        Assert.Equal(2L, Convert.ToInt64(Ctx2(refreshed["access_token"]!.ToString()!)["user_id"]));

        var admin = service.Login(new() { ["email"] = "a", ["password"] = "password123" });
        var adminCtx = Ctx2(admin["access_token"]!.ToString()!);
        Assert.True(adminCtx.ContainsKey("user_id"));
        Assert.Null(adminCtx["user_id"]);
    }

    [Fact]
    public void Self_cannot_be_combined_with_multi_or_claim_table()
    {
        Assert.Throws<ApiException>(() => Shape.Metadata.TenancyResolver.ValidateScope("user_id", new ScopeConfig { Self = true, Multi = true }));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (File.Exists(_dbFile)) File.Delete(_dbFile);
    }
}
