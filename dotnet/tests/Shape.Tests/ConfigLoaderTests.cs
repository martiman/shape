using Shape.Config;
using Shape.Core;
using Xunit;

namespace Shape.Tests;

public sealed class ConfigLoaderTests
{
    private static string WriteTempConfig(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), $"shape-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void Load_populates_db_auth_and_masks_from_json()
    {
        var path = WriteTempConfig("""
        {
            "serviceName": "to-api",
            "endpoint": "/",
            "db": {
                "driver": "pgsql",
                "connectionString": "Host=localhost;Database=aps_dev"
            },
            "auth": {
                "driver": "jwt",
                "jwtSecret": "test-secret-32-bytes-of-keymaterial",
                "oidc": {
                    "preset": "entra",
                    "tenantId": "tenant-123",
                    "audience": "api://my-api"
                }
            },
            "masks": {
                "books.featured": {
                    "sql": "SELECT id FROM books WHERE featured = 1",
                    "roles": ["user"],
                    "description": "Featured books"
                }
            },
            "access": {
                "locations": ["admin"]
            },
            "accessByRole": {
                "ucetni": ["ledger", "invoices"]
            },
            "adminMasks": true,
            "secretTables": ["users", "refresh_tokens"],
            "defaultListLimit": 50
        }
        """);

        try
        {
            var cfg = ConfigLoader.Load(path);

            Assert.Equal("to-api", cfg.ServiceName);
            Assert.Equal("pgsql", cfg.Db.Driver);
            Assert.Equal("Host=localhost;Database=aps_dev", cfg.Db.ConnectionString);
            Assert.Equal("jwt", cfg.Auth.Driver);
            Assert.Equal("test-secret-32-bytes-of-keymaterial", cfg.Auth.JwtSecret);
            Assert.NotNull(cfg.Auth.Oidc);
            Assert.Equal("entra", cfg.Auth.Oidc!.Preset);
            Assert.Equal("tenant-123", cfg.Auth.Oidc.TenantId);
            Assert.Equal("api://my-api", cfg.Auth.Oidc.Audience);
            Assert.Single(cfg.Masks);
            Assert.Equal("SELECT id FROM books WHERE featured = 1", cfg.Masks["books.featured"].Sql);
            Assert.Equal(new[] { "user" }, cfg.Masks["books.featured"].Roles);
            Assert.Equal(new[] { "admin" }, cfg.Access!["locations"]);
            Assert.Equal(new[] { "ledger", "invoices" }, cfg.AccessByRole!["ucetni"]);
            Assert.True(cfg.AdminMasks);
            Assert.Equal(50, cfg.DefaultListLimit);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_overrides_supply_code_only_fields()
    {
        var path = WriteTempConfig("""
        {
            "serviceName": "to-api",
            "db": { "driver": "sqlite", "connectionString": "Data Source=:memory:" }
        }
        """);

        try
        {
            var overrides = new ShapeConfig
            {
                Commands = new()
                {
                    ["book.publish"] = (p, ctx) => new() { ["ok"] = true }
                }
            };

            var cfg = ConfigLoader.Load(path, overrides);

            // JSON wins on the fields it sets; overrides keep their delegate-bearing fields.
            Assert.Equal("to-api", cfg.ServiceName);
            Assert.Equal("sqlite", cfg.Db.Driver);
            Assert.NotNull(cfg.Commands);
            Assert.Contains("book.publish", cfg.Commands!.Keys);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_supports_jsonc_comments_and_trailing_commas()
    {
        var path = WriteTempConfig("""
        {
            // top-level comment
            "serviceName": "demo",
            "db": {
                "driver": "sqlite",
                "connectionString": "Data Source=:memory:",
            }, /* trailing comma + block comment */
        }
        """);

        try
        {
            var cfg = ConfigLoader.Load(path);
            Assert.Equal("demo", cfg.ServiceName);
            Assert.Equal("sqlite", cfg.Db.Driver);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_throws_CONFIG_INVALID_on_malformed_json()
    {
        var path = WriteTempConfig("{ this is not json }");

        try
        {
            var ex = Assert.Throws<ApiException>(() => ConfigLoader.Load(path));
            Assert.Equal("CONFIG_INVALID", ex.ApiCode);
            Assert.Equal(500, ex.Status);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_throws_FileNotFound_for_missing_path()
    {
        var ghost = Path.Combine(Path.GetTempPath(), $"shape-config-missing-{Guid.NewGuid():N}.json");
        Assert.Throws<FileNotFoundException>(() => ConfigLoader.Load(ghost));
    }

    [Fact]
    public void Load_auth_string_shorthand_expands_to_jwt_driver()
    {
        var path = WriteTempConfig("""
        {
            "auth": "jwt",
            "db": { "driver": "sqlite", "connectionString": "Data Source=:memory:" }
        }
        """);

        try
        {
            var cfg = ConfigLoader.Load(path);
            Assert.Equal("jwt", cfg.Auth.Driver);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_auth_true_shorthand_expands_to_jwt_driver()
    {
        var path = WriteTempConfig("""
        {
            "auth": true,
            "db": { "driver": "sqlite", "connectionString": "Data Source=:memory:" }
        }
        """);

        try
        {
            var cfg = ConfigLoader.Load(path);
            Assert.Equal("jwt", cfg.Auth.Driver);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_auth_false_shorthand_expands_to_none_driver()
    {
        var path = WriteTempConfig("""
        {
            "auth": false,
            "db": { "driver": "sqlite", "connectionString": "Data Source=:memory:" }
        }
        """);

        try
        {
            var cfg = ConfigLoader.Load(path);
            Assert.Equal("none", cfg.Auth.Driver);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_auth_object_form_still_works()
    {
        var path = WriteTempConfig("""
        {
            "auth": {
                "driver": "jwt",
                "jwtSecret": "explicit-secret",
                "passwordMinLength": 12
            }
        }
        """);

        try
        {
            var cfg = ConfigLoader.Load(path);
            Assert.Equal("jwt", cfg.Auth.Driver);
            Assert.Equal("explicit-secret", cfg.Auth.JwtSecret);
            Assert.Equal(12, cfg.Auth.PasswordMinLength);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_db_dsn_translates_pdo_form_to_native()
    {
        var path = WriteTempConfig("""
        {
            "db": {
                "dsn": "pgsql:host=db.internal;port=5432;dbname=aps_dev",
                "user": "aps",
                "pass": "secret"
            }
        }
        """);

        try
        {
            var cfg = ConfigLoader.Load(path);
            Assert.Equal("pgsql", cfg.Db.Driver);
            Assert.Equal("Host=db.internal;Port=5432;Database=aps_dev;Username=aps;Password=secret", cfg.Db.ConnectionString);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_db_explicit_connection_string_overrides_dsn()
    {
        // Operator escape hatch: when both dsn and connectionString are
        // present, the explicit native form wins.
        var path = WriteTempConfig("""
        {
            "db": {
                "dsn": "pgsql:host=ignored;dbname=ignored",
                "driver": "sqlite",
                "connectionString": "Data Source=:memory:"
            }
        }
        """);

        try
        {
            var cfg = ConfigLoader.Load(path);
            Assert.Equal("sqlite", cfg.Db.Driver);
            Assert.Equal("Data Source=:memory:", cfg.Db.ConnectionString);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_mask_sqlFile_reads_external_file_relative_to_config_dir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shape-sqlfile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "masks"));
        try
        {
            var sqlPath = Path.Combine(dir, "masks", "stock.byLocation.sql");
            File.WriteAllText(sqlPath, """
                SELECT id, location_id, qty
                FROM stock
                WHERE location_id = :location_id
                  AND deleted_at IS NULL
                """);

            var configPath = Path.Combine(dir, "shape.json");
            File.WriteAllText(configPath, """
            {
                "masks": {
                    "stock.byLocation": {
                        "sqlFile": "masks/stock.byLocation.sql",
                        "params": { "location_id": { "type": "string", "required": true } },
                        "roles": ["admin"]
                    }
                }
            }
            """);

            var cfg = ConfigLoader.Load(configPath);
            var sql = cfg.Masks["stock.byLocation"].Sql;
            Assert.Contains("FROM stock", sql);
            Assert.Contains(":location_id", sql); // raw — :param rewrite happens at MaskRegistry
            Assert.Contains("\n", sql);           // multi-line preserved
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_mask_with_both_sql_and_sqlFile_throws_CONFIG_INVALID()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shape-sqlfile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "masks"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "masks", "x.sql"), "SELECT 1");
            var configPath = Path.Combine(dir, "shape.json");
            File.WriteAllText(configPath, """
            {
                "masks": {
                    "x": {
                        "sql": "SELECT 1",
                        "sqlFile": "masks/x.sql"
                    }
                }
            }
            """);

            var ex = Assert.Throws<ApiException>(() => ConfigLoader.Load(configPath));
            Assert.Equal("CONFIG_INVALID", ex.ApiCode);
            Assert.Contains("both `sql` and `sqlFile`", ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_mask_sqlFile_absolute_path_throws_CONFIG_INVALID()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shape-sqlfile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, "shape.json");
            // Use a path absolute on both Windows and Unix (Path.GetTempPath always rooted).
            var abs = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "evil.sql"));
            File.WriteAllText(configPath, $$"""
            {
                "masks": {
                    "x": { "sqlFile": {{System.Text.Json.JsonSerializer.Serialize(abs)}} }
                }
            }
            """);

            var ex = Assert.Throws<ApiException>(() => ConfigLoader.Load(configPath));
            Assert.Equal("CONFIG_INVALID", ex.ApiCode);
            Assert.Contains("absolute or home-relative", ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_mask_sqlFile_missing_file_throws_CONFIG_INVALID()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"shape-sqlfile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, "shape.json");
            File.WriteAllText(configPath, """
            {
                "masks": {
                    "x": { "sqlFile": "masks/missing.sql" }
                }
            }
            """);

            var ex = Assert.Throws<ApiException>(() => ConfigLoader.Load(configPath));
            Assert.Equal("CONFIG_INVALID", ex.ApiCode);
            Assert.Contains("missing `sqlFile`", ex.Message);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_mask_sqlFile_pdo_params_still_get_rewritten_at_registry()
    {
        // End-to-end: file-loaded SQL with PDO :params → MaskRegistry runs
        // RewriteColonParams → resolved mask carries Dapper @params.
        var dir = Path.Combine(Path.GetTempPath(), $"shape-sqlfile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(dir, "masks"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "masks", "x.sql"),
                "SELECT * FROM t WHERE id = :id");
            var configPath = Path.Combine(dir, "shape.json");
            File.WriteAllText(configPath, """
            {
                "masks": {
                    "t.byId": { "sqlFile": "masks/x.sql" }
                }
            }
            """);

            var cfg = ConfigLoader.Load(configPath);
            var registry = new MaskRegistry(cfg.Masks);
            var mask = registry.Resolve("t.byId");

            Assert.Equal("SELECT * FROM t WHERE id = @id", mask.Sql);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Load_param_schema_round_trips()
    {
        var path = WriteTempConfig("""
        {
            "masks": {
                "books.byId": {
                    "sql": "SELECT id FROM books WHERE id = @id",
                    "params": {
                        "id": { "type": "int", "required": true }
                    }
                }
            }
        }
        """);

        try
        {
            var cfg = ConfigLoader.Load(path);
            var mask = cfg.Masks["books.byId"];
            Assert.NotNull(mask.Params);
            Assert.Equal("int", mask.Params!["id"].Type);
            Assert.True(mask.Params["id"].Required);
        }
        finally { File.Delete(path); }
    }
}
