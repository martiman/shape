using System.Text;
using System.Text.Json;
using Shape.Auth;
using Shape.Core;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

/// <summary>
/// brownfield adapter guide coverage: §3 auth.exchange bridge,
/// §4 security:strict preset, §2 TenantExposurePlanner.
/// </summary>
public sealed class TokenExchangeTests
{
    private sealed class FakeSource : ITokenExchangeSource
    {
        public Dictionary<string, Dictionary<string, object?>> Tokens { get; } = new();

        public Dictionary<string, object?>? Validate(string externalToken, Database db)
            => Tokens.TryGetValue(externalToken, out var identity)
                ? new Dictionary<string, object?>(identity)
                : null;
    }

    private static (LoginService Service, FakeSource Source, string DbFile) BuildExchange(
        bool withTenancy = true, bool issueRefresh = false)
    {
        var dbFile = Path.Combine(Path.GetTempPath(), $"shape-exchange-{Guid.NewGuid():N}.db");
        var dbConfig = new DatabaseConfig { Driver = "sqlite", ConnectionString = $"Data Source={dbFile}" };
        using (var db = new Database(dbConfig))
        {
            db.Exec("CREATE TABLE orgs (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1)", new());
            db.Exec("INSERT INTO orgs (id, name, is_active) VALUES (1, 'Acme', 1), (2, 'Globex', 0)", new());
        }

        var source = new FakeSource();
        var auth = new AuthConfig
        {
            Driver = "jwt",
            JwtSecret = "test-secret-must-be-long-enough",
            Tenancy = withTenancy
                ? new TenancyConfig { Table = "orgs", Column = "OrganizationId", UnscopedRoles = new() { "admin" } }
                : null,
            Exchange = new TokenExchangeConfig
            {
                Source = source,
                ClaimMapping = new() { ["OrganizationId"] = "tenant_id" },
                RoleMapping = new() { ["Referent"] = "user" },
                IssueRefreshToken = issueRefresh
            }
        };
        return (new LoginService(dbConfig, auth, new JwtIssuer(auth)), source, dbFile);
    }

    private static Dictionary<string, JsonElement> DecodeJwtPayload(string jwt)
    {
        var seg = jwt.Split('.')[1];
        var pad = seg.Length % 4;
        if (pad > 0) seg += new string('=', 4 - pad);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(seg.Replace('-', '+').Replace('_', '/')));
        return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    }

    [Fact]
    public void Exchange_swaps_external_token_for_stateless_shape_jwt()
    {
        var (svc, source, dbFile) = BuildExchange();
        try
        {
            var guid = Guid.NewGuid().ToString();
            source.Tokens["tis-token-1"] = new Dictionary<string, object?>
            {
                ["id"] = guid,
                ["email"] = "alice@acme.test",
                ["role"] = "Referent",           // mapped → user
                ["OrganizationId"] = 1           // mapped → tenant_id
            };

            var response = svc.Exchange(new Dictionary<string, object?> { ["token"] = "tis-token-1" });

            Assert.NotNull(response["access_token"]);
            // Stateless by default: no refresh pair, no refresh_tokens table needed.
            Assert.False(response.ContainsKey("refresh_token"));

            var claims = DecodeJwtPayload((string)response["access_token"]!);
            Assert.Equal(guid, claims["sub"].GetString());          // Guid id survives verbatim
            Assert.Equal("user", claims["role"].GetString());       // role mapping applied
            Assert.Equal(1, claims["tenant_id"].GetInt32());        // claim mapping applied
        }
        finally
        {
            if (File.Exists(dbFile)) File.Delete(dbFile);
        }
    }

    [Fact]
    public void Exchange_rejects_unknown_external_token()
    {
        var (svc, _, dbFile) = BuildExchange();
        try
        {
            var ex = Assert.Throws<ApiException>(() =>
                svc.Exchange(new Dictionary<string, object?> { ["token"] = "forged" }));
            Assert.Equal("AUTH_EXCHANGE_INVALID", ex.ApiCode);
            Assert.Equal(401, ex.Status);
        }
        finally
        {
            if (File.Exists(dbFile)) File.Delete(dbFile);
        }
    }

    [Fact]
    public void Exchange_blocks_suspended_tenant_but_not_unscoped_role()
    {
        var (svc, source, dbFile) = BuildExchange();
        try
        {
            // Scoped user of the suspended org 2 → TENANT_SUSPENDED at exchange.
            source.Tokens["scoped"] = new Dictionary<string, object?>
            {
                ["id"] = "u1", ["role"] = "user", ["OrganizationId"] = 2
            };
            var ex = Assert.Throws<ApiException>(() =>
                svc.Exchange(new Dictionary<string, object?> { ["token"] = "scoped" }));
            Assert.Equal("TENANT_SUSPENDED", ex.ApiCode);

            // Unscoped admin of the same org still exchanges (recovery path).
            source.Tokens["admin"] = new Dictionary<string, object?>
            {
                ["id"] = "u2", ["role"] = "admin", ["OrganizationId"] = 2
            };
            var response = svc.Exchange(new Dictionary<string, object?> { ["token"] = "admin" });
            var claims = DecodeJwtPayload((string)response["access_token"]!);
            Assert.Equal(JsonValueKind.Null, claims["tenant_id"].ValueKind); // unscoped → null claim
        }
        finally
        {
            if (File.Exists(dbFile)) File.Delete(dbFile);
        }
    }

    [Fact]
    public void Exchange_passes_extra_identity_keys_to_user_object_but_never_secrets()
    {
        var (svc, source, dbFile) = BuildExchange();
        try
        {
            source.Tokens["extra"] = new Dictionary<string, object?>
            {
                ["id"] = "u7",
                ["role"] = "user",
                ["OrganizationId"] = 1,
                ["organization_name"] = "Acme",       // app extra → passes through
                ["password_hash"] = "secret-hash"     // reserved → never echoed
            };

            var response = svc.Exchange(new Dictionary<string, object?> { ["token"] = "extra" });
            var user = (Dictionary<string, object?>)response["user"]!;
            Assert.Equal("Acme", user["organization_name"]);
            Assert.False(user.ContainsKey("password_hash"));
        }
        finally
        {
            if (File.Exists(dbFile)) File.Delete(dbFile);
        }
    }

    [Fact]
    public void Exchange_without_config_is_not_configured()
    {
        var dbConfig = new DatabaseConfig { Driver = "sqlite", ConnectionString = "Data Source=:memory:" };
        var auth = new AuthConfig { Driver = "jwt", JwtSecret = "test-secret-must-be-long-enough" };
        var svc = new LoginService(dbConfig, auth, new JwtIssuer(auth));

        var ex = Assert.Throws<ApiException>(() =>
            svc.Exchange(new Dictionary<string, object?> { ["token"] = "anything" }));
        Assert.Equal("AUTH_NOT_CONFIGURED", ex.ApiCode);
    }

    [Fact]
    public void Exchange_requires_token_field()
    {
        var (svc, _, dbFile) = BuildExchange();
        try
        {
            var ex = Assert.Throws<ApiException>(() => svc.Exchange(new Dictionary<string, object?>()));
            Assert.Equal("AUTH_VALIDATION_ERROR", ex.ApiCode);
        }
        finally
        {
            if (File.Exists(dbFile)) File.Delete(dbFile);
        }
    }

    // ── §4 security: strict ───────────────────────────────────────────

    private static ShapeConfig StrictConfig(Action<ShapeConfig>? mutate = null)
    {
        var config = new ShapeConfig
        {
            Security = "strict",
            Auth = new AuthConfig { Driver = "jwt", JwtSecret = "test-secret-must-be-long-enough" },
            ManifestRoles = new List<string>(),
            Masks = new Dictionary<string, MaskConfig>
            {
                ["report"] = new() { Sql = "SELECT 1 AS one", Roles = new() { "admin" } }
            }
        };
        mutate?.Invoke(config);
        return config;
    }

    [Fact]
    public void Strict_passes_a_fully_gated_config()
    {
        var config = ShapeApp.FromTypes(Array.Empty<Type>(), StrictConfig());
        Assert.Equal("strict", config.Security);
    }

    [Fact]
    public void Strict_rejects_driver_none()
    {
        var ex = Assert.Throws<ApiException>(() =>
            ShapeApp.FromTypes(Array.Empty<Type>(), StrictConfig(c => c.Auth = new AuthConfig())));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("auth driver", ex.Message);
    }

    [Fact]
    public void Strict_rejects_roleless_mask()
    {
        var ex = Assert.Throws<ApiException>(() =>
            ShapeApp.FromTypes(Array.Empty<Type>(), StrictConfig(c =>
                c.Masks["open"] = new MaskConfig { Sql = "SELECT 1 AS one" })));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("open", ex.Message);
    }

    [Fact]
    public void Strict_rejects_unset_manifest_roles()
    {
        var ex = Assert.Throws<ApiException>(() =>
            ShapeApp.FromTypes(Array.Empty<Type>(), StrictConfig(c => c.ManifestRoles = null)));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("manifest_roles", ex.Message);
    }

    // ── §2 TenantExposurePlanner ──────────────────────────────────────

    [Fact]
    public void Planner_exposes_only_fk_verified_tenant_tables()
    {
        var dbFile = Path.Combine(Path.GetTempPath(), $"shape-planner-{Guid.NewGuid():N}.db");
        var dbConfig = new DatabaseConfig { Driver = "sqlite", ConnectionString = $"Data Source={dbFile}" };
        try
        {
            using var db = new Database(dbConfig);
            db.Exec("CREATE TABLE Organizations (Id INTEGER PRIMARY KEY AUTOINCREMENT, Name TEXT NOT NULL)", new());
            // Verified FK → exposed.
            db.Exec("CREATE TABLE Requests (Id INTEGER PRIMARY KEY AUTOINCREMENT, OrganizationId INT NOT NULL REFERENCES Organizations(Id), Title TEXT)", new());
            // Column with the right name but NO foreign key → not exposed.
            db.Exec("CREATE TABLE Drafts (Id INTEGER PRIMARY KEY AUTOINCREMENT, OrganizationId INT NOT NULL, Body TEXT)", new());
            // No tenant column at all → not exposed.
            db.Exec("CREATE TABLE Lookups (Id INTEGER PRIMARY KEY AUTOINCREMENT, Code TEXT)", new());
            // Internal table with a valid FK → excluded explicitly.
            db.Exec("CREATE TABLE Tokens (Id INTEGER PRIMARY KEY AUTOINCREMENT, OrganizationId INT REFERENCES Organizations(Id))", new());

            var tenancy = new TenancyConfig { Table = "Organizations", Column = "OrganizationId" };
            var plan = TenantExposurePlanner.PlanTables(
                new DatabaseIntrospector(db), tenancy, new[] { "Tokens" });

            Assert.Equal(new List<string> { "Requests" }, plan);
        }
        finally
        {
            if (File.Exists(dbFile)) File.Delete(dbFile);
        }
    }
}
