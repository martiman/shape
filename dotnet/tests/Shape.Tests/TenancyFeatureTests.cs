using Shape;
using Shape.Attributes;
using Shape.Auth;
using Shape.Core;
using Shape.Generator;
using Shape.Metadata;
using Xunit;

namespace Shape.Tests;

[Entity("tenant_books", Writable = true)]
public sealed class TenantBook03
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    public int TenantId { get; set; }

    [Column(Length = 100)]
    public string Title { get; set; } = "";
}

[Entity("tenant_authors", Writable = true)]
public sealed class TenantAuthor04
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    public int TenantId { get; set; }

    [Column(Length = 100)]
    public string Name { get; set; } = "";
}

[Entity("tenant_books_fk", Writable = true)]
public sealed class TenantBook04
{
    [Id]
    [AutoIncrement]
    public int Id { get; set; }

    [Fk(typeof(TenantAuthor04))]
    public int AuthorId { get; set; }

    public int TenantId { get; set; }

    [Column(Length = 100)]
    public string Title { get; set; } = "";
}

public sealed class TenancyFeatureTests
{
    [Fact]
    public void Manual_mask_on_scoped_table_requires_tenant_contract()
    {
        var config = new ShapeConfig
        {
            Auth = new AuthConfig { Driver = "none" },
            Tenancy = new TenancyConfig { UnscopedRoles = new() { "superadmin" } },
            Masks = new()
            {
                ["tenant_books.report"] = new MaskConfig
                {
                    Sql = "SELECT id, title FROM tenant_books",
                    Roles = new() { "superadmin" }
                }
            }
        };

        var ex = Assert.Throws<ApiException>(() =>
            ShapeApp.FromTypes(new[] { typeof(TenantBook03) }, config));

        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("tenant scope", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scoped_public_cache_control_is_rejected()
    {
        var config = new ShapeConfig
        {
            Auth = new AuthConfig { Driver = "none" },
            Tenancy = new TenancyConfig { UnscopedRoles = new() { "superadmin" } },
            Masks = new()
            {
                ["tenant_books.report"] = new MaskConfig
                {
                    Sql = "SELECT id, title FROM tenant_books WHERE tenant_id = :__tenant",
                    Roles = new() { "superadmin" },
                    TenantScope = "tenant_id",
                    SqlUnscoped = "SELECT id, title FROM tenant_books",
                    CacheControl = "public, max-age=60"
                }
            }
        };

        var ex = Assert.Throws<ApiException>(() =>
            ShapeApp.FromTypes(new[] { typeof(TenantBook03) }, config));

        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("public cache-control", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oidc_auto_provision_requires_a_tenant_source()
    {
        var config = new ShapeConfig
        {
            Auth = new AuthConfig
            {
                Driver = "jwt",
                Oidc = new OidcConfig
                {
                    // issuer/audience are mandatory (SECURITY-REVIEW M2); set
                    // them so the boot reaches the tenant-source check this test
                    // actually targets.
                    Issuer = "https://issuer.example/",
                    Audience = "shape-app",
                    AutoProvision = true
                }
            },
            Tenancy = new TenancyConfig { UnscopedRoles = new() { "superadmin" } }
        };

        var ex = Assert.Throws<ApiException>(() =>
            ShapeApp.FromTypes(new[] { typeof(TenantBook03) }, config));

        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
        Assert.Contains("tenant source", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Writable_scoped_mask_passes_contract_validation()
    {
        // Regression: a writable mask on a tenant-scoped table carries
        // `tenant_scope` (so ApplyTenantColumn can stamp/guard writes) but its
        // SQL is only an existence probe with no `:__tenant`. ValidateMaskContracts
        // must exempt writable masks — before the fix this threw CONFIG_INVALID
        // at boot for any writable scoped table.
        var config = new ShapeConfig
        {
            Tenancy = new TenancyConfig { UnscopedRoles = new() { "superadmin" } },
            Masks = new Dictionary<string, MaskConfig>
            {
                ["books"] = new MaskConfig
                {
                    Sql = "SELECT id FROM books LIMIT 1",
                    Roles = new() { "admin" },
                    Writable = true,
                    TenantScope = "tenant_id"
                }
            }
        };

        // Writable scoped mask with no :__tenant in its probe SQL — no throw.
        TenancyResolver.ValidateMaskContracts(config, new List<EntityMeta>());

        // Control: a *read* mask that declares tenant scope but omits :__tenant
        // is still a real contract violation and must throw.
        config.Masks["books.list"] = new MaskConfig
        {
            Sql = "SELECT id FROM books",
            Roles = new() { "admin" },
            TenantScope = "tenant_id"
        };
        var ex = Assert.Throws<ApiException>(() =>
            TenancyResolver.ValidateMaskContracts(config, new List<EntityMeta>()));
        Assert.Equal("CONFIG_INVALID", ex.ApiCode);
    }

    [Fact]
    public void Superadmin_login_not_blocked_when_home_tenant_suspended()
    {
        var dbFile = Path.Combine(Path.GetTempPath(), $"shape-suspended-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbFile}";
        var dbConfig = new DatabaseConfig { Driver = "sqlite", ConnectionString = connectionString };
        try
        {
            using (var db = new Database(dbConfig))
            {
                db.Exec("CREATE TABLE tenants (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL, is_active INTEGER NOT NULL DEFAULT 1)", new());
                db.Exec(@"CREATE TABLE users (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  tenant_id INTEGER NOT NULL,
                  email TEXT NOT NULL,
                  password_hash TEXT NOT NULL,
                  display_name TEXT,
                  role TEXT NOT NULL,
                  is_active INTEGER NOT NULL DEFAULT 1
                )", new());
                db.Exec(@"CREATE TABLE refresh_tokens (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  user_id INTEGER NOT NULL,
                  token_hash TEXT NOT NULL,
                  expires_at TEXT NOT NULL,
                  revoked_at TEXT
                )", new());
                // Tenant 1 is suspended (is_active = 0).
                db.Exec("INSERT INTO tenants (id, name, is_active) VALUES (1, 'Acme', 0)", new());
                var hash = BCrypt.Net.BCrypt.HashPassword("password123");
                db.Exec(
                    "INSERT INTO users (tenant_id, email, password_hash, role) VALUES (1, 'root@acme.test', @h, 'superadmin')",
                    new Dictionary<string, object?> { ["h"] = hash });
                db.Exec(
                    "INSERT INTO users (tenant_id, email, password_hash, role) VALUES (1, 'user@acme.test', @h, 'user')",
                    new Dictionary<string, object?> { ["h"] = hash });
            }

            var auth = new AuthConfig
            {
                Driver = "jwt",
                JwtSecret = "test-secret-must-be-long-enough",
                Tenancy = new TenancyConfig { UnscopedRoles = new() { "superadmin" } }
            };
            var svc = new LoginService(dbConfig, auth, new JwtIssuer(auth));

            // Superadmin (unscoped) is exempt from the tenant-active gate — it must
            // still log in so it can re-activate the suspended tenant.
            var ok = svc.Login(new Dictionary<string, object?>
            {
                ["email"] = "root@acme.test",
                ["password"] = "password123"
            });
            Assert.True(ok.ContainsKey("access_token"));

            // A scoped user in the same suspended tenant is still blocked (control).
            var blocked = Assert.Throws<ApiException>(() => svc.Login(new Dictionary<string, object?>
            {
                ["email"] = "user@acme.test",
                ["password"] = "password123"
            }));
            Assert.Equal("TENANT_SUSPENDED", blocked.ApiCode);
        }
        finally
        {
            try { File.Delete(dbFile); } catch { }
        }
    }

    [Fact]
    public void Tenancy_auto_exposes_registry_and_tenant_provision_command()
    {
        var dbFile = Path.Combine(Path.GetTempPath(), $"shape-tenancy-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={dbFile}";

        try
        {
            using (var bootstrap = new Database(new DatabaseConfig { Driver = "sqlite", ConnectionString = connectionString }))
            {
                bootstrap.Exec(@"CREATE TABLE tenants (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  name TEXT NOT NULL,
                  is_active INTEGER NOT NULL DEFAULT 1
                )", new());
                bootstrap.Exec(@"CREATE TABLE users (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  tenant_id INTEGER NOT NULL,
                  email TEXT NOT NULL,
                  password_hash TEXT NOT NULL,
                  display_name TEXT,
                  role TEXT NOT NULL,
                  is_active INTEGER NOT NULL DEFAULT 1
                )", new());
            }

            var config = ShapeApp.FromDatabase(new ShapeConfig
            {
                Db = new DatabaseConfig { Driver = "sqlite", ConnectionString = connectionString },
                Auth = new AuthConfig { Driver = "none" },
                Tenancy = new TenancyConfig { UnscopedRoles = new() { "superadmin" } }
            });

            Assert.Contains("tenants.list", config.Masks);
            Assert.Contains("tenants.get", config.Masks);
            Assert.Contains("tenant.provision", config.Runtime.Commands);
            Assert.Equal(new[] { "superadmin" }, config.Masks["tenants.list"].Roles);

            using var db = new Database(new DatabaseConfig { Driver = "sqlite", ConnectionString = connectionString });
            var executor = new BatchExecutor(db, new MaskRegistry(config.Masks), new ValidatorRegistry(), config.Tenancy);
            var commandContext = new CommandContext(
                db,
                executor,
                new Dictionary<string, object?> { ["role"] = "superadmin" });

            var result = config.Commands!["tenant.provision"](
                new Dictionary<string, object?>
                {
                    ["tenant"] = new Dictionary<string, object?> { ["name"] = "Acme" },
                    ["user"] = new Dictionary<string, object?>
                    {
                        ["email"] = "admin@acme.test",
                        ["password"] = "password123",
                        ["role"] = "admin"
                    }
                },
                commandContext);

            Assert.True(Convert.ToInt64(result["tenant_id"]) > 0);
            Assert.True(Convert.ToInt64(result["user_id"]) > 0);
        }
        finally
        {
            try { File.Delete(dbFile); } catch { }
        }
    }

    [Fact]
    public void Tenant_fk_mismatch_throws_typed_error_when_validation_is_enabled()
    {
        using var db = new Database(new DatabaseConfig { Driver = "sqlite", ConnectionString = "Data Source=:memory:" });
        db.Exec(@"CREATE TABLE tenant_authors (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          tenant_id INTEGER NOT NULL,
          name TEXT NOT NULL
        )", new());
        db.Exec(@"CREATE TABLE tenant_books_fk (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          tenant_id INTEGER NOT NULL,
          author_id INTEGER NOT NULL,
          title TEXT NOT NULL
        )", new());
        db.Exec("INSERT INTO tenant_authors (tenant_id, name) VALUES (2, 'Other tenant')", new());

        var tenancy = new TenancyConfig
        {
            UnscopedRoles = new() { "superadmin" },
            ValidateFks = true
        };
        var entities = new EntityReader().ReadTypes(new[] { typeof(TenantAuthor04), typeof(TenantBook04) });
        var maskConfigs = new MaskGenerator(driver: "sqlite", tenancy: tenancy).Generate(entities);
        var registry = new MaskRegistry(maskConfigs);
        var executor = new BatchExecutor(db, registry, new ValidatorRegistry(), tenancy);

        var ex = Assert.Throws<ApiException>(() =>
            executor.ExecuteMutations(new List<Dictionary<string, object?>>
            {
                new()
                {
                    ["table"] = "tenant_books_fk",
                    ["action"] = "insert",
                    ["data"] = new Dictionary<string, object?>
                    {
                        ["author_id"] = 1,
                        ["title"] = "Cross-tenant"
                    }
                }
            }, new Dictionary<string, object?> { ["tenant_id"] = 1 }));

        Assert.Equal("VALIDATION_ERROR", ex.ApiCode);
        Assert.NotNull(ex.Details);
        Assert.Equal("TENANT_FK_MISMATCH", ex.Details!["detail"]);
    }
}
